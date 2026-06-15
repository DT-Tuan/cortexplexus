using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests cho ExploreTools (ExploreTopic, OnboardProject).
///
/// Phạm vi: TEST-PLAN.md #51, #52, #53, #54, #55, #56
///
/// ExploreTopic là composite tool — replace 5+ individual tool calls.
/// Quan trọng test depth semantics + fallback logic.
/// </summary>
public class ExploreToolsTests
{
    // Helper: tạo mock graph store với tất cả queries return empty (baseline).
    private static IGraphStore CreateEmptyGraphStore()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryCalleesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryDependenciesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryImplementationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        return graphStore;
    }

    // Helper: build router trả search results theo ý.
    private static (HybridQueryRouter router, IFullTextStore fullText, IVectorStore vector)
        BuildRouterWithResults(IReadOnlyList<SearchResult> bm25, IReadOnlyList<SearchResult>? hybrid = null)
    {
        var fullText = Substitute.For<IFullTextStore>();
        fullText.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(bm25));

        var vector = Substitute.For<IVectorStore>();
        vector.SearchAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(hybrid ?? bm25));

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));

        var router = TestHelpers.BuildRouter(
            vectorStore: vector,
            fullTextStore: fullText,
            embeddingService: embedding);

        return (router, fullText, vector);
    }

    // === #51: ExploreTopic_Shallow_SearchOnly ===
    [Fact]
    public async Task ExploreTopic_DepthShallow_OnlyCallsSearch_NoGraphQueries()
    {
        // Mục đích: depth="shallow" chỉ search, KHÔNG gọi bất kỳ graph query nào.
        var (router, _, _) = BuildRouterWithResults([
            TestHelpers.MakeResult("App.FooService", "FooService", "class", "Search"),
        ]);

        var graphStore = CreateEmptyGraphStore();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            "FooService", null, "shallow",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        // Phải có search results section.
        Assert.Contains("Exploration: FooService", result);
        Assert.Contains("Search Results", result);
        Assert.DoesNotContain("Deep Dive", result);

        // Graph store không được gọi.
        await graphStore.DidNotReceive().QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await graphStore.DidNotReceive().QueryDependenciesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // === #52: ExploreTopic_Deep_AllPhases ===
    [Fact]
    public async Task ExploreTopic_DepthDeep_CallsAllGraphQueries()
    {
        // Mục đích: depth="deep" chạy đầy đủ: search + callers + deps + callees + impls + references.
        var (router, _, _) = BuildRouterWithResults([
            TestHelpers.MakeResult("App.FooService", "FooService", "class", "Search"),
        ]);

        var graphStore = CreateEmptyGraphStore();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        await ExploreTools.ExploreTopic(
            "FooService", null, "deep",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        // Tất cả 5 graph queries phải được gọi.
        await graphStore.Received().QueryCallersAsync("App.FooService", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await graphStore.Received().QueryDependenciesAsync("App.FooService", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await graphStore.Received().QueryCalleesAsync("App.FooService", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await graphStore.Received().QueryImplementationsAsync("App.FooService", Arg.Any<CancellationToken>());
        await graphStore.Received().QueryReferencedByAsync("App.FooService", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExploreTopic_DepthNormal_CallsOnlyCallersAndDependencies()
    {
        // Mục đích: depth="normal" = search + callers + deps (KHÔNG callees/impls/references).
        var (router, _, _) = BuildRouterWithResults([
            TestHelpers.MakeResult("App.FooService", "FooService", "class", "Search"),
        ]);

        var graphStore = CreateEmptyGraphStore();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        await ExploreTools.ExploreTopic(
            "FooService", null, "normal",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        // Callers + Dependencies phải được gọi.
        await graphStore.Received().QueryCallersAsync("App.FooService", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await graphStore.Received().QueryDependenciesAsync("App.FooService", Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Callees/impls/references KHÔNG được gọi ở normal depth.
        await graphStore.DidNotReceive().QueryCalleesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await graphStore.DidNotReceive().QueryImplementationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await graphStore.DidNotReceive().QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // === #53: ExploreTopic_HybridEmpty_FallbackBm25 ===
    [Fact]
    public async Task ExploreTopic_InitialSearchEmpty_FallsBackToBm25()
    {
        // Mục đích: Nếu Hybrid search trả empty → fallback sang BM25 với limit cao hơn (20).
        // Router.SearchAsync được gọi 2 lần với type khác nhau.
        var fullText = Substitute.For<IFullTextStore>();

        // Call 1 (Hybrid): empty
        // Call 2 (BM25 fallback): 1 result
        var callCount = 0;
        fullText.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult<IReadOnlyList<SearchResult>>([]);
                return Task.FromResult<IReadOnlyList<SearchResult>>([
                    TestHelpers.MakeResult("App.FooFound", "FooFound", "class", "Bm25Fallback"),
                ]);
            });

        var vector = Substitute.For<IVectorStore>();
        vector.SearchAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));

        var router = TestHelpers.BuildRouter(
            vectorStore: vector,
            fullTextStore: fullText,
            embeddingService: embedding);

        var graphStore = CreateEmptyGraphStore();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            "unknown query", null, "shallow",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        // Sau fallback phải có result.
        Assert.Contains("FooFound", result);
        Assert.DoesNotContain("No code found", result);
    }

    [Fact]
    public async Task ExploreTopic_AllSearchesEmpty_ReturnsNotFoundGuidance()
    {
        // Mục đích: Search trả 0 + fallback BM25 cũng 0 → message guide AI.
        var (router, _, _) = BuildRouterWithResults([]);

        var graphStore = CreateEmptyGraphStore();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            "ghost symbol", null, "normal",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        Assert.Contains("No code found for 'ghost symbol'", result);
        Assert.Contains("more specific query", result); // actionable hint
    }

    // === #54: ExploreTopic_FilterDocSymbols ===
    [Fact]
    public async Task ExploreTopic_TopResultIsDoc_SkipsDeepDive()
    {
        // Mục đích: Nếu top result là document/section (không phải code symbol),
        // tool KHÔNG đi vào deep dive — chỉ show search results.
        var (router, _, _) = BuildRouterWithResults([
            TestHelpers.MakeResult("doc:README", "README", "document", "Search"),
            TestHelpers.MakeResult("doc:README#setup", "Setup", "section", "Search"),
        ]);

        var graphStore = CreateEmptyGraphStore();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            "README", null, "deep",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        // Có search section nhưng KHÔNG có "Deep Dive" (vì không có code symbol để explore).
        Assert.Contains("Search Results", result);
        Assert.DoesNotContain("Deep Dive", result);

        // Graph queries không được gọi vì top non-doc symbol là null.
        await graphStore.DidNotReceive().QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // === #55: OnboardProject_NonExistentRepo_ReturnsError ===
    [Fact]
    public async Task OnboardProject_RepoNotFound_ReturnsClearErrorWithHint()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(); // empty

        var result = await ExploreTools.OnboardProject("nonexistent", graphStore, repoStore);

        Assert.Contains("Repository 'nonexistent' not found", result);
        Assert.Contains("ListRepositories", result); // guide AI đến tool đúng
    }

    // === #56: OnboardProject_CompleteOutput ===
    [Fact]
    public async Task OnboardProject_ValidRepo_IncludesDiEndpointsEntities()
    {
        // Mục đích: Output phải có overview đầy đủ: Path, Last indexed, DI, Endpoints, Entities.
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("DI:IFooService", "AddScoped", "di_registration", null,
                    "/workspace/test-repo/Startup.cs", 10, 1.0, "Graph:DI"),
            ]));

        graphStore.QueryApiEndpointsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("API:GET:/api/foo", "GetFoo", "api_endpoint", "GET /api/foo",
                    "/workspace/test-repo/Controllers/FooController.cs", 15, 1.0, "Graph:Endpoints"),
            ]));

        graphStore.QueryEntityMappingsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("App.Models.User", "User", "class", null,
                    "/workspace/test-repo/Models/User.cs", 5, 1.0, "Graph:EntityMapping"),
            ]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await ExploreTools.OnboardProject("test-repo", graphStore, repoStore);

        // Header section
        Assert.Contains("# test-repo — Project Overview", result);
        Assert.Contains("Path:", result);
        Assert.Contains("Last indexed:", result);

        // All 3 sections
        Assert.Contains("DI Registrations (1)", result);
        Assert.Contains("IFooService", result);
        Assert.Contains("API Endpoints (1)", result);
        Assert.Contains("GET /api/foo", result);
        Assert.Contains("EF Core Entities (1)", result);
        Assert.Contains("User", result);
    }

    // === R22 Fix #11: ExploreTopic missing query returns friendly error ===
    [Fact]
    public async Task ExploreTopic_MissingQuery_ReturnsFriendlyError()
    {
        // Before R22: SDK threw 500 because `query` was a non-nullable string with no default.
        // After R22: nullable + explicit validation returns helpful message + example.
        var graphStore = CreateEmptyGraphStore();
        var (router, _, _) = BuildRouterWithResults([]);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            query: null,
            repository: null,
            depth: "normal",
            router: router,
            graphStore: graphStore,
            compressor: compressor,
            repoStore: repoStore);

        Assert.Contains("Missing required parameter", result);
        Assert.Contains("'query'", result);
        Assert.Contains("Example:", result);
    }

    [Fact]
    public async Task ExploreTopic_EmptyQuery_ReturnsFriendlyError()
    {
        // Whitespace-only also counts as missing.
        var graphStore = CreateEmptyGraphStore();
        var (router, _, _) = BuildRouterWithResults([]);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            query: "   ",
            repository: null,
            depth: "normal",
            router: router,
            graphStore: graphStore,
            compressor: compressor,
            repoStore: repoStore);

        Assert.Contains("Missing required parameter", result);
    }
}
