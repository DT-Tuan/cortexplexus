using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests cho DotNetTools (GetDiRegistrations, GetEntityMapping, GetApiEndpoints,
/// GetDataFlow, GetConfigUsage, GetArchitecture, GetMiddlewarePipeline, GetCircularDependencies).
///
/// Phạm vi: TEST-PLAN.md #43, #44, #45, #46, #47, #48, #49, #50
///
/// Lưu ý: GetNuGetAudit dùng NuGetAuditAnalyzer real filesystem — test nhẹ nhàng.
/// </summary>
public class DotNetToolsTests
{
    // === #43: GetDiRegistrations_FiltersByRepo ===
    [Fact]
    public async Task GetDiRegistrations_WithRepositoryParameter_FiltersByFilePathContains()
    {
        // Mục đích (GH #3): repository được truyền → tool resolve repoId và truyền XUỐNG
        // query layer (scoping bằng repo_id), KHÔNG post-filter FilePath theo tên repo nữa.
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("DI:MyApp.IFooService", "AddScoped<IFooService>", "di_registration",
                    "services.AddScoped", "src/Startup.cs", 10, 1.0, "Graph:DI"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("myapp", repoId, "/workspace/myapp"));

        var result = await DotNetTools.GetDiRegistrations(null, "myapp", graphStore, compressor, repoStore);

        Assert.Contains("IFooService", result);
        // The resolved repoId is handed to the query (no fragile FilePath-substring filter).
        // Note relative path "src/Startup.cs" never contains "myapp" — old code returned empty.
        await graphStore.Received(1).QueryDiRegistrationsAsync(null, repoId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiRegistrations_NoRepositoryMultiRepo_ReturnsGuardAndSkipsQuery()
    {
        // GH #3 token-trap: omitting repository with >1 repo indexed must NOT dump all repos.
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("RepoA", Guid.NewGuid(), "/ws/a"),
            TestHelpers.MakeRepo("RepoB", Guid.NewGuid(), "/ws/b"));

        var result = await DotNetTools.GetDiRegistrations(null, null, graphStore, compressor, repoStore);

        Assert.Contains("repositories are indexed", result);
        Assert.Contains("RepoA", result);
        Assert.Contains("RepoB", result);
        await graphStore.DidNotReceive().QueryDiRegistrationsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiRegistrations_UnknownRepository_ReturnsNotFound()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("RepoA", Guid.NewGuid(), "/ws/a"));

        var result = await DotNetTools.GetDiRegistrations(null, "nope", graphStore, compressor, repoStore);

        Assert.Contains("not found", result);
        await graphStore.DidNotReceive().QueryDiRegistrationsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiRegistrations_ServiceTypeFilterNoRepo_QueriesAllReposUnscoped()
    {
        // A content filter (serviceType) keeps the result bounded, so the cross-repo
        // exact lookup must still work without forcing repository: (no guard).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("DI:X.IWebPushSender", "AddScoped<IWebPushSender>", "di_registration",
                    "services.AddScoped", "src/Program.cs", 61, 1.0, "Graph:DI"),
            ]));
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("RepoA", Guid.NewGuid(), "/ws/a"),
            TestHelpers.MakeRepo("RepoB", Guid.NewGuid(), "/ws/b"));

        var result = await DotNetTools.GetDiRegistrations("WebPushSender", null, graphStore, compressor, repoStore);

        Assert.Contains("IWebPushSender", result);
        await graphStore.Received(1).QueryDiRegistrationsAsync("WebPushSender", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiRegistrations_NoResults_ReturnsActionableMessage()
    {
        // Mục đích: Empty result → message guide AI index trước.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await DotNetTools.GetDiRegistrations(null, null, graphStore, compressor, repoStore);

        Assert.Contains("No DI registrations found", result);
        Assert.Contains("Index a project", result); // actionable hint
    }

    // === #44: GetApiEndpoints_ReturnsRouteAndMethod ===
    [Fact]
    public async Task GetApiEndpoints_WithResults_IncludesSignatureAndFileLine()
    {
        // Mục đích: Format output phải có signature + file:line để AI navigate source.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryApiEndpointsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("API:GET:/api/users", "GetUsers", "api_endpoint",
                    "GET /api/users", "/workspace/UserController.cs", 42, 1.0, "Graph:Endpoints"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await DotNetTools.GetApiEndpoints(null, null, graphStore, compressor, repoStore);

        Assert.Contains("GET /api/users", result);
        Assert.Contains("/workspace/UserController.cs:42", result);
    }

    // === #45: GetDataFlow_InvalidRoute_ReturnsEmpty ===
    [Fact]
    public async Task GetDataFlow_RouteNotFound_ReturnsClearMessage()
    {
        // Mục đích: Route không tồn tại → message rõ ràng với tên route để AI biết đã check nhầm.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDataFlowAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await DotNetTools.GetDataFlow("/api/ghost", graphStore, compressor);

        Assert.Contains("No data flow found", result);
        Assert.Contains("/api/ghost", result);
    }

    [Fact]
    public async Task GetDataFlow_WithHandlers_ShowsDownstreamCount()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDataFlowAsync("/api/users", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("App.UsersController.GetUsers()", "GetUsers", "method",
                    "GetUsers()", "/repo/UsersController.cs", 10, 1.0, "Graph:DataFlow"),
                new("App.UserService.FetchAll()", "FetchAll", "method",
                    "FetchAll()", "/repo/UserService.cs", 20, 1.0, "Graph:DataFlow"),
            ]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await DotNetTools.GetDataFlow("/api/users", graphStore, compressor);

        Assert.Contains("Data Flow for: /api/users", result);
        Assert.Contains("Downstream methods (2)", result);
    }

    // === #46: GetConfigUsage_CrossLanguage ===
    [Fact]
    public async Task GetConfigUsage_SplitsConfigKeysFromCodeReaders()
    {
        // Mục đích: Output phải chia 2 section:
        // 1. "Config Keys" (kind="config_key")
        // 2. "Code Reading This Config" (kind khác)
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryConfigUsageAsync("DATABASE_URL", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("config:DATABASE_URL", "DATABASE_URL", "config_key",
                    null, "/repo/.env", 1, 1.0, "Graph:Config"),
                new("App.DbContext", "DbContext", "class",
                    "class DbContext", "/repo/DbContext.cs", 10, 1.0, "Graph:ConfigReader"),
                new("App.Program.Main()", "Main", "method",
                    "Main()", "/repo/Program.py", 5, 1.0, "Graph:ConfigReader"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await DotNetTools.GetConfigUsage("DATABASE_URL", null, graphStore, compressor, repoStore);

        // Config key section
        Assert.Contains("Config Keys (1)", result);
        Assert.Contains("DATABASE_URL", result);
        // Code readers section (cross-language: C# + Python)
        Assert.Contains("Code Reading This Config (2)", result);
        Assert.Contains("DbContext", result);
        Assert.Contains("Main", result);
    }

    // === #47: GetArchitecture_GroupsByModule ===
    [Fact]
    public async Task GetArchitecture_GroupsDiRegistrationsByModule()
    {
        // Mục đích: DI registrations phải được group theo module
        // (ExtractModule lấy từ file path như "CortexPlexus.App").
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("DI:IFooService", "AddScoped", "di_registration", null,
                    "/workspace/src/CortexPlexus.App/Program.cs", 10, 1.0, "Graph:DI"),
                new("DI:IBarService", "AddSingleton", "di_registration", null,
                    "/workspace/src/CortexPlexus.Graph/Startup.cs", 20, 1.0, "Graph:DI"),
            ]));

        graphStore.QueryApiEndpointsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        graphStore.QueryEntityMappingsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await DotNetTools.GetArchitecture(null, graphStore, repoStore);

        Assert.Contains("Architecture Overview", result);
        Assert.Contains("DI Registrations (2)", result);
        // Modules phải được tách (ExtractModule tìm part có dot nhưng không .cs).
        Assert.Contains("CortexPlexus.App", result);
        Assert.Contains("CortexPlexus.Graph", result);
    }

    // === GH #3: GetArchitecture scopes every sub-query by the resolved repoId ===
    [Fact]
    public async Task GetArchitecture_WithRepository_ScopesSubQueriesByRepoId()
    {
        // Scoping is delegated to the query layer (repo_id), not post-filtered by FilePath.
        // Mocks return already-scoped data; we assert each sub-query got the resolved repoId.
        var cfId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryDiRegistrationsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                new("DI:CortexFlow.IService", "AddScoped", "di_registration", null,
                    "/workspace/cortexflow/Startup.cs", 10, 1.0, "Graph:DI"),
            ]));
        graphStore.QueryApiEndpointsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryEntityMappingsAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("CortexFlow", cfId, "/workspace/cortexflow"),
            TestHelpers.MakeRepo("CortexPlexus", Guid.NewGuid(), "/workspace/cortexplexus"));

        var result = await DotNetTools.GetArchitecture("CortexFlow", graphStore, repoStore);

        // Header lists only the scoped repo; scoped DI item shows.
        Assert.Contains("Repositories (1)", result);
        Assert.Contains("CortexFlow.IService", result);
        // Every sub-query received the resolved CortexFlow repoId.
        await graphStore.Received(1).QueryDiRegistrationsAsync(Arg.Any<string?>(), cfId, Arg.Any<CancellationToken>());
        await graphStore.Received(1).QueryApiEndpointsAsync(Arg.Any<string?>(), cfId, Arg.Any<CancellationToken>());
        await graphStore.Received(1).QueryEntityMappingsAsync(Arg.Any<string?>(), cfId, Arg.Any<CancellationToken>());
    }

    // === #48: GetMiddlewarePipeline_SortedByOrder ===
    [Fact]
    public async Task GetMiddlewarePipeline_MultipleMiddlewares_SortedByStartLine()
    {
        // Mục đích: Middleware phải trả về theo đúng thứ tự execution (sắp xếp theo StartLine).
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        // Tạo 3 middleware với StartLine theo thứ tự không tăng dần để kiểm tra sort.
        graphStore.GetGraphOverviewAsync(
                repoId,
                Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GraphOverview(
                [
                    new GraphNode("UseRouting", "UseRouting", "middleware", "app.UseRouting()", "/repo/Program.cs", 30),
                    new GraphNode("UseAuth", "UseAuth", "middleware", "app.UseAuthentication()", "/repo/Program.cs", 10),
                    new GraphNode("UseCors", "UseCors", "middleware", "app.UseCors()", "/repo/Program.cs", 20),
                ],
                [])));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await DotNetTools.GetMiddlewarePipeline("test-repo", graphStore, repoStore);

        Assert.Contains("Middleware Pipeline (3 stages)", result);

        // Thứ tự phải theo StartLine: UseAuth (10) → UseCors (20) → UseRouting (30)
        var authIdx = result.IndexOf("UseAuth", StringComparison.Ordinal);
        var corsIdx = result.IndexOf("UseCors", StringComparison.Ordinal);
        var routingIdx = result.IndexOf("UseRouting", StringComparison.Ordinal);

        Assert.True(authIdx > 0 && corsIdx > authIdx && routingIdx > corsIdx,
            $"Middleware phải sorted theo StartLine. Positions: auth={authIdx}, cors={corsIdx}, routing={routingIdx}");
    }

    [Fact]
    public async Task GetMiddlewarePipeline_NoRepos_ReturnsActionableMessage()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(); // empty

        var result = await DotNetTools.GetMiddlewarePipeline(null, graphStore, repoStore);

        Assert.Contains("No repositories found", result);
    }

    // === #49: GetCircularDeps_ReturnsAllCycles ===
    [Fact]
    public async Task GetCircularDependencies_MultipleCycles_ListsAll()
    {
        // Mục đích: Phải list TẤT CẢ cycles, không chỉ cycle đầu tiên.
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryCircularDependenciesAsync(repoId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([
                ["App.A", "App.B", "App.A"],
                ["App.C", "App.D", "App.E", "App.C"],
            ]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await DotNetTools.GetCircularDependencies("test-repo", graphStore, repoStore);

        Assert.Contains("2 cycles", result);
        Assert.Contains("Cycle 1: App.A → App.B → App.A", result);
        Assert.Contains("Cycle 2: App.C → App.D → App.E → App.C", result);
        // Advice phải có cho AI biết cách fix.
        Assert.Contains("breaking these cycles", result);
    }

    [Fact]
    public async Task GetCircularDependencies_NonExistentRepo_ReturnsNotFound()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(); // empty

        var result = await DotNetTools.GetCircularDependencies("ghost", graphStore, repoStore);

        Assert.Contains("Repository 'ghost' not found", result);
    }

    [Fact]
    public async Task GetCircularDependencies_NoCycles_ReturnsPositiveMessage()
    {
        // Mục đích: Không có cycle → message positive cho AI biết là healthy, không phải error.
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryCircularDependenciesAsync(repoId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("test-repo", repoId));

        var result = await DotNetTools.GetCircularDependencies("test-repo", graphStore, repoStore);

        Assert.Contains("No circular dependencies", result);
        Assert.Contains("acyclic", result); // rõ ràng là healthy state
    }

    // === #50: GetNuGetAudit_NoSolution_ReturnsEmpty ===
    [Fact]
    public void GetNuGetAudit_EmptyDirectory_ReturnsNoPackages()
    {
        // Mục đích: Directory không có .csproj → graceful empty message.
        // GetNuGetAudit là sync method, dùng NuGetAuditAnalyzer real.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = DotNetTools.GetNuGetAudit(tempDir);
            Assert.Contains("No NuGet packages found", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // === R20 Issue #3: Missing path must not throw ===
    [Fact]
    public void GetNuGetAudit_NonExistentPath_ReturnsHelpfulMessage()
    {
        // Before R20: Directory.GetFiles threw DirectoryNotFoundException which
        // surfaced as "An error occurred invoking 'get_nu_get_audit'". Reproduced
        // from user smoke test (path=/workspace/_agent/CortexFlow where only graph
        // metadata exists, no source files). After R20: return a message
        // explaining the limitation instead of throwing.
        var nonExistent = Path.Combine(Path.GetTempPath(), $"cortex-nope-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(nonExistent));

        var result = DotNetTools.GetNuGetAudit(nonExistent);

        Assert.Contains("does not exist on the server", result);
        Assert.Contains("agent-uploaded projects", result);
        // Must not throw
    }

    [Fact]
    public void NuGetAuditAnalyzer_NonExistentDirectory_ReturnsEmpty()
    {
        // Unit test at the analyzer level: MUST NOT throw on missing path.
        var analyzer = new CortexPlexus.Parsing.NuGetAuditAnalyzer();
        var nonExistent = Path.Combine(Path.GetTempPath(), $"cortex-nope-{Guid.NewGuid():N}");

        var result = analyzer.AnalyzeDirectory(nonExistent);

        Assert.Empty(result);
    }
}
