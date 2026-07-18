using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Memory.Tests;

/// <summary>
/// ADR-018 memory half: per-row space stamps on save, and recall ranking that gives
/// foreign/unknown-space rows a NEUTRAL 0.5 factor instead of garbage cross-space
/// cosine. Runs against real Postgres (same fixture as AgentMemoryStoreTests) so the
/// actual CASE SQL is exercised, not a string facsimile.
/// </summary>
[Collection("Memory")]
public sealed class EmbeddingSpaceRecallTests(MemoryFixture fixture) : IAsyncLifetime
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

    // 768-dim unit vector with a single hot component — cosine between two of these is
    // 1.0 (same index) or 0.0 (different index), which makes rank assertions exact.
    private static float[] Unit(int hotIndex)
    {
        var v = new float[768];
        v[hotIndex] = 1f;
        return v;
    }

    // Vector at a chosen cosine to Unit(0): cos·e0 + sin·e1.
    private static float[] AtCosine(double cos)
    {
        var v = new float[768];
        v[0] = (float)cos;
        v[1] = (float)Math.Sqrt(1 - cos * cos);
        return v;
    }

    [Fact]
    public async Task SaveAsync_StampsSpace_WhenEmbeddingPresent()
    {
        await _store.SaveAsync(
            "stamped memory", MemoryScope.Global, null, MemoryTopic.Note, 0.5, null,
            Unit(0), "vertex", "text-embedding-005", 768);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedding_provider, embedding_model, embedding_dim FROM agent_memories";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("vertex", reader.GetString(0));
        Assert.Equal("text-embedding-005", reader.GetString(1));
        Assert.Equal(768, reader.GetInt32(2));
    }

    [Fact]
    public async Task SaveAsync_NullEmbedding_NeverStampsSpace()
    {
        // A stamp without a vector would be a lie — SaveAsync must null the triple.
        await _store.SaveAsync(
            "no vector", MemoryScope.Global, null, MemoryTopic.Note, 0.5, null,
            embedding: null, "vertex", "text-embedding-005", 768);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedding_provider IS NULL AND embedding_model IS NULL AND embedding_dim IS NULL FROM agent_memories";
        Assert.Equal(true, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RecallAsync_ForeignSpacePerfectCosine_RanksNeutral_NotFirst()
    {
        // The ADR-018 failure mode: a foreign-space vector that happens to sit at
        // cosine 1.0 to the query must NOT outrank a matched-space memory with a
        // merely-good cosine. Foreign → neutral 0.5; matched 0.9 wins.
        await _store.SaveAsync(
            "foreign space, accidentally perfect cosine", MemoryScope.Global, null,
            MemoryTopic.Note, 0.5, null, Unit(0), "ollama", "nomic-embed-text", 768);
        await _store.SaveAsync(
            "matched space, good cosine", MemoryScope.Global, null,
            MemoryTopic.Note, 0.5, null, AtCosine(0.9), "vertex", "text-embedding-005", 768);

        var hits = await _store.RecallAsync(
            Unit(0), MemoryScope.Global, null, null, null, 10,
            currentProvider: "vertex", currentModel: "text-embedding-005");

        Assert.Equal(2, hits.Count);
        Assert.Equal("matched space, good cosine", hits[0].Memory.Content);
        Assert.False(hits[0].ForeignEmbeddingSpace);
        Assert.True(hits[1].ForeignEmbeddingSpace);
    }

    [Fact]
    public async Task RecallAsync_UnstampedLegacyRow_AlsoNeutral()
    {
        await _store.SaveAsync(
            "legacy unstamped, perfect cosine", MemoryScope.Global, null,
            MemoryTopic.Note, 0.5, null, Unit(0));
        await _store.SaveAsync(
            "matched space, good cosine", MemoryScope.Global, null,
            MemoryTopic.Note, 0.5, null, AtCosine(0.9), "vertex", "text-embedding-005", 768);

        var hits = await _store.RecallAsync(
            Unit(0), MemoryScope.Global, null, null, null, 10,
            currentProvider: "vertex", currentModel: "text-embedding-005");

        Assert.Equal("matched space, good cosine", hits[0].Memory.Content);
        Assert.True(hits[1].ForeignEmbeddingSpace);
    }

    [Fact]
    public async Task RecallAsync_NoCurrentSpace_KeepsLegacyCosineRanking()
    {
        // Callers that don't know the current space (legacy hosts) keep the pre-ADR-018
        // formula: raw cosine ranks, nothing is flagged foreign.
        await _store.SaveAsync(
            "perfect cosine", MemoryScope.Global, null, MemoryTopic.Note, 0.5, null,
            Unit(0), "ollama", "nomic-embed-text", 768);
        await _store.SaveAsync(
            "orthogonal", MemoryScope.Global, null, MemoryTopic.Note, 0.5, null,
            Unit(1), "vertex", "text-embedding-005", 768);

        var hits = await _store.RecallAsync(
            Unit(0), MemoryScope.Global, null, null, null, 10);

        Assert.Equal("perfect cosine", hits[0].Memory.Content);
        Assert.All(hits, h => Assert.False(h.ForeignEmbeddingSpace));
    }

    [Fact]
    public void BuildSemanticOrderClause_SpaceAware_UsesNotDistinctCase()
    {
        var sql = AgentMemoryStore.BuildSemanticOrderClause(spaceAware: true);

        // IS NOT DISTINCT FROM: NULL-stamped rows fall into the ELSE 0.5 branch instead
        // of evaporating from the ORDER BY (plain '=' would yield NULL).
        Assert.Contains("embedding_provider IS NOT DISTINCT FROM @curProvider", sql);
        Assert.Contains("embedding_model    IS NOT DISTINCT FROM @curModel", sql);
        Assert.Contains("ELSE 0.5", sql);
    }

    [Fact]
    public void BuildSemanticOrderClause_LegacyPath_Unchanged()
    {
        var sql = AgentMemoryStore.BuildSemanticOrderClause(spaceAware: false);

        // No space CASE — plain cosine factor as pre-ADR-018. (The decay expression
        // itself contains a CASE; only the space-aware branch is asserted absent.)
        Assert.DoesNotContain("IS NOT DISTINCT FROM", sql);
        Assert.Contains("COALESCE((1.0 - (embedding <=> @q)), 0.5)", sql);
    }
}
