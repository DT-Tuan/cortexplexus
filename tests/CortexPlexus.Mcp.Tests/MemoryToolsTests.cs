using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Unit tests for the 4 MCP memory tools. All use NSubstitute — no real DB. The
/// integration side is covered by CortexPlexus.Memory.Tests.
/// </summary>
public sealed class MemoryToolsTests
{
    private static IOptions<MemoryOptions> Opts(bool enabled) =>
        Options.Create(new MemoryOptions { Enabled = enabled });

    private static IAgentMemoryStore BuildStore() => Substitute.For<IAgentMemoryStore>();

    private static IEmbeddingService BuildEmbeddings()
    {
        var e = Substitute.For<IEmbeddingService>();
        e.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));
        return e;
    }

    private static ISecretsScanner BuildScanner(bool containsSecrets = false)
    {
        var s = Substitute.For<ISecretsScanner>();
        s.ContainsSecrets(Arg.Any<string>()).Returns(containsSecrets);
        // Must be stubbed in step with ContainsSecrets: NSubstitute auto-returns ""
        // (not null) for unstubbed string members, so a DetectSecret-driven code path
        // would read as "secret found" on every input.
        s.DetectSecret(Arg.Any<string>()).Returns(containsSecrets ? "'api_key' assigned a value" : null);
        s.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        return s;
    }

    [Fact]
    public async Task SaveMemory_Disabled_ReturnsDisabledMessage()
    {
        var result = await MemoryTools.SaveMemory(
            content: "x", scope: MemoryScope.Global,
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(false));

        Assert.Contains("Memory is disabled", result);
        Assert.Contains("Memory__Enabled=true", result);
    }

    [Fact]
    public async Task SaveMemory_MissingContent_ReturnsError()
    {
        var result = await MemoryTools.SaveMemory(
            content: null, scope: MemoryScope.Global,
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(true));

        Assert.Contains("'content' is required", result);
    }

    [Fact]
    public async Task SaveMemory_MissingScope_ReturnsError()
    {
        var result = await MemoryTools.SaveMemory(
            content: "x", scope: null,
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(true));

        Assert.Contains("'scope' is required", result);
    }

    [Fact]
    public async Task SaveMemory_InvalidScope_ReturnsError()
    {
        var result = await MemoryTools.SaveMemory(
            content: "x", scope: "bogus",
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(true));

        Assert.Contains("invalid scope", result);
    }

    [Fact]
    public async Task SaveMemory_ProjectWithoutScopeId_ReturnsError()
    {
        var result = await MemoryTools.SaveMemory(
            content: "x", scope: MemoryScope.Project, scopeId: null,
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(true));

        Assert.Contains("requires either `repository`", result);
    }

    [Fact]
    public async Task SaveMemory_ContentWithSecrets_Rejected()
    {
        var result = await MemoryTools.SaveMemory(
            content: "api_key=abc123",
            scope: MemoryScope.Global,
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(containsSecrets: true),
            options: Opts(true));

        Assert.Contains("secrets or credentials", result);
        // #30: the rejection must name what tripped it, or callers rewrite blindly.
        Assert.Contains("api_key", result);
    }

    [Fact]
    public async Task SaveMemory_InvalidTopic_ReturnsError()
    {
        var result = await MemoryTools.SaveMemory(
            content: "x", scope: MemoryScope.Global, topic: "unknown",
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(true));

        Assert.Contains("invalid topic", result);
    }

    [Fact]
    public async Task SaveMemory_ImportanceOutOfRange_ReturnsError()
    {
        var result = await MemoryTools.SaveMemory(
            content: "x", scope: MemoryScope.Global, importance: 1.5,
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), options: Opts(true));

        Assert.Contains("importance must be in [0, 1]", result);
    }

    [Fact]
    public async Task SaveMemory_HappyPath_ReturnsJsonWithId()
    {
        var store = BuildStore();
        var id = Guid.NewGuid();
        store.SaveAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<double>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<float[]?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AgentMemory(
                Id: id, Content: "x", Scope: MemoryScope.Global, ScopeId: null,
                Topic: MemoryTopic.Preference, Importance: 0.5,
                RelatedFqns: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow,
                LastAccessedAt: DateTimeOffset.UtcNow,
                AccessCount: 0)));

        var result = await MemoryTools.SaveMemory(
            content: "User prefers async",
            scope: MemoryScope.Global,
            topic: MemoryTopic.Preference,
            store: store,
            embeddings: BuildEmbeddings(),
            secrets: BuildScanner(),
            options: Opts(true));

        Assert.Contains(id.ToString(), result);
        Assert.Contains("\"stored\": true", result);
    }

    [Fact]
    public async Task RecallMemory_Disabled_ReturnsDisabled()
    {
        var result = await MemoryTools.RecallMemory(
            query: "x",
            store: BuildStore(),
            embeddings: BuildEmbeddings(),
            options: Opts(false));

        Assert.Contains("Memory is disabled", result);
    }

    [Fact]
    public async Task RecallMemory_MissingQuery_ReturnsError()
    {
        var result = await MemoryTools.RecallMemory(
            query: null,
            store: BuildStore(),
            embeddings: BuildEmbeddings(),
            options: Opts(true));

        Assert.Contains("'query' is required", result);
    }

    [Fact]
    public async Task RecallMemory_NoHits_ReturnsFriendlyMessage()
    {
        var store = BuildStore();
        store.RecallAsync(
                Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentMemoryResult>>(
                Array.Empty<AgentMemoryResult>()));

        var result = await MemoryTools.RecallMemory(
            query: "nothing", store: store,
            embeddings: BuildEmbeddings(), options: Opts(true));

        Assert.Contains("No memories matched", result);
    }

    [Fact]
    public async Task RecallMemory_ReturnsHitsInJson()
    {
        var store = BuildStore();
        var id = Guid.NewGuid();
        store.RecallAsync(
                Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentMemoryResult>>(
                [new AgentMemoryResult(new AgentMemory(
                    Id: id, Content: "pref", Scope: MemoryScope.Global, ScopeId: null,
                    Topic: MemoryTopic.Preference, Importance: 0.9,
                    RelatedFqns: Array.Empty<string>(),
                    CreatedAt: DateTimeOffset.UtcNow,
                    LastAccessedAt: DateTimeOffset.UtcNow,
                    AccessCount: 3), Score: 0.9)]));

        var result = await MemoryTools.RecallMemory(
            query: "preferences", store: store,
            embeddings: BuildEmbeddings(), repoStore: TestHelpers.BuildRepoStore(),
            options: Opts(true));

        Assert.Contains(id.ToString(), result);
        Assert.Contains("\"count\": 1", result);
    }

    [Fact]
    public async Task RecallMemory_ProjectScoped_ResolvesRepositoryName()
    {
        // Cross-project recall (scope='all') mixes repos; the output must name each memory's
        // project so the agent can attribute it. A project-scoped memory whose scope_id matches
        // a known repo gets `repository: "<name>"`.
        var store = BuildStore();
        var repoId = Guid.NewGuid();
        store.RecallAsync(
                Arg.Any<float[]?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentMemoryResult>>(
                [new AgentMemoryResult(new AgentMemory(
                    Id: Guid.NewGuid(), Content: "uses AGE", Scope: MemoryScope.Project,
                    ScopeId: repoId.ToString(), Topic: MemoryTopic.Pattern, Importance: 0.7,
                    RelatedFqns: Array.Empty<string>(),
                    CreatedAt: DateTimeOffset.UtcNow,
                    LastAccessedAt: DateTimeOffset.UtcNow, AccessCount: 1), Score: 0.7)]));

        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("CortexPlexus", id: repoId));

        var result = await MemoryTools.RecallMemory(
            query: "graph db", store: store,
            embeddings: BuildEmbeddings(), repoStore: repoStore, options: Opts(true));

        Assert.Contains("\"repository\": \"CortexPlexus\"", result);
    }

    [Fact]
    public async Task ClearSession_Disabled_ReturnsDisabled()
    {
        var result = await MemoryTools.ClearSession(
            sessionId: "s1", store: BuildStore(), options: Opts(false));
        Assert.Contains("Memory is disabled", result);
    }

    [Fact]
    public async Task ClearSession_MissingSessionId_ReturnsError()
    {
        var result = await MemoryTools.ClearSession(
            sessionId: null, store: BuildStore(), options: Opts(true));
        Assert.Contains("'sessionId' is required", result);
    }

    [Fact]
    public async Task ClearSession_Success_ReturnsDeletedCount()
    {
        var store = BuildStore();
        store.ClearSessionAsync("s-42", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(3));

        var result = await MemoryTools.ClearSession(
            sessionId: "s-42", store: store, options: Opts(true));

        Assert.Contains("\"cleared\": true", result);
        Assert.Contains("\"deleted\": 3", result);
        await store.Received(1).ClearSessionAsync("s-42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListMemories_Disabled_ReturnsDisabled()
    {
        var result = await MemoryTools.ListMemories(
            store: BuildStore(), options: Opts(false));
        Assert.Contains("Memory is disabled", result);
    }

    [Fact]
    public async Task ForgetMemory_Disabled_ReturnsDisabled()
    {
        var result = await MemoryTools.ForgetMemory(
            id: Guid.NewGuid().ToString(),
            store: BuildStore(),
            options: Opts(false));
        Assert.Contains("Memory is disabled", result);
    }

    [Fact]
    public async Task ForgetMemory_InvalidUuid_ReturnsError()
    {
        var result = await MemoryTools.ForgetMemory(
            id: "not-a-uuid",
            store: BuildStore(),
            options: Opts(true));
        Assert.Contains("invalid UUID", result);
    }

    [Fact]
    public async Task ForgetMemory_MissingId_ReturnsError()
    {
        var result = await MemoryTools.ForgetMemory(
            id: null, store: BuildStore(), options: Opts(true));
        Assert.Contains("'id' is required", result);
    }

    [Fact]
    public async Task ForgetMemory_Success_ReturnsForgottenTrue()
    {
        var store = BuildStore();
        store.ForgetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await MemoryTools.ForgetMemory(
            id: Guid.NewGuid().ToString(),
            store: store, options: Opts(true));

        Assert.Contains("\"forgotten\": true", result);
    }

    [Fact]
    public async Task ForgetMemory_NotFound_ReturnsForgottenFalse()
    {
        var store = BuildStore();
        store.ForgetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var result = await MemoryTools.ForgetMemory(
            id: Guid.NewGuid().ToString(),
            store: store, options: Opts(true));

        Assert.Contains("\"forgotten\": false", result);
        Assert.Contains("not_found", result);
    }

    // --- v0.8.3 Option A: repository name acceptance ---

    [Fact]
    public async Task SaveMemory_RepositoryName_ResolvesToScopeId()
    {
        var store = BuildStore();
        var repoId = Guid.NewGuid();
        store.SaveAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Is<string?>(s => s == repoId.ToString()),
                Arg.Any<string?>(), Arg.Any<double>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<float[]?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AgentMemory(
                Id: Guid.NewGuid(), Content: "x",
                Scope: MemoryScope.Project, ScopeId: repoId.ToString(),
                Topic: null, Importance: 0.5,
                RelatedFqns: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow,
                LastAccessedAt: DateTimeOffset.UtcNow, AccessCount: 0)));

        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("MyProject", id: repoId));

        var result = await MemoryTools.SaveMemory(
            content: "a convention",
            scope: MemoryScope.Project,
            repository: "MyProject",
            store: store, embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), repoStore: repoStore,
            options: Opts(true));

        Assert.Contains("\"stored\": true", result);
        // Store.SaveAsync was invoked with the resolved UUID, not the name.
        await store.Received(1).SaveAsync(
            Arg.Any<string>(), MemoryScope.Project, repoId.ToString(),
            Arg.Any<string?>(), Arg.Any<double>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<float[]?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveMemory_RepositoryName_NotFound_ReturnsError()
    {
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("SomeOtherProject"));

        var result = await MemoryTools.SaveMemory(
            content: "x",
            scope: MemoryScope.Project,
            repository: "GhostProject",
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), repoStore: repoStore,
            options: Opts(true));

        Assert.Contains("repository 'GhostProject' not found", result);
        Assert.Contains("ListRepositories()", result);
    }

    [Fact]
    public async Task SaveMemory_RepositoryNameTakesPrecedenceOverScopeId()
    {
        var realId = Guid.NewGuid();
        var wrongId = Guid.NewGuid();
        var store = BuildStore();
        store.SaveAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<double>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<float[]?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AgentMemory(
                Id: Guid.NewGuid(), Content: "x",
                Scope: MemoryScope.Project, ScopeId: realId.ToString(),
                Topic: null, Importance: 0.5,
                RelatedFqns: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow,
                LastAccessedAt: DateTimeOffset.UtcNow, AccessCount: 0)));
        var repoStore = TestHelpers.BuildRepoStore(
            TestHelpers.MakeRepo("RealProject", id: realId));

        await MemoryTools.SaveMemory(
            content: "x",
            scope: MemoryScope.Project,
            repository: "RealProject",
            scopeId: wrongId.ToString(),    // should be ignored
            store: store, embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), repoStore: repoStore,
            options: Opts(true));

        await store.Received(1).SaveAsync(
            Arg.Any<string>(), MemoryScope.Project, realId.ToString(),
            Arg.Any<string?>(), Arg.Any<double>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<float[]?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveMemory_InvalidUuidScopeId_ReturnsError()
    {
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await MemoryTools.SaveMemory(
            content: "x",
            scope: MemoryScope.Project,
            scopeId: "not-a-uuid-at-all",
            store: BuildStore(), embeddings: BuildEmbeddings(),
            secrets: BuildScanner(), repoStore: repoStore,
            options: Opts(true));

        Assert.Contains("not a valid UUID", result);
        Assert.Contains("`repository`", result);
    }
}
