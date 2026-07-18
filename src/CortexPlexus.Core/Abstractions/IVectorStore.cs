using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

/// <summary>
/// Summary of a vector-upsert call. Returned instead of plain Task so the
/// caller (and, transitively, the local agent and the AI client) can tell
/// whether every symbol actually landed in the vector store or whether the
/// implementation silently dropped some — e.g. pgvector type-cache misses
/// that used to surface only as server-side WARN logs (see issue #1).
/// </summary>
/// <param name="Persisted">Symbols whose row was written to <c>code_symbols</c>, with or without an embedding.</param>
/// <param name="Failed">Symbols whose batch threw during write; each failure is logged with its exception at WARN level by the implementation.</param>
/// <param name="VectorRowsWritten">Symbols whose row landed in <c>code_symbols</c> AND has a non-null <c>embedding</c> column. ≤ <c>Persisted</c>; the difference is symbols of non-embeddable kinds (field, property, event, …) which are persisted with NULL embedding by design.</param>
public readonly record struct VectorUpsertResult(int Persisted, int Failed, int VectorRowsWritten)
{
    public int Total => Persisted + Failed;
    public bool HasFailures => Failed > 0;
    public static readonly VectorUpsertResult Empty = new(0, 0, 0);
}

public interface IVectorStore
{
    Task<VectorUpsertResult> UpsertAsync(IEnumerable<CodeSymbol> symbols, IReadOnlyDictionary<string, float[]> embeddings, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryEmbedding, int limit = 10, Guid? repoId = null, string? kind = null, CancellationToken ct = default);
    Task DeleteByRepoAsync(Guid repoId, CancellationToken ct = default);

    /// <summary>
    /// Delete all <c>code_symbols</c> rows (incl. embedding + FTS columns) of a repository
    /// whose <c>file_path</c> is in <paramref name="filePaths"/>. Returns rows deleted.
    /// Explicit-deletion half of the stale-symbol fix: the agent reports files it saw
    /// disappear (watch delete events, server-vs-local hash diff).
    /// </summary>
    Task<int> DeleteByFilePathsAsync(Guid repoId, IReadOnlyCollection<string> filePaths, CancellationToken ct = default);

    /// <summary>
    /// Sweep half of the stale-symbol fix: delete every <c>code_symbols</c> row of the
    /// repository whose <c>file_path</c> is NOT in <paramref name="presentFilePaths"/>
    /// (the full file list of a just-completed, successful index run). Returns the
    /// distinct file paths that were removed so the caller can clean the graph too.
    /// Rows with NULL file_path are never swept (cannot be attributed to a file).
    /// An empty snapshot is a no-op — a failed/partial crawl must not wipe the repo.
    /// </summary>
    Task<IReadOnlyList<string>> SweepMissingFilesAsync(Guid repoId, IReadOnlyCollection<string> presentFilePaths, CancellationToken ct = default);
}
