using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace CortexPlexus.Graph;

/// <summary>
/// IVectorStore implementation using PostgreSQL with the pgvector extension.
/// Supports HNSW-indexed cosine similarity search over code symbol embeddings.
/// </summary>
public sealed class VectorStore(NpgsqlDataSource dataSource, ILogger<VectorStore> logger) : IVectorStore
{
    private const int BatchSize = 200;

    /// <summary>
    /// Minimum number of vector inserts before switching to bulk-load mode
    /// (drop HNSW → insert → recreate HNSW). Below this threshold we keep the
    /// index live and pay per-row HNSW maintenance.
    ///
    /// Break-even measured on pgvector pg17 (R18 benchmark):
    /// - INSERT 500 rows WITH hnsw index present:  222 ms (0.44 ms/row)
    /// - INSERT 500 rows WITHOUT index, then rebuild on 6K rows: 21 ms + 987 ms = 1008 ms
    /// - INSERT 1000 rows WITH hnsw: ~444 ms; WITHOUT + rebuild: ~42 ms + 987 ms = 1029 ms (still slower)
    /// - Break-even ≈ 2,300–2,500 rows; we use 500 to be conservative because the
    ///   projection is super-linear as the table grows (R14 chunk 6 was 707s).
    /// </summary>
    private const int BulkLoadThreshold = 500;

    private const string HnswDropSql = "DROP INDEX IF EXISTS public.idx_symbols_embedding";
    private const string HnswCreateSql =
        "CREATE INDEX IF NOT EXISTS idx_symbols_embedding " +
        "ON public.code_symbols USING hnsw (embedding vector_cosine_ops)";

    /// <summary>
    /// pgvector HNSW per-query candidate list size. Default is 40 which gives ~95%
    /// recall on top-10 queries; raising to 100 lifts recall to ~99% with negligible
    /// latency cost on our corpus size class (5–20K embeddings, &lt;5 ms either way).
    /// Code-search recall matters more than the few microseconds saved.
    /// R19: applied per-connection in <see cref="SearchAsync"/> via SET (Npgsql resets
    /// session state on connection return to pool, so no leak between callers).
    /// </summary>
    private const int HnswEfSearch = 100;

