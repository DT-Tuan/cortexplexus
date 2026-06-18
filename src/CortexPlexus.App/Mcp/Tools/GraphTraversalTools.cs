using System.ComponentModel;
using CortexPlexus.Core;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using ModelContextProtocol.Server;
using Npgsql;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class GraphTraversalTools
{
    // R21 Fix #2: params declared as nullable strings (default null) so the MCP
    // framework doesn't throw ArgumentException for missing-param cases. We
    // validate explicitly and return a friendly error string instead.
    private static string? RequireFqn(string? provided, string paramName, string alternative)
        => string.IsNullOrWhiteSpace(provided)
            ? $"Missing required parameter '{paramName}'. Pass the fully qualified name of the target symbol. {alternative}"
            : null;

    // R23 N3+N4+N6: shared filter for stdlib / framework / primitive symbols.
    // Used by tools that traverse the call graph and dependency graph (get_callees,
    // get_callers, get_dependencies, get_data_flow, get_impact_analysis) to suppress
    // noise from System.*, Microsoft.*, third-party SDKs, and CLR primitives.
    public static readonly string[] FrameworkPrefixes =
    {
        "System.",
        "Microsoft.",
        "StackExchange.",
        "Npgsql.",
        "Polly.",
        "MediatR.",
        "Serilog.",
        "Newtonsoft.",
        "Qdrant.",
    };

    // CLR primitive aliases that show up in dependency results as their literal name
    // (no namespace prefix). These are language keywords, not "dependencies" worth
    // listing in any analysis.
    public static readonly HashSet<string> ClrPrimitives = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "object", "void",
        "string?", "int?", "long?", "bool?", "double?", "decimal?",
        "DateTime", "Guid", "TimeSpan", "DateTimeOffset",
    };

    public static bool IsExternalOrPrimitive(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return false;
        if (ClrPrimitives.Contains(fqn)) return true;
        foreach (var prefix in FrameworkPrefixes)
        {
            if (fqn.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<SearchResult> StripFrameworkNoise(
        IReadOnlyList<SearchResult> results)
        => results.Where(r => !IsExternalOrPrimitive(r.Fqn)).ToList();

    /// <summary>
    /// R22 Fix #3: When a tool that expects a method FQN receives what looks like a
    /// class FQN (no parens, kind != method/constructor) and returns no results,
    /// build a "did you mean" hint listing actual methods of that class. Returns
    /// <c>null</c> if no methods are found (caller falls back to plain "no results").
    /// </summary>
    private static async Task<string?> BuildClassFqnHintAsync(
        IGraphStore graphStore, string fqn, string toolName)
    {
        // Heuristic: if the FQN already contains '(' it was a method signature.
        // Don't bother running the lookup.
        if (fqn.Contains('(')) return null;

        // Strategy 1: treat the FQN as a class and list its methods.
        // Common case: user passed `Namespace.Class` instead of a method FQN.
        var methods = await graphStore.LookupMethodsByContainingTypeAsync(fqn, limit: 10);
        var hintTarget = fqn;

        // R25 R24-5 fix: strategy 2 — if strategy 1 found nothing, the FQN might
        // be a typo'd method name like "Namespace.Class.AnalyzeAsync" (real:
        // DirectAsync). Walk up one segment and try the parent as a class FQN.
        // This catches the common AI-agent failure mode of guessing a wrong
        // method name on a real class.
        if (methods.Count == 0)
        {
            var lastDot = fqn.LastIndexOf('.');
            if (lastDot > 0)
            {
                var parentFqn = fqn[..lastDot];
                methods = await graphStore.LookupMethodsByContainingTypeAsync(parentFqn, limit: 10);
                hintTarget = parentFqn;
            }
        }

        if (methods.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"No results for '{fqn}'.");
        sb.AppendLine();
        if (hintTarget != fqn)
        {
            sb.AppendLine($"Hint: '{fqn}' may be a typo. {toolName} found no method by that name " +
                          $"on '{hintTarget.Split('.').LastOrDefault() ?? hintTarget}'.");
            sb.AppendLine($"Did you mean one of these methods?");
        }
        else
        {
            sb.AppendLine($"Hint: '{fqn}' looks like a class FQN. {toolName} expects a method FQN.");
            sb.AppendLine($"Try one of these methods of '{fqn.Split('.').LastOrDefault() ?? fqn}':");
        }
        foreach (var m in methods)
        {
            sb.AppendLine($"  {m.Fqn}");
        }
        if (methods.Count == 10)
            sb.AppendLine($"  ... (more methods exist; this is the first 10 alphabetically)");
        return sb.ToString();
    }

    [McpServerTool, Description("Find all methods that call a specified method")]
    public static async Task<string> GetCallers(
        [Description("Fully qualified method name (e.g. 'Namespace.Class.Method')")] string? methodFqn = null,
        [Description("Depth of call chain to traverse (1-5)")] int depth = 1,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(methodFqn, "methodFqn",
            "Example: GetCallers(methodFqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator.ProcessAsync')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 5);
        var results = await graphStore.QueryCallersAsync(methodFqn!, depth);
        if (results.Count > 0)
            return compressor.Compress(results);

        // R22 Fix #3: try to provide a hint if user passed a class FQN
        var hint = await BuildClassFqnHintAsync(graphStore, methodFqn!, "GetCallers");
        return hint ?? $"No callers found for '{methodFqn}'.";
    }

    [McpServerTool, Description("Find all methods called by a specified method")]
    public static async Task<string> GetCallees(
        [Description("Fully qualified method name (e.g. 'Namespace.Class.Method')")] string? methodFqn = null,
        [Description("Depth of call chain to traverse (1-5)")] int depth = 1,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(methodFqn, "methodFqn",
            "Example: GetCallees(methodFqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator.ProcessAsync')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 5);
        var rawResults = await graphStore.QueryCalleesAsync(methodFqn!, depth);
        // R23 N3+N4: drop System.*/Microsoft.*/CLR primitives — they dominate output
        // at depth>=2 and are never the answer to "what does this method call internally".
        var results = StripFrameworkNoise(rawResults);
        if (results.Count > 0)
            return compressor.Compress(results);

        // R22 Fix #3: same hint for callees
        var hint = await BuildClassFqnHintAsync(graphStore, methodFqn!, "GetCallees");
        return hint ?? $"No callees found for '{methodFqn}'.";
    }

    [McpServerTool, Description("Get dependencies of a class or method")]
    public static async Task<string> GetDependencies(
        [Description("Fully qualified name of class or method")] string? fqn = null,
        [Description("Depth of dependency chain (1-3)")] int depth = 1,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(fqn, "fqn",
            "Example: GetDependencies(fqn: 'CortexFlow.API.Controllers.ChatController')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 3);
        var rawResults = await graphStore.QueryDependenciesAsync(fqn!, depth);
        // R23 N3+N6: filter framework + CLR primitive types so user sees their own
        // domain dependencies, not 50 lines of System.Collections.Generic.List<T>.
        var results = StripFrameworkNoise(rawResults);
        return results.Count == 0
            ? $"No dependencies found for '{fqn}'."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Find all implementations of an interface")]
    public static async Task<string> GetImplementations(
        [Description("Fully qualified interface name (e.g. 'App.Interfaces.IFoo')")] string? interfaceFqn = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(interfaceFqn, "interfaceFqn",
            "Example: GetImplementations(interfaceFqn: 'CortexFlow.Core.Interfaces.IChatOrchestrator')");
        if (err is not null) return err;

        var results = await graphStore.QueryImplementationsAsync(interfaceFqn!);
        return results.Count == 0
            ? $"No implementations found for '{interfaceFqn}'."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Get the inheritance hierarchy of a class")]
    public static async Task<string> GetClassHierarchy(
        [Description("Fully qualified class name")] string? classFqn = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(classFqn, "classFqn",
            "Example: GetClassHierarchy(classFqn: 'CortexFlow.API.Controllers.ChatController')");
        if (err is not null) return err;

        var rawResults = await graphStore.QueryClassHierarchyAsync(classFqn!);

        // R23: use shared framework filter (was duplicated inline before).
        var results = StripFrameworkNoise(rawResults)
            .Where(r => !string.IsNullOrWhiteSpace(r.FilePath))
            .ToList();

        return results.Count == 0
            ? $"No hierarchy found for '{classFqn}'."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Analyze blast radius: find all code affected if a method or class changes. Pass includeMemories=true to also surface agent memories linked to this symbol (opt-in; requires Memory__Enabled=true).")]
    public static async Task<string> GetImpactAnalysis(
        [Description("Fully qualified name of method or class to analyze")] string? fqn = null,
        [Description("Depth of impact chain (1-5)")] int depth = 2,
        [Description("Include agent memories linked to this symbol (default false)")] bool includeMemories = false,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IAgentMemoryStore? memoryStore = null,
        Microsoft.Extensions.Options.IOptions<CortexPlexus.Memory.MemoryOptions>? memoryOptions = null)
    {
        var err = RequireFqn(fqn, "fqn",
            "Example: GetImpactAnalysis(fqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 5);

        var callers = await graphStore.QueryCallersAsync(fqn!, depth);
        var implementations = await graphStore.QueryImplementationsAsync(fqn!);
        var hierarchy = await graphStore.QueryClassHierarchyAsync(fqn!);

        // Also find code that References this type (property access, field usage)
        var referencedBy = await graphStore.QueryReferencedByAsync(fqn!, depth);

        var allAffected = callers
            .Concat(implementations)
            .Concat(hierarchy)
            .Concat(referencedBy)
            .GroupBy(r => r.Fqn)
            .Select(g => g.First())
            .ToList();

        // Memory linkage is a soft link — we fetch even if the impact list is empty,
        // because a saved note on a rarely-called method is still worth surfacing.
        IReadOnlyList<AgentMemoryResult> linkedMemories = Array.Empty<AgentMemoryResult>();
        if (includeMemories && memoryOptions?.Value.Enabled == true && memoryStore is not null)
        {
            try
            {
                linkedMemories = await memoryStore.RecallAsync(
                    queryEmbedding: null,
                    scope: null, scopeId: null, topic: null,
                    relatedFqn: fqn!,
                    limit: 10);
            }
            catch
            {
                // Memory is opt-in and best-effort: never let a memory-store glitch
                // break the core impact analysis response.
            }
        }

        if (allAffected.Count == 0 && linkedMemories.Count == 0)
            return $"No impact found for '{fqn}'. It may not be referenced by other code.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Impact analysis for: {fqn}");
        sb.AppendLine($"Total affected symbols: {allAffected.Count}");
        sb.AppendLine();

        if (callers.Count > 0)
        {
            sb.AppendLine($"--- Callers ({callers.Count}) ---");
            sb.AppendLine(compressor.Compress(callers));
        }
        if (referencedBy.Count > 0)
        {
            sb.AppendLine($"--- Referenced By ({referencedBy.Count}) ---");
            sb.AppendLine(compressor.Compress(referencedBy));
        }
        if (implementations.Count > 0)
        {
            sb.AppendLine($"--- Implementations ({implementations.Count}) ---");
            sb.AppendLine(compressor.Compress(implementations));
        }
        if (hierarchy.Count > 0)
        {
            sb.AppendLine($"--- Class Hierarchy ({hierarchy.Count}) ---");
            sb.AppendLine(compressor.Compress(hierarchy));
        }
        if (linkedMemories.Count > 0)
        {
            sb.AppendLine($"--- Linked Memories ({linkedMemories.Count}) ---");
            foreach (var m in linkedMemories)
            {
                var topicLabel = m.Memory.Topic is null ? "" : $"[{m.Memory.Topic}] ";
                sb.AppendLine($"  {topicLabel}{m.Memory.Content}");
            }
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Get test coverage for a method: which test methods exercise this production code. " +
        "Returns test methods linked via TestCovers edges (detected from [Fact], @Test, test_ prefix, etc.).")]
    public static async Task<string> GetTestCoverage(
        [Description("Fully qualified method name of the production code")] string? methodFqn = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(methodFqn, "methodFqn",
            "Example: GetTestCoverage(methodFqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator.ProcessAsync')");
        if (err is not null) return err;

        var results = await graphStore.QueryTestCoverageAsync(methodFqn!);
        if (results.Count > 0)
            return $"Tests covering '{methodFqn}':\n\n{compressor.Compress(results)}";

        // R22 Fix #3: hint when user passes a class FQN
        var hint = await BuildClassFqnHintAsync(graphStore, methodFqn!, "GetTestCoverage");
        return hint ?? $"No tests found covering '{methodFqn}'. The method may not have tests, or test detection may not apply to its language.";
    }

    [McpServerTool, Description(
        "Find potentially dead code: functions/methods with no callers — candidates for cleanup. " +
        "Works across languages: .NET public/internal methods, plus Python/TS/JS/Go/… top-level " +
        "functions and class methods (leading-underscore 'private' names are skipped as noise).")]
    public static async Task<string> GetDeadCode(
        [Description("Repository name to scan for dead code")] string repository,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        if (repoId is null)
            return $"Repository '{repository}' not found. Use ListRepositories to see available repos.";

        var results = await graphStore.QueryDeadCodeAsync(repoId.Value);
        if (results.Count == 0)
            return $"No dead code found in '{repository}'. All public/internal methods have callers.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Potentially dead code in '{repository}' ({results.Count} methods):");
        sb.AppendLine("These public/internal methods have no callers in the codebase:");
        sb.AppendLine();
        sb.AppendLine(compressor.Compress(results));
        sb.AppendLine();
        sb.AppendLine("Note: Entry points, event handlers, and reflection-called methods may appear as false positives.");
        return sb.ToString();
    }

    [McpServerTool, Description("List all indexed repositories with their names, last-indexed timestamp, and persistence health (symbol count + embedding coverage). Check the 'health' line before assuming a repo is queryable — a registered repo with 0 symbols or missing embeddings means the last indexing run did not fully commit.")]
    public static async Task<string> ListRepositories(
        IRepositoryStore repoStore = default!,
        NpgsqlDataSource? dataSource = null,
        IAgentMemoryStore? memoryStore = null,
        Microsoft.Extensions.Options.IOptions<CortexPlexus.Memory.MemoryOptions>? memoryOptions = null)
    {
        var repos = await repoStore.ListAsync();
        if (repos.Count == 0)
            return "No repositories indexed yet.";

        // DB health check: per-repo total / embeddable / actually-embedded symbol counts.
        // The label is computed against EMBEDDABLE kinds (class, method, interface,
        // struct, record, function, type, document, section) — not against the total —
        // because field / property / event / constructor / enum / parameter / namespace
        // are intentionally not embedded. Comparing to total made every healthy .NET
        // repo show "PARTIAL" (ADR 008, docs/HEALTH-METRICS.md).
        //
        // Skipped when dataSource is null — happens in unit tests that don't spin up
        // Postgres; real MCP invocation always gets one via DI.
        var healthByRepo = new Dictionary<Guid, (long Total, long Embeddable, long WithEmbedding)>();
        if (dataSource is not null)
        {
            await using var conn = await dataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $$"""
                SELECT repo_id,
                       COUNT(*) AS total,
                       COUNT(*) FILTER (WHERE kind IN ({{EmbeddableKinds.SqlInClause}})) AS embeddable,
                       COUNT(*) FILTER (WHERE embedding IS NOT NULL) AS with_embedding
                FROM code_symbols
                GROUP BY repo_id
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                healthByRepo[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            }
        }

        // R21 Fix #1: when the same project has been indexed multiple times (e.g.
        // once via /workspace server path, once via agent at _agent/<name>), we end
        // up with multiple rows for the same logical repo. Group by Name and keep
        // the most recently indexed row; mention stale duplicates as a footnote so
        // users notice and can clean them up manually.
        var groups = repos
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(r => r.LastIndexed ?? DateTimeOffset.MinValue).ToList();
                return new { Name = g.Key, Primary = ordered[0], Stale = ordered.Skip(1).ToList() };
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Indexed repositories:");
        sb.AppendLine();
        foreach (var g in groups)
        {
            var r = g.Primary;
            sb.AppendLine($"  Name: {r.Name}");
            sb.AppendLine($"  Path: {r.Path}");
            var timestamp = r.LastIndexed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never";
            var stalenessLabel = StalenessLabel.Format(r.LastIndexed, DateTimeOffset.UtcNow);
            var stalenessSuffix = stalenessLabel is null ? "" : $" {stalenessLabel}";
            sb.AppendLine($"  Last indexed: {timestamp}{stalenessSuffix}");

            if (dataSource is null)
            {
                // No DB probe available (e.g. unit test harness). Silently skip
                // the health line rather than print a misleading "UNKNOWN".
            }
            else if (healthByRepo.TryGetValue(r.Id, out var h))
            {
                sb.AppendLine($"  Health: {FormatHealthLabel(h.Total, h.Embeddable, h.WithEmbedding)}");
            }
            else
            {
                sb.AppendLine("  Health: UNKNOWN — no rows in code_symbols for this repo.");
            }

            if (g.Stale.Count > 0)
            {
                var stalePaths = string.Join(", ", g.Stale.Select(s => s.Path));
                sb.AppendLine($"  Note: {g.Stale.Count} stale duplicate(s) hidden: {stalePaths}");
            }
            sb.AppendLine();
        }

        // Memory feature state (ADR-013): off by default; on when enabled.
        // Rendered once per ListRepositories call so users / agents can see whether
        // Save/Recall/List/Forget memory tools will work.
        if (memoryOptions is not null)
        {
            if (memoryOptions.Value.Enabled && memoryStore is not null)
            {
                try
                {
                    var count = await memoryStore.CountAsync();
                    sb.AppendLine($"Memory: enabled ({count} items).");
                    sb.AppendLine();
                }
                catch
                {
                    sb.AppendLine("Memory: enabled (count unavailable).");
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("Memory: disabled. Enable via Memory__Enabled=true (see docs/MEMORY-SYSTEM.md).");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Tip: Use the 'repository' parameter in SearchCode/SemanticSearch/GetDiRegistrations/GetApiEndpoints to scope results to a specific project.");
        return sb.ToString();
    }

    /// <summary>
    /// Render the Health label for a repository given its three counts. Pure logic
    /// (no DB / no MCP plumbing) so unit tests can exercise every branch without
    /// spinning up Postgres. See ADR 008 / docs/HEALTH-METRICS.md for semantics.
    /// </summary>
    internal static string FormatHealthLabel(long total, long embeddable, long withEmbedding)
    {
        // Coverage compares ACTUAL embeddings to EMBEDDABLE kinds (not to total).
        // Comparing to total made every healthy .NET repo show "PARTIAL" because
        // field/property/event/constructor are intentionally not embedded.
        //
        // Percent is rendered with InvariantCulture so the output is deterministic
        // across deployments — some locales (e.g. vi-VN) render `P0` as "100 %"
        // with a non-breaking space, which drifts from snapshot tests and is just
        // ugly in JSON-parseable outputs. InvariantCulture always emits "100 %"
        // with a regular space — we then strip the space manually for a stable
        // "100%" form.
        var coverage = embeddable > 0 ? (double)withEmbedding / embeddable : 0;
        var coveragePct = coverage.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace("\u00A0", "").Replace(" ", "");  // normalize both non-breaking and regular space

        return (total, embeddable, withEmbedding) switch
        {
            (0, _, _) => "EMPTY — registered but no symbols persisted. Re-run indexing.",
            (_, 0, _) => $"OK — {total} symbols, no embeddable kinds (config-only or schema-only repo)",
            (_, _, 0) => $"DEGRADED — {total} symbols indexed, 0 with embeddings out of {embeddable} embeddable. Semantic search will fail for this repo; check server logs for vector-upsert warnings.",
            _ when coverage >= 0.9 => $"OK — {total} symbols, {withEmbedding} embeddings ({coveragePct} of {embeddable} embeddable kinds)",
            _ => $"PARTIAL — {withEmbedding}/{embeddable} ({coveragePct}) embeddable symbols embedded ({total} total). Some semantic hits will be missing — re-run indexing or call force_reindex."
        };
    }

    [McpServerTool, Description(
        "Force a full re-index of a previously indexed repository on the next indexing run. " +
        "Wipes the server-side file-hash cache for that repo so every file is treated as changed. " +
        "Does NOT delete symbols in place — fresh upserts overwrite by FQN. " +
        "Use when: the repo is stuck in PARTIAL / DEGRADED health, incremental indexing missed changes, " +
        "or you want a clean rebuild after a server-side fix. Run ActivateAgent or index_from_local afterwards.")]
    public static async Task<string> ForceReindex(
        [Description("Repository name as shown by ListRepositories. Must match exactly (case-insensitive).")] string name,
        IRepositoryStore repoStore = default!,
        NpgsqlDataSource? dataSource = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required. Call ListRepositories() to see valid names.";
        if (dataSource is null)
            return "Error: database connection is not available (server misconfigured).";

        var repos = await repoStore.ListAsync();
        var match = repos.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"No repository named '{name}'. Call ListRepositories() for valid names.";

        int deletedHashes;
        await using (var conn = await dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM file_hashes WHERE repo_id = @repoId";
            cmd.Parameters.AddWithValue("@repoId", match.Id);
            deletedHashes = await cmd.ExecuteNonQueryAsync();
        }

        return $"""
            Force-reindex armed for '{match.Name}':
              Hashes cleared: {deletedHashes}
              Path:           {match.Path}
              Next step:      call ActivateAgent (for .NET projects) or index_from_local (for server-side
                              TS/JS/Py/Md) to run the full re-index. Existing symbols are NOT deleted —
                              they will be overwritten by upsert on matching FQN, and stale entries
                              whose files no longer exist will remain until a maintenance sweep.
            """;
    }
}
