using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Memory.Tests;

[Collection("Memory")]
public sealed class AgentMemoryStoreTests(MemoryFixture fixture) : IAsyncLifetime
{
    private readonly MemoryFixture _fixture = fixture;
    private AgentMemoryStore _store = null!;
    private global::Npgsql.NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        _dataSource = _fixture.CreateDataSource();
        _store = new AgentMemoryStore(_dataSource, NullLogger<AgentMemoryStore>.Instance);
        await _store.InitializeSchemaAsync();
        await _fixture.CleanAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task InitializeSchemaAsync_IsIdempotent()
    {
        await _store.InitializeSchemaAsync();
        await _store.InitializeSchemaAsync();

        var count = await _store.CountAsync();
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsSessionMemory()
    {
        var saved = await _store.SaveAsync(
            content: "User is debugging auth flow",
            scope: MemoryScope.Session,
            scopeId: "session-abc",
            topic: MemoryTopic.Note,
            importance: 0.7,
            relatedFqns: new[] { "App.AuthService" },
            embedding: null);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(MemoryScope.Session, saved.Scope);
        Assert.Equal("session-abc", saved.ScopeId);
        Assert.Equal(MemoryTopic.Note, saved.Topic);
        Assert.Equal(0.7, saved.Importance, precision: 3);
        Assert.Single(saved.RelatedFqns);
        Assert.Equal("App.AuthService", saved.RelatedFqns[0]);
        Assert.Equal(0, saved.AccessCount);
    }

    [Fact]
    public async Task ClearSessionAsync_DeletesOnlyThatSession()
    {
        // Two sessions + a project memory; clearing session-A removes only its rows.
        await _store.SaveAsync("a1", MemoryScope.Session, "session-A", MemoryTopic.Note, 0.5, null, null);
        await _store.SaveAsync("a2", MemoryScope.Session, "session-A", MemoryTopic.Todo, 0.5, null, null);
        await _store.SaveAsync("b1", MemoryScope.Session, "session-B", MemoryTopic.Note, 0.5, null, null);
        await _store.SaveAsync("p1", MemoryScope.Project, Guid.NewGuid().ToString(), MemoryTopic.Pattern, 0.5, null, null);

        var deleted = await _store.ClearSessionAsync("session-A");

        Assert.Equal(2, deleted);
        Assert.Equal(2, await _store.CountAsync()); // session-B + project survive

        Assert.Empty(await _store.ListAsync(MemoryScope.Session, "session-A", null, 50));
        Assert.Single(await _store.ListAsync(MemoryScope.Session, "session-B", null, 50));
    }

    [Fact]
    public async Task SaveAsync_GlobalWithNullScopeId_IsAllowed()
    {
        var saved = await _store.SaveAsync(
            content: "User prefers terse answers",
            scope: MemoryScope.Global,
            scopeId: null,
            topic: MemoryTopic.Preference,
            importance: 0.9,
            relatedFqns: null,
            embedding: null);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Null(saved.ScopeId);
    }

    [Fact]
    public async Task SaveAsync_ProjectWithNullScopeId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync(
                content: "Test",
                scope: MemoryScope.Project,
                scopeId: null,
                topic: null,
                importance: 0.5,
                relatedFqns: null,
                embedding: null));
    }

    [Fact]
    public async Task SaveAsync_InvalidScope_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync(
                content: "Test",
                scope: "invalid-scope",
                scopeId: "x",
                topic: null,
                importance: 0.5,
                relatedFqns: null,
                embedding: null));
    }

    [Fact]
    public async Task SaveAsync_InvalidTopic_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync(
                content: "Test",
                scope: MemoryScope.Global,
                scopeId: null,
                topic: "bogus-topic",
                importance: 0.5,
                relatedFqns: null,
                embedding: null));
    }

    [Fact]
    public async Task SaveAsync_ImportanceOutOfRange_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync("x", MemoryScope.Global, null, null, importance: 1.5, null, null));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync("x", MemoryScope.Global, null, null, importance: -0.1, null, null));
    }

    [Fact]
    public async Task SaveAsync_ContentEmpty_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync("", MemoryScope.Global, null, null, 0.5, null, null));
    }

    [Fact]
    public async Task SaveAsync_ContentTooLong_Throws()
    {
        var longContent = new string('x', 4001);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.SaveAsync(longContent, MemoryScope.Global, null, null, 0.5, null, null));
    }

    [Fact]
    public async Task ListAsync_FiltersByScopeAndScopeId()
    {
        await SeedBasicMemoriesAsync();

        var sessionA = await _store.ListAsync(MemoryScope.Session, "session-A", null, 100);
        Assert.Equal(2, sessionA.Count);
        Assert.All(sessionA, m => Assert.Equal("session-A", m.ScopeId));

        var sessionB = await _store.ListAsync(MemoryScope.Session, "session-B", null, 100);
        Assert.Single(sessionB);
    }

    [Fact]
    public async Task ListAsync_FiltersByTopic()
    {
        await SeedBasicMemoriesAsync();
        var bugs = await _store.ListAsync(scope: null, scopeId: null, topic: MemoryTopic.Bug, limit: 100);
        Assert.Single(bugs);
        Assert.Equal(MemoryTopic.Bug, bugs[0].Topic);
    }

    [Fact]
    public async Task RecallAsync_FiltersByRelatedFqn()
    {
        await _store.SaveAsync("A", MemoryScope.Project, "repo-1", null, 0.5,
            new[] { "X.Y.Foo" }, embedding: null);
        await _store.SaveAsync("B", MemoryScope.Project, "repo-1", null, 0.5,
            new[] { "X.Y.Bar" }, embedding: null);

        var hits = await _store.RecallAsync(
            queryEmbedding: null,
            scope: MemoryScope.Project,
            scopeId: "repo-1",
            topic: null,
            relatedFqn: "X.Y.Foo",
            limit: 10);

        Assert.Single(hits);
        Assert.Equal("A", hits[0].Memory.Content);
    }

    [Fact]
    public async Task RecallAsync_BumpsAccessCount()
    {
        var saved = await _store.SaveAsync("A", MemoryScope.Global, null, null, 0.5, null, null);
        Assert.Equal(0, saved.AccessCount);

        await _store.RecallAsync(null, MemoryScope.Global, null, null, null, 10);
        var afterList = await _store.ListAsync(null, null, null, 100);
        var bumped = afterList.Single(m => m.Id == saved.Id);
        Assert.Equal(1, bumped.AccessCount);
    }

    [Fact]
    public async Task ForgetAsync_ReturnsTrueWhenRemoved()
    {
        var saved = await _store.SaveAsync("A", MemoryScope.Global, null, null, 0.5, null, null);
        var ok = await _store.ForgetAsync(saved.Id);
        Assert.True(ok);

        var again = await _store.ForgetAsync(saved.Id);
        Assert.False(again);
    }

    [Fact]
    public async Task ForgetAsync_ReturnsFalseForUnknownId()
    {
        var result = await _store.ForgetAsync(Guid.NewGuid());
        Assert.False(result);
    }

    [Fact]
    public async Task CountAsync_MatchesInsertedRows()
    {
        Assert.Equal(0L, await _store.CountAsync());
        await SeedBasicMemoriesAsync();
        Assert.Equal(4L, await _store.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_WithEmbedding_Persists()
    {
        var embedding = new float[768];
        for (var i = 0; i < 768; i++) embedding[i] = 0.1f;

        var saved = await _store.SaveAsync(
            content: "embedded memory",
            scope: MemoryScope.Global,
            scopeId: null,
            topic: null,
            importance: 0.5,
            relatedFqns: null,
            embedding: embedding);

        Assert.NotEqual(Guid.Empty, saved.Id);
        // We don't re-read the embedding here (no getter on the record);
        // Wave 2 will exercise the semantic-recall path end-to-end.
    }

    // === Empty vector is "no vector", not "a zero-length vector" ===
    //
    // Regression for a live incident (2026-08-14): every IEmbeddingService returns an
    // EMPTY float[] when the provider fails rather than throwing, so a provider outage
    // reaches this layer as Length == 0. The store checked `is null` only, built a
    // 0-dimension pgvector Vector against a vector(768) column, and save_memory died
    // with an unhandled exception for every save until the provider came back.

    [Fact]
    public async Task SaveAsync_EmptyEmbedding_PersistsWithoutVectorInsteadOfThrowing()
    {
        var saved = await _store.SaveAsync(
            content: "saved while the embedding provider was down",
            scope: MemoryScope.Global,
            scopeId: null,
            topic: null,
            importance: 0.5,
            relatedFqns: null,
            embedding: Array.Empty<float>());

        Assert.NotEqual(Guid.Empty, saved.Id);
    }

    [Fact]
    public async Task SaveAsync_EmptyEmbedding_DoesNotStampASpaceItNeverProduced()
    {
        // The ADR-018 stamp must describe a vector that exists. Stamping an absent one
        // would make the row look space-compatible and rank it as if it had a vector.
        await _store.SaveAsync(
            content: "no vector, no stamp",
            scope: MemoryScope.Global,
            scopeId: null,
            topic: null,
            importance: 0.5,
            relatedFqns: null,
            embedding: Array.Empty<float>(),
            embeddingProvider: "vertex",
            embeddingModel: "text-embedding-005",
            embeddingDim: 768);

        await using var cmd = _dataSource.CreateCommand(
            "SELECT embedding IS NULL, embedding_provider IS NULL, embedding_model IS NULL, embedding_dim IS NULL " +
            "FROM agent_memories WHERE content = 'no vector, no stamp'");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.True(r.GetBoolean(0), "embedding column should be NULL");
        Assert.True(r.GetBoolean(1), "embedding_provider should not be stamped");
        Assert.True(r.GetBoolean(2), "embedding_model should not be stamped");
        Assert.True(r.GetBoolean(3), "embedding_dim should not be stamped");
    }

    [Fact]
    public async Task RecallAsync_EmptyQueryEmbedding_DegradesToFilterOnlyInsteadOfThrowing()
    {
        await SeedBasicMemoriesAsync();

        // Same defect on the read path: an empty query vector must mean "rank by decay
        // only", not "search with a 0-dimension vector".
        var hits = await _store.RecallAsync(
            queryEmbedding: Array.Empty<float>(),
            scope: MemoryScope.Session,
            scopeId: "session-A",
            topic: null,
            relatedFqn: null,
            limit: 10);

        Assert.Equal(2, hits.Count);
    }

    private async Task SeedBasicMemoriesAsync()
    {
        await _store.SaveAsync("s1a", MemoryScope.Session, "session-A", MemoryTopic.Note, 0.5, null, null);
        await _store.SaveAsync("s1b", MemoryScope.Session, "session-A", MemoryTopic.Todo, 0.5, null, null);
        await _store.SaveAsync("s2",  MemoryScope.Session, "session-B", MemoryTopic.Note, 0.5, null, null);
        await _store.SaveAsync("p1",  MemoryScope.Project, "repo-1",    MemoryTopic.Bug,  0.5, null, null);
    }
}