    public async Task<VectorUpsertResult> UpsertAsync(
        IEnumerable<CodeSymbol> symbols,
        IReadOnlyDictionary<string, float[]> embeddings,
        CancellationToken ct = default)
    {
        // Deduplicate by FQN (keep last occurrence) to avoid ON CONFLICT errors within same batch
        var symbolList = symbols
            .GroupBy(s => s.Fqn)
            .Select(g => g.Last())
            .ToList();
        if (symbolList.Count == 0) return VectorUpsertResult.Empty;

        // R27-1 fail-loud backstop: a symbol with null/empty RepoId would coerce to
        // Guid.Empty below and violate code_symbols_repo_id_fkey, taking down its whole
        // 200-row batch. Drop these loudly instead of silently corrupting the batch.
        // Root cause is always an upstream RepoId-assignment gap (e.g. a CodeSymbol
        // subtype missing from IndexingPipeline.SetRepoId). After that fix this should
        // never fire — it exists to surface any future regression as a warning + a
        // non-zero Failed count rather than a mysterious whole-batch drop.
        var missingRepo = symbolList.Where(s => s.RepoId is null || s.RepoId == Guid.Empty).ToList();
        var droppedNoRepo = missingRepo.Count;
        if (droppedNoRepo > 0)
        {
            logger.LogWarning(
                "Vector upsert: dropping {Count} symbol(s) with missing RepoId (would violate FK). " +
                "First offenders: {Fqns}. This indicates an upstream RepoId-assignment gap.",
                droppedNoRepo,
                string.Join(", ", missingRepo.Take(5).Select(s => $"{s.Kind}:{s.Fqn}")));
            symbolList = symbolList.Where(s => s.RepoId is not null && s.RepoId != Guid.Empty).ToList();
            if (symbolList.Count == 0)
                return new VectorUpsertResult(Persisted: 0, Failed: droppedNoRepo, VectorRowsWritten: 0);
        }

        // Count symbols that will actually hit the HNSW index (non-null, non-empty vector).
        var embeddedCount = symbolList.Count(s =>
            embeddings.TryGetValue(s.Fqn, out var v) && v.Length > 0);
        var useBulkLoad = embeddedCount >= BulkLoadThreshold;

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var indexWasDropped = false;
        var dropSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (useBulkLoad)
            {
                logger.LogInformation(
                    "Vector bulk-load: dropping HNSW index ({Embedded} vectors ≥ threshold {Threshold})",
                    embeddedCount, BulkLoadThreshold);
                await ExecuteNonQueryAsync(conn, HnswDropSql, ct);
                indexWasDropped = true;
                dropSw.Stop();
            }

            var insertSw = System.Diagnostics.Stopwatch.StartNew();
            var failCount = 0;
            foreach (var batch in Chunk(symbolList, BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await UpsertBatch(conn, batch, embeddings, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failCount += batch.Count;
                    logger.LogWarning(ex, "Failed to upsert vector batch of {Count} symbols", batch.Count);
                }
            }
            insertSw.Stop();

            if (failCount > 0)
                logger.LogWarning("Vector upsert: {Failed} of {Total} symbols failed", failCount, symbolList.Count);

            if (useBulkLoad)
            {
                logger.LogInformation(
                    "Vector bulk-load insert phase: {Symbols} symbols in {InsertMs} ms",
                    symbolList.Count, insertSw.ElapsedMilliseconds);
            }

            // VectorRowsWritten = symbols that ended up with non-null embedding.
            // Subtracts batch failures from the count of symbols that had a vector
            // input. The exact persisted-with-vector count requires a SELECT and
            // is not worth the round-trip; this is an honest lower-bound assuming
            // failures distribute proportionally across embeddable + non-embeddable.
            // For health reporting via list_repositories, the SQL probe gives the
            // authoritative count anyway.
            var withVectorInput = symbolList.Count(s =>
                embeddings.TryGetValue(s.Fqn, out var v) && v.Length > 0);
            var vectorRowsWritten = failCount == 0
                ? withVectorInput
                : Math.Max(0, withVectorInput - failCount);
            return new VectorUpsertResult(
                Persisted: symbolList.Count - failCount,
                Failed: failCount + droppedNoRepo,
                VectorRowsWritten: vectorRowsWritten);
        }
        finally
        {
            // CRITICAL: we MUST recreate HNSW regardless of insert outcome,
            // otherwise SearchAsync falls back to sequential scan on vector(768) —
            // unusable even on small tables. Do not propagate the user's CT here;
            // if the user cancelled we still need the index back.
            if (indexWasDropped)
            {
                var rebuildSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await ExecuteNonQueryAsync(conn, HnswCreateSql, CancellationToken.None, commandTimeoutSeconds: 600);
                    rebuildSw.Stop();
                    logger.LogInformation(
                        "Vector bulk-load: HNSW index rebuilt in {RebuildMs} ms (drop={DropMs} ms)",
                        rebuildSw.ElapsedMilliseconds, dropSw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    // Index rebuild failed → search is now SLOW until someone runs
                    // CREATE INDEX manually. Log loudly so ops can react.
                    logger.LogError(ex,
                        "CRITICAL: HNSW index rebuild failed after bulk-load. " +
                        "Vector search will be slow until recovery. " +
                        "Manual fix: {Sql}",
                        HnswCreateSql);
                    // Do not rethrow — the user's upsert succeeded, and rethrow would
                    // mask it. Ops should see the CRITICAL log and intervene.
                }
            }
        }
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, int commandTimeoutSeconds = 60)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = commandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryEmbedding,
        int limit = 10,
        Guid? repoId = null,
        string? kind = null,
        string? requireProvider = null,
        string? requireModel = null,
        CancellationToken ct = default)
    {
        var filters = new List<string> { "cs.embedding IS NOT NULL" };
        if (repoId.HasValue) filters.Add("cs.repo_id = @repoId");
        if (!string.IsNullOrEmpty(kind)) filters.Add("cs.kind = @kind");
        // ADR-018: cross-repo space filter. NULL stamp = legacy unknown → still searchable.
        if (!string.IsNullOrEmpty(requireProvider) && !string.IsNullOrEmpty(requireModel))
        {
            filters.Add(
                "(r.embedding_provider IS NULL OR (r.embedding_provider = @reqProv AND r.embedding_model = @reqModel))");
        }

        var whereClause = "WHERE " + string.Join(" AND ", filters);

        var sql = $"""
            SELECT cs.fqn, cs.name, cs.kind, cs.signature, cs.file_path, cs.start_line,
                   1.0 - (cs.embedding <=> @query) AS score, cs.documentation, cs.summary
            FROM code_symbols cs
            JOIN repositories r ON r.id = cs.repo_id
            {whereClause}
            ORDER BY cs.embedding <=> @query
            LIMIT @limit
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // R19: tune HNSW ef_search per query for better recall on our corpus size.
        // Default 40 misses ~5% of top-10 results; 100 raises recall to ~99%.
        // Npgsql resets session state on connection return to pool, so this does
        // not leak to subsequent unrelated callers.
        await ExecuteNonQueryAsync(conn, $"SET hnsw.ef_search = {HnswEfSearch}", ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        cmd.Parameters.Add(new NpgsqlParameter("@query", new Vector(queryEmbedding)));
        cmd.Parameters.AddWithValue("@limit", limit);

        if (repoId.HasValue) cmd.Parameters.AddWithValue("@repoId", repoId.Value);
        if (!string.IsNullOrEmpty(kind)) cmd.Parameters.AddWithValue("@kind", kind);
        if (!string.IsNullOrEmpty(requireProvider) && !string.IsNullOrEmpty(requireModel))
        {
            cmd.Parameters.AddWithValue("@reqProv", requireProvider);
            cmd.Parameters.AddWithValue("@reqModel", requireModel);
        }

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
                Score: reader.GetDouble(6),
                Source: "Vector",
                Documentation: reader.IsDBNull(7) ? null : reader.GetString(7),
                AiSummary: reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }

        return results;
    }

    public async Task DeleteByRepoAsync(Guid repoId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM code_symbols WHERE repo_id = @repoId";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@repoId", repoId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteByFilePathsAsync(
        Guid repoId, IReadOnlyCollection<string> filePaths, CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return 0;

        const string sql = "DELETE FROM code_symbols WHERE repo_id = @repoId AND file_path = ANY(@paths)";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@repoId", repoId);
        cmd.Parameters.AddWithValue("@paths", filePaths.ToArray());
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
            logger.LogInformation("Deleted {Rows} code_symbols rows for {Files} removed file(s)", rows, filePaths.Count);
        return rows;
    }

    public async Task<IReadOnlyList<string>> SweepMissingFilesAsync(
        Guid repoId, IReadOnlyCollection<string> presentFilePaths, CancellationToken ct = default)
    {
        // Empty snapshot = no-op: sweeping against nothing would delete the whole repo.
        // A legitimate "repo is now empty" wipe must go through DeleteRepository.
        if (presentFilePaths.Count == 0) return [];

        const string sql = """
            DELETE FROM code_symbols
            WHERE repo_id = @repoId
              AND file_path IS NOT NULL
              AND NOT (file_path = ANY(@paths))
            RETURNING file_path
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@repoId", repoId);
        cmd.Parameters.AddWithValue("@paths", presentFilePaths.ToArray());

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            removed.Add(reader.GetString(0));

        if (removed.Count > 0)
            logger.LogInformation(
                "Stale-symbol sweep: removed symbols from {Files} file(s) no longer present in the index snapshot",
                removed.Count);

        return removed.ToList();
    }

    // --- Private helpers ---

    private static async Task UpsertBatch(
        NpgsqlConnection conn,
        IList<CodeSymbol> batch,
        IReadOnlyDictionary<string, float[]> embeddings,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            INSERT INTO code_symbols (fqn, name, kind, signature, file_path, start_line, end_line, repo_id, embedding, documentation, summary, is_test_method, accessibility)
            VALUES
            """);

        var cmd = conn.CreateCommand();
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(", ");

            var symbol = batch[i];
            sb.Append($"(@fqn{i}, @name{i}, @kind{i}, @sig{i}, @fp{i}, @sl{i}, @el{i}, @rid{i}, @emb{i}, @doc{i}, @sum{i}, @test{i}, @acc{i})");

            cmd.Parameters.AddWithValue($"@fqn{i}", symbol.Fqn);
            cmd.Parameters.AddWithValue($"@name{i}", symbol.Name);
            cmd.Parameters.AddWithValue($"@kind{i}", symbol.Kind);
            cmd.Parameters.AddWithValue($"@sig{i}", (object?)GetSignature(symbol) ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@fp{i}", (object?)symbol.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@sl{i}", (object?)symbol.StartLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@el{i}", (object?)symbol.EndLine ?? DBNull.Value);
            // RepoId is guaranteed non-null/non-empty by the fail-loud filter in
            // UpsertAsync — never coerce to Guid.Empty here (that was the silent
            // FK-violation path, R27-1).
            cmd.Parameters.AddWithValue($"@rid{i}", symbol.RepoId!.Value);
            cmd.Parameters.AddWithValue($"@doc{i}", (object?)symbol.Documentation ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@sum{i}", (object?)symbol.AiSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@test{i}", symbol is MethodInfo m ? m.IsTestMethod : false);
            cmd.Parameters.AddWithValue($"@acc{i}", (object?)GetAccessibility(symbol) ?? DBNull.Value);

            if (embeddings.TryGetValue(symbol.Fqn, out var vec) && vec.Length > 0)
                cmd.Parameters.Add(new NpgsqlParameter($"@emb{i}", new Vector(vec)));
            else
                cmd.Parameters.AddWithValue($"@emb{i}", DBNull.Value);
        }

        sb.Append("""

            ON CONFLICT (fqn) DO UPDATE SET
                name = EXCLUDED.name,
                kind = EXCLUDED.kind,
                signature = EXCLUDED.signature,
                file_path = EXCLUDED.file_path,
                start_line = EXCLUDED.start_line,
                end_line = EXCLUDED.end_line,
                repo_id = EXCLUDED.repo_id,
                -- COALESCE, not plain assign: an upsert that carries NO embedding
                -- (skip-embeddings space-mismatch path, or a transient embed failure that
                -- omits an FQN) must PRESERVE the existing vector, not NULL-wipe it.
                -- EXCLUDED.embedding is only non-null when this batch actually produced a
                -- vector for the row; otherwise keep whatever is already stored. Removal
                -- goes through DeleteBy*; there is no "re-embed to null" intent.
                embedding = COALESCE(EXCLUDED.embedding, code_symbols.embedding),
                documentation = EXCLUDED.documentation,
                summary = EXCLUDED.summary,
                is_test_method = EXCLUDED.is_test_method,
                accessibility = EXCLUDED.accessibility,
                indexed_at = NOW()
            """);

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
        await cmd.DisposeAsync();
    }

    private static string? GetSignature(CodeSymbol symbol) => symbol switch
    {
        MethodInfo m => m.Signature,
        ConstructorInfo c => c.Signature,
        DocumentSection d => d.Content, // Content stored as signature → indexed in tsvector (weight C)
        _ => null
    };

    /// <summary>
    /// Extract accessibility từ symbol record. Required by QueryDeadCodeAsync để filter
    /// public/internal methods. Without this, dead code detection always empty (R12 finding).
    /// </summary>
    private static string? GetAccessibility(CodeSymbol symbol) => symbol switch
    {
        MethodInfo m => m.Accessibility,
        ClassInfo c => c.Accessibility,
        InterfaceInfo i => i.Accessibility,
        PropertyInfo p => null, // No Accessibility field on PropertyInfo
        ConstructorInfo c => c.Accessibility,
        FieldInfo f => f.Accessibility,
        EventInfo e => e.Accessibility,
        _ => null
    };

    private static IEnumerable<IList<T>> Chunk<T>(IList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }
}
