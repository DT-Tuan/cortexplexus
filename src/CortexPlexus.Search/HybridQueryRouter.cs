using System.Text.RegularExpressions;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.Search;

/// <summary>
/// Search results plus the ADR-018 embedding-space footer (skip / excluded / unknown),
/// null when every touched repo matches the server's current space. Returned by value so
/// the router keeps no per-call state — it is DI-scoped today, but nothing may rely on that.
/// </summary>
public sealed record HybridSearchOutcome(IReadOnlyList<SearchResult> Results, string? SpaceFooter);

public sealed partial class HybridQueryRouter(
    IVectorStore vectorStore,
    IFullTextStore fullTextStore,
    IEmbeddingService embeddingService,
    IQueryExpander queryExpander,
    IRepositoryStore repositoryStore,
    EmbeddingSpace currentSpace,
    ILogger<HybridQueryRouter> logger)
{
    /// <summary>Back-compat entry point for callers that don't render footers (CLI, explore).</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        => (await SearchWithFooterAsync(request, ct)).Results;

    public async Task<HybridSearchOutcome> SearchWithFooterAsync(SearchRequest request, CancellationToken ct = default)
    {
        var queryType = request.Type == SearchType.Hybrid
            ? ClassifyQuery(request.Query)
            : request.Type;

        logger.LogDebug("Query classified as {Type}: {Query} (expand={Expand})", queryType, request.Query, request.Expand);

        // ADR-018: only the vector leg is space-sensitive — plan once per call and thread
        // it through; BM25/graph-classified queries never pay the repo-stamp lookup.
        var (plan, footer) = queryType is SearchType.Vector or SearchType.Hybrid
            ? await PlanVectorSpaceAsync(request.RepoId, ct)
            : (VectorSpacePlan.AllowAll, null);

        var results = queryType switch
        {
            SearchType.Graph => await SearchGraphAsync(request, ct),
            SearchType.Vector => await SearchVectorAsync(request, plan, ct),
            SearchType.Bm25 => await SearchBm25Async(request, ct),
            _ => await SearchHybridAsync(request, plan, ct)
        };

        return new HybridSearchOutcome(results, footer);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchHybridAsync(
        SearchRequest request, VectorSpacePlan spacePlan, CancellationToken ct)
    {
        var useExpand = request.Expand && queryExpander.IsEnabled;

        // Preprocess: extract code-like tokens for a focused BM25 query
        var codeQuery = ExtractCodeQuery(request.Query);
        var bm25Query = codeQuery ?? request.Query;

        if (codeQuery is not null && codeQuery != request.Query)
            logger.LogDebug("Preprocessed query for BM25: '{Original}' → '{Extracted}'", request.Query, codeQuery);

        // Run BM25 with preprocessed query (doesn't need embedding)
        var bm25Task = fullTextStore.SearchAsync(bm25Query, request.Limit, request.RepoId, request.Kind, ct);

        // Vector search uses original natural language query (better for semantic matching)
        var vectorResults = await SafeVectorSearchAsync(request, useExpand, spacePlan, ct);
        var bm25Results = await bm25Task;

        if (vectorResults.Count == 0 && bm25Results.Count > 0)
            logger.LogDebug("Vector search returned empty (embedding unavailable?), using BM25 only");

        var rankedLists = new List<(string Source, IReadOnlyList<SearchResult> Results)>
        {
            ("vector", vectorResults),
            ("bm25", bm25Results)
        };

        // Multi-query expansion: search additional variants and add to RRF
        if (useExpand)
        {
            var multiQueryResults = await SearchMultiQueryAsync(request, ct);
            if (multiQueryResults.Count > 0)
                rankedLists.Add(("expanded", multiQueryResults));
        }

        return RrfFusion.Fuse(rankedLists, request.Limit);
    }

    /// <summary>
    /// Attempts vector search; returns empty list on embedding failure (rate limit, network, etc.)
    /// instead of propagating the error. Hybrid search degrades to BM25-only.
    /// Also applies the precomputed ADR-018 embedding-space plan (skip / filter).
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>> SafeVectorSearchAsync(
        SearchRequest request, bool useExpand, VectorSpacePlan spacePlan, CancellationToken ct)
    {
        try
        {
            if (spacePlan.SkipVectorLeg)
                return [];

            if (useExpand)
                return await SearchVectorWithHydeAsync(request, spacePlan.RequireProvider, spacePlan.RequireModel, ct);

            var embedding = await embeddingService.EmbedAsync(request.Query, ct);
            if (embedding.Length == 0) return [];
            return await vectorStore.SearchAsync(
                embedding, request.Limit, request.RepoId, request.Kind,
                spacePlan.RequireProvider, spacePlan.RequireModel, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Vector search failed, falling back to BM25");
            return [];
        }
    }

    /// <summary>
    /// Decide whether the vector leg may run, which space filter to apply, and the footer
    /// to surface (ADR-018). Pure with respect to the router: everything travels by value.
    /// </summary>
    private async Task<(VectorSpacePlan Plan, string? Footer)> PlanVectorSpaceAsync(
        Guid? repoId, CancellationToken ct)
    {
        var repos = await repositoryStore.ListAsync(ct);

        if (repoId is { } id)
        {
            var repo = repos.FirstOrDefault(r => r.Id == id);
            if (repo is null)
                return (VectorSpacePlan.AllowAll, null);

            if (repo.EmbeddingProvider is null)
            {
                // Legacy unstamped: vector leg runs, surface the unknown hint.
                return (VectorSpacePlan.AllowAll, "space unknown — stamp by re-indexing");
            }

            if (repo.EmbeddingProvider != currentSpace.Provider
                || repo.EmbeddingModel != currentSpace.Model)
            {
                var footer =
                    $"⚠️ semantic leg skipped: repo carries {repo.EmbeddingProvider}:{repo.EmbeddingModel} vectors, " +
                    $"server queries with {currentSpace.Provider}:{currentSpace.Model}. Re-index to migrate.";
                return (VectorSpacePlan.Skip, footer);
            }

            return (VectorSpacePlan.AllowAll, null);
        }

        // Cross-repo: filter vector hits to matching (or NULL) stamps; report exclusions.
        var mismatched = repos
            .Where(r => r.EmbeddingProvider is not null
                        && (r.EmbeddingProvider != currentSpace.Provider
                            || r.EmbeddingModel != currentSpace.Model))
            .ToList();
        var crossRepoFooter = mismatched.Count > 0
            ? $"{mismatched.Count} repo(s) excluded from semantic ranking (space mismatch: {string.Join(", ", mismatched.Select(r => r.Name))})"
            : null;

        var plan = new VectorSpacePlan(
            SkipVectorLeg: false,
            RequireProvider: currentSpace.Provider,
            RequireModel: currentSpace.Model);
        return (plan, crossRepoFooter);
    }

    private readonly record struct VectorSpacePlan(
        bool SkipVectorLeg,
        string? RequireProvider = null,
        string? RequireModel = null)
    {
        public static VectorSpacePlan AllowAll => new(false);
        public static VectorSpacePlan Skip => new(true);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchVectorWithHydeAsync(
        SearchRequest request, string? requireProvider, string? requireModel, CancellationToken ct)
    {
        // HyDE: generate hypothetical document, embed it, search
        var hypothetical = await queryExpander.ExpandHydeAsync(request.Query, ct);

        var textToEmbed = hypothetical ?? request.Query;
        var embedding = await embeddingService.EmbedAsync(textToEmbed, ct);

        if (embedding.Length == 0) return [];
        return await vectorStore.SearchAsync(
            embedding, request.Limit, request.RepoId, request.Kind,
            requireProvider, requireModel, ct);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchMultiQueryAsync(SearchRequest request, CancellationToken ct)
    {
        var variants = await queryExpander.ExpandMultiQueryAsync(request.Query, ct: ct);

        // Skip if only original query returned
        if (variants.Count <= 1) return [];

        // Search each variant via BM25 (cheap, parallel)
        var tasks = variants
            .Skip(1) // Skip original query (already searched above)
            .Select(v => fullTextStore.SearchAsync(v, request.Limit, request.RepoId, request.Kind, ct));

        var resultSets = await Task.WhenAll(tasks);

        // Flatten all variant results for RRF
        return resultSets.SelectMany(r => r).ToList();
    }

    private async Task<IReadOnlyList<SearchResult>> SearchVectorAsync(
        SearchRequest request, VectorSpacePlan spacePlan, CancellationToken ct)
    {
        IReadOnlyList<SearchResult> vectorResults;
        try
        {
            if (spacePlan.SkipVectorLeg)
            {
                // Pure vector mode: degrade to BM25 when space mismatch blocks the vector leg.
                logger.LogInformation("Vector search skipped (space mismatch), falling back to BM25 for: {Query}", request.Query);
                return await fullTextStore.SearchAsync(request.Query, request.Limit, request.RepoId, request.Kind, ct);
            }

            if (request.Expand && queryExpander.IsEnabled)
            {
                vectorResults = await SearchVectorWithHydeAsync(
                    request, spacePlan.RequireProvider, spacePlan.RequireModel, ct);
            }
            else
            {
                var embedding = await embeddingService.EmbedAsync(request.Query, ct);
                vectorResults = embedding.Length > 0
                    ? await vectorStore.SearchAsync(
                        embedding, request.Limit, request.RepoId, request.Kind,
                        spacePlan.RequireProvider, spacePlan.RequireModel, ct)
                    : [];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Embedding failed, falling back to BM25");
            vectorResults = [];
        }

        // Fallback: if vector search returned nothing (embedding unavailable), use BM25
        if (vectorResults.Count == 0)
        {
            logger.LogInformation("Vector search unavailable, falling back to BM25 for: {Query}", request.Query);
            return await fullTextStore.SearchAsync(request.Query, request.Limit, request.RepoId, request.Kind, ct);
        }

        return vectorResults;
    }

    private Task<IReadOnlyList<SearchResult>> SearchBm25Async(SearchRequest request, CancellationToken ct)
    {
        return fullTextStore.SearchAsync(request.Query, request.Limit, request.RepoId, request.Kind, ct);
    }

    private Task<IReadOnlyList<SearchResult>> SearchGraphAsync(SearchRequest request, CancellationToken ct)
    {
        // Graph search delegates to specific graph queries via MCP tools directly
        // For generic search, fall back to BM25
        return SearchBm25Async(request, ct);
    }

    /// <summary>
    /// Extracts code-like tokens (PascalCase, camelCase, FQN patterns) from a mixed
    /// natural language + code query. Returns a cleaner query for BM25, or null if
    /// the original query is already clean.
    /// </summary>
    internal static string? ExtractCodeQuery(string query)
    {
        // If query has no spaces, it's already a single token — no preprocessing needed
        if (!query.Contains(' ')) return null;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var codeTokens = new List<string>();

        foreach (var token in tokens)
        {
            var cleaned = token.Trim(',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '?', '!');
            if (string.IsNullOrEmpty(cleaned)) continue;

            // FQN pattern (dots): MyApp.Services.OrderService
            if (cleaned.Contains('.') && !cleaned.Contains(' '))
            {
                codeTokens.Add(cleaned);
                continue;
            }

            // PascalCase: LocalIndexer, FileWatcher, HttpClient
            if (IsPascalCase(cleaned))
            {
                codeTokens.Add(cleaned);
                continue;
            }

            // camelCase: getCallers, indexAsync
            if (IsCamelCase(cleaned))
            {
                codeTokens.Add(cleaned);
                continue;
            }

            // ALL_CAPS or UPPER_SNAKE: API, HTTP, MAX_RETRY
            if (cleaned.Length >= 2 && cleaned == cleaned.ToUpperInvariant() && cleaned.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                codeTokens.Add(cleaned);
                continue;
            }
        }

        // If we found code tokens, use them as the BM25 query (space-separated)
        if (codeTokens.Count > 0)
            return string.Join(" ", codeTokens);

        // No code tokens detected — return null to use original query
        return null;
    }

    private static bool IsPascalCase(string s)
    {
        if (s.Length < 2 || !char.IsUpper(s[0])) return false;
        // Must have at least one lowercase after initial upper (not ALL_CAPS)
        var hasLower = false;
        var upperCount = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsUpper(s[i])) upperCount++;
            if (char.IsLower(s[i])) hasLower = true;
        }
        // PascalCase: starts upper, has lowercase, has 2+ uppercase transitions
        return hasLower && upperCount >= 2;
    }

    private static bool IsCamelCase(string s)
    {
        if (s.Length < 2 || !char.IsLower(s[0])) return false;
        return s.Any(char.IsUpper);
    }

    private static SearchType ClassifyQuery(string query)
    {
        var lower = query.ToLowerInvariant();

        // Structural queries → Graph
        if (ContainsAny(lower, ["caller", "callers", "called by", "depends on", "dependency",
            "implements", "inherits", "hierarchy", "override"]))
            return SearchType.Graph;

        // Exact name match patterns → BM25
        if (query.Contains('.') && !query.Contains(' '))
            return SearchType.Bm25;

        // Everything else → Hybrid (vector + BM25)
        return SearchType.Hybrid;
    }

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(text.Contains);
}
