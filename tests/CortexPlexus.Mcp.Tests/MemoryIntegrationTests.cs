using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests that the include_memories opt-in on get_impact_analysis and explore_topic
/// surfaces linked memories alongside the normal output — and stays silent when
/// memory is disabled or not requested.
/// </summary>
public sealed class MemoryIntegrationTests
{
    private static AgentMemoryResult MakeMemory(string content, string? topic = null) =>
        new(new AgentMemory(
            Id: Guid.NewGuid(),
            Content: content,
            Scope: MemoryScope.Project,
            ScopeId: "repo-1",
            Topic: topic,
            Importance: 0.7,
            RelatedFqns: ["App.Target"],
            CreatedAt: DateTimeOffset.UtcNow,
            LastAccessedAt: DateTimeOffset.UtcNow,
            AccessCount: 1), Score: 0.7);

    [Fact]
    public async Task GetImpactAnalysis_IncludeMemoriesFalse_DoesNotQueryMemoryStore()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryImplementationsAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryClassHierarchyAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var compressor = TestHelpers.BuildCompressor();
        var memoryStore = Substitute.For<IAgentMemoryStore>();

        await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, includeMemories: false,
            graphStore: graphStore, compressor: compressor,
            memoryStore: memoryStore,
            memoryOptions: Options.Create(new MemoryOptions { Enabled = true }));

        await memoryStore.DidNotReceive().RecallAsync(
            Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetImpactAnalysis_IncludeMemoriesTrueButDisabled_DoesNotQuery()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([TestHelpers.MakeResult("App.Caller", "Caller")]));
        graphStore.QueryImplementationsAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryClassHierarchyAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var memoryStore = Substitute.For<IAgentMemoryStore>();
        var result = await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, includeMemories: true,
            graphStore: graphStore, compressor: TestHelpers.BuildCompressor(),
            memoryStore: memoryStore,
            memoryOptions: Options.Create(new MemoryOptions { Enabled = false }));

        await memoryStore.DidNotReceive().RecallAsync(
            Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("Linked Memories", result);
    }

    [Fact]
    public async Task GetImpactAnalysis_IncludeMemoriesTrue_SurfaceMemories()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([TestHelpers.MakeResult("App.Caller", "Caller")]));
        graphStore.QueryImplementationsAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryClassHierarchyAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var memoryStore = Substitute.For<IAgentMemoryStore>();
        memoryStore.RecallAsync(
                Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Is<string?>("App.Target"),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentMemoryResult>>(
                [MakeMemory("Team prefers async/await on this class", MemoryTopic.Preference)]));

        var result = await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, includeMemories: true,
            graphStore: graphStore, compressor: TestHelpers.BuildCompressor(),
            memoryStore: memoryStore,
            memoryOptions: Options.Create(new MemoryOptions { Enabled = true }));

        Assert.Contains("Linked Memories (1)", result);
        Assert.Contains("[preference]", result);
        Assert.Contains("async/await", result);
    }

    [Fact]
    public async Task GetImpactAnalysis_MemoryStoreThrows_DoesNotBreakOutput()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.QueryCallersAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([TestHelpers.MakeResult("App.Caller", "Caller")]));
        graphStore.QueryImplementationsAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryClassHierarchyAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        graphStore.QueryReferencedByAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var memoryStore = Substitute.For<IAgentMemoryStore>();
        memoryStore.RecallAsync(
                Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AgentMemoryResult>>>(_ => throw new InvalidOperationException("boom"));

        var result = await GraphTraversalTools.GetImpactAnalysis(
            "App.Target", 2, includeMemories: true,
            graphStore: graphStore, compressor: TestHelpers.BuildCompressor(),
            memoryStore: memoryStore,
            memoryOptions: Options.Create(new MemoryOptions { Enabled = true }));

        // Main impact data is still there; memory glitch did not propagate.
        Assert.Contains("Impact analysis for: App.Target", result);
        Assert.Contains("App.Caller", result);
        Assert.DoesNotContain("Linked Memories", result);
    }
}
