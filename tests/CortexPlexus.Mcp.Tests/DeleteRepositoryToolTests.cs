using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// DeleteRepository tool: exact-name resolution + delete across both stores (AGE graph via
/// DeleteByRepoAsync, relational via DeleteAsync). A destructive op must never fuzzy-match.
/// </summary>
public class DeleteRepositoryToolTests
{
    [Fact]
    public async Task EmptyName_ReturnsError_NoDeletes()
    {
        var graph = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await IndexTools.DeleteRepository("  ", graph, repoStore);

        Assert.Contains("required", result);
        await graph.DidNotReceive().DeleteByRepoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownRepo_ReturnsNotFound_NoDeletes()
    {
        var graph = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("RealRepo"));

        var result = await IndexTools.DeleteRepository("ghost", graph, repoStore);

        Assert.Contains("not found", result);
        Assert.Contains("Nothing was deleted", result);
        await graph.DidNotReceive().DeleteByRepoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await repoStore.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistingRepo_DeletesGraphThenRelational_AndReportsCount()
    {
        var id = Guid.NewGuid();
        var graph = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("old-test-repo", id, "/workspace/old"));
        repoStore.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult(123));

        var result = await IndexTools.DeleteRepository("old-test-repo", graph, repoStore);

        await graph.Received(1).DeleteByRepoAsync(id, Arg.Any<CancellationToken>());
        await repoStore.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        Assert.Contains("old-test-repo", result);
        Assert.Contains("123", result);
    }

    [Fact]
    public async Task NameMatch_IsCaseInsensitive()
    {
        var id = Guid.NewGuid();
        var graph = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("MyRepo", id));

        var result = await IndexTools.DeleteRepository("myrepo", graph, repoStore);

        await graph.Received(1).DeleteByRepoAsync(id, Arg.Any<CancellationToken>());
        Assert.Contains("Deleted repository", result);
    }

    [Fact]
    public async Task DuplicateNames_RefusesToDelete()
    {
        var graph = Substitute.For<IGraphStore>();
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("dup", Guid.NewGuid(), "/ws/a"),
            TestHelpers.MakeRepo("dup", Guid.NewGuid(), "/ws/b"));

        var result = await IndexTools.DeleteRepository("dup", graph, repoStore);

        Assert.Contains("Ambiguous", result);
        await graph.DidNotReceive().DeleteByRepoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await repoStore.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
