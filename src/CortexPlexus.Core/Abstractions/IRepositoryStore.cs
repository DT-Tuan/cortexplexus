using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

public interface IRepositoryStore
{
    Task<RepositoryInfo> RegisterAsync(string name, string path, CancellationToken ct = default);
    Task<RepositoryInfo?> GetByPathAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Delete a repository and its relational data. The <c>code_symbols</c> (incl. their embedding
    /// vector + FTS) and <c>file_hashes</c> rows cascade on the FK. Does NOT remove AGE graph
    /// vertices — call <see cref="IGraphStore.DeleteByRepoAsync"/> for those. Returns the number of
    /// <c>code_symbols</c> rows removed (0 if the repo had none / didn't exist).
    /// </summary>
    Task<int> DeleteAsync(Guid repoId, CancellationToken ct = default);
    Task UpdateLastIndexedAsync(Guid repoId, CancellationToken ct = default);

    /// <summary>
    /// Stamp the embedding space that produced this repo's vectors (ADR-018).
    /// Called only after a full index run succeeds — never on incremental sync.
    /// </summary>
    Task UpdateEmbeddingSpaceAsync(Guid repoId, string provider, string model, int dim, CancellationToken ct = default);
    Task<bool> IsFileChangedAsync(string filePath, Guid repoId, string contentHash, CancellationToken ct = default);
    Task UpdateFileHashAsync(string filePath, Guid repoId, string contentHash, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetFileHashesAsync(Guid repoId, CancellationToken ct = default);

    /// <summary>Delete the <c>file_hashes</c> rows of the given files. Returns rows deleted.</summary>
    Task<int> DeleteFileHashesAsync(Guid repoId, IReadOnlyCollection<string> filePaths, CancellationToken ct = default);

    /// <summary>
    /// Delete every <c>file_hashes</c> row of the repository whose path is NOT in
    /// <paramref name="presentFilePaths"/> (full file list of a successful index run).
    /// Returns the paths removed. Empty snapshot is a no-op (see
    /// <see cref="IVectorStore.SweepMissingFilesAsync"/> for rationale).
    /// </summary>
    Task<IReadOnlyList<string>> SweepFileHashesAsync(Guid repoId, IReadOnlyCollection<string> presentFilePaths, CancellationToken ct = default);

    /// <summary>
    /// Record a watch-agent heartbeat for a repository (upsert). <paramref name="lastSyncUtc"/>
    /// only moves the stored value forward when non-null — a heartbeat that carries no sync
    /// info must not erase the last known good sync. Lets list_repositories expose a
    /// watch that is alive-but-not-uploading (the zombie-agent failure mode).
    /// </summary>
    Task UpsertWatchHeartbeatAsync(
        Guid repoId, string agentVersion, DateTimeOffset? lastSyncUtc,
        int consecutiveFailures, string? lastError, CancellationToken ct = default);

    /// <summary>Latest watch-agent status for a repository, or null if no agent ever reported.</summary>
    Task<WatchStatusInfo?> GetWatchStatusAsync(Guid repoId, CancellationToken ct = default);
}
