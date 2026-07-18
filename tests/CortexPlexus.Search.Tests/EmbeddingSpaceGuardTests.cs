using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CortexPlexus.Search.Tests;

/// <summary>
/// ADR-018 read-path guards in HybridQueryRouter: the vector leg must be skipped or
/// space-filtered when repo stamps don't match the server's current embedding space,
/// with the exact footer texts tools surface to the agent. Legacy NULL stamps keep
/// today's behavior (vector leg runs) plus an "unknown" hint.
/// </summary>
public sealed class EmbeddingSpaceGuardTests
{
    private static readonly EmbeddingSpace Current = new("vertex", "text-embedding-005", 768);

    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IFullTextStore _fullTextStore = Substitute.For<IFullTextStore>();
    private readonly IEmbeddingService _embeddingService = Substitute.For<IEmbeddingService>();
    private readonly IQueryExpander _queryExpander = Substitute.For<IQueryExpander>();
    private readonly IRepositoryStore _repositoryStore = Substitute.For<IRepositoryStore>();

    public EmbeddingSpaceGuardTests()
    {
        _queryExpander.IsEnabled.Returns(false);
        _embeddingService.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f });
        _vectorStore.SearchAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        _fullTextStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
    }

    private HybridQueryRouter CreateRouter(params RepositoryInfo[] repos)
    {
        _repositoryStore.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>(repos.ToList()));
        return new HybridQueryRouter(
            _vectorStore, _fullTextStore, _embeddingService, _queryExpander,
            _repositoryStore, Current, NullLogger<HybridQueryRouter>.Instance);
    }

    private static RepositoryInfo Repo(
        string name, Guid id, string? provider, string? model, int? dim = 768)
        => new(id, name, $"/test/{name}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
               provider, model, provider is null ? null : dim);

    [Fact]
    public async Task SingleRepo_SpaceMismatch_SkipsVectorLeg_WithExactFooter()
    {
        var repoId = Guid.NewGuid();
        var router = CreateRouter(Repo("iTAS", repoId, "ollama", "nomic-embed-text"));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("payment flow logic", SearchType.Hybrid, RepoId: repoId));

        Assert.Equal(
            "⚠️ semantic leg skipped: repo carries ollama:nomic-embed-text vectors, " +
            "server queries with vertex:text-embedding-005. Re-index to migrate.",
            outcome.SpaceFooter);
        // The space-sensitive leg must never have touched the vector store.
        await _vectorStore.DidNotReceive().SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // BM25 leg still ran — degraded, not dead.
        await _fullTextStore.Received().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), repoId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleRepo_NullStamp_VectorLegRuns_WithUnknownHint()
    {
        var repoId = Guid.NewGuid();
        var router = CreateRouter(Repo("legacy", repoId, provider: null, model: null));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("payment flow logic", SearchType.Hybrid, RepoId: repoId));

        Assert.Equal("space unknown — stamp by re-indexing", outcome.SpaceFooter);
        await _vectorStore.Received().SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), repoId, Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleRepo_SpaceMatch_NoFooter()
    {
        var repoId = Guid.NewGuid();
        var router = CreateRouter(Repo("fresh", repoId, "vertex", "text-embedding-005"));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("payment flow logic", SearchType.Hybrid, RepoId: repoId));

        Assert.Null(outcome.SpaceFooter);
    }

    [Fact]
    public async Task CrossRepo_MismatchedRepos_ExcludedViaFilter_AndListedInFooter()
    {
        var router = CreateRouter(
            Repo("fresh", Guid.NewGuid(), "vertex", "text-embedding-005"),
            Repo("iTAS", Guid.NewGuid(), "ollama", "nomic-embed-text"),
            Repo("OpsFlow", Guid.NewGuid(), "ollama", "nomic-embed-text"),
            Repo("legacy", Guid.NewGuid(), provider: null, model: null));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("payment flow logic", SearchType.Hybrid, RepoId: null));

        Assert.Equal(
            "2 repo(s) excluded from semantic ranking (space mismatch: iTAS, OpsFlow)",
            outcome.SpaceFooter);
        // Vector leg ran WITH the space filter of the current space.
        await _vectorStore.Received().SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), null, Arg.Any<string?>(),
            "vertex", "text-embedding-005", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrossRepo_AllMatchedOrUnknown_NoFooter()
    {
        var router = CreateRouter(
            Repo("fresh", Guid.NewGuid(), "vertex", "text-embedding-005"),
            Repo("legacy", Guid.NewGuid(), provider: null, model: null));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("payment flow logic", SearchType.Hybrid, RepoId: null));

        Assert.Null(outcome.SpaceFooter);
    }

    [Fact]
    public async Task Bm25Query_NeverConsultsRepoStamps()
    {
        // BM25/graph legs are space-insensitive — the plan (and its repo-list DB hit)
        // must not run at all for a query classified away from the vector leg.
        var router = CreateRouter(Repo("iTAS", Guid.NewGuid(), "ollama", "nomic-embed-text"));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("MyApp.OrderService", SearchType.Bm25));

        Assert.Null(outcome.SpaceFooter);
        await _repositoryStore.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PureVectorMode_Mismatch_DegradesToBm25()
    {
        var repoId = Guid.NewGuid();
        var expected = new List<SearchResult>
        {
            new("A.B", "B", "method", "B()", "/r/B.cs", 1, 0.9, "FullText"),
        };
        _fullTextStore.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), repoId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(expected);
        var router = CreateRouter(Repo("iTAS", repoId, "ollama", "nomic-embed-text"));

        var outcome = await router.SearchWithFooterAsync(
            new SearchRequest("payment", SearchType.Vector, RepoId: repoId));

        Assert.Single(outcome.Results);
        Assert.Equal("A.B", outcome.Results[0].Fqn);
        Assert.NotNull(outcome.SpaceFooter);
        Assert.StartsWith("⚠️ semantic leg skipped", outcome.SpaceFooter);
    }
}
