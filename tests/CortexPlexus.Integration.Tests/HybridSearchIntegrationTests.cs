using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using CortexPlexus.Search;
using CortexPlexus.Search.QueryExpansion;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CortexPlexus.Integration.Tests;

/// <summary>
/// End-to-end hybrid search tests against real PostgreSQL (pgvector + tsvector).
/// Verifies: BM25 search, vector search, RRF fusion, weighted ranking, query expansion integration.
/// Note: Graph search is excluded (requires Apache AGE extension).
/// </summary>
[Collection("Postgres")]
public sealed class HybridSearchIntegrationTests(PostgresFixture fixture)
{
    // ADR-018: router ctor takes the server's current space; seeded repos are unstamped
    // (NULL) so the space filter must not exclude them — that IS part of what we test.
    private static readonly EmbeddingSpace TestSpace = new("test", "test-model", 768);

    [Fact]
    public async Task HybridSearch_FusesBm25AndVector()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "hybrid", "/hybrid");

        var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
        var fullTextStore = new FullTextStore(ds);
        var embeddingService = Substitute.For<IEmbeddingService>();
        var queryExpander = new NoOpQueryExpander();

        // Seed: 3 symbols with embeddings
        var paymentEmb = NormalizedEmbedding(0.5f);
        var orderEmb = NormalizedEmbedding(0.3f);
        var userEmb = NormalizedEmbedding(0.8f);

        await vectorStore.UpsertAsync(
            [MakeClass("H.PaymentService", "PaymentService", repoId),
             MakeClass("H.OrderService", "OrderService", repoId),
             MakeClass("H.UserService", "UserService", repoId)],
            new Dictionary<string, float[]>
            {
                ["H.PaymentService"] = paymentEmb,
                ["H.OrderService"] = orderEmb,
                ["H.UserService"] = userEmb
            });

        // Setup embedding service to return the payment embedding for this query
        embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(paymentEmb);

        var router = new HybridQueryRouter(
            vectorStore, fullTextStore, embeddingService, queryExpander,
            new RepositoryStore(ds), TestSpace,
            NullLogger<HybridQueryRouter>.Instance);

        // Hybrid search — should use BOTH vector (via embedding) and BM25
        var results = await router.SearchAsync(
            new SearchRequest("PaymentService", SearchType.Hybrid, Limit: 10));

        Assert.NotEmpty(results);
        // PaymentService should rank highest (matches both vector AND BM25)
        Assert.Equal("H.PaymentService", results[0].Fqn);
    }

    [Fact]
    public async Task Bm25Search_ReturnsWeightedResults()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "bm25", "/bm25");

        var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
        var fullTextStore = new FullTextStore(ds);
        var embeddingService = Substitute.For<IEmbeddingService>();
        var queryExpander = new NoOpQueryExpander();

        // Seed symbols — one with "Payment" in name, another only in signature
        await InsertSymbol(ds, "B.PaymentGateway", "PaymentGateway", "class", null, repoId);
        await InsertSymbol(ds, "B.Processor", "Processor", "class",
            "void Processor(PaymentGateway gateway)", repoId);

        var router = new HybridQueryRouter(
            vectorStore, fullTextStore, embeddingService, queryExpander,
            new RepositoryStore(ds), TestSpace,
            NullLogger<HybridQueryRouter>.Instance);

        var results = await router.SearchAsync(
            new SearchRequest("PaymentGateway", SearchType.Bm25, Limit: 10));

        Assert.NotEmpty(results);
        // Class named "PaymentGateway" (weight A) should outrank "Processor" (weight C in sig)
        Assert.Equal("B.PaymentGateway", results[0].Fqn);
    }

    [Fact]
    public async Task VectorSearch_FindsSemanticMatch()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "vec-sem", "/vec-sem");

        var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
        var fullTextStore = new FullTextStore(ds);
        var embeddingService = Substitute.For<IEmbeddingService>();
        var queryExpander = new NoOpQueryExpander();

        // Two symbols with different embeddings
        var targetEmb = NormalizedEmbedding(0.42f);
        var otherEmb = NormalizedEmbedding(0.99f);

        await vectorStore.UpsertAsync(
            [MakeClass("V.Target", "Target", repoId), MakeClass("V.Other", "Other", repoId)],
            new Dictionary<string, float[]> { ["V.Target"] = targetEmb, ["V.Other"] = otherEmb });

        // Query embedding close to target
        embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(targetEmb);

        var router = new HybridQueryRouter(
            vectorStore, fullTextStore, embeddingService, queryExpander,
            new RepositoryStore(ds), TestSpace,
            NullLogger<HybridQueryRouter>.Instance);

        var results = await router.SearchAsync(
            new SearchRequest("find target", SearchType.Vector, Limit: 5));

        Assert.NotEmpty(results);
        Assert.Equal("V.Target", results[0].Fqn);
        Assert.True(results[0].Score > 0.99);
    }

    [Fact]
    public async Task HybridSearch_WithExpandFlag_IncludesExpandedResults()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "expand", "/expand");

        var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
        var fullTextStore = new FullTextStore(ds);
        var embeddingService = Substitute.For<IEmbeddingService>();

        // Mock a query expander that IS enabled
        var queryExpander = Substitute.For<IQueryExpander>();
        queryExpander.IsEnabled.Returns(true);
        queryExpander.ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical document about error handling in services");
        queryExpander.ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "error handling", "exception management in services", "retry logic patterns" });

        // Seed
        await InsertSymbol(ds, "E.ErrorHandler", "ErrorHandler", "class", null, repoId);
        await InsertSymbol(ds, "E.RetryService", "RetryService", "class",
            "void RetryService(ILogger logger)", repoId);

        var emb = NormalizedEmbedding(0.6f);
        await vectorStore.UpsertAsync(
            [MakeClass("E.ErrorHandler", "ErrorHandler", repoId)],
            new Dictionary<string, float[]> { ["E.ErrorHandler"] = emb });
        embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(emb);

        var router = new HybridQueryRouter(
            vectorStore, fullTextStore, embeddingService, queryExpander,
            new RepositoryStore(ds), TestSpace,
            NullLogger<HybridQueryRouter>.Instance);

        var results = await router.SearchAsync(
            new SearchRequest("error handling", SearchType.Hybrid, Limit: 10, Expand: true));

        // Should have called both HyDE and multi-query
        await queryExpander.Received(1).ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await queryExpander.Received(1).ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Should find results (from vector and/or BM25 + expanded)
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task RrfFusion_ConsensusRanksHigher()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "rrf", "/rrf");

        var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
        var fullTextStore = new FullTextStore(ds);
        var embeddingService = Substitute.For<IEmbeddingService>();
        var queryExpander = new NoOpQueryExpander();

        // Seed: "AuthService" should rank in BOTH vector AND BM25
        var authEmb = NormalizedEmbedding(0.33f);
        var otherEmb = NormalizedEmbedding(0.88f);

        await vectorStore.UpsertAsync(
            [MakeClass("R.AuthService", "AuthService", repoId),
             MakeClass("R.CacheService", "CacheService", repoId)],
            new Dictionary<string, float[]>
            {
                ["R.AuthService"] = authEmb,
                ["R.CacheService"] = otherEmb
            });

        // Embedding returns authEmb — AuthService ranks #1 in vector
        embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(authEmb);

        var router = new HybridQueryRouter(
            vectorStore, fullTextStore, embeddingService, queryExpander,
            new RepositoryStore(ds), TestSpace,
            NullLogger<HybridQueryRouter>.Instance);

        // "AuthService" matches BOTH vector (exact embedding) AND BM25 (keyword in name)
        var results = await router.SearchAsync(
            new SearchRequest("AuthService", SearchType.Hybrid, Limit: 10));

        Assert.NotEmpty(results);
        // AuthService appears in both sources → RRF gives highest score
        Assert.Equal("R.AuthService", results[0].Fqn);
    }

    [Fact]
    public async Task Upsert_WithoutEmbedding_PreservesExistingVector()
    {
        // ADR-018 / finder: a re-upsert that carries NO embedding (skip-embeddings
        // space-mismatch path, or a transient embed failure that omits an FQN) must
        // PRESERVE the stored vector via COALESCE, not NULL-wipe it. Before the fix,
        // ON CONFLICT SET embedding = EXCLUDED.embedding destroyed good vectors.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "coalesce", "/coalesce");
        var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
        try
        {
            var emb = NormalizedEmbedding(0.42f);
            await vectorStore.UpsertAsync(
                [MakeClass("C.Service", "Service", repoId)],
                new Dictionary<string, float[]> { ["C.Service"] = emb });

            Assert.True(await HasEmbedding(ds, "C.Service"), "precondition: vector stored");

            // Re-upsert the SAME symbol with an EMPTY embeddings map (the skip path).
            await vectorStore.UpsertAsync(
                [MakeClass("C.Service", "Service", repoId)],
                new Dictionary<string, float[]>());

            Assert.True(await HasEmbedding(ds, "C.Service"),
                "vector must survive an embedding-less re-upsert (COALESCE), not be NULL-wiped");
        }
        finally
        {
            // The Postgres fixture is shared across this collection; drop this repo's rows
            // so the seeded vector can't pollute another test's unfiltered vector search.
            await vectorStore.DeleteByRepoAsync(repoId);
        }
    }

    private static async Task<bool> HasEmbedding(Npgsql.NpgsqlDataSource ds, string fqn)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedding IS NOT NULL FROM code_symbols WHERE fqn = @fqn";
        cmd.Parameters.AddWithValue("@fqn", fqn);
        return (bool)(await cmd.ExecuteScalarAsync() ?? false);
    }

    // --- Helpers ---

    private static ClassInfo MakeClass(string fqn, string name, Guid repoId) => new()
    {
        Fqn = fqn, Name = name, Kind = "class", FilePath = $"{name}.cs",
        StartLine = 1, EndLine = 10, RepoId = repoId
    };

    private static float[] NormalizedEmbedding(float seed)
    {
        var emb = new float[768];
        for (int i = 0; i < 768; i++) emb[i] = MathF.Sin(seed * (i + 1));
        var norm = MathF.Sqrt(emb.Sum(x => x * x));
        for (int i = 0; i < 768; i++) emb[i] /= norm;
        return emb;
    }

    private static async Task InsertSymbol(
        Npgsql.NpgsqlDataSource ds, string fqn, string name, string kind, string? signature, Guid repoId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO code_symbols (fqn, name, kind, signature, file_path, start_line, end_line, repo_id)
            VALUES (@fqn, @name, @kind, @sig, @fp, 1, 10, @rid)
            ON CONFLICT (fqn) DO UPDATE SET name = EXCLUDED.name
            """;
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@sig", (object?)signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fp", $"{name}.cs");
        cmd.Parameters.AddWithValue("@rid", repoId);
        await cmd.ExecuteNonQueryAsync();
    }
}
