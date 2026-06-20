using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Npgsql;

namespace CortexPlexus.Graph;

/// <summary>
/// IRepositoryStore implementation for managing repository metadata and file hashes
/// in PostgreSQL. Supports incremental indexing via content hash comparison.
/// </summary>
public sealed class RepositoryStore(NpgsqlDataSource dataSource) : IRepositoryStore
{
    public async Task<RepositoryInfo> RegisterAsync(string name, string path, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO repositories (name, path)
            VALUES (@name, @path)
            ON CONFLICT (path) DO UPDATE SET name = EXCLUDED.name
            RETURNING id, name, path, created_at, last_indexed
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@path", path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return ReadRepositoryInfo(reader);
    }

    public async Task<RepositoryInfo?> GetByPathAsync(string path, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, name, path, created_at, last_indexed
            FROM repositories
            WHERE path = @path
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@path", path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadRepositoryInfo(reader);
    }

    public async Task<IReadOnlyList<RepositoryInfo>> ListAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, name, path, created_at, last_indexed
            FROM repositories
            ORDER BY name
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<RepositoryInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadRepositoryInfo(reader));
        }

        return results;
    }

    public async Task<int> DeleteAsync(Guid repoId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Count first so the caller can report what was removed (the DELETE cascades, so
        // code_symbols rows are gone by the time it returns).
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT count(*) FROM code_symbols WHERE repo_id = @id";
        countCmd.Parameters.AddWithValue("@id", repoId);
        var symbolCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);

        // FK cascade removes code_symbols (incl. embedding vector + tsvector) and file_hashes.
        await using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM repositories WHERE id = @id";
        delCmd.Parameters.AddWithValue("@id", repoId);
        await delCmd.ExecuteNonQueryAsync(ct);

        return symbolCount;
    }

    public async Task UpdateLastIndexedAsync(Guid repoId, CancellationToken ct = default)
    {
        const string sql = "UPDATE repositories SET last_indexed = NOW() WHERE id = @id";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", repoId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsFileChangedAsync(
        string filePath, Guid repoId, string contentHash, CancellationToken ct = default)
    {
        const string sql = """
            SELECT content_hash
            FROM file_hashes
            WHERE file_path = @filePath AND repo_id = @repoId
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        cmd.Parameters.AddWithValue("@repoId", repoId);

        var result = await cmd.ExecuteScalarAsync(ct);

        // File is "changed" if it doesn't exist in the table or the hash differs
        if (result is null || result is DBNull)
            return true;

        return !string.Equals((string)result, contentHash, StringComparison.Ordinal);
    }

    public async Task UpdateFileHashAsync(
        string filePath, Guid repoId, string contentHash, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO file_hashes (file_path, repo_id, content_hash, indexed_at)
            VALUES (@filePath, @repoId, @contentHash, NOW())
            ON CONFLICT (file_path) DO UPDATE SET
                repo_id = EXCLUDED.repo_id,
                content_hash = EXCLUDED.content_hash,
                indexed_at = NOW()
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        cmd.Parameters.AddWithValue("@repoId", repoId);
        cmd.Parameters.AddWithValue("@contentHash", contentHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<string, string>> GetFileHashesAsync(Guid repoId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, content_hash
            FROM file_hashes
            WHERE repo_id = @repoId
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@repoId", repoId);

        var result = new Dictionary<string, string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static RepositoryInfo ReadRepositoryInfo(NpgsqlDataReader reader)
    {
        return new RepositoryInfo(
            Id: reader.GetGuid(0),
            Name: reader.GetString(1),
            Path: reader.GetString(2),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(3),
            LastIndexed: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)
        );
    }
}
