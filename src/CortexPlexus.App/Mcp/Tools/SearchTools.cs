using System.ComponentModel;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class SearchTools
{
    [McpServerTool, Description("Search code by exact name or keyword using full-text search")]
    public static async Task<string> SearchCode(
        [Description("Search query (class name, method name, or keyword)")] string query,
        [Description("Repository name to search in (optional — omit to search all repos). Use ListRepositories to see available names.")] string? repository = null,
        [Description("Maximum results to return")] int limit = 20,
        [Description("Enable query expansion (HyDE + multi-query) for better recall")] bool expand = false,
        HybridQueryRouter router = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        var results = await router.SearchAsync(new SearchRequest(query, SearchType.Bm25, limit, RepoId: repoId, Expand: expand));
        var body = results.Count == 0 ? "No results found." : compressor.Compress(results);
        // BM25 does not use the vector leg; space footer is N/A here.
        return await AppendStalenessFooter(body, repoId, repoStore);
    }

    [McpServerTool, Description("Search code using natural language semantic search with optional query expansion")]
    public static async Task<string> SemanticSearch(
        [Description("Natural language query (e.g., 'payment processing logic')")] string query,
        [Description("Repository name to search in (optional — omit to search all repos)")] string? repository = null,
        [Description("Maximum results to return")] int limit = 10,
        [Description("Enable query expansion (HyDE + multi-query) for better recall")] bool expand = false,
        HybridQueryRouter router = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        var outcome = await router.SearchWithFooterAsync(new SearchRequest(query, SearchType.Hybrid, limit, RepoId: repoId, Expand: expand));
        var body = outcome.Results.Count == 0 ? "No results found." : compressor.Compress(outcome.Results);
        // ADR-018: append embedding-space footer (skip / excluded / unknown) when present.
        body = AppendIfPresent(body, outcome.SpaceFooter);
        return await AppendStalenessFooter(body, repoId, repoStore);
    }

    /// <summary>
    /// Append the staleness warning when the relevant repository/repositories
    /// have a last-indexed timestamp older than 24h. For scoped searches we
    /// check only that repo; for cross-repo searches we check the stalest.
    /// See <see cref="StalenessLabel"/> for thresholds.
    /// </summary>
    private static async Task<string> AppendStalenessFooter(string body, Guid? repoId, IRepositoryStore repoStore)
    {
        var repos = await repoStore.ListAsync();
        var relevant = repoId is null
            ? repos.Where(r => r.LastIndexed is not null).ToList()
            : repos.Where(r => r.Id == repoId.Value).ToList();
        if (relevant.Count == 0) return body;

        var stalest = relevant
            .OrderBy(r => r.LastIndexed ?? DateTimeOffset.MaxValue)
            .First();
        var footer = StalenessLabel.SearchFooter(stalest.LastIndexed, DateTimeOffset.UtcNow);
        return AppendIfPresent(body, footer);
    }

    /// <summary>Shared "append footer if non-null" seam (ADR-015 + ADR-018).</summary>
    private static string AppendIfPresent(string body, string? footer)
        => footer is null ? body : $"{body}\n\n{footer}";
}
