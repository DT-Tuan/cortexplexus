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
    Task<bool> IsFileChangedAsync(string filePath, Guid repoId, string contentHash, CancellationToken ct = default);
    Task UpdateFileHashAsync(string filePath, Guid repoId, string contentHash, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetFileHashesAsync(Guid repoId, CancellationToken ct = default);
}
