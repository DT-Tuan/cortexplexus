namespace CortexPlexus.Core.Models;

/// <summary>
/// Scope isolating memories along the axis of "who should see this memory".
/// See <see cref="MemoryScope"/> values and ADR-011 for the scope-model rationale.
/// </summary>
public static class MemoryScope
{
    public const string Session = "session";
    public const string Project = "project";
    public const string Global = "global";

    public static bool IsValid(string? scope) =>
        scope is Session or Project or Global;
}

/// <summary>
/// Bounded topic enum. Used for decay half-life selection and filter queries.
/// See <see cref="MemoryTopic"/> values and ADR-012 (Wave 2) for decay rationale.
/// </summary>
public static class MemoryTopic
{
    public const string Preference = "preference";
    public const string Pattern = "pattern";
    public const string Decision = "decision";
    public const string Bug = "bug";
    public const string Todo = "todo";
    public const string Note = "note";

    public static readonly string[] All =
        [Preference, Pattern, Decision, Bug, Todo, Note];

    public static bool IsValid(string? topic) =>
        topic is null || Array.IndexOf(All, topic) >= 0;
}

/// <summary>
/// A single agent memory record. Stored in the <c>agent_memories</c> Postgres table.
/// See <see cref="MemoryScope"/>, <see cref="MemoryTopic"/>, and docs/MEMORY-SYSTEM.md.
/// </summary>
public sealed record AgentMemory(
    Guid Id,
    string Content,
    string Scope,
    string? ScopeId,
    string? Topic,
    double Importance,
    IReadOnlyList<string> RelatedFqns,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    int AccessCount
);

/// <summary>
/// A memory row decorated with its current decay-weighted score. Returned by recall queries.
/// <paramref name="ForeignEmbeddingSpace"/> is true when the row's stamp does not match the
/// server's current embedding space — ranked with neutral 0.5 (content-only; ADR-018).
/// </summary>
public sealed record AgentMemoryResult(
    AgentMemory Memory,
    double Score,
    bool ForeignEmbeddingSpace = false
);
