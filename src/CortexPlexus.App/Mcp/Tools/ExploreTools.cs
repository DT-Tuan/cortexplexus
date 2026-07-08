using System.ComponentModel;
using System.Text;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Memory;
using CortexPlexus.Search;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class ExploreTools
{
    /// <summary>
    /// Multi-step exploration: search → identify top symbol → get callers/callees/deps/impls
    /// Returns rich context in a single call instead of requiring 5+ separate tool calls.
    /// </summary>
    [McpServerTool, Description(
        "Deep-explore a topic by query string: searches code, then automatically fetches callers, callees, " +
        "dependencies, and implementations for the top result. Returns rich context in one call. " +
        "Use this instead of calling search + get_callers + get_dependencies separately. " +
        "Required parameter: 'query' (the topic/keywords to explore).")]
    public static async Task<string> ExploreTopic(
        [Description("Required: natural language query or code symbol name to explore (e.g. 'chat message flow', 'PaymentService')")] string? query = null,
        [Description("Repository name to scope (optional)")] string? repository = null,
        [Description("Exploration depth: 'shallow' (search only), 'normal' (+ callers/deps), 'deep' (+ callees/impls/hierarchy)")] string depth = "normal",
        [Description("Include agent memories linked to the top symbol (default false; requires Memory__Enabled=true)")] bool includeMemories = false,
        HybridQueryRouter router = default!,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!,
        IAgentMemoryStore? memoryStore = null,
        IOptions<MemoryOptions>? memoryOptions = null)
    {
        // R22 Fix #11: friendly missing-param error (was throwing 500 via SDK before).
        if (string.IsNullOrWhiteSpace(query))
            return "Missing required parameter 'query'. Pass the topic/keywords to explore. " +
                   "Example: ExploreTopic(query: 'chat message flow', repository: 'CortexFlow')";

        var sb = new StringBuilder();
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);

        // Step 1: Search for the topic
        var searchRequest = new SearchRequest(query, SearchType.Hybrid, 10, RepoId: repoId);
        var searchResults = await router.SearchAsync(searchRequest);

        if (searchResults.Count == 0)
        {
            // Fallback: try BM25 exact search
            searchRequest = searchRequest with { Type = SearchType.Bm25, Limit = 20 };
            searchResults = await router.SearchAsync(searchRequest);
        }

        if (searchResults.Count == 0)
            return $"No code found for '{query}'. Try a more specific query or check repository name.";

        sb.AppendLine($"## Exploration: {query}");
        sb.AppendLine();

        // Step 2: Show search results
        sb.AppendLine($"### Search Results ({searchResults.Count})");
        sb.AppendLine(compressor.Compress(searchResults));
        sb.AppendLine();

        if (depth == "shallow")
            return sb.ToString();

        // Step 3: Pick the top non-doc result for deep exploration
        var topSymbol = searchResults.FirstOrDefault(r =>
            r.Kind is "class" or "interface" or "method" or "record" or "struct");

        if (topSymbol is null)
            return sb.ToString();

        var targetFqn = topSymbol.Fqn;
        sb.AppendLine($"### Deep Dive: {targetFqn}");
        sb.AppendLine();

        // Step 4: Get callers (who uses this?)
        var callers = await graphStore.QueryCallersAsync(targetFqn, depth: 1);
        // Filter framework noise
        callers = callers.Where(r => !r.Fqn.StartsWith("System.") && !r.Fqn.StartsWith("Microsoft.")).ToList();
        if (callers.Count > 0)
        {
            sb.AppendLine($"**Callers ({callers.Count}):** Who uses this?");
            sb.AppendLine(compressor.Compress(callers.Take(10).ToList()));
            sb.AppendLine();
        }

        // Step 5: Get dependencies (what does this depend on?)
        var deps = await graphStore.QueryDependenciesAsync(targetFqn, depth: 1);
        deps = deps.Where(r => !r.Fqn.StartsWith("System.") && !r.Fqn.StartsWith("Microsoft.")).ToList();
        if (deps.Count > 0)
        {
            sb.AppendLine($"**Dependencies ({deps.Count}):** What does it depend on?");
            sb.AppendLine(compressor.Compress(deps.Take(10).ToList()));
            sb.AppendLine();
        }

        if (depth == "normal")
            return sb.ToString();

        // Step 6 (deep): Get callees
        var callees = await graphStore.QueryCalleesAsync(targetFqn, depth: 1);
        callees = callees.Where(r => !r.Fqn.StartsWith("System.") && !r.Fqn.StartsWith("Microsoft.")).ToList();
        if (callees.Count > 0)
        {
            sb.AppendLine($"**Callees ({callees.Count}):** What does it call?");
            sb.AppendLine(compressor.Compress(callees.Take(10).ToList()));
            sb.AppendLine();
        }

        // Step 7 (deep): Get implementations (if interface/abstract)
        if (topSymbol.Kind is "interface" or "class")
        {
            var impls = await graphStore.QueryImplementationsAsync(targetFqn);
            if (impls.Count > 0)
            {
                sb.AppendLine($"**Implementations ({impls.Count}):**");
                sb.AppendLine(compressor.Compress(impls));
                sb.AppendLine();
            }
        }

        // Step 8 (deep): Get references (who accesses properties of this type?)
        var referencedBy = await graphStore.QueryReferencedByAsync(targetFqn, depth: 1);
        referencedBy = referencedBy.Where(r =>
            !r.Fqn.StartsWith("System.") && !r.Fqn.StartsWith("Microsoft.")).ToList();
        if (referencedBy.Count > 0)
        {
            sb.AppendLine($"**Referenced By ({referencedBy.Count}):** Who accesses this type?");
            sb.AppendLine(compressor.Compress(referencedBy.Take(10).ToList()));
            sb.AppendLine();
        }

        // Step 9 (opt-in): linked agent memories. Best-effort — never break explore
        // on a memory-store hiccup.
        if (includeMemories && memoryOptions?.Value.Enabled == true && memoryStore is not null)
        {
            try
            {
                var memories = await memoryStore.RecallAsync(
                    queryEmbedding: null,
                    scope: null, scopeId: null, topic: null,
                    relatedFqn: targetFqn, limit: 5);
                if (memories.Count > 0)
                {
                    sb.AppendLine($"**Linked Memories ({memories.Count}):**");
                    foreach (var m in memories)
                    {
                        var topicLabel = m.Memory.Topic is null ? "" : $"[{m.Memory.Topic}] ";
                        sb.AppendLine($"  - {topicLabel}{m.Memory.Content}");
                    }
                    sb.AppendLine();
                }
            }
            catch { /* ignore — memory is best-effort */ }
        }

        // Append staleness footer if the target repo's index is > 24h old.
        // Uses the stalest of the probed repos so cross-repo searches don't
        // hide the problem with a fresh repo.
        var allRepos = await repoStore.ListAsync();
        var relevantRepos = repoId is null
            ? allRepos.Where(r => r.LastIndexed is not null).ToList()
            : allRepos.Where(r => r.Id == repoId.Value).ToList();
        if (relevantRepos.Count > 0)
        {
            var stalest = relevantRepos
                .OrderBy(r => r.LastIndexed ?? DateTimeOffset.MaxValue).First();
            var footer = StalenessLabel.SearchFooter(stalest.LastIndexed, DateTimeOffset.UtcNow);
            if (footer is not null)
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Onboard to a new codebase: architecture overview + key entry points + DI wiring.
    /// Single tool call replaces: GetArchitecture + GetApiEndpoints + GetDiRegistrations + GetEntityMapping.
    /// </summary>
    [McpServerTool, Description(
        "Get a complete codebase overview for onboarding — one call: architecture, API " +
        "endpoints (ASP.NET/FastAPI/Flask/NestJS/Express) and DI registrations. On C#/.NET " +
        "projects it also includes EF Core entity mappings. Works for any indexed language.")]
    public static async Task<string> OnboardProject(
        [Description("Repository name (use ListRepositories to see available)")] string repository,
        IGraphStore graphStore = default!,
        IRepositoryStore repoStore = default!)
    {
        var sb = new StringBuilder();

        var repos = await repoStore.ListAsync();
        var matchedRepo = repos.FirstOrDefault(r =>
            r.Name.Contains(repository, StringComparison.OrdinalIgnoreCase) ||
            r.Path.Contains(repository, StringComparison.OrdinalIgnoreCase));

        if (matchedRepo is null)
            return $"Repository '{repository}' not found. Use ListRepositories to see available repos.";

        sb.AppendLine($"# {matchedRepo.Name} — Project Overview");
        sb.AppendLine($"Path: {matchedRepo.Path}");
        sb.AppendLine($"Last indexed: {matchedRepo.LastIndexed?.ToString("yyyy-MM-dd HH:mm") ?? "never"}");
        sb.AppendLine();

        // DI Registrations (grouped by module)
        var diRegs = await graphStore.QueryDiRegistrationsAsync();
        var projectDiRegs = diRegs.Where(r =>
            r.FilePath?.Contains(repository, StringComparison.OrdinalIgnoreCase) == true).ToList();

        if (projectDiRegs.Count > 0)
        {
            sb.AppendLine($"## DI Registrations ({projectDiRegs.Count})");
            var grouped = projectDiRegs.GroupBy(r => ExtractModule(r.FilePath));
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                sb.AppendLine($"  **[{group.Key}]**");
                foreach (var reg in group.Take(15))
                    sb.AppendLine($"    {reg.Fqn}");
                if (group.Count() > 15)
                    sb.AppendLine($"    ... and {group.Count() - 15} more");
            }
            sb.AppendLine();
        }

        // API Endpoints
        var endpoints = await graphStore.QueryApiEndpointsAsync();
        var projectEndpoints = endpoints.Where(r =>
            r.FilePath?.Contains(repository, StringComparison.OrdinalIgnoreCase) == true).ToList();

        if (projectEndpoints.Count > 0)
        {
            sb.AppendLine($"## API Endpoints ({projectEndpoints.Count})");
            foreach (var ep in projectEndpoints.Take(30))
                sb.AppendLine($"  {ep.Signature ?? ep.Fqn}");
            if (projectEndpoints.Count > 30)
                sb.AppendLine($"  ... and {projectEndpoints.Count - 30} more");
            sb.AppendLine();
        }

        // Entity Mappings
        var entities = await graphStore.QueryEntityMappingsAsync();
        if (entities.Count > 0)
        {
            var projectEntities = entities.Where(r =>
                r.FilePath?.Contains(repository, StringComparison.OrdinalIgnoreCase) == true).ToList();
            if (projectEntities.Count > 0)
            {
                sb.AppendLine($"## EF Core Entities ({projectEntities.Count})");
                foreach (var e in projectEntities)
                    sb.AppendLine($"  {e.Name} ({e.Fqn})");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string ExtractModule(string? filePath)
    {
        if (filePath is null) return "Unknown";
        var parts = filePath.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Contains('.') && !part.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return part;
        }
        return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(filePath)) ?? "Unknown";
    }
}
