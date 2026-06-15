using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// AI Agent UX tests — verify output format từ MCP tool handlers nhất quán
/// và error messages đủ thông tin để AI tự sửa.
///
/// Phạm vi: TEST-PLAN.md #119 (ConsistentMarkdown), #120 (ActionableErrors),
///         #123 (ExploreTopic SufficientContext), #124 (OnboardProject CompletePicture)
/// </summary>
public class AiAgentUxTests
{
    // === #120: MCP_ErrorMessages_Actionable ===
    // Error messages phải chứa: (1) tên symbol/repo bị lookup, (2) hành động AI có thể thử tiếp.

    [Fact]
    public async Task GetCallers_NotFound_ErrorMentionsFqnSearched()
    {
        // Mục đích: Error chứa FQN AI đã search → AI biết đã try đúng symbol nhưng empty.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers("App.Domain.Service", 1, graphStore, compressor);

        Assert.Contains("App.Domain.Service", result);
        Assert.Contains("No callers found", result);
    }

    [Fact]
    public async Task GetDeadCode_RepoNotFound_ErrorMentionsListRepositories()
    {
        // Mục đích: Error guide AI tới tool ListRepositories → biết phải làm gì tiếp.
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore(); // empty

        var result = await GraphTraversalTools.GetDeadCode("ghost", graphStore, compressor, repoStore);

        Assert.Contains("Repository 'ghost' not found", result); // (1) tên đã try
        Assert.Contains("ListRepositories", result); // (2) next action
    }

    [Fact]
    public async Task GetDataFlow_RouteNotFound_ErrorMentionsRoute()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDataFlowAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await DotNetTools.GetDataFlow("/api/ghost", graphStore, compressor);

        Assert.Contains("/api/ghost", result);
        Assert.Contains("No data flow found", result);
    }

    [Fact]
    public async Task GetTestCoverage_NoResults_ErrorIncludesActionableHint()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryTestCoverageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetTestCoverage("App.Service.DoWork()", graphStore, compressor);

        Assert.Contains("No tests found", result);
        // Hints về possible reasons để AI biết next steps.
        Assert.Contains("may not", result); // "may not have tests, or test detection..."
    }

    [Fact]
    public async Task GetCircularDependencies_NoCycles_ReturnsHealthyMessage()
    {
        // Mục đích: Empty result cho circular deps là tin tốt — message phải reflect rõ.
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCircularDependenciesAsync(repoId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await DotNetTools.GetCircularDependencies("test-repo", graphStore, repoStore);

        Assert.Contains("No circular dependencies", result);
        Assert.Contains("acyclic", result); // healthy state, không phải error
    }

    // === #119: MCP_ResultFormat_ConsistentMarkdown ===
    [Fact]
    public async Task GetCallers_WithResults_FormatsConsistently()
    {
        // Mục đích: Output có "[kind] FQN" pattern (từ ContextCompressor).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Caller", "Caller", "method"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers("App.Target", 1, graphStore, compressor);

        Assert.Contains("[method]", result);
        Assert.Contains("App.Caller", result);
    }

    [Fact]
    public async Task GetImpactAnalysis_FormatHasMarkdownHeadings()
    {
        // Mục đích: Output có markdown-like section headings ("--- Callers ---" etc.)
        // → AI có thể parse output sections riêng biệt.
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Caller", "Caller"),
            ]));
        graphStore.QueryImplementationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Impl", "Impl", "class"),
            ]));
        graphStore.QueryClassHierarchyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, graphStore: graphStore, compressor: compressor);

        Assert.Contains("Impact analysis for: App.Target", result);
        Assert.Contains("--- Callers", result);
        Assert.Contains("--- Implementations", result);
    }

    // === #123: MCP_ExploreTopic_SufficientContext ===
    [Fact]
    public async Task ExploreTopic_NormalDepth_OutputContainsSearchResultsAndCallers()
    {
        // Mục đích: depth="normal" output phải có cả search section + callers + dependencies
        // (đủ context cho AI hiểu architecture của symbol).
        var fullText = Substitute.For<IFullTextStore>();
        fullText.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Service", "Service", "class"),
            ]));

        var vector = Substitute.For<IVectorStore>();
        vector.SearchAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));

        var router = TestHelpers.BuildRouter(
            vectorStore: vector, fullTextStore: fullText, embeddingService: embedding);

        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Controller", "Controller"),
            ]));
        graphStore.QueryDependenciesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.IRepository", "IRepository", "interface"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await ExploreTools.ExploreTopic(
            query: "Service", repository: null, depth: "normal",
            router: router, graphStore: graphStore, compressor: compressor, repoStore: repoStore);

        // 4 sections cho 1 single tool call → AI nhận được context đủ.
        Assert.Contains("## Exploration: Service", result);
        Assert.Contains("### Search Results", result);
        Assert.Contains("### Deep Dive: App.Service", result);
        Assert.Contains("**Callers", result);
        Assert.Contains("**Dependencies", result);
        Assert.Contains("App.Controller", result);
        Assert.Contains("App.IRepository", result);
    }

    // === #124: MCP_OnboardProject_CompletePicture ===
    [Fact]
    public async Task OnboardProject_CompletePicture_HasAllSections()
    {
        // Mục đích: OnboardProject output phải có ĐỦ DI + Endpoints + Entities
        // (4 trong 1 — composite tool replace 4 individual calls).
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("DI:IFoo", "AddScoped", "di_registration", null,
                    "/workspace/test-repo/Startup.cs", 1, 1.0, "DI"),
            ]));

        graphStore.QueryApiEndpointsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("API:GET:/api/foo", "GetFoo", "api_endpoint", "GET /api/foo",
                    "/workspace/test-repo/FooController.cs", 1, 1.0, "API"),
            ]));

        graphStore.QueryEntityMappingsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("App.Models.Foo", "Foo", "class", null,
                    "/workspace/test-repo/Models/Foo.cs", 1, 1.0, "Entity"),
            ]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await ExploreTools.OnboardProject("test-repo", graphStore, repoStore);

        // Title section
        Assert.Contains("# test-repo", result);
        Assert.Contains("Path:", result);

        // 3 component sections
        Assert.Contains("## DI Registrations", result);
        Assert.Contains("## API Endpoints", result);
        Assert.Contains("## EF Core Entities", result);

        // Concrete data từ mỗi section (FQN, không phải Name)
        Assert.Contains("DI:IFoo", result);
        Assert.Contains("GET /api/foo", result);
        Assert.Contains("Foo", result);
    }

    // === #122: MCP_SemanticVsBm25_Complementary (proxy test via routing) ===
    [Fact]
    public async Task SearchCode_BM25_DoesNotCallEmbeddingService()
    {
        // Mục đích: SearchCode dùng BM25 → KHÔNG cần embedding service.
        // Verify routing không waste API call cho exact-name search.
        var fullText = Substitute.For<IFullTextStore>();
        fullText.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));

        var router = TestHelpers.BuildRouter(fullTextStore: fullText, embeddingService: embedding);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        await SearchTools.SearchCode("PaymentService", null, 20, false, router, compressor, repoStore);

        // BM25 search → KHÔNG gọi embedding → tiết kiệm API quota.
        await embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fullText.Received().SearchAsync(
            "PaymentService", Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
