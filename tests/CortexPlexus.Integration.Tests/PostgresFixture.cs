using Npgsql;
using Testcontainers.PostgreSql;

namespace CortexPlexus.Integration.Tests;

/// <summary>
/// Shared PostgreSQL container (pgvector-enabled) for all integration tests.
/// Uses pgvector/pgvector:pg17 image which includes pgvector extension pre-installed.
/// Note: Apache AGE is NOT available in this image — graph store tests are excluded.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cortexplexus_test")
        .WithUsername("postgres")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseVector();
        return builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply schema (pgvector + tsvector parts only — no AGE)
        await using var dataSource = CreateDataSource();
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS repositories (
                id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                name        TEXT NOT NULL,
                path        TEXT NOT NULL UNIQUE,
                created_at  TIMESTAMPTZ DEFAULT NOW(),
                last_indexed TIMESTAMPTZ,
                -- ADR-018 embedding-space stamp (mirrors Graph/Schema/Migrations.sql)
                embedding_provider TEXT,
                embedding_model    TEXT,
                embedding_dim      INT
            );

            CREATE TABLE IF NOT EXISTS code_symbols (
                id             BIGSERIAL PRIMARY KEY,
                fqn            TEXT UNIQUE NOT NULL,
                name           TEXT NOT NULL,
                kind           TEXT NOT NULL,
                signature      TEXT,
                file_path      TEXT,
                start_line     INT,
                end_line       INT,
                repo_id        UUID NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
                indexed_at     TIMESTAMPTZ DEFAULT NOW(),
                embedding      vector(768),
                accessibility  TEXT,
                documentation  TEXT,
                summary        TEXT,
                is_test_method BOOLEAN DEFAULT FALSE,
                search_text tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('english', coalesce(name, '')), 'A') ||
                    setweight(to_tsvector('english', coalesce(fqn, '')), 'B') ||
                    setweight(to_tsvector('english', coalesce(signature, '')), 'C') ||
                    setweight(to_tsvector('english', coalesce(documentation, '')), 'D')
                ) STORED
            );

            CREATE INDEX IF NOT EXISTS idx_symbols_embedding
                ON code_symbols USING hnsw (embedding vector_cosine_ops);
            CREATE INDEX IF NOT EXISTS idx_symbols_fts
                ON code_symbols USING gin (search_text);
            CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON code_symbols (fqn);
            CREATE INDEX IF NOT EXISTS idx_symbols_repo ON code_symbols (repo_id);
            CREATE INDEX IF NOT EXISTS idx_symbols_kind ON code_symbols (kind);

            CREATE TABLE IF NOT EXISTS file_hashes (
                file_path    TEXT PRIMARY KEY,
                repo_id      UUID NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
                content_hash TEXT NOT NULL,
                indexed_at   TIMESTAMPTZ DEFAULT NOW()
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>Insert a test repository and return its ID.</summary>
    public async Task<Guid> SeedRepositoryAsync(NpgsqlDataSource dataSource, string name = "test-repo", string path = "/test/repo")
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO repositories (name, path) VALUES (@name, @path) RETURNING id";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@path", path);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
