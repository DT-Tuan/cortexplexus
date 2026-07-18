using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

/// <summary>
/// Storage abstraction for agent memories. See docs/MEMORY-SYSTEM.md for the
/// feature spec; ADR-010 for why this reuses the existing PostgreSQL DB.
/// </summary>
public interface IAgentMemoryStore
{
    /// <summary>Applies the memory-table migration; idempotent. Called from startup.</summary>
    Task InitializeSchemaAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert a new memory. <paramref name="embedding"/> may be null if the embedding service is not
    /// configured; recall-by-query will then miss this row. Content is the caller's responsibility
    /// to sanitize — the MCP tool layer runs <c>ISecretsScanner</c> before calling this method.
    /// </summary>
    Task<AgentMemory> SaveAsync(
        string content,
        string scope,
        string? scopeId,
        string? topic,
        double importance,
        IReadOnlyList<string>? relatedFqns,
        float[]? embedding,
        string? embeddingProvider = null,
        string? embeddingModel = null,
        int? embeddingDim = null,
        CancellationToken ct = default);

    /// <summary>
    /// Semantic + filter recall. Wave 2 ranks by decay × cosine; foreign/unknown embedding
    /// spaces (ADR-018) contribute a neutral 0.5 factor so content is still recalled.
    /// Pass <paramref name="currentProvider"/>/<paramref name="currentModel"/> so the ranker
    /// can detect space mismatch; omit them to keep legacy cosine-only ranking.
    /// </summary>
    Task<IReadOnlyList<AgentMemoryResult>> RecallAsync(
        float[]? queryEmbedding,
        string? scope,
        string? scopeId,
        string? topic,
        string? relatedFqn,
        int limit,
        string? currentProvider = null,
        string? currentModel = null,
        CancellationToken ct = default);

    /// <summary>Pure filter/paginate. No embedding compute. For management/audit.</summary>
    Task<IReadOnlyList<AgentMemory>> ListAsync(
        string? scope,
        string? scopeId,
        string? topic,
        int limit,
        CancellationToken ct = default);

    /// <summary>Delete by id. Returns true if a row was removed, false if not found.</summary>
    Task<bool> ForgetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Delete all <c>session</c>-scoped memories for a given session id (the client-supplied
    /// session UUID). Used by the <c>clear_session</c> tool to drop transient working memory
    /// when a task/conversation ends. Returns the number of rows deleted. Only touches
    /// <c>scope='session'</c> rows — project/global memories are never affected.
    /// </summary>
    Task<int> ClearSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Total row count in the store (used by Health reporting).</summary>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Delete all memories whose decay-weighted score is below the forget threshold.
    /// Called periodically by <c>MemoryReaper</c>. Returns the number of rows deleted.
    /// </summary>
    Task<int> ReapAsync(CancellationToken ct = default);
}
