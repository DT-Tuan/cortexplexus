using System.Text;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CortexPlexus.Graph;

/// <summary>
/// IGraphStore implementation backed by Apache AGE (A Graph Extension) for PostgreSQL.
/// Uses Cypher queries via the AGE SQL bridge.
/// </summary>
public sealed class AgeGraphStore(NpgsqlDataSource dataSource, ILogger<AgeGraphStore> logger) : IGraphStore
{
    private const string GraphName = "code_graph";
    private const int BatchSize = 200;

    public async Task InitializeSchemaAsync(CancellationToken ct = default)
    {
        var assembly = typeof(AgeGraphStore).Assembly;
        var resourceName = "CortexPlexus.Graph.Schema.Migrations.sql";

        string sql;
        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            sql = await reader.ReadToEndAsync(ct);
        }
        else
        {
            // Fallback: read from disk relative to assembly location
            var assemblyDir = Path.GetDirectoryName(assembly.Location)!;
            var filePath = Path.Combine(assemblyDir, "Schema", "Migrations.sql");
            if (!File.Exists(filePath))
            {
                // Try source-relative path for development
                filePath = Path.Combine(AppContext.BaseDirectory, "Schema", "Migrations.sql");
            }
            sql = await File.ReadAllTextAsync(filePath, ct);
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertNodesAsync(IEnumerable<CodeSymbol> symbols, CancellationToken ct = default)
    {
        var symbolList = symbols as IList<CodeSymbol> ?? symbols.ToList();
        if (symbolList.Count == 0) return;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await SetAgePath(conn, ct);

        // Group by label (Cypher requires static label in MERGE).
        // Within each label group, batch via UNWIND for one round-trip per batch.
        var byLabel = symbolList
            .Where(s => !string.IsNullOrWhiteSpace(s.Fqn))
            .GroupBy(s => SanitizeLabel(s.Kind));

        var failCount = 0;
        foreach (var labelGroup in byLabel)
        {
            var label = labelGroup.Key;
            var nodesForLabel = labelGroup.ToList();

            foreach (var batch in Chunk(nodesForLabel, BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await UpsertNodesBatchAsync(conn, label, batch, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failCount += batch.Count;
                    logger.LogWarning(ex, "Failed to batch-upsert {Count} {Label} nodes", batch.Count, label);
                }
            }
        }

        if (failCount > 0)
            logger.LogWarning("Graph upsert: {Failed} of {Total} nodes failed", failCount, symbolList.Count);
    }

    private static async Task UpsertNodesBatchAsync(
        NpgsqlConnection conn, string label, IList<CodeSymbol> batch, CancellationToken ct)
    {
        // Build a single Cypher query:
        //   UNWIND [{fqn:'..', name:'..', ...}, ...] AS n
        //   MERGE (m:Label {fqn: n.fqn})
        //   SET m.name = n.name, m.file_path = n.file_path, ...
        var sb = new StringBuilder(batch.Count * 200);
        sb.Append("UNWIND [");

        for (var i = 0; i < batch.Count; i++)
        {
            var symbol = batch[i];
            if (i > 0) sb.Append(", ");

            var fqn = EscapeCypher(symbol.Fqn);
            var name = EscapeCypher(symbol.Name ?? "");
            var filePath = EscapeCypher(symbol.FilePath ?? "");
            var repoId = EscapeCypher(symbol.RepoId?.ToString() ?? "");
            var signature = symbol is MethodInfo mi ? EscapeCypher(mi.Signature ?? "") : "";

            sb.Append('{');
            sb.Append("fqn: '").Append(fqn).Append("', ");
            sb.Append("name: '").Append(name).Append("', ");
            sb.Append("file_path: '").Append(filePath).Append("', ");
            sb.Append("start_line: ").Append(symbol.StartLine ?? 0).Append(", ");
            sb.Append("end_line: ").Append(symbol.EndLine ?? 0).Append(", ");
            sb.Append("repo_id: '").Append(repoId).Append("', ");
            sb.Append("signature: '").Append(signature).Append('\'');
            sb.Append('}');
        }

        sb.Append("] AS n ");
        sb.Append("MERGE (m:").Append(label).Append(" {fqn: n.fqn}) ");
        sb.Append("SET m.name = n.name, ");
        sb.Append("m.file_path = n.file_path, ");
        sb.Append("m.start_line = n.start_line, ");
        sb.Append("m.end_line = n.end_line, ");
        sb.Append("m.repo_id = n.repo_id, ");
        sb.Append("m.signature = n.signature");

        await ExecuteCypher(conn, sb.ToString(), ct);
    }

    // ADR 009: threshold above which we switch from MERGE to delete+CREATE
    // for edge upsert. Same concept as VectorStore.BulkLoadThreshold for HNSW.
    //
    // MERGE does a sequential scan per edge on the label table (linear degradation);
    // delete+CREATE has higher constant cost (~7.8ms/edge vs MERGE's ~2.4ms start)
    // but stays flat as the graph grows.
    //
    // Measured on CortexFlow 19K edges (2026-04-15):
    //   MERGE 4 chunks:         91.6s (11.8→20.9→28.5→30.4, linear)
    //   delete+CREATE 4 chunks: 156.1s (39→39→38→39, flat)
    //   Break-even:             ~35K edges per single UpsertEdgesAsync call
    //
    // Set at 20K so the agent's chunked uploads (5K/chunk) always use MERGE
    // (acceptable linear growth at that scale), while the server-side
    // IndexingPipeline (which passes ALL edges in one call) triggers bulk-load
    // for large repos. Tune based on profiling.
    private const int EdgeBulkLoadThreshold = 20_000;

    public async Task UpsertEdgesAsync(IEnumerable<Relationship> relationships, CancellationToken ct = default)
    {
        var relList = relationships as IList<Relationship> ?? relationships.ToList();
        if (relList.Count == 0) return;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await SetAgePath(conn, ct);

        var useBulkLoad = relList.Count >= EdgeBulkLoadThreshold;
        if (useBulkLoad)
        {
            // Collect all source FQNs so we can delete their outgoing edges in
            // one Cypher pass, then CREATE fresh (no per-edge existence check).
            // This is the edge equivalent of VectorStore's HNSW drop+rebuild.
            var srcFqns = relList
                .Select(r => r.FromFqn)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .ToList();

            if (srcFqns.Count > 0)
            {
                var deleteSw = System.Diagnostics.Stopwatch.StartNew();
                await DeleteEdgesBySourceFqns(conn, srcFqns, ct);
                deleteSw.Stop();
                logger.LogInformation(
                    "Edge bulk-load: deleted outgoing edges for {Sources} source vertices in {Ms} ms",
                    srcFqns.Count, deleteSw.ElapsedMilliseconds);
            }
        }

        // Group by (edge type, sorted metadata keys) so each batch has a uniform SET clause.
        // Cypher requires static edge type in MERGE / CREATE, and UNWIND batches need the same
        // shape across all items (same set of properties).
        var grouped = relList
            .Where(r => !string.IsNullOrWhiteSpace(r.FromFqn) && !string.IsNullOrWhiteSpace(r.ToFqn))
            .GroupBy(r => new EdgeGroupKey(
                r.Type.ToString(),
                r.Metadata is { Count: > 0 }
                    ? string.Join(",", r.Metadata.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    : ""
            ));

        var failCount = 0;
        foreach (var group in grouped)
        {
            var edgeLabel = group.Key.Type;
            var metaKeys = string.IsNullOrEmpty(group.Key.KeysCsv)
                ? Array.Empty<string>()
                : group.Key.KeysCsv.Split(',');

            foreach (var batch in Chunk(group.ToList(), BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (useBulkLoad)
                        await CreateEdgesBatchAsync(conn, edgeLabel, metaKeys, batch, ct);
                    else
                        await UpsertEdgesBatchAsync(conn, edgeLabel, metaKeys, batch, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failCount += batch.Count;
                    logger.LogWarning(ex, "Failed to batch-upsert {Count} {Type} edges", batch.Count, edgeLabel);
                }
            }
        }

        if (failCount > 0)
            logger.LogWarning("Graph upsert: {Failed} of {Total} edges failed", failCount, relList.Count);
    }

    /// <summary>
    /// Delete all outgoing edges from the given source vertices. Used by the
    /// bulk-load path (ADR 009) to clear stale edges before CREATE-fresh.
    /// </summary>
    private async Task DeleteEdgesBySourceFqns(
        NpgsqlConnection conn,
        IReadOnlyList<string> srcFqns,
        CancellationToken ct)
    {
        // Chunk to avoid massive Cypher strings; 500 FQNs at a time.
        foreach (var chunk in Chunk((IList<string>)srcFqns, 500))
        {
            var sb = new System.Text.StringBuilder(chunk.Count * 80);
            sb.Append("MATCH (n)-[r]->() WHERE n.fqn IN [");
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('\'').Append(EscapeCypher(chunk[i])).Append('\'');
            }
            sb.Append("] DELETE r");
            await ExecuteCypher(conn, sb.ToString(), ct);
        }
    }

    /// <summary>
    /// CREATE edges (no existence check). Used by bulk-load after edges have
    /// been deleted. Uses MATCH on existing vertices instead of MERGE.
    /// </summary>
    private static async Task CreateEdgesBatchAsync(
        NpgsqlConnection conn,
        string edgeLabel,
        string[] metaKeys,
        IList<Relationship> batch,
        CancellationToken ct)
    {
        var sanitizedKeys = metaKeys.Select(SanitizeIdentifier).ToArray();
        var sb = new System.Text.StringBuilder(batch.Count * 200);
        sb.Append("UNWIND [");

        for (var i = 0; i < batch.Count; i++)
        {
            var rel = batch[i];
            if (i > 0) sb.Append(", ");
            sb.Append('{');
            sb.Append("src: '").Append(EscapeCypher(rel.FromFqn)).Append("', ");
            sb.Append("dst: '").Append(EscapeCypher(rel.ToFqn)).Append('\'');
            for (var k = 0; k < metaKeys.Length; k++)
            {
                var value = rel.Metadata is { } meta && meta.TryGetValue(metaKeys[k], out var v) ? v : "";
                sb.Append(", m_").Append(sanitizedKeys[k])
                  .Append(": '").Append(EscapeCypher(value)).Append('\'');
            }
            sb.Append('}');
        }

        sb.Append("] AS e ");
        // MATCH instead of MERGE — vertices were created in the node phase.
        // CREATE instead of MERGE — edges were deleted by DeleteEdgesBySourceFqns.
        sb.Append("MATCH (a {fqn: e.src}) ");
        sb.Append("MATCH (b {fqn: e.dst}) ");
        sb.Append("CREATE (a)-[r:").Append(edgeLabel).Append("]->(b) ");
        sb.Append("SET r.type = '").Append(edgeLabel).Append('\'');

        for (var k = 0; k < sanitizedKeys.Length; k++)
        {
            sb.Append(", r.").Append(sanitizedKeys[k])
              .Append(" = e.m_").Append(sanitizedKeys[k]);
        }

        await ExecuteCypher(conn, sb.ToString(), ct);
    }

    private static async Task UpsertEdgesBatchAsync(
        NpgsqlConnection conn,
        string edgeLabel,
        string[] metaKeys,
        IList<Relationship> batch,
        CancellationToken ct)
    {
        // Build a single Cypher query:
        //   UNWIND [{src:'..', dst:'..', m_key1:'..'}, ...] AS e
        //   MERGE (a {fqn: e.src})
        //   MERGE (b {fqn: e.dst})
        //   MERGE (a)-[r:EdgeType]->(b)
        //   SET r.type = 'EdgeType', r.key1 = e.m_key1, ...
        //
        // Metadata keys are prefixed with "m_" in the UNWIND map to avoid
        // collision with the structural "src"/"dst" keys.
        var sanitizedKeys = metaKeys.Select(SanitizeIdentifier).ToArray();

        var sb = new StringBuilder(batch.Count * 200);
        sb.Append("UNWIND [");

        for (var i = 0; i < batch.Count; i++)
        {
            var rel = batch[i];
            if (i > 0) sb.Append(", ");

            sb.Append('{');
            sb.Append("src: '").Append(EscapeCypher(rel.FromFqn)).Append("', ");
            sb.Append("dst: '").Append(EscapeCypher(rel.ToFqn)).Append('\'');

            for (var k = 0; k < metaKeys.Length; k++)
            {
                var value = rel.Metadata is { } meta && meta.TryGetValue(metaKeys[k], out var v) ? v : "";
                sb.Append(", m_").Append(sanitizedKeys[k])
                  .Append(": '").Append(EscapeCypher(value)).Append('\'');
            }

            sb.Append('}');
        }

        sb.Append("] AS e ");
        sb.Append("MERGE (a {fqn: e.src}) ");
        sb.Append("MERGE (b {fqn: e.dst}) ");
        sb.Append("MERGE (a)-[r:").Append(edgeLabel).Append("]->(b) ");
        sb.Append("SET r.type = '").Append(edgeLabel).Append('\'');

        for (var k = 0; k < sanitizedKeys.Length; k++)
        {
            sb.Append(", r.").Append(sanitizedKeys[k])
              .Append(" = e.m_").Append(sanitizedKeys[k]);
        }

        await ExecuteCypher(conn, sb.ToString(), ct);
    }

    private readonly record struct EdgeGroupKey(string Type, string KeysCsv);

    public async Task<IReadOnlyList<SearchResult>> QueryCallersAsync(
        string methodFqn, int depth = 1, CancellationToken ct = default)
    {
        var fqn = EscapeCypher(methodFqn);
        // R21 Fix #7: Previous implementation used CONTAINS which matched *any* FQN
        // containing the query as a substring. This caused impact_analysis on a class
        // like "ChatController" to return its own methods as "callers" because
        // "ChatController.Completion".Contains("ChatController") is true.
        //
        // Fix: anchor the match to the END of the FQN (or immediately before "("
        // for method signatures), so "ChatController" only matches the class node
        // and "ChatController.Completion" only matches that exact method.
        // AGE doesn't have ENDS WITH in older versions; build a regex-safe predicate
        // using CONTAINS with disambiguators.
        var directCypher =
            $"MATCH (caller)-[:Calls*1..{depth}]->(target)" +
            $" WHERE target.fqn = '{fqn}'" +
            $"    OR target.fqn STARTS WITH '{fqn}('" +
            " RETURN caller.fqn, caller.name, caller.file_path, caller.start_line, caller.signature";

        var directCallers = await ExecuteCypherQuery(directCypher, "Graph:Callers", ct);

        // Issue #4 fix: Also find callers via interface methods.
        // CallGraphExtractor resolves invocations through SemanticModel.GetSymbolInfo() —
        // when caller uses `_orchestrator.ProcessAsync()` where _orchestrator is typed as
        // `IChatOrchestrator`, the resolved symbol is `IChatOrchestrator.ProcessAsync`,
        // not `ChatOrchestrator.ProcessAsync`. Result: querying concrete method returns 0
        // callers even though calls exist through the interface (a very common DI pattern).
        //
        // Strategy: extract method name from target FQN, then find callers of any method
        // with same name on a type that implements the target's containing type.
        // Cypher steps:
        //   1. Match concrete method node to find its containing type
        //   2. Find interfaces that containing type Implements
        //   3. Match interface methods with same name
        //   4. Find callers of those interface methods
        var methodName = ExtractMethodNameFromFqn(methodFqn);
        if (string.IsNullOrEmpty(methodName))
            return directCallers;

        // Try to find the containing type by stripping the trailing ".MethodName(args)" part.
        var containingTypeFqn = ExtractContainingTypeFqn(methodFqn);
        if (string.IsNullOrEmpty(containingTypeFqn))
            return directCallers;

        var escapedType = EscapeCypher(containingTypeFqn);
        var escapedMethodName = EscapeCypher(methodName);

        // Find interface methods that have callers, where the interface is implemented by
        // the target's containing type, and the method name matches.
        var interfaceCypher =
            "MATCH (concrete)-[:Implements]->(iface) " +
            $"WHERE concrete.fqn CONTAINS '{escapedType}' " +
            "MATCH (caller)-[:Calls]->(ifaceMethod) " +
            "WHERE ifaceMethod.fqn CONTAINS iface.fqn " +
            $"  AND ifaceMethod.name = '{escapedMethodName}' " +
            "  AND NOT caller.fqn CONTAINS iface.fqn " +
            "RETURN caller.fqn, caller.name, caller.file_path, caller.start_line, caller.signature";

        try
        {
            var interfaceCallers = await ExecuteCypherQuery(interfaceCypher, "Graph:Callers:Interface", ct);
            if (interfaceCallers.Count == 0)
                return directCallers;

            // Merge + dedupe by FQN. Direct callers take precedence (preserve order).
            var seen = new HashSet<string>(directCallers.Select(c => c.Fqn));
            var merged = new List<SearchResult>(directCallers);
            foreach (var caller in interfaceCallers)
            {
                if (seen.Add(caller.Fqn))
                    merged.Add(caller);
            }
            return merged;
        }
        catch
        {
            // If interface lookup fails (rare AGE edge case), don't crash the whole call.
            // User still gets direct callers — better than nothing.
            return directCallers;
        }
    }

    /// <summary>
    /// Extract method name từ FQN. Examples:
    /// - "App.Service.ProcessAsync()" → "ProcessAsync"
    /// - "App.Service.ProcessAsync" → "ProcessAsync"
    /// - "App.Service.ProcessAsync(int, string)" → "ProcessAsync"
    /// </summary>
    internal static string ExtractMethodNameFromFqn(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return "";

        // Strip parameter list nếu có
        var parenIdx = fqn.IndexOf('(');
        var withoutParens = parenIdx > 0 ? fqn[..parenIdx] : fqn;

        // Last segment after final dot
        var lastDot = withoutParens.LastIndexOf('.');
        return lastDot > 0 ? withoutParens[(lastDot + 1)..] : withoutParens;
    }

    /// <summary>
    /// Extract containing type FQN from method FQN.
    /// "App.Service.ProcessAsync(int)" → "App.Service"
    /// </summary>
    internal static string ExtractContainingTypeFqn(string methodFqn)
    {
        if (string.IsNullOrEmpty(methodFqn)) return "";

        var parenIdx = methodFqn.IndexOf('(');
        var withoutParens = parenIdx > 0 ? methodFqn[..parenIdx] : methodFqn;

        var lastDot = withoutParens.LastIndexOf('.');
        return lastDot > 0 ? withoutParens[..lastDot] : "";
    }

    public async Task<IReadOnlyList<SearchResult>> QueryTestCoverageAsync(
        string methodFqn, CancellationToken ct = default)
    {
        var fqn = EscapeCypher(methodFqn);
        // R21 Fix #7: anchored match. Querying "ChatOrchestrator" (class) should
        // return tests covering the class OR any of its methods; querying
        // "ChatOrchestrator.ProcessAsync" should return only tests for that method.
        var cypher =
            $"MATCH (test)-[:TestCovers]->(target)" +
            $" WHERE target.fqn = '{fqn}'" +
            $"    OR target.fqn STARTS WITH '{fqn}.'" +
            $"    OR target.fqn STARTS WITH '{fqn}('" +
            " RETURN test.fqn, test.name, test.file_path, test.start_line, test.signature";

        return await ExecuteCypherQuery(cypher, "Graph:TestCoverage", ct);
    }

    public async Task<IReadOnlyList<SearchResult>> QueryCalleesAsync(
        string methodFqn, int depth = 1, CancellationToken ct = default)
    {
        var fqn = EscapeCypher(methodFqn);
        // R21 Fix #7: use anchored match (exact OR starts-with-"(") instead of
        // unbounded CONTAINS so querying a class FQN doesn't silently return its
        // methods as if they were the query target. See QueryCallersAsync for
        // the same rationale.
        var cypher =
            $"MATCH (source)-[:Calls*1..{depth}]->(callee)" +
            $" WHERE source.fqn = '{fqn}'" +
            $"    OR source.fqn STARTS WITH '{fqn}('" +
            " RETURN callee.fqn, callee.name, callee.file_path, callee.start_line, callee.signature";

        return await ExecuteCypherQuery(cypher, "Graph:Callees", ct);
    }

    public async Task<IReadOnlyList<SearchResult>> QueryDependenciesAsync(
        string fqn, int depth = 1, CancellationToken ct = default)
    {
        var escaped = EscapeCypher(fqn);

        // AGE does not support pipe (|) in relationship type patterns.
        // Query each relationship type separately and merge results.
        var relTypes = new[] { "DependsOn", "UsesType", "References" };
        var allResults = new List<SearchResult>();
        var seen = new HashSet<string>();

        foreach (var relType in relTypes)
        {
            // R21 Fix #7: anchored match on source FQN
            var cypher =
                $"MATCH (source)-[:{relType}*1..{depth}]->(dep)" +
                $" WHERE source.fqn = '{escaped}'" +
                $"    OR source.fqn STARTS WITH '{escaped}('" +
                " RETURN dep.fqn, dep.name, dep.file_path, dep.start_line, dep.signature";

            var results = await ExecuteCypherQuery(cypher, "Graph:Dependencies", ct);
            foreach (var r in results)
            {
                if (seen.Add(r.Fqn))
                    allResults.Add(r);
            }
        }

        return allResults;
    }

    public async Task<IReadOnlyList<SearchResult>> QueryImplementationsAsync(
        string interfaceFqn, CancellationToken ct = default)
    {
        var fqn = EscapeCypher(interfaceFqn);
        // R21 Fix #7: anchored match so querying "IFoo" doesn't accidentally also
        // match "IFooBar". Interfaces typically have no parens in their FQN, so the
        // second clause is defensive for generic interface signatures.
        var cypher =
            "MATCH (impl)-[:Implements]->(iface)" +
            $" WHERE iface.fqn = '{fqn}'" +
            $"    OR iface.fqn STARTS WITH '{fqn}<'" +
            " RETURN impl.fqn, impl.name, impl.file_path, impl.start_line, impl.signature";

        return await ExecuteCypherQuery(cypher, "Graph:Implementations", ct);
    }

    public async Task<IReadOnlyList<SearchResult>> QueryClassHierarchyAsync(
        string classFqn, CancellationToken ct = default)
    {
        // R21 Fix #5: previous implementation used `CONTAINS` on the anchor FQN which
        // matched every sibling class whose FQN shared the substring (e.g. querying
        // "ChatController" returned all *Controller classes). Switch to EXACT FQN match
        // and **directional** traversal: bidirectional pattern would walk up to
        // ControllerBase then back down to all sibling controllers, which is also wrong.
        //
        // We run 4 separate queries:
        //   1. (node)-[:Inherits*1..10]->(ancestor)   — base classes
        //   2. (descendant)-[:Inherits*1..10]->(node) — derived classes
        //   3. (node)-[:Implements*1..10]->(iface)    — interfaces this class implements
        //   4. (impl)-[:Implements*1..10]->(node)     — only relevant if `node` is itself an interface
        //
        // The anchor node is included separately in the result list for completeness.
        var fqn = EscapeCypher(classFqn);

        var queries = new[]
        {
            "MATCH (node {fqn: '" + fqn + "'})-[:Inherits*1..10]->(related)" +
            " RETURN related.fqn, related.name, related.file_path, related.start_line, related.signature",

            "MATCH (related)-[:Inherits*1..10]->(node {fqn: '" + fqn + "'})" +
            " RETURN related.fqn, related.name, related.file_path, related.start_line, related.signature",

            "MATCH (node {fqn: '" + fqn + "'})-[:Implements*1..10]->(related)" +
            " RETURN related.fqn, related.name, related.file_path, related.start_line, related.signature",

            "MATCH (related)-[:Implements*1..10]->(node {fqn: '" + fqn + "'})" +
            " RETURN related.fqn, related.name, related.file_path, related.start_line, related.signature",

            // The anchor node itself
            "MATCH (node {fqn: '" + fqn + "'})" +
            " RETURN node.fqn, node.name, node.file_path, node.start_line, node.signature",
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<SearchResult>();
        foreach (var cypher in queries)
        {
            var results = await ExecuteCypherQuery(cypher, "Graph:Hierarchy", ct);
            foreach (var r in results)
            {
                if (!string.IsNullOrEmpty(r.Fqn) && seen.Add(r.Fqn))
                    merged.Add(r);
            }
        }

        return merged;
    }

    public async Task<IReadOnlyList<SearchResult>> QueryReferencedByAsync(
        string fqn, int depth = 1, CancellationToken ct = default)
    {
        var escaped = EscapeCypher(fqn);
        // R22: anchored match to avoid prefix-confusion (same fix as R21 #7).
        var cypher =
            $"MATCH (referrer)-[:References*1..{depth}]->(target)" +
            $" WHERE target.fqn = '{escaped}'" +
            $"    OR target.fqn STARTS WITH '{escaped}('" +
            " RETURN referrer.fqn, referrer.name, referrer.file_path, referrer.start_line, referrer.signature";

        return await ExecuteCypherQuery(cypher, "Graph:ReferencedBy", ct);
    }

    /// <summary>
    /// R22 Fix #3: Look up methods that belong to a class FQN. Used by GetCallers/
    /// GetCallees to provide a "did you mean" hint when the user passes a class FQN
    /// instead of a method FQN. Uses the relational <c>code_symbols</c> table because
    /// it's authoritative for both <c>kind</c> and FQN listing, and is indexed on
    /// <c>fqn</c> for fast LIKE prefix scans.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> LookupMethodsByContainingTypeAsync(
        string classFqn, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(classFqn)) return [];

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        const string sql = """
            SELECT fqn, name, kind, signature, file_path, start_line
            FROM public.code_symbols
            WHERE kind IN ('method', 'constructor')
              AND fqn LIKE @prefix
            ORDER BY name
            LIMIT @limit
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        // The FQN of a method always starts with "{classFqn}." — append a dot to anchor.
        // Escape SQL LIKE wildcards to avoid false matches when the class name contains
        // % or _ (rare but possible in generic types like List`1).
        var escapedPrefix = classFqn.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("@prefix", $"{escapedPrefix}.%");
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SearchResult(
                Fqn: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
                FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Score: 1.0,
                Source: "Lookup:MethodsOfClass"
            ));
        }

        return results;
    }

    public async Task DeleteByRepoAsync(Guid repoId, CancellationToken ct = default)
    {
        var repo = EscapeCypher(repoId.ToString());
        var cypher = "MATCH (n " + "{repo_id: '" + repo + "'}) DETACH DELETE n";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await SetAgePath(conn, ct);
        await ExecuteCypher(conn, cypher, ct);
    }

    // --- Phase 3: .NET Deep Analysis queries ---

    public async Task<IReadOnlyList<SearchResult>> QueryDiRegistrationsAsync(
        string? serviceTypeFqn = null, Guid? repoId = null, CancellationToken ct = default)
    {
        var preds = new List<string>();
        if (repoId is { } rid) preds.Add($"reg.repo_id = '{rid}'");
        if (serviceTypeFqn is not null) preds.Add($"reg.fqn CONTAINS '{EscapeCypher(serviceTypeFqn)}'");
        var cypher = "MATCH (reg:di_registration)" + WhereClause(preds) +
            " RETURN reg.fqn, reg.name, reg.file_path, reg.start_line, reg.signature";

        return await ExecuteCypherQuery(cypher, "Graph:DI", ct);
    }

    // Joins zero or more Cypher predicates into a " WHERE a AND b" suffix (empty if none).
    // Used to add optional repo_id scoping alongside the existing name/fqn filters.
    private static string WhereClause(IReadOnlyList<string> predicates) =>
        predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);

    public async Task<IReadOnlyList<SearchResult>> QueryEntityMappingsAsync(
        string? entityName = null, Guid? repoId = null, CancellationToken ct = default)
    {
        // Query entities via MapsTo (DbSet properties) OR Configures (IEntityTypeConfiguration)
        // AGE doesn't support | in relationship types, so run both and merge
        var allResults = new List<SearchResult>();
        var seen = new HashSet<string>();

        foreach (var relType in new[] { "MapsTo", "Configures" })
        {
            var preds = new List<string>();
            if (repoId is { } rid) preds.Add($"entity.repo_id = '{rid}'");
            if (entityName is not null) preds.Add($"entity.name CONTAINS '{EscapeCypher(entityName)}'");
            var cypher = $"MATCH ()-[:{relType}]->(entity)" + WhereClause(preds) +
                " RETURN entity.fqn, entity.name, entity.file_path, entity.start_line, entity.signature";

            var results = await ExecuteCypherQuery(cypher, "Graph:EntityMapping", ct);
            foreach (var r in results)
            {
                if (seen.Add(r.Fqn))
                    allResults.Add(r);
            }
        }

        return allResults;
    }

    public async Task<IReadOnlyList<SearchResult>> QueryApiEndpointsAsync(
        string? moduleName = null, Guid? repoId = null, CancellationToken ct = default)
    {
        // Issue #A (R16): moduleName filter must match against file_path (or signature),
        // NOT fqn. FQN của API endpoint là "API:POST:api/Chat/completion" — KHÔNG chứa
        // "ChatController" → old `fqn CONTAINS 'ChatController'` returns 0 results.
        //
        // Use relational table (code_symbols) for filtering when moduleName specified —
        // supports FilePath LIKE '%ChatController%', which naturally matches endpoints
        // defined in ChatController.cs.
        //
        // No filter → still use graph (backward compatible).
        if (moduleName is null)
        {
            var preds = new List<string>();
            if (repoId is { } rid) preds.Add($"ep.repo_id = '{rid}'");
            var cypher =
                "MATCH (ep:api_endpoint)" + WhereClause(preds) +
                " RETURN ep.fqn, ep.name, ep.file_path, ep.start_line, ep.signature";
            return await ExecuteCypherQuery(cypher, "Graph:Endpoints", ct);
        }

        // With filter — use relational table for reliable FilePath matching.
        // R23 N2 fix: previous filter `name ILIKE '%Chat%'` matched routes like
        // `api/cea/chat` from CeaController because the route name contained "chat"
        // as a substring. Now match against the **controller class** in the file path
        // with a path-segment boundary using POSITION (LIKE+backslash escaping is
        // a minefield in PG — backslash is the default escape char so '%\Auth\%'
        // gets misparsed). POSITION returns 0 if not found, >0 if found.
        //
        // We require a path separator before the module name so "Chat" matches
        // \ChatController.cs and /ChatController.cs but NOT \MyChatController.cs.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var repoSql = repoId is not null ? " AND repo_id = @repoId" : "";
        cmd.CommandText = $"""
            SELECT fqn, name, kind, signature, file_path, start_line
            FROM public.code_symbols
            WHERE kind = 'api_endpoint'{repoSql}
              AND (
                   POSITION(LOWER(@controllerWin)  IN LOWER(file_path)) > 0
                OR POSITION(LOWER(@controllerUnix) IN LOWER(file_path)) > 0
                OR POSITION(LOWER(@folderWin)      IN LOWER(file_path)) > 0
                OR POSITION(LOWER(@folderUnix)     IN LOWER(file_path)) > 0
              )
            ORDER BY name
            LIMIT 500
            """;
        if (repoId is { } endpointRepoId) cmd.Parameters.AddWithValue("repoId", endpointRepoId);
        cmd.Parameters.AddWithValue("controllerWin",  $"\\{moduleName}Controller.cs");
        cmd.Parameters.AddWithValue("controllerUnix", $"/{moduleName}Controller.cs");
        cmd.Parameters.AddWithValue("folderWin",      $"\\{moduleName}\\");
        cmd.Parameters.AddWithValue("folderUnix",     $"/{moduleName}/");

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SearchResult(
                Fqn: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
                FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Score: 1.0,
                Source: "Graph:Endpoints"
            ));
        }
        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> QueryDataFlowAsync(
        string endpointRoute, CancellationToken ct = default)
    {
        // Issue #C (R16): Normalize route input
        // - Strip leading "/" — user often types "/api/chat/completion" but store is "api/Chat/completion"
        // - Case-insensitive match — user types "chat" but store has "Chat"
        //
        // Strategy: resolve endpoint FQN via relational table (ILIKE), THEN do graph traversal
        // for handlers + downstream calls. This gives us the best of both worlds:
        // robust matching + graph power for call chain.
        var normalizedRoute = NormalizeRoute(endpointRoute);

        // Step 1: Find endpoint FQN via relational table (case-insensitive).
        // Match against fqn (e.g., "API:POST:api/Chat/completion") with the user's query
        // as a fragment anywhere in the FQN.
        List<string> endpointFqns = [];
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var endpointCmd = conn.CreateCommand())
        {
            endpointCmd.CommandText = """
                SELECT fqn
                FROM public.code_symbols
                WHERE kind = 'api_endpoint'
                  AND fqn ILIKE '%' || @route || '%'
                ORDER BY fqn
                LIMIT 20
                """;
            endpointCmd.Parameters.AddWithValue("route", normalizedRoute);
            await using var reader = await endpointCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                endpointFqns.Add(reader.GetString(0));
        }

        if (endpointFqns.Count == 0)
            return Array.Empty<SearchResult>();

        // Step 2: For each matched endpoint FQN, find handlers via graph.
        // Use exact {fqn: 'X'} match (fast) instead of CONTAINS.
        var allHandlers = new List<SearchResult>();
        var seen = new HashSet<string>();
        foreach (var epFqn in endpointFqns)
        {
            var escaped = EscapeCypher(epFqn);
            var handlerCypher =
                "MATCH (ep {fqn: '" + escaped + "'})-[:HandledBy]->(handler)" +
                " RETURN handler.fqn, handler.name, handler.file_path, handler.start_line, handler.signature";

            var handlers = await ExecuteCypherQuery(handlerCypher, "Graph:DataFlow", ct);
            foreach (var h in handlers)
            {
                if (seen.Add(h.Fqn))
                    allHandlers.Add(h);
            }
        }

        if (allHandlers.Count == 0)
            return allHandlers;

        // Step 3: For each handler, find downstream calls (depth up to 3)
        var allResults = new List<SearchResult>(allHandlers);
        var downstreamSeen = new HashSet<string>(allHandlers.Select(h => h.Fqn));

        foreach (var handler in allHandlers)
        {
            var handlerFqn = EscapeCypher(handler.Fqn);
            var downstreamCypher =
                "MATCH (h " + "{fqn: '" + handlerFqn + "'})" + "-[:Calls*1..3]->(downstream)" +
                " RETURN downstream.fqn, downstream.name, downstream.file_path, downstream.start_line, downstream.signature";

            var downstream = await ExecuteCypherQuery(downstreamCypher, "Graph:DataFlow", ct);
            foreach (var r in downstream)
            {
                if (downstreamSeen.Add(r.Fqn))
                    allResults.Add(r);
            }
        }

        return allResults;
    }

    /// <summary>
    /// Normalize route input for endpoint lookup.
    /// User input có thể là: "/api/chat/completion" (leading /), "api/Chat/completion",
    /// "/API/CHAT/COMPLETION"... Server stored format: "api/Chat/completion" (no leading /,
    /// original casing). Normalize: strip leading slash + trim.
    /// Casing handled by ILIKE in SQL query — no lowercasing here.
    /// </summary>
    internal static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "";
        var trimmed = route.Trim();
        // Strip leading slash(es)
        while (trimmed.StartsWith('/'))
            trimmed = trimmed[1..];
        return trimmed;
    }

    // --- P1b: Configuration Mapping ---

    public async Task<IReadOnlyList<SearchResult>> QueryConfigUsageAsync(
        string? configKey = null, Guid? repoId = null, CancellationToken ct = default)
    {
        // Query both directions:
        // 1. "Who reads this config key?" — MATCH (reader)-[:ReadsConfig]->(config) WHERE config.fqn CONTAINS key
        // 2. "What config keys exist?" — MATCH ()-[:ReadsConfig]->(config) RETURN config (when key is null)
        // Also return the readers so AI agents know which code depends on a config key.
        var allResults = new List<SearchResult>();
        var seen = new HashSet<string>();

        // Query 1: Config key nodes (config_key symbols)
        var ckPreds = new List<string>();
        if (repoId is { } cfgRid) ckPreds.Add($"ck.repo_id = '{cfgRid}'");
        if (configKey is not null) ckPreds.Add($"ck.fqn CONTAINS '{EscapeCypher(configKey)}'");
        var configCypher = "MATCH (ck:config_key)" + WhereClause(ckPreds) +
            " RETURN ck.fqn, ck.name, ck.file_path, ck.start_line, ck.signature";

        var configResults = await ExecuteCypherQuery(configCypher, "Graph:Config", ct);
        foreach (var r in configResults)
        {
            if (seen.Add(r.Fqn))
                allResults.Add(r);
        }

        // Query 2: Code symbols that read the config key via ReadsConfig edges
        if (configKey is not null)
        {
            var readerPreds = new List<string> { $"target.fqn CONTAINS '{EscapeCypher(configKey)}'" };
            if (repoId is { } readerRid) readerPreds.Add($"reader.repo_id = '{readerRid}'");
            var readerCypher =
                "MATCH (reader)-[:ReadsConfig]->(target)" + WhereClause(readerPreds) +
                " RETURN reader.fqn, reader.name, reader.file_path, reader.start_line, reader.signature";

            var readerResults = await ExecuteCypherQuery(readerCypher, "Graph:ConfigReader", ct);
            foreach (var r in readerResults)
            {
                if (seen.Add(r.Fqn))
                    allResults.Add(r);
            }
        }

        return allResults;
    }

    // --- P2d: Dead Code Detection ---

    public async Task<IReadOnlyList<SearchResult>> QueryDeadCodeAsync(
        Guid repoId, CancellationToken ct = default)
    {
        // Find public/internal methods that have NO incoming Calls, TestCovers, HandledBy, or Subscribes edges.
        // Uses relational table for listing methods + AGE graph for checking incoming edges.
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Step 1: Get all public/internal methods for this repo, EXCLUDING test methods.
        // R23 N1 fix: test methods (xUnit [Fact]/[Theory], NUnit [Test], MSTest [TestMethod])
        // are invoked by the test runner via reflection — not by other C# code — so they
        // have no incoming Calls edges. Before this fix every test method showed up as
        // dead code on CortexFlow. Same root pattern as R21 #6 (HTTP endpoints).
        var methodsSql = """
            SELECT fqn, name, kind, signature, file_path, start_line
            FROM public.code_symbols
            WHERE repo_id = @repoId
              AND kind = 'method'
              AND (accessibility = 'public' OR accessibility = 'internal')
              AND COALESCE(is_test_method, FALSE) = FALSE
            ORDER BY name
            LIMIT 500
            """;

        await using var methodsCmd = conn.CreateCommand();
        methodsCmd.CommandText = methodsSql;
        methodsCmd.Parameters.AddWithValue("repoId", repoId);

        var candidates = new List<SearchResult>();
        await using var reader = await methodsCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(new SearchResult(
                Fqn: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
                FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Score: 0.0,
                Source: "Graph:DeadCode"
            ));
        }
        await reader.CloseAsync();

        if (candidates.Count == 0)
            return candidates;

        // Step 2: Pre-load FQNs that have any form of "usage" so we can skip them.
        // R21 Fix #6: HTTP endpoint methods have no incoming Calls edges (they're
        // invoked by the ASP.NET runtime, not by other C# code) so they showed up as
        // dead code. Exclude them by checking HandledBy edges from api_endpoint nodes.
        // Also exclude methods covered by tests (TestCovers) and event subscribers
        // (Subscribes edges) which are likewise reached without direct C# calls.
        await SetAgePath(conn, ct);

        var usedFqns = new HashSet<string>();

        // Methods with incoming Calls edges
        await CollectIncomingEdgeTargetsAsync(conn, "Calls", usedFqns, ct);
        // Methods hooked to HTTP endpoints via HandledBy: (api_endpoint)-[:HandledBy]->(method)
        await CollectIncomingEdgeTargetsAsync(conn, "HandledBy", usedFqns, ct);
        // Methods referenced as event subscribers
        await CollectIncomingEdgeTargetsAsync(conn, "Subscribes", usedFqns, ct);
        // Methods covered by tests — definitely not dead
        await CollectIncomingEdgeTargetsAsync(conn, "TestCovers", usedFqns, ct);

        var deadCode = new List<SearchResult>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Skip constructors, Main, Program entry points
            if (candidate.Name is "Main" or ".ctor" || candidate.Fqn.Contains(".ctor"))
                continue;

            // Skip anything reached via any form of "usage" edge
            if (usedFqns.Contains(candidate.Fqn))
                continue;

            deadCode.Add(candidate);
        }

        return deadCode;
    }

    /// <summary>
    /// R21 Fix #6: Collect all target FQNs that have incoming edges of the given
    /// relationship type. Uses a single Cypher query per edge type (AGE does not
    /// support `|` in relationship patterns) and aggregates into a shared hash set.
    /// </summary>
    private async Task CollectIncomingEdgeTargetsAsync(
        NpgsqlConnection conn,
        string relationshipType,
        HashSet<string> target,
        CancellationToken ct)
    {
        var cypher = $"MATCH ()-[:{relationshipType}]->(t) RETURN DISTINCT t.fqn";
        var sql = $"""
            SELECT t_fqn::text
            FROM cypher('{GraphName}', $$
            {cypher}
            $$) AS (t_fqn agtype);
            """;

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0)) continue;
                var fqn = UnquoteAgtype(reader.GetString(0));
                if (!string.IsNullOrEmpty(fqn))
                    target.Add(fqn);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to collect incoming {Edge} edge targets — treating as empty", relationshipType);
        }
    }

    // --- P4c: Circular Dependency Detection ---

    public async Task<IReadOnlyList<IReadOnlyList<string>>> QueryCircularDependenciesAsync(
        Guid repoId, CancellationToken ct = default)
    {
        // Find circular dependencies by checking for DependsOn cycles.
        // For each class, check if there's a path back to itself via DependsOn edges.
        // Uses relational table for listing classes + AGE graph for cycle detection.
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Step 1: Get all classes for this repo
        var classesSql = """
            SELECT fqn FROM public.code_symbols
            WHERE repo_id = @repoId AND kind IN ('class', 'struct', 'record')
            ORDER BY fqn LIMIT 200
            """;

        await using var classesCmd = conn.CreateCommand();
        classesCmd.CommandText = classesSql;
        classesCmd.Parameters.AddWithValue("repoId", repoId);

        var classFqns = new List<string>();
        await using var reader = await classesCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            classFqns.Add(reader.GetString(0));
        await reader.CloseAsync();

        if (classFqns.Count == 0)
            return [];

        // Step 2: Build adjacency list from DependsOn edges
        await SetAgePath(conn, ct);
        var adjacency = new Dictionary<string, HashSet<string>>();
        var classSet = classFqns.ToHashSet();

        foreach (var fqn in classFqns)
        {
            ct.ThrowIfCancellationRequested();
            var escaped = EscapeCypher(fqn);
            // NOTE: ExecuteCypherQuery expects 5 return columns (fqn, name, file_path, start_line, signature).
            // Trả đủ 5 columns để Cypher wrapper không fail — trước đây chỉ RETURN b.fqn → exception →
            // catch nuốt lỗi → adjacency rỗng → không phát hiện được cycle nào. Đã fix Sprint 1.
            var cypher =
                "MATCH (a {fqn: '" + escaped + "'})-[:DependsOn]->(b)" +
                " RETURN b.fqn, b.name, b.file_path, b.start_line, b.signature";

            try
            {
                var deps = await ExecuteCypherQuery(cypher, "Graph:CircularDep", ct);
                foreach (var dep in deps)
                {
                    if (classSet.Contains(dep.Fqn))
                    {
                        if (!adjacency.ContainsKey(fqn))
                            adjacency[fqn] = [];
                        adjacency[fqn].Add(dep.Fqn);
                    }
                }
            }
            catch { /* skip */ }
        }

        // Step 3: DFS cycle detection
        var cycles = new List<IReadOnlyList<string>>();
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();
        var stack = new List<string>();

        foreach (var node in adjacency.Keys)
        {
            if (!visited.Contains(node))
                DfsFindCycles(node, adjacency, visited, inStack, stack, cycles);
        }

        return cycles;
    }

    private static void DfsFindCycles(
        string node,
        Dictionary<string, HashSet<string>> adjacency,
        HashSet<string> visited,
        HashSet<string> inStack,
        List<string> stack,
        List<IReadOnlyList<string>> cycles)
    {
        visited.Add(node);
        inStack.Add(node);
        stack.Add(node);

        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (inStack.Contains(neighbor))
                {
                    // Found a cycle — extract it
                    var cycleStart = stack.IndexOf(neighbor);
                    if (cycleStart >= 0)
                    {
                        var cycle = stack.Skip(cycleStart).Append(neighbor).ToList();
                        // Only add if we haven't seen an equivalent cycle
                        if (cycles.Count < 20) // Limit to prevent explosion
                            cycles.Add(cycle);
                    }
                }
                else if (!visited.Contains(neighbor))
                {
                    DfsFindCycles(neighbor, adjacency, visited, inStack, stack, cycles);
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        inStack.Remove(node);
    }

    // --- Phase 6: Graph visualization ---

    public async Task<GraphOverview> GetGraphOverviewAsync(
        Guid repoId, int nodeLimit = 500, IReadOnlyList<string>? kindFilter = null, CancellationToken ct = default)
    {
        // Step 1: Fetch nodes from relational table (faster than Cypher for bulk reads, has kind column)
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var countSql = kindFilter is { Count: > 0 }
            ? "SELECT COUNT(*) FROM public.code_symbols WHERE repo_id = @repoId AND kind = ANY(@kinds)"
            : "SELECT COUNT(*) FROM public.code_symbols WHERE repo_id = @repoId";

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = countSql;
        countCmd.Parameters.AddWithValue("repoId", repoId);
        if (kindFilter is { Count: > 0 })
            countCmd.Parameters.AddWithValue("kinds", kindFilter.ToArray());
        var totalMatchingNodes = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        var nodesSql = kindFilter is { Count: > 0 }
            ? "SELECT fqn, name, kind, signature, file_path, start_line FROM public.code_symbols WHERE repo_id = @repoId AND kind = ANY(@kinds) ORDER BY kind, name LIMIT @limit"
            : "SELECT fqn, name, kind, signature, file_path, start_line FROM public.code_symbols WHERE repo_id = @repoId ORDER BY kind, name LIMIT @limit";

        await using var nodesCmd = conn.CreateCommand();
        nodesCmd.CommandText = nodesSql;
        nodesCmd.Parameters.AddWithValue("repoId", repoId);
        nodesCmd.Parameters.AddWithValue("limit", nodeLimit);
        if (kindFilter is { Count: > 0 })
            nodesCmd.Parameters.AddWithValue("kinds", kindFilter.ToArray());

        var nodes = new List<GraphNode>();
        var fqnSet = new HashSet<string>();
        await using var nodesReader = await nodesCmd.ExecuteReaderAsync(ct);
        while (await nodesReader.ReadAsync(ct))
        {
            var fqn = nodesReader.GetString(0);
            fqnSet.Add(fqn);
            nodes.Add(new GraphNode(
                Fqn: fqn,
                Name: nodesReader.GetString(1),
                Kind: nodesReader.GetString(2),
                Signature: nodesReader.IsDBNull(3) ? null : nodesReader.GetString(3),
                FilePath: nodesReader.IsDBNull(4) ? null : nodesReader.GetString(4),
                StartLine: nodesReader.IsDBNull(5) ? null : nodesReader.GetInt32(5)
            ));
        }
        await nodesReader.CloseAsync();

        if (nodes.Count == 0)
            return new GraphOverview(nodes, [], totalMatchingNodes);

        // Step 2: Fetch edges from AGE graph (relationships only exist in graph)
        await SetAgePath(conn, ct);
        var repo = EscapeCypher(repoId.ToString());
        var edgeCypher =
            "MATCH (a)-[r]->(b)" +
            $" WHERE a.repo_id = '{repo}' AND b.repo_id = '{repo}'" +
            " RETURN a.fqn, b.fqn, r.type";

        var edgeSql = $"""
            SELECT from_fqn::text, to_fqn::text, rel_type::text
            FROM cypher('{GraphName}', $$
            {edgeCypher}
            $$) AS (from_fqn agtype, to_fqn agtype, rel_type agtype);
            """;

        await using var edgeCmd = conn.CreateCommand();
        edgeCmd.CommandText = edgeSql;

        var edges = new List<GraphEdge>();
        await using var edgeReader = await edgeCmd.ExecuteReaderAsync(ct);
        while (await edgeReader.ReadAsync(ct))
        {
            var fromFqn = UnquoteAgtype(edgeReader.IsDBNull(0) ? "" : edgeReader.GetString(0));
            var toFqn = UnquoteAgtype(edgeReader.IsDBNull(1) ? "" : edgeReader.GetString(1));
            var relType = UnquoteAgtype(edgeReader.IsDBNull(2) ? "" : edgeReader.GetString(2));

            // Only include edges where both endpoints are in the returned node set
            if (fqnSet.Contains(fromFqn) && fqnSet.Contains(toFqn))
                edges.Add(new GraphEdge(fromFqn, toFqn, relType));
        }

        return new GraphOverview(nodes, edges, totalMatchingNodes);
    }

    public async Task<GraphOverview> GetNodeNeighborsAsync(
        string fqn, int depth = 1, CancellationToken ct = default)
    {
        var escaped = EscapeCypher(fqn);
        var safeDepth = Math.Clamp(depth, 1, 3);

        // Query center node + all neighbors + edges
        var cypher =
            "MATCH (center {fqn: '" + escaped + "'})-[r*1.." + safeDepth + "]-(neighbor)" +
            " RETURN center.fqn, center.name, center.file_path, center.start_line, center.signature," +
            " neighbor.fqn, neighbor.name, neighbor.file_path, neighbor.start_line, neighbor.signature";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await SetAgePath(conn, ct);

        var sql = $"""
            SELECT c_fqn::text, c_name::text, c_fp::text, c_sl::text, c_sig::text,
                   n_fqn::text, n_name::text, n_fp::text, n_sl::text, n_sig::text
            FROM cypher('{GraphName}', $$
            {cypher}
            $$) AS (c_fqn agtype, c_name agtype, c_fp agtype, c_sl agtype, c_sig agtype,
                    n_fqn agtype, n_name agtype, n_fp agtype, n_sl agtype, n_sig agtype);
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var nodeMap = new Dictionary<string, GraphNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Center node
            var cFqn = UnquoteAgtype(reader.IsDBNull(0) ? "" : reader.GetString(0));
            if (!string.IsNullOrEmpty(cFqn) && !nodeMap.ContainsKey(cFqn))
            {
                nodeMap[cFqn] = new GraphNode(
                    cFqn,
                    UnquoteAgtype(reader.IsDBNull(1) ? "" : reader.GetString(1)),
                    InferKind(cFqn),
                    NullIfEmpty(UnquoteAgtype(reader.IsDBNull(4) ? "" : reader.GetString(4))),
                    NullIfEmpty(UnquoteAgtype(reader.IsDBNull(2) ? "" : reader.GetString(2))),
                    ParseAgtypeInt(reader.IsDBNull(3) ? "" : reader.GetString(3)) is var sl and > 0 ? sl : null
                );
            }

            // Neighbor node
            var nFqn = UnquoteAgtype(reader.IsDBNull(5) ? "" : reader.GetString(5));
            if (!string.IsNullOrEmpty(nFqn) && !nodeMap.ContainsKey(nFqn))
            {
                nodeMap[nFqn] = new GraphNode(
                    nFqn,
                    UnquoteAgtype(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                    InferKind(nFqn),
                    NullIfEmpty(UnquoteAgtype(reader.IsDBNull(9) ? "" : reader.GetString(9))),
                    NullIfEmpty(UnquoteAgtype(reader.IsDBNull(7) ? "" : reader.GetString(7))),
                    ParseAgtypeInt(reader.IsDBNull(8) ? "" : reader.GetString(8)) is var nsl and > 0 ? nsl : null
                );
            }
        }
        await reader.CloseAsync();

        // Now fetch edges between these nodes
        var edgeCypher =
            "MATCH (center {fqn: '" + escaped + "'})-[r]-(neighbor)" +
            " RETURN center.fqn, neighbor.fqn, r.type";

        var edgeSql = $"""
            SELECT from_fqn::text, to_fqn::text, rel_type::text
            FROM cypher('{GraphName}', $$
            {edgeCypher}
            $$) AS (from_fqn agtype, to_fqn agtype, rel_type agtype);
            """;

        await using var edgeCmd = conn.CreateCommand();
        edgeCmd.CommandText = edgeSql;

        var edges = new List<GraphEdge>();
        await using var edgeReader = await edgeCmd.ExecuteReaderAsync(ct);
        while (await edgeReader.ReadAsync(ct))
        {
            var fromFqn = UnquoteAgtype(edgeReader.IsDBNull(0) ? "" : edgeReader.GetString(0));
            var toFqn = UnquoteAgtype(edgeReader.IsDBNull(1) ? "" : edgeReader.GetString(1));
            var relType = UnquoteAgtype(edgeReader.IsDBNull(2) ? "" : edgeReader.GetString(2));

            if (!string.IsNullOrEmpty(fromFqn) && !string.IsNullOrEmpty(toFqn))
                edges.Add(new GraphEdge(fromFqn, toFqn, relType));
        }

        return new GraphOverview(nodeMap.Values.ToList(), edges);
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrEmpty(value) ? null : value;

    // --- Private helpers ---

    private async Task<IReadOnlyList<SearchResult>> ExecuteCypherQuery(
        string cypher, string source, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await SetAgePath(conn, ct);

        // Wrap Cypher in a subquery and cast agtype → text so Npgsql can read it.
        // Direct agtype reading is unsupported by Npgsql (custom PG type without a CLR mapping).
        var sql = $"""
            SELECT fqn::text, name::text, file_path::text, start_line::text, signature::text
            FROM cypher('{GraphName}', $$
            {cypher}
            $$) AS (fqn agtype, name agtype, file_path agtype, start_line agtype, signature agtype);
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<SearchResult>();
        var fqnToUnquoted = new List<(int Index, string Fqn)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var resultFqn = UnquoteAgtype(reader.IsDBNull(0) ? "" : reader.GetString(0));
                var resultName = UnquoteAgtype(reader.IsDBNull(1) ? "" : reader.GetString(1));
                var filePath = UnquoteAgtype(reader.IsDBNull(2) ? "" : reader.GetString(2));
                var startLine = ParseAgtypeInt(reader.IsDBNull(3) ? "" : reader.GetString(3));
                var signature = UnquoteAgtype(reader.IsDBNull(4) ? "" : reader.GetString(4));

                fqnToUnquoted.Add((results.Count, resultFqn));
                results.Add(new SearchResult(
                    Fqn: resultFqn,
                    Name: resultName,
                    Kind: "", // filled in below
                    Signature: string.IsNullOrEmpty(signature) ? null : signature,
                    FilePath: string.IsNullOrEmpty(filePath) ? null : filePath,
                    StartLine: startLine == 0 ? null : startLine,
                    Score: 1.0,
                    Source: source
                ));
            }
        }

        // R21 Fix #9/#10: resolve Kind from the stored code_symbols table instead of
        // heuristically inferring it from FQN shape. AGE Cypher queries return only
        // the node's raw properties; the label is what carries the kind but RETURN
        // labels(n) is not universally supported and changing every query is invasive.
        // Doing a second SELECT by FQN is cheap (indexed on fqn) and authoritative.
        var unknownFqns = fqnToUnquoted
            .Where(x => !string.IsNullOrEmpty(x.Fqn))
            .Select(x => x.Fqn)
            .Distinct()
            .ToList();

        var kindByFqn = await LookupKindsAsync(conn, unknownFqns, ct);

        // R25 R24-4 fix: also build a "generic-normalized" lookup so a graph FQN like
        // `Foo.Bar.GetAsync<CognitiveDirectiveObject>` matches the stored declaration
        // FQN `Foo.Bar.GetAsync<T>`. The graph stores per-call-site specializations
        // (with the concrete type argument) while the relational table only has the
        // generic declaration. We collapse everything inside `<...>` to find the
        // declaration, but keep the original FQN as the result key.
        var genericFallbackFqns = unknownFqns
            .Where(f => !kindByFqn.ContainsKey(f) && f.Contains('<'))
            .Select(NormalizeGenericFqn)
            .Where(f => !string.IsNullOrEmpty(f) && !kindByFqn.ContainsKey(f))
            .Distinct()
            .ToList();
        if (genericFallbackFqns.Count > 0)
        {
            var genericKinds = await LookupKindsAsync(conn, genericFallbackFqns, ct);
            foreach (var (k, v) in genericKinds)
                kindByFqn[k] = v;
        }

        for (var i = 0; i < results.Count; i++)
        {
            var fqn = results[i].Fqn;
            string kind;
            if (kindByFqn.TryGetValue(fqn, out var storedKind) && !string.IsNullOrEmpty(storedKind))
            {
                kind = storedKind;
            }
            else if (fqn.Contains('<') &&
                     kindByFqn.TryGetValue(NormalizeGenericFqn(fqn), out var genericKind) &&
                     !string.IsNullOrEmpty(genericKind))
            {
                kind = genericKind;
            }
            else
            {
                kind = InferKind(fqn);
            }
            results[i] = results[i] with { Kind = kind };
        }

        // R25 R24-1 fix: dedupe results by FQN. AGE variable-depth patterns like
        // `[:Calls*1..N]` return one row per *path*, so a target reachable from the
        // source via multiple call chains shows up multiple times in the output.
        // Centralizing the dedup here means every Query*Async method benefits
        // without needing to remember to filter at each call site. Order is
        // preserved (first occurrence wins).
        if (results.Count > 1)
        {
            var seenFqns = new HashSet<string>(StringComparer.Ordinal);
            var deduped = new List<SearchResult>(results.Count);
            foreach (var r in results)
            {
                if (string.IsNullOrEmpty(r.Fqn) || seenFqns.Add(r.Fqn))
                    deduped.Add(r);
            }
            results = deduped;
        }

        return results;
    }

    private static async Task ExecuteCypher(NpgsqlConnection conn, string cypher, CancellationToken ct)
    {
        var sql = $"""
            SELECT * FROM cypher('{GraphName}', $$
            {cypher}
            $$) AS (result agtype);
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        // AGE Cypher MERGE/SET returns agtype results that MUST be consumed.
        // Using ExecuteReaderAsync to properly drain the result set.
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { } // Drain results
    }

    private static async Task SetAgePath(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Escapes single quotes for safe injection into AGE Cypher strings.
    /// AGE does not support parameterized Cypher, so we must sanitize values.
    /// </summary>
    internal static string EscapeCypher(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>
    /// Sanitizes a label name for use as a Cypher node label.
    /// Only allows alphanumeric characters and underscores.
    /// </summary>
    internal static string SanitizeLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : "Unknown";
    }

    internal static string SanitizeIdentifier(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : "prop";
    }

    /// <summary>
    /// AGE returns agtype values as JSON-like strings. This removes surrounding quotes.
    /// </summary>
    private static string UnquoteAgtype(string agtype)
    {
        if (string.IsNullOrEmpty(agtype)) return "";
        var trimmed = agtype.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\'", "'");
        return trimmed;
    }

    private static int ParseAgtypeInt(string agtype)
    {
        var trimmed = agtype.Trim().Trim('"');
        return int.TryParse(trimmed, out var val) ? val : 0;
    }

    /// <summary>
    /// R25 R24-4 fix: collapse type arguments inside generic methods/types so we
    /// can match call-site specializations against the declaration row in
    /// <c>code_symbols</c>. Examples:
    /// <list type="bullet">
    /// <item><c>Foo.GetAsync&lt;CognitiveDirectiveObject&gt;</c> → <c>Foo.GetAsync&lt;T&gt;</c></item>
    /// <item><c>List&lt;User&gt;.Find</c> → <c>List&lt;T&gt;.Find</c></item>
    /// <item><c>Foo.Bar</c> → <c>Foo.Bar</c> (unchanged)</item>
    /// </list>
    /// We do not try to preserve the original arity (number of type params) — the
    /// declaration always uses single-letter T/U/V conventions, but the indexer
    /// stores them in <c>&lt;T&gt;</c> form regardless of how many. A simple
    /// "replace anything between &lt; &gt; with T" works for the common case.
    /// </summary>
    internal static string NormalizeGenericFqn(string fqn)
    {
        if (string.IsNullOrEmpty(fqn) || !fqn.Contains('<')) return fqn;

        var sb = new StringBuilder(fqn.Length);
        var depth = 0;
        for (var i = 0; i < fqn.Length; i++)
        {
            var ch = fqn[i];
            if (ch == '<')
            {
                if (depth == 0) sb.Append("<T>");
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
            }
            else if (depth == 0)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// R21 Fix #9/#10: Look up the stored <c>kind</c> column for a batch of FQNs
    /// from the relational <c>code_symbols</c> table. Authoritative source vs the
    /// legacy heuristic <see cref="InferKind"/> which was guessing based on FQN shape.
    /// </summary>
    private static async Task<Dictionary<string, string>> LookupKindsAsync(
        NpgsqlConnection conn, IReadOnlyList<string> fqns, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (fqns.Count == 0) return result;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fqn, kind FROM public.code_symbols WHERE fqn = ANY(@fqns)";
        var param = cmd.CreateParameter();
        param.ParameterName = "@fqns";
        param.Value = fqns.ToArray();
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fqn = reader.GetString(0);
            var kind = reader.IsDBNull(1) ? "" : reader.GetString(1);
            result[fqn] = kind;
        }

        return result;
    }

    private static string InferKind(string fqn)
    {
        // Infer kind from FQN prefix conventions used during indexing
        if (fqn.StartsWith("DI:")) return "di_registration";
        if (fqn.StartsWith("API:")) return "api_endpoint";
        if (fqn.StartsWith("config:") || fqn.StartsWith("env:")) return "config_key";
        if (fqn.StartsWith("doc:")) return fqn.Contains('#') ? "section" : "document";
        // For code symbols, the node label IS the kind — but Cypher RETURN doesn't include labels.
        // Fall back to heuristic: methods have signatures with parens, interfaces start with I + uppercase
        if (fqn.Contains('(')) return "method";
        if (fqn.Contains('.') && fqn.Split('.').Last() is { Length: > 1 } last && last[0] == 'I' && char.IsUpper(last[1]))
            return "interface";
        return "class";
    }

    internal static IEnumerable<IList<T>> Chunk<T>(IList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }
}
