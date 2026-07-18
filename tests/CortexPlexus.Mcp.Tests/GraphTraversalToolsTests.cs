using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests cho GraphTraversalTools (GetCallers, GetCallees, GetDependencies,
/// GetImplementations, GetClassHierarchy, GetImpactAnalysis, GetTestCoverage, GetDeadCode).
///
/// Phạm vi: TEST-PLAN.md #34, #35, #36, #37, #38, #39, #40, #41, #42
/// </summary>
public class GraphTraversalToolsTests
{
    // === #34: GetCallers_Depth0_ClampedTo1 ===
    [Fact]
    public async Task GetCallers_Depth0_IsClampedToMinimum1()
    {
        // Mục đích: depth=0 → auto clamp thành 1 (Math.Clamp(depth, 1, 5)).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        await GraphTraversalTools.GetCallers("App.Foo()", depth: 0, graphStore, compressor);

        // Verify được gọi với depth=1, không phải 0.
        await graphStore.Received().QueryCallersAsync(
            "App.Foo()",
            1,
            Arg.Any<CancellationToken>());
    }

    // === #35: GetCallers_Depth99_ClampedTo5 ===
    [Fact]
    public async Task GetCallers_Depth99_IsClampedToMaximum5()
    {
        // Mục đích: depth=99 → auto clamp thành 5 (upper bound).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        await GraphTraversalTools.GetCallers("App.Foo()", depth: 99, graphStore, compressor);

        await graphStore.Received().QueryCallersAsync(
            "App.Foo()",
            5,
            Arg.Any<CancellationToken>());
    }

    // === #35b: GetCallers_NegativeDepth_ClampedTo1 ===
    [Fact]
    public async Task GetCallers_NegativeDepth_IsClampedToMinimum1()
    {
        // Mục đích: depth=-5 → clamp thành 1 (không crash).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        await GraphTraversalTools.GetCallers("App.Foo()", depth: -5, graphStore, compressor);

        await graphStore.Received().QueryCallersAsync(
            "App.Foo()",
            1,
            Arg.Any<CancellationToken>());
    }

    // === #35c: GetDependencies_Depth5_ClampedTo3 ===
    [Fact]
    public async Task GetDependencies_Depth5_IsClampedToMaximum3()
    {
        // Mục đích: GetDependencies có depth limit riêng (1-3, khác call chains 1-5).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDependenciesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        await GraphTraversalTools.GetDependencies("App.Foo", depth: 5, graphStore, compressor);

        await graphStore.Received().QueryDependenciesAsync(
            "App.Foo",
            3,
            Arg.Any<CancellationToken>());
    }

    // === #37: GetCallers_NonExistentFqn_ReturnsNotFound ===
    [Fact]
    public async Task GetCallers_NoResults_ReturnsNotFoundMessage()
    {
        // Mục đích: FQN không có caller → trả message rõ ràng (không crash, không empty string).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetCallers("App.Ghost.Method()", 1, graphStore, compressor);

        Assert.Contains("No callers found", result);
        Assert.Contains("App.Ghost.Method()", result);
    }

    // === #38: GetImpactAnalysis_CombinesAllSources ===
    [Fact]
    public async Task GetImpactAnalysis_CombinesCallersImplementationsHierarchyReferences()
    {
        // Mục đích: Impact analysis phải gọi tất cả 4 sources (callers + implementations
        // + hierarchy + references) và gộp kết quả.
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
                [TestHelpers.MakeResult("App.Caller", "Caller")]));

        graphStore.QueryImplementationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
                [TestHelpers.MakeResult("App.Impl", "Impl", "class")]));

        graphStore.QueryClassHierarchyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
                [TestHelpers.MakeResult("App.BaseClass", "BaseClass", "class")]));

        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
                [TestHelpers.MakeResult("App.Ref", "Ref")]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, graphStore: graphStore, compressor: compressor);

        // Verify tất cả 4 queries được gọi.
        await graphStore.Received().QueryCallersAsync("App.Target", 2, Arg.Any<CancellationToken>());
        await graphStore.Received().QueryImplementationsAsync("App.Target", Arg.Any<CancellationToken>());
        await graphStore.Received().QueryClassHierarchyAsync("App.Target", Arg.Any<CancellationToken>());
        await graphStore.Received().QueryReferencedByAsync("App.Target", 2, Arg.Any<CancellationToken>());

        // Output phải chứa các sections.
        Assert.Contains("Impact analysis for: App.Target", result);
        Assert.Contains("Callers", result);
        Assert.Contains("Implementations", result);
        Assert.Contains("Class Hierarchy", result);
        Assert.Contains("Referenced By", result);
    }

    // === #39: GetImpactAnalysis_DeduplicatesAcrossSources ===
    [Fact]
    public async Task GetImpactAnalysis_SameFqnInMultipleSources_CountsOnce()
    {
        // Mục đích: Cùng FQN xuất hiện trong callers VÀ hierarchy → total count chỉ +1.
        var graphStore = Substitute.For<IGraphStore>();

        var dupFqn = TestHelpers.MakeResult("App.DuplicateSymbol", "DuplicateSymbol");

        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([dupFqn]));

        graphStore.QueryImplementationsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        graphStore.QueryClassHierarchyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([dupFqn])); // duplicate

        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, graphStore: graphStore, compressor: compressor);

        // Total unique affected = 1 (dedupe by FQN).
        Assert.Contains("Total affected symbols: 1", result);
    }

    // === #40: GetClassHierarchy_FiltersFrameworkTypes ===
    [Fact]
    public async Task GetClassHierarchy_FiltersSystemAndMicrosoftTypes()
    {
        // Mục đích: System.*, Microsoft.* phải bị filter để AI agent không bị noise.
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryClassHierarchyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("System.Object", "Object", "class"),
                TestHelpers.MakeResult("Microsoft.Extensions.Logging.ILogger", "ILogger", "interface"),
                TestHelpers.MakeResult("App.Domain.Entity", "Entity", "class"),
            ]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetClassHierarchy("App.Domain.Entity", graphStore, compressor);

        // Framework types phải bị loại.
        Assert.DoesNotContain("System.Object", result);
        Assert.DoesNotContain("Microsoft.Extensions.Logging.ILogger", result);
        // User code phải còn.
        Assert.Contains("App.Domain.Entity", result);
    }

    // === #42: GetTestCoverage_ReturnsTestsForMethod ===
    [Fact]
    public async Task GetTestCoverage_WithResults_FormatsWithHeading()
    {
        // Mục đích: Trả đúng format "Tests covering 'FQN':" + danh sách tests.
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryTestCoverageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Tests.FooTests.TestFoo", "TestFoo"),
                TestHelpers.MakeResult("App.Tests.FooTests.TestFoo2", "TestFoo2"),
            ]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetTestCoverage("App.Production.Foo()", graphStore, compressor);

        Assert.Contains("Tests covering 'App.Production.Foo()'", result);
        Assert.Contains("TestFoo", result);
    }

    [Fact]
    public async Task GetTestCoverage_NoResults_ReturnsGuidance()
    {
        // Mục đích: Không có test cover → message actionable cho AI agent.
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryTestCoverageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetTestCoverage("App.Production.Foo()", graphStore, compressor);

        Assert.Contains("No tests found", result);
        // Message phải guide AI (gợi ý nguyên nhân).
        Assert.Contains("may not have tests", result);
    }

    // === #41: GetDeadCode_FiltersTestMethods ===
    // Lưu ý: filtering test methods được thực hiện ở tầng QueryDeadCodeAsync
    // (đã test ở Graph.Tests). MCP tool chỉ format output.
    [Fact]
    public async Task GetDeadCode_NonExistentRepo_ReturnsNotFound()
    {
        // Mục đích: Repo không tồn tại → error message rõ ràng.
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore(); // empty repo list

        var result = await GraphTraversalTools.GetDeadCode("nonexistent", graphStore, compressor, repoStore);

        Assert.Contains("Repository 'nonexistent' not found", result);
        Assert.Contains("ListRepositories", result); // guide AI tới tool đúng
    }

    [Fact]
    public async Task GetDeadCode_WithResults_IncludesFalsePositiveWarning()
    {
        // Mục đích: Output phải có warning về false positives (entry points, reflection).
        var repoId = Guid.NewGuid();
        var graphStore = Substitute.For<IGraphStore>();

        graphStore.QueryDeadCodeAsync(repoId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Foo.Unused()", "Unused"),
            ]));

        var repo = TestHelpers.MakeRepo("my-repo", repoId);
        var repoStore = TestHelpers.BuildRepoStore(repo);
        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetDeadCode("my-repo", graphStore, compressor, repoStore);

        Assert.Contains("Unused", result);
        // Quan trọng: warning về false positives giúp AI không drop code sai.
        Assert.Contains("false positives", result);
    }

    // === R21 Fix #2: missing required params return friendly error instead of throwing ===

    [Fact]
    public async Task GetCallers_MissingMethodFqn_ReturnsFriendlyError()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetCallers(
            methodFqn: null, depth: 1, graphStore, compressor);

        // No exception should escape; result should explain what's missing
        Assert.Contains("Missing required parameter", result);
        Assert.Contains("methodFqn", result);
        Assert.Contains("Example:", result);
        // Store must NOT be called with a null/empty FQN
        await graphStore.DidNotReceive().QueryCallersAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCallees_MissingMethodFqn_ReturnsFriendlyError()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetCallees(
            methodFqn: null, depth: 1, graphStore, compressor);

        Assert.Contains("Missing required parameter", result);
        Assert.Contains("methodFqn", result);
    }

    [Fact]
    public async Task GetImpactAnalysis_MissingFqn_ReturnsFriendlyError()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetImpactAnalysis(
            fqn: null, depth: 2,
            graphStore: graphStore, compressor: compressor);

        Assert.Contains("Missing required parameter", result);
        Assert.Contains("'fqn'", result);
    }

    [Fact]
    public async Task GetTestCoverage_MissingMethodFqn_ReturnsFriendlyError()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();

        var result = await GraphTraversalTools.GetTestCoverage(
            methodFqn: null, graphStore, compressor);

        Assert.Contains("Missing required parameter", result);
        Assert.Contains("methodFqn", result);
    }

    [Fact]
    public async Task GetClassHierarchy_EmptyString_ReturnsFriendlyError()
    {
        var graphStore = Substitute.For<IGraphStore>();
        var compressor = TestHelpers.BuildCompressor();

        // Empty string should also be treated as missing (not just null)
        var result = await GraphTraversalTools.GetClassHierarchy(
            classFqn: "   ", graphStore, compressor);

        Assert.Contains("Missing required parameter", result);
        Assert.Contains("classFqn", result);
    }

    // === R21 Fix #1: ListRepositories de-dups stale entries by name ===

    [Fact]
    public async Task ListRepositories_DuplicateNames_KeepsMostRecentAndNotesStale()
    {
        // Same project indexed twice: once via /workspace (old), once via _agent/ (new).
        // Output should show only the newest and mention the stale path as a footnote.
        var repoStore = Substitute.For<IRepositoryStore>();
        repoStore.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>([
                new RepositoryInfo(
                    Guid.NewGuid(),
                    "CortexPlexus",
                    "/workspace",
                    DateTimeOffset.Parse("2026-04-08T12:00:00Z"),
                    DateTimeOffset.Parse("2026-04-08T12:00:00Z")),
                new RepositoryInfo(
                    Guid.NewGuid(),
                    "CortexPlexus",
                    "_agent/CortexPlexus",
                    DateTimeOffset.Parse("2026-04-11T02:00:00Z"),
                    DateTimeOffset.Parse("2026-04-11T02:00:00Z")),
            ]));

        var result = await GraphTraversalTools.ListRepositories(repoStore);

        // The newer path should be the primary line
        Assert.Contains("_agent/CortexPlexus", result);
        // The older path must be listed as stale (not hidden entirely) so the user
        // knows it exists and can clean it up
        Assert.Contains("stale duplicate", result);
        Assert.Contains("/workspace", result);
        // The primary "Path:" line must NOT be the stale one
        var firstPathLineIdx = result.IndexOf("Path:", StringComparison.Ordinal);
        var afterPathLine = result[firstPathLineIdx..(firstPathLineIdx + 80)];
        Assert.Contains("_agent/CortexPlexus", afterPathLine);
    }

    [Fact]
    public async Task ListRepositories_UniqueNames_NoStaleNote()
    {
        var repoStore = Substitute.For<IRepositoryStore>();
        repoStore.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>([
                new RepositoryInfo(Guid.NewGuid(), "Alpha", "/a",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new RepositoryInfo(Guid.NewGuid(), "Beta", "/b",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]));

        var result = await GraphTraversalTools.ListRepositories(repoStore);

        Assert.Contains("Alpha", result);
        Assert.Contains("Beta", result);
        Assert.DoesNotContain("stale duplicate", result);
    }

    // === ADR 008: kind-aware Health metric (docs/HEALTH-METRICS.md) ===

    [Theory]
    // (total, embeddable, withEmbedding, expectedSubstring)
    [InlineData(0, 0, 0, "EMPTY")]
    [InlineData(50, 0, 0, "no embeddable kinds")]                       // config-only repo
    [InlineData(5273, 2130, 0, "DEGRADED")]                              // issue-#1 silent drop
    [InlineData(5273, 2130, 2130, "OK")]                                 // CortexFlow real numbers
    [InlineData(5273, 2130, 2130, "100%")]                               // shows full coverage
    [InlineData(5273, 2130, 2130, "of 2130 embeddable kinds")]           // self-explanatory denominator
    [InlineData(95, 60, 54, "OK")]                                       // 90% of embeddable, just at threshold
    [InlineData(95, 60, 53, "PARTIAL")]                                  // 88% of embeddable, just below
    [InlineData(95, 60, 30, "PARTIAL")]                                  // 50% — clearly partial
    [InlineData(100, 50, 50, "OK")]                                      // 100% of half-embeddable kinds
    public void FormatHealthLabel_KindAware(long total, long embeddable, long withEmbedding, string expectedSubstring)
    {
        // Old logic compared withEmbedding/total. With (5273, 2130, 2130) that was 40%
        // and the label was "PARTIAL — 2130/5273 (40%)". After ADR 008 it must be OK.
        var label = CortexPlexus.App.Mcp.Tools.GraphTraversalTools.FormatHealthLabel(total, embeddable, withEmbedding);
        Assert.Contains(expectedSubstring, label);
    }

    [Fact]
    public void FormatHealthLabel_HealthyDotNetRepo_DoesNotSayPartial()
    {
        // Regression guard for the v0.6.0 false alarm: every healthy .NET repo
        // showed PARTIAL because the old denominator was total symbols.
        // CortexFlow real numbers from 2026-04-15 indexing run.
        var label = CortexPlexus.App.Mcp.Tools.GraphTraversalTools.FormatHealthLabel(5273, 2130, 2130);
        Assert.StartsWith("OK", label);
        Assert.DoesNotContain("PARTIAL", label);
        Assert.DoesNotContain("DEGRADED", label);
    }

    // === R22 Fix #3: hint when class FQN is given to method-expecting tools ===

    [Fact]
    public async Task GetCallers_NoResults_AndClassFqnGiven_ReturnsHintWithMethodList()
    {
        // After R21's anchored matching, calling get_callers with a class FQN
        // (no parens) returns 0 results silently. R22 detects this and queries
        // LookupMethodsByContainingTypeAsync to suggest the actual method FQNs.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        graphStore.LookupMethodsByContainingTypeAsync(
                Arg.Is<string>(s => s == "App.Service"),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Service.ProcessAsync()", "ProcessAsync", "method"),
                TestHelpers.MakeResult("App.Service.Validate()", "Validate", "method"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers(
            methodFqn: "App.Service", graphStore: graphStore, compressor: compressor);

        Assert.Contains("looks like a class FQN", result);
        Assert.Contains("App.Service.ProcessAsync()", result);
        Assert.Contains("App.Service.Validate()", result);
        // Lookup helper must have been called
        await graphStore.Received().LookupMethodsByContainingTypeAsync(
            "App.Service", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCallees_NoResults_AndClassFqnGiven_ReturnsHint()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCalleesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.LookupMethodsByContainingTypeAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Foo.DoIt()", "DoIt", "method"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallees(
            methodFqn: "App.Foo", graphStore: graphStore, compressor: compressor);

        Assert.Contains("looks like a class FQN", result);
        Assert.Contains("App.Foo.DoIt()", result);
    }

    [Fact]
    public async Task GetTestCoverage_NoResults_AndClassFqnGiven_ReturnsHint()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryTestCoverageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.LookupMethodsByContainingTypeAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Service.SaveAsync()", "SaveAsync", "method"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetTestCoverage(
            methodFqn: "App.Service", graphStore: graphStore, compressor: compressor);

        Assert.Contains("looks like a class FQN", result);
        Assert.Contains("App.Service.SaveAsync()", result);
    }

    [Fact]
    public async Task GetCallers_NoResults_MethodFqnWithParens_NoHint()
    {
        // When the FQN already contains '(' it's clearly a method signature, so the
        // hint shouldn't fire (would be wasted lookup work).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers(
            methodFqn: "App.Service.ProcessAsync(string)",
            graphStore: graphStore,
            compressor: compressor);

        Assert.Contains("No callers found", result);
        Assert.DoesNotContain("looks like a class", result);
        // Lookup helper must NOT have been called for a method-shaped FQN
        await graphStore.DidNotReceive().LookupMethodsByContainingTypeAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCallers_NoResults_AndNoMethodsFound_FallsBackToDefaultMessage()
    {
        // If lookup also returns empty, the tool should fall back to the original
        // "No callers found" message instead of showing an empty hint block.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.LookupMethodsByContainingTypeAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers(
            methodFqn: "Some.Unknown.Thing", graphStore: graphStore, compressor: compressor);

        Assert.Contains("No callers found", result);
        Assert.DoesNotContain("looks like a class", result);
    }

    // === R23 N3+N4+N6: framework / CLR primitive filter ===

    [Theory]
    [InlineData("System.String.IsNullOrEmpty", true)]
    [InlineData("System.Collections.Generic.List`1", true)]
    [InlineData("Microsoft.Extensions.Logging.ILogger.LogWarning", true)]
    [InlineData("StackExchange.Redis.IDatabaseAsync.StringSetAsync", true)]
    [InlineData("Npgsql.NpgsqlConnection", true)]
    [InlineData("string", true)]
    [InlineData("int", true)]
    [InlineData("Guid", true)]
    [InlineData("DateTime", true)]
    [InlineData("CortexFlow.Infrastructure.Services.ChatOrchestrator", false)]
    [InlineData("MyApp.Domain.User", false)]
    [InlineData("", false)]
    public void IsExternalOrPrimitive_ClassifiesCorrectly(string fqn, bool expected)
    {
        Assert.Equal(expected, GraphTraversalTools.IsExternalOrPrimitive(fqn));
    }

    [Fact]
    public async Task GetCallees_StripsFrameworkNoise()
    {
        // R23 N3+N4: at depth>=2, get_callees was dumping System.*/Microsoft.*/
        // StackExchange.* method calls. The framework filter removes them so the
        // user only sees their own code's downstream calls.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCalleesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Service.DoWork()", "DoWork", "method"),
                TestHelpers.MakeResult("System.String.IsNullOrEmpty", "IsNullOrEmpty", "class"),
                TestHelpers.MakeResult("Microsoft.Extensions.Logging.ILogger.LogWarning", "LogWarning", "class"),
                TestHelpers.MakeResult("App.Service.Validate()", "Validate", "method"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallees(
            methodFqn: "App.Service.Caller(int)", graphStore: graphStore, compressor: compressor);

        Assert.Contains("App.Service.DoWork()", result);
        Assert.Contains("App.Service.Validate()", result);
        // Framework calls must be filtered out
        Assert.DoesNotContain("System.String.IsNullOrEmpty", result);
        Assert.DoesNotContain("Microsoft.Extensions.Logging.ILogger.LogWarning", result);
    }

    [Fact]
    public async Task GetDependencies_StripsCLRPrimitivesAndFramework()
    {
        // R23 N6: dependency results showed "string", "System.Array", and other
        // CLR primitives as [class] nodes. Filter them so user sees only domain
        // dependencies.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryDependenciesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Domain.User", "User", "class"),
                TestHelpers.MakeResult("string", "string", "class"),
                TestHelpers.MakeResult("System.Array", "Array", "class"),
                TestHelpers.MakeResult("App.Domain.Order", "Order", "class"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetDependencies(
            fqn: "App.Service.Process(User)", graphStore: graphStore, compressor: compressor);

        Assert.Contains("App.Domain.User", result);
        Assert.Contains("App.Domain.Order", result);
        // Primitives + System.Array filtered out
        Assert.DoesNotContain("[class] string", result);
        Assert.DoesNotContain("System.Array", result);
    }

    // === R25 R24-5: parent-prefix walk hint for typo'd method FQNs ===

    [Fact]
    public async Task GetCallers_TypoedMethodFqn_HintWalksParentClass()
    {
        // User typed "IDirector.AnalyzeAsync" but real method is "IDirector.DirectAsync".
        // Strategy 1 (treat FQN as class) returns empty.
        // Strategy 2 (R25): strip ".AnalyzeAsync", treat "IDirector" as class, list its methods.
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        // Strategy 1: lookup with full FQN — empty (no methods called "AnalyzeAsync.X")
        graphStore.LookupMethodsByContainingTypeAsync(
                "App.Interfaces.IDirector.AnalyzeAsync",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        // Strategy 2: lookup with parent — finds the real methods
        graphStore.LookupMethodsByContainingTypeAsync(
                "App.Interfaces.IDirector",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Interfaces.IDirector.DirectAsync()", "DirectAsync", "method"),
                TestHelpers.MakeResult("App.Interfaces.IDirector.ResetAsync()", "ResetAsync", "method"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers(
            methodFqn: "App.Interfaces.IDirector.AnalyzeAsync",
            graphStore: graphStore,
            compressor: compressor);

        Assert.Contains("may be a typo", result);
        Assert.Contains("Did you mean", result);
        Assert.Contains("App.Interfaces.IDirector.DirectAsync()", result);
        Assert.Contains("App.Interfaces.IDirector.ResetAsync()", result);
    }

    [Fact]
    public async Task GetCallers_NoResultsAndParentAlsoEmpty_FallsBackToDefault()
    {
        // Both strategies fail → fall back to "No callers found".
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.LookupMethodsByContainingTypeAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetCallers(
            methodFqn: "Foo.Bar.Baz",
            graphStore: graphStore,
            compressor: compressor);

        Assert.Contains("No callers found", result);
        Assert.DoesNotContain("Did you mean", result);
        Assert.DoesNotContain("looks like a class", result);
    }

    [Fact]
    public async Task GetClassHierarchy_StillUsesSharedFilter()
    {
        // R23: GetClassHierarchy was already filtering — verify it now uses the
        // shared StripFrameworkNoise helper (deduplication, not behavior change).
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryClassHierarchyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
                TestHelpers.MakeResult("App.Domain.MyClass", "MyClass", "class"),
                TestHelpers.MakeResult("System.Object", "Object", "class"),  // framework
                TestHelpers.MakeResult("App.Domain.BaseClass", "BaseClass", "class"),
            ]));

        var compressor = TestHelpers.BuildCompressor();
        var result = await GraphTraversalTools.GetClassHierarchy(
            classFqn: "App.Domain.MyClass", graphStore: graphStore, compressor: compressor);

        Assert.Contains("App.Domain.MyClass", result);
        Assert.Contains("App.Domain.BaseClass", result);
        Assert.DoesNotContain("System.Object", result);
    }

    // === Watch-agent liveness label (zombie-watch fix) ===
    // A watch that is alive-but-not-uploading used to be invisible: "Health: OK",
    // fresh-looking scores, index frozen for weeks. FormatWatchLabel renders the
    // agent's self-reported state so the failure mode shows in list_repositories.

    private static WatchStatusInfo WatchStatus(
        DateTimeOffset now,
        TimeSpan heartbeatAge,
        TimeSpan? syncAge = null,
        int failures = 0,
        string? lastError = null) =>
        new(Guid.NewGuid(), "1.2.0", now - heartbeatAge,
            syncAge is { } s ? now - s : null, failures, lastError);

    [Fact]
    public void FormatWatchLabel_NoStatus_ReturnsNull()
    {
        // Repo không có watch agent (server-side indexed) → không in dòng Watch.
        Assert.Null(GraphTraversalTools.FormatWatchLabel(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FormatWatchLabel_FreshHeartbeatNoFailures_Active()
    {
        var now = DateTimeOffset.UtcNow;
        var label = GraphTraversalTools.FormatWatchLabel(
            WatchStatus(now, heartbeatAge: TimeSpan.FromMinutes(2), syncAge: TimeSpan.FromMinutes(10)), now);

        Assert.NotNull(label);
        Assert.StartsWith("ACTIVE", label);
        Assert.Contains("10 minutes", label);
    }

    [Fact]
    public void FormatWatchLabel_AliveButFailing_ShowsFailingWithError()
    {
        // The CortexFlow zombie: systemd says active, MSBuildWorkspace throws on every
        // flush, index frozen. Heartbeats keep flowing (process alive) but carry a
        // failure count — the label must scream FAILING, not ACTIVE.
        var now = DateTimeOffset.UtcNow;
        var label = GraphTraversalTools.FormatWatchLabel(
            WatchStatus(now, heartbeatAge: TimeSpan.FromMinutes(1), syncAge: TimeSpan.FromDays(28),
                failures: 4, lastError: "MSBuildWorkspace.OpenSolutionAsync threw"), now);

        Assert.NotNull(label);
        Assert.StartsWith("FAILING", label);
        Assert.Contains("4", label);
        Assert.Contains("MSBuildWorkspace.OpenSolutionAsync threw", label);
        Assert.Contains("28 days", label);
    }

    [Fact]
    public void FormatWatchLabel_StaleHeartbeat_Disconnected()
    {
        var now = DateTimeOffset.UtcNow;
        var label = GraphTraversalTools.FormatWatchLabel(
            WatchStatus(now, heartbeatAge: TimeSpan.FromHours(3), syncAge: TimeSpan.FromHours(3)), now);

        Assert.NotNull(label);
        Assert.StartsWith("DISCONNECTED", label);
        Assert.Contains("3 hours", label);
        Assert.Contains("NOT flowing", label);
    }

    [Fact]
    public void FormatWatchLabel_NeverSynced_SaysSo()
    {
        var now = DateTimeOffset.UtcNow;
        var label = GraphTraversalTools.FormatWatchLabel(
            WatchStatus(now, heartbeatAge: TimeSpan.FromMinutes(1)), now);

        Assert.NotNull(label);
        Assert.Contains("no successful sync recorded", label);
    }

    // === ADR-018: Embedding-space label in list_repositories ===
    // Makes fleet migration progress self-documenting: which repos still carry
    // old-space vectors (⚠️ mismatch), which are unstamped legacy (unknown).

    private static readonly EmbeddingSpace CurrentSpace = new("vertex", "text-embedding-005", 768);

    [Fact]
    public void FormatEmbeddingSpaceLabel_Unstamped_SaysUnknown()
    {
        var label = GraphTraversalTools.FormatEmbeddingSpaceLabel(null, null, null, CurrentSpace);
        Assert.Equal("space unknown — stamp by re-indexing", label);
    }

    [Fact]
    public void FormatEmbeddingSpaceLabel_Matched_PlainSpace()
    {
        var label = GraphTraversalTools.FormatEmbeddingSpaceLabel(
            "vertex", "text-embedding-005", 768, CurrentSpace);
        Assert.Equal("vertex/text-embedding-005 (768d)", label);
    }

    [Fact]
    public void FormatEmbeddingSpaceLabel_Mismatch_Warns()
    {
        var label = GraphTraversalTools.FormatEmbeddingSpaceLabel(
            "ollama", "nomic-embed-text", 768, CurrentSpace);
        Assert.Equal(
            "ollama/nomic-embed-text (768d) ⚠️ mismatch — semantic_search degraded until re-index",
            label);
    }

    [Fact]
    public void FormatEmbeddingSpaceLabel_NoCurrentSpace_NoMismatchCheck()
    {
        // Bare harness without a registered current space: show the stamp, never warn
        // (a mismatch claim needs both sides).
        var label = GraphTraversalTools.FormatEmbeddingSpaceLabel(
            "ollama", "nomic-embed-text", 768, current: null);
        Assert.Equal("ollama/nomic-embed-text (768d)", label);
    }
}
