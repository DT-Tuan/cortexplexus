using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CortexPlexus.Search.Tests;

public sealed class HybridQueryRouterTests
{
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IFullTextStore _fullTextStore = Substitute.For<IFullTextStore>();
    private readonly IEmbeddingService _embeddingService = Substitute.For<IEmbeddingService>();
    private readonly IQueryExpander _queryExpander = Substitute.For<IQueryExpander>();
    private readonly IRepositoryStore _repositoryStore = Substitute.For<IRepositoryStore>();
    private readonly HybridQueryRouter _router;

    public HybridQueryRouterTests()
    {
        // Default: query expansion disabled
        _queryExpander.IsEnabled.Returns(false);
        _repositoryStore.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>([]));

        _router = CreateRouter();
    }

    private static readonly EmbeddingSpace TestSpace = new("vertex", "text-embedding-005", 768);

    private HybridQueryRouter CreateRouter(
        EmbeddingSpace? currentSpace = null,
        IRepositoryStore? repoStore = null)
    {
        return new HybridQueryRouter(
            _vectorStore, _fullTextStore, _embeddingService, _queryExpander,
            repoStore ?? _repositoryStore,
            currentSpace ?? TestSpace,
            NullLogger<HybridQueryRouter>.Instance);
    }

    [Fact]
    public async Task SearchAsync_Bm25Type_CallsFullTextOnly()
    {
        var expected = new List<SearchResult> { MakeResult("A") };
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var results = await _router.SearchAsync(new SearchRequest("OrderService", SearchType.Bm25));

        Assert.Single(results);
        Assert.Equal("A", results[0].Fqn);
        await _vectorStore.DidNotReceive().SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_VectorType_CallsEmbeddingAndVectorStore()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(embedding);
        _vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("B") });

        var results = await _router.SearchAsync(new SearchRequest("payment logic", SearchType.Vector));

        Assert.Single(results);
        await _embeddingService.Received(1).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_EmptyEmbedding_ReturnsEmptyForVector()
    {
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());

        var results = await _router.SearchAsync(new SearchRequest("test", SearchType.Vector));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_HybridWithExactFqn_RoutesBm25()
    {
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("MyApp.OrderService") });

        // Exact FQN pattern (dots, no spaces) → should route to BM25
        var results = await _router.SearchAsync(new SearchRequest("MyApp.OrderService"));

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_ExpandDisabled_DoesNotCallExpander()
    {
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await _router.SearchAsync(new SearchRequest("test", Expand: false));

        await _queryExpander.DidNotReceive().ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queryExpander.DidNotReceive().ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_ExpandEnabled_CallsHydeAndMultiQuery()
    {
        _queryExpander.IsEnabled.Returns(true);
        _queryExpander.ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical document about payment processing");
        _queryExpander.ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "payment processing", "how to handle payments", "payment service implementation" });

        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("PaymentService") });
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("PaymentHandler") });

        // Use Hybrid type to trigger full hybrid path (query will be classified as Hybrid since "payment processing" has spaces)
        var results = await _router.SearchAsync(new SearchRequest("payment processing", SearchType.Hybrid, Expand: true));

        Assert.NotEmpty(results);
        await _queryExpander.Received(1).ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _queryExpander.Received(1).ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_ExpandRequestedButExpanderDisabled_SkipsExpansion()
    {
        _queryExpander.IsEnabled.Returns(false);
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await _router.SearchAsync(new SearchRequest("test", Expand: true));

        // Should NOT call expander since it's disabled
        await _queryExpander.DidNotReceive().ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_VectorWithExpand_UsesHydeForEmbedding()
    {
        _queryExpander.IsEnabled.Returns(true);
        _queryExpander.ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("expanded hypothetical text");
        _embeddingService.EmbedAsync("expanded hypothetical text", Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.5f });
        _vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("Found") });

        var results = await _router.SearchAsync(new SearchRequest("test", SearchType.Vector, Expand: true));

        Assert.Single(results);
        // Should embed the hypothetical text, not the original query
        await _embeddingService.Received(1).EmbedAsync("expanded hypothetical text", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_HydeReturnsNull_FallsBackToOriginalQuery()
    {
        _queryExpander.IsEnabled.Returns(true);
        _queryExpander.ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _queryExpander.ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "test" });

        _embeddingService.EmbedAsync("test", Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        // Should not throw — gracefully falls back to original query
        var results = await _router.SearchAsync(new SearchRequest("test", SearchType.Hybrid, Expand: true));

        // Falls back to embedding the original query "test"
        await _embeddingService.Received(1).EmbedAsync("test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_VectorType_EmbeddingFails_FallsBackToBm25()
    {
        // Embedding returns empty (simulates Gemini 429 rate limit)
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<float>());
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("FallbackResult") });

        var results = await _router.SearchAsync(new SearchRequest("test query", SearchType.Vector));

        // Should fallback to BM25 instead of returning empty
        Assert.Single(results);
        Assert.Equal("FallbackResult", results[0].Fqn);
    }

    [Fact]
    public async Task SearchAsync_VectorType_EmbeddingThrows_FallsBackToBm25()
    {
        // Embedding throws (simulates network error)
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<float[]>(_ => throw new HttpRequestException("Connection refused"));
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("FallbackResult") });

        var results = await _router.SearchAsync(new SearchRequest("test query", SearchType.Vector));

        Assert.Single(results);
        Assert.Equal("FallbackResult", results[0].Fqn);
    }

    [Fact]
    public async Task SearchAsync_Hybrid_EmbeddingFails_StillReturnsBm25Results()
    {
        // Embedding returns empty — hybrid should still return BM25 results
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<float>());
        _fullTextStore.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { MakeResult("Bm25Only") });

        var results = await _router.SearchAsync(new SearchRequest("some query", SearchType.Hybrid));

        Assert.NotEmpty(results);
        Assert.Equal("Bm25Only", results[0].Fqn);
    }

    private static SearchResult MakeResult(string fqn) =>
        new(fqn, fqn, "class", null, "test.cs", 1, 1.0, "test");
}
