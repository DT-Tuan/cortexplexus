using Npgsql;
using Testcontainers.PostgreSql;

namespace CortexPlexus.Graph.Tests;

/// <summary>
/// PostgreSQL container với Apache AGE extension (không có pgvector).
/// Dùng cho AgeGraphStore integration tests — chỉ cần graph store, không cần vector.
///
/// Schema tối thiểu: repositories + code_symbols (không có vector column) + code_graph.
/// </summary>
public sealed class AgeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("apache/age:latest")
        .WithDatabase("cortexplexus_age_test")
        .WithUsername("postgres")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        return builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Schema cho AGE tests — tương đương Migrations.sql nhưng bỏ pgvector.
        await using var dataSource = CreateDataSource();
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- Load AGE extension (apache/age image đã pre-install)
            CREATE EXTENSION IF NOT EXISTS age;

            -- Repositories (relational)
            CREATE TABLE IF NOT EXISTS public.repositories (
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

            -- Code symbols (relational, không có vector column để tương thích apache/age)
            CREATE TABLE IF NOT EXISTS public.code_symbols (
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
                accessibility  TEXT,
                documentation  TEXT,
                summary        TEXT,
                is_test_method BOOLEAN DEFAULT FALSE
            );

            CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON code_symbols (fqn);
            CREATE INDEX IF NOT EXISTS idx_symbols_repo ON code_symbols (repo_id);
            CREATE INDEX IF NOT EXISTS idx_symbols_kind ON code_symbols (kind);

            -- File hashes (cho test về RepositoryStore nếu cần)
            CREATE TABLE IF NOT EXISTS public.file_hashes (
                file_path    TEXT PRIMARY KEY,
                repo_id      UUID NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
                content_hash TEXT NOT NULL,
                indexed_at   TIMESTAMPTZ DEFAULT NOW()
            );

            -- Setup AGE graph
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'code_graph') THEN
                    PERFORM create_graph('code_graph');
                END IF;
            END $$;

            -- Pre-create vertex labels + fqn indexes (mirror Migrations.sql).
            -- create_vlabel takes cstring; agtype must be fully qualified inside DO blocks.
            DO $$
            DECLARE
                vertex_labels TEXT[] := ARRAY[
                    'class', 'method', 'interface', 'struct', 'record', 'enum',
                    'property', 'constructor', 'event', 'field', 'trait', 'type',
                    'function', 'namespace', 'dbcontext', 'di_registration',
                    'api_endpoint', 'middleware', 'document', 'section', 'config_key',
                    'Unknown'
                ];
                lbl TEXT;
            BEGIN
                FOREACH lbl IN ARRAY vertex_labels
                LOOP
                    BEGIN
                        EXECUTE format(
                            'SELECT ag_catalog.create_vlabel(%L::cstring, %L::cstring)',
                            'code_graph', lbl);
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END;
                END LOOP;
            END $$;

            DO $$
            DECLARE
                vertex_labels TEXT[] := ARRAY[
                    'class', 'method', 'interface', 'struct', 'record', 'enum',
                    'property', 'constructor', 'event', 'field', 'trait', 'type',
                    'function', 'namespace', 'dbcontext', 'di_registration',
                    'api_endpoint', 'middleware', 'document', 'section', 'config_key',
                    'Unknown'
                ];
                lbl TEXT;
            BEGIN
                FOREACH lbl IN ARRAY vertex_labels
                LOOP
                    BEGIN
                        EXECUTE format(
                            'CREATE INDEX IF NOT EXISTS %I ON code_graph.%I USING gin (properties)',
                            'idx_graph_' || lbl || '_props',
                            lbl
                        );
                    EXCEPTION WHEN OTHERS THEN NULL;
                    END;
                END LOOP;
            END $$;

            SET search_path = public, ag_catalog, "$user";
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>Insert một test repository và trả về ID.</summary>
    public async Task<Guid> SeedRepositoryAsync(
        NpgsqlDataSource dataSource,
        string name = "test-repo",
        string? path = null)
    {
        path ??= $"/test/{Guid.NewGuid():N}";
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO repositories (name, path) VALUES (@name, @path) RETURNING id";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@path", path);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Xoá toàn bộ code_symbols + graph nodes giữa các test.</summary>
    public async Task CleanAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        // Xoá relational data
        await using (var truncate = conn.CreateCommand())
        {
            truncate.CommandText = "TRUNCATE code_symbols, file_hashes, repositories CASCADE";
            await truncate.ExecuteNonQueryAsync();
        }

        // Xoá AGE graph nodes (MATCH-DELETE-ALL)
        await using (var loadAge = conn.CreateCommand())
        {
            loadAge.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
            await loadAge.ExecuteNonQueryAsync();
        }

        await using (var clearGraph = conn.CreateCommand())
        {
            clearGraph.CommandText = """
                SELECT * FROM cypher('code_graph', $$
                    MATCH (n) DETACH DELETE n
                $$) AS (result agtype);
                """;
            try
            {
                await using var reader = await clearGraph.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            catch
            {
                // Graph có thể trống — bỏ qua
            }
        }
    }
}

[CollectionDefinition("Age")]
public class AgeCollection : ICollectionFixture<AgeFixture> { }
