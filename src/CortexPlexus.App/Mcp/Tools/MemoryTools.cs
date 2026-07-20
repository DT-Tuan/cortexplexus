using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;

using CortexPlexus.Memory;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

/// <summary>
/// Agent memory MCP tools. All four gate on <see cref="MemoryOptions.Enabled"/>;
/// when the feature is disabled they return a clear "enable via config" message
/// (see ADR-013). See docs/MEMORY-SYSTEM.md for the full spec.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private const string DisabledMessage =
        "Memory is disabled. Enable by setting Memory.Enabled=true in appsettings.json " +
        "or Memory__Enabled=true as an environment variable, then restart the server. " +
        "See docs/MEMORY-SYSTEM.md.";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool, Description(
        "Save a memory the agent should remember across sessions. " +
        "USE for: user preferences, project conventions not obvious from code, bug notes tied to symbols (pass relatedFqns), architecture decisions without an ADR yet. " +
        "DO NOT USE for: facts derivable from code (use search_code/get_callers/get_config_usage instead), duplicates of CLAUDE.md/ADR content, credentials, current-turn-only state. " +
        "Pick scope: 'project' (default, pass `repository` name OR `scopeId` UUID), 'global' (rare, user-wide), 'session' (transient). " +
        "Pick topic (shapes decay): preference/pattern/decision=sticky, bug=medium, todo/note=short. " +
        "Content is PII-scanned before storage. Requires Memory__Enabled=true on the server. " +
        "See GetHelp(topic: 'memory') for the full playbook.")]
    public static async Task<string> SaveMemory(
        [Description("Free-text content, 1..4000 chars. No credentials or PII.")] string? content = null,
        [Description("Scope: 'session' | 'project' | 'global'")] string? scope = null,
        [Description("For scope='project': repository NAME (preferred, matches ListRepositories). Alternative to scopeId.")] string? repository = null,
        [Description("scope_id UUID: session id for 'session', repository UUID for 'project', ignored for 'global'. If scope='project', prefer `repository` name; scopeId UUID is power-user fallback.")] string? scopeId = null,
        [Description("Topic: 'preference' | 'pattern' | 'decision' | 'bug' | 'todo' | 'note' (optional)")] string? topic = null,
        [Description("Importance 0..1; omit to use server default (0.5)")] double? importance = null,
        [Description("Optional FQNs to link this memory to (soft link, no FK)")] string[]? relatedFqns = null,
        IAgentMemoryStore store = default!,
        IEmbeddingService embeddings = default!,
        ISecretsScanner secrets = default!,
        IRepositoryStore repoStore = default!,
        IOptions<MemoryOptions> options = default!,
        EmbeddingSpace? currentSpace = null,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (string.IsNullOrWhiteSpace(content))
            return "Error: 'content' is required (1..4000 chars).";
        if (string.IsNullOrWhiteSpace(scope))
            return "Error: 'scope' is required — one of 'session', 'project', 'global'.";
        if (!MemoryScope.IsValid(scope))
            return $"Error: invalid scope '{scope}'. Valid: 'session', 'project', 'global'.";

        // Resolve repository name → UUID for project scope. Repository takes precedence
        // over scopeId if both provided. Eliminates typo/wrong-UUID-copy risks (v0.8.3).
        var resolvedScopeId = await ResolveProjectScopeIdAsync(
            scope!, repository, scopeId, repoStore, ct);
        if (resolvedScopeId.Error is not null) return resolvedScopeId.Error;
        scopeId = resolvedScopeId.ScopeId;

        if (scope != MemoryScope.Global && string.IsNullOrWhiteSpace(scopeId))
            return $"Error: scope='{scope}' requires either `repository` (name) or `scopeId` (UUID).";
        if (!MemoryTopic.IsValid(topic))
            return $"Error: invalid topic '{topic}'. Valid: {string.Join(", ", MemoryTopic.All)}, or omit.";

        // Name the trigger. The old message ("contains secrets — sanitize before saving")
        // gave callers nothing to act on, so agents rewrote the same memory repeatedly
        // without ever hitting the one offending token (issue #30).
        //
        // The GATE stays on ContainsSecrets — DetectSecret only decorates the message.
        // Gating on `DetectSecret(...) is not null` looks equivalent but is not: test
        // doubles return "" rather than null for unstubbed string members, which turns
        // the gate permanently on.
        if (secrets.ContainsSecrets(content))
        {
            var detected = secrets.DetectSecret(content);
            var what = string.IsNullOrWhiteSpace(detected) ? "a credential pattern" : detected;
            return $"Error: content appears to contain secrets or credentials — detected {what}. " +
                   "Remove or redact the VALUE and retry. Prose that merely discusses credentials " +
                   "(\"use an Authorization: Bearer header\", \"the private_key field must never be logged\") is allowed.";
        }

        var importanceValue = importance ?? options.Value.DefaultImportance;
        if (importanceValue is < 0.0 or > 1.0)
            return $"Error: importance must be in [0, 1], got {importanceValue}.";

        float[]? embedding;
        try
        {
            embedding = await embeddings.EmbedAsync(content, ct);
        }
        catch (Exception ex)
        {
            // Abort the save — without an embedding the memory is not semantically
            // recall-able, which is the whole point. User should fix the provider and retry.
            return $"Error: embedding service failed ({ex.Message}). Memory NOT saved. " +
                   "Check the embedding provider (Ollama reachable? Gemini API key?) and retry.";
        }

        try
        {
            // ADR-018: stamp the space that produced this vector (null when absent or when
            // the composition root registered no current space — never fabricate one).
            var space = embedding is { Length: > 0 } ? currentSpace : null;
            var saved = await store.SaveAsync(
                content, scope!, scopeId, topic,
                importanceValue, relatedFqns, embedding,
                space?.Provider, space?.Model, space?.Dimensions, ct);

            return JsonSerializer.Serialize(new
            {
                id = saved.Id,
                scope = saved.Scope,
                topic = saved.Topic,
                importance = saved.Importance,
                savedAt = saved.CreatedAt,
                stored = true,
            }, JsonOpts);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Retrieve memories relevant to a query, ranked by semantic similarity × decay score. " +
        "CALL AT SESSION START after ListRepositories — before you start exploring the codebase. " +
        "Returned rows have their access timestamp refreshed (useful memories stay fresh; unused ones decay). " +
        "Pass scope='project' + `repository` name (or scopeId UUID) for project-scoped recall. " +
        "Pass relatedFqn=<symbol> to retrieve memories linked to a specific symbol. " +
        "Forgotten rows (below decay threshold) are auto-filtered out. Requires Memory__Enabled=true.")]
    public static async Task<string> RecallMemory(
        [Description("Query text for semantic search, 1..500 chars")] string? query = null,
        [Description("Scope filter: 'session' | 'project' | 'global' | 'all' (default 'all')")] string? scope = null,
        [Description("For scope='project': repository NAME (preferred). Alternative to scopeId UUID.")] string? repository = null,
        [Description("Optional scope_id UUID (alt to `repository`)")] string? scopeId = null,
        [Description("Filter by topic (preference|pattern|decision|bug|todo|note)")] string? topic = null,
        [Description("Filter to memories linked to a specific symbol FQN")] string? relatedFqn = null,
        [Description("Max results, 1..50 (default 10)")] int limit = 10,
        IAgentMemoryStore store = default!,
        IEmbeddingService embeddings = default!,
        IRepositoryStore repoStore = default!,
        IOptions<MemoryOptions> options = default!,
        EmbeddingSpace? currentSpace = null,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' is required (1..500 chars).";
        if (query.Length > 500)
            return "Error: 'query' must be ≤500 chars.";
        if (scope is not null && scope != "all" && !MemoryScope.IsValid(scope))
            return $"Error: invalid scope '{scope}'. Valid: 'session', 'project', 'global', 'all', or omit.";
        if (!MemoryTopic.IsValid(topic))
            return $"Error: invalid topic '{topic}'.";

        // Resolve repository name → UUID if project scope.
        var resolvedScopeId = await ResolveProjectScopeIdAsync(
            scope, repository, scopeId, repoStore, ct);
        if (resolvedScopeId.Error is not null) return resolvedScopeId.Error;
        scopeId = resolvedScopeId.ScopeId;

        float[]? queryEmbedding;
        try
        {
            queryEmbedding = await embeddings.EmbedAsync(query, ct);
        }
        catch
        {
            queryEmbedding = null; // Fall back to filter-only recall.
        }

        // ADR-018: pass the current space so foreign-space rows rank neutrally. When no
        // space is registered (legacy hosts, bare test harnesses) recall keeps the
        // pre-ADR-018 cosine-only ranking rather than pretending to know the space.
        var hits = await store.RecallAsync(
            queryEmbedding, scope, scopeId, topic, relatedFqn,
            Math.Clamp(limit, 1, 50),
            currentSpace?.Provider, currentSpace?.Model, ct);

        if (hits.Count == 0)
            return "No memories matched. Try broader scope='all' or remove topic/relatedFqn filters.";

        // Resolve project scope_id UUIDs → repository names so the agent can attribute each
        // memory to a project (essential for cross-project recall, where scope='all' mixes repos).
        var repoNames = await BuildRepoNameMapAsync(repoStore, ct);

        // ADR-018: how many hits were ranked WITHOUT a semantic score because their
        // embedding space differs from (or is unknown to) the server's current one.
        // Carried as a JSON field, NOT trailing prose — the tool output must stay valid
        // JSON for clients that parse it (recall's non-match branch is the only prose case).
        var foreignCount = hits.Count(h => h.ForeignEmbeddingSpace);
        var spaceNote = foreignCount > 0
            ? $"{foreignCount} mem(s) ranked by content only — their embedding space differs from the server's current one (foreign/unknown); re-index or backfill to restore semantic ranking."
            : null;

        return JsonSerializer.Serialize(new
        {
            count = hits.Count,
            spaceNote,
            memories = hits.Select(h => new
            {
                id = h.Memory.Id,
                content = h.Memory.Content,
                scope = h.Memory.Scope,
                scopeId = h.Memory.ScopeId,
                repository = ResolveRepoName(h.Memory, repoNames),
                topic = h.Memory.Topic,
                importance = h.Memory.Importance,
                score = Math.Round(h.Score, 4),
                foreignEmbeddingSpace = h.ForeignEmbeddingSpace,
                relatedFqns = h.Memory.RelatedFqns,
                createdAt = h.Memory.CreatedAt,
                lastAccessedAt = h.Memory.LastAccessedAt,
                accessCount = h.Memory.AccessCount,
            })
        }, JsonOpts);
    }

    [McpServerTool, Description(
        "List memories matching a scope/topic filter. No semantic search, no cost for embedding. " +
        "USE for: auditing what's stored, finding a memory to forget, checking before you save a duplicate. " +
        "Unlike RecallMemory, this returns near-forgotten rows too so you can explicitly ForgetMemory them. " +
        "For normal retrieval before working, use RecallMemory instead. Requires Memory__Enabled=true.")]
    public static async Task<string> ListMemories(
        [Description("Scope filter: 'session' | 'project' | 'global' | 'all' (default 'all')")] string? scope = null,
        [Description("For scope='project': repository NAME (preferred). Alternative to scopeId UUID.")] string? repository = null,
        [Description("Optional scope_id UUID (alt to `repository`)")] string? scopeId = null,
        [Description("Filter by topic")] string? topic = null,
        [Description("Max results, 1..500 (default 50)")] int limit = 50,
        IAgentMemoryStore store = default!,
        IRepositoryStore repoStore = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (scope is not null && scope != "all" && !MemoryScope.IsValid(scope))
            return $"Error: invalid scope '{scope}'.";
        if (!MemoryTopic.IsValid(topic))
            return $"Error: invalid topic '{topic}'.";

        // Resolve repository name → UUID if project scope.
        var resolvedScopeId = await ResolveProjectScopeIdAsync(
            scope, repository, scopeId, repoStore, ct);
        if (resolvedScopeId.Error is not null) return resolvedScopeId.Error;
        scopeId = resolvedScopeId.ScopeId;

        var memories = await store.ListAsync(
            scope, scopeId, topic, Math.Clamp(limit, 1, 500), ct);

        var repoNames = await BuildRepoNameMapAsync(repoStore, ct);

        return JsonSerializer.Serialize(new
        {
            count = memories.Count,
            memories = memories.Select(m => new
            {
                id = m.Id,
                content = m.Content,
                scope = m.Scope,
                scopeId = m.ScopeId,
                repository = ResolveRepoName(m, repoNames),
                topic = m.Topic,
                importance = m.Importance,
                relatedFqns = m.RelatedFqns,
                createdAt = m.CreatedAt,
                lastAccessedAt = m.LastAccessedAt,
                accessCount = m.AccessCount,
            })
        }, JsonOpts);
    }

    [McpServerTool, Description(
        "Delete a specific memory by UUID. " +
        "USE when: the user says 'forget that', you saved something wrong, you saw a stale/obsolete memory in RecallMemory/ListMemories. " +
        "Get the id from a prior RecallMemory or ListMemories call. " +
        "Returns { forgotten: true } on success, { forgotten: false, reason: 'not_found' } otherwise. " +
        "Requires Memory__Enabled=true.")]
    public static async Task<string> ForgetMemory(
        [Description("UUID of the memory to delete")] string? id = null,
        IAgentMemoryStore store = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (string.IsNullOrWhiteSpace(id))
            return "Error: 'id' is required (memory UUID).";
        if (!Guid.TryParse(id, out var guid))
            return $"Error: invalid UUID '{id}'.";

        var removed = await store.ForgetAsync(guid, ct);
        return removed
            ? JsonSerializer.Serialize(new { forgotten = true, id = guid }, JsonOpts)
            : JsonSerializer.Serialize(new { forgotten = false, id = guid, reason = "not_found" }, JsonOpts);
    }

    [McpServerTool, Description(
        "Clear all transient SESSION-scoped memories for a session id (the working-memory scratchpad). " +
        "USE when a task or conversation ends, to drop short-lived state so it doesn't linger as noise. " +
        "Only deletes scope='session' rows for the given sessionId — project and global memories are never touched. " +
        "(Session memories also auto-decay within ~1-2 days, so this is for proactive cleanup.) " +
        "Returns { cleared: true, deleted: N }. Requires Memory__Enabled=true.")]
    public static async Task<string> ClearSession(
        [Description("The session id (the client-supplied session UUID used when saving scope='session' memories)")] string? sessionId = null,
        IAgentMemoryStore store = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;
        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: 'sessionId' is required (the session UUID whose session memories to clear).";

        var deleted = await store.ClearSessionAsync(sessionId, ct);
        return JsonSerializer.Serialize(new { cleared = true, sessionId, deleted }, JsonOpts);
    }

    /// <summary>
    /// Build an id→name map of all repositories so memory output can show which project a
    /// project-scoped memory belongs to (recall scope='all' mixes repos). Repos are few, so
    /// one ListAsync per call is cheap.
    /// </summary>
    private static async Task<Dictionary<string, string>> BuildRepoNameMapAsync(
        IRepositoryStore repoStore, CancellationToken ct)
    {
        var repos = await repoStore.ListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in repos)
            map[r.Id.ToString()] = r.Name;
        return map;
    }

    /// <summary>
    /// Resolve a memory's project name: the repo name for a project-scoped memory whose scope_id
    /// matches a known repository, else null (session/global, or an orphan scope_id from a deleted repo).
    /// </summary>
    private static string? ResolveRepoName(AgentMemory memory, IReadOnlyDictionary<string, string> repoNames)
        => memory.Scope == MemoryScope.Project
           && memory.ScopeId is { } id
           && repoNames.TryGetValue(id, out var name)
            ? name
            : null;

    /// <summary>
    /// Resolves the effective project scope_id from (repository name, scopeId UUID).
    /// Rules (v0.8.3 Option A):
    ///   - If `repository` is provided: resolve via RepoResolver. If it resolves, that wins
    ///     (even if a scopeId UUID was also passed). If it doesn't resolve, return error.
    ///   - Else if `scopeId` is a valid UUID: pass through as-is.
    ///   - Else return (null, null) — caller decides whether the lack-of-id is an error.
    /// This helper keeps the 3 memory tools' resolution logic consistent.
    /// </summary>
    private static async Task<(string? ScopeId, string? Error)> ResolveProjectScopeIdAsync(
        string? scope,
        string? repository,
        string? scopeId,
        IRepositoryStore repoStore,
        CancellationToken ct)
    {
        // Non-project scope doesn't need resolution. scopeId passes through as-is
        // (session scope uses a client session UUID; global requires no id).
        if (scope != MemoryScope.Project)
            return (scopeId, null);

        // Repository name takes precedence over scopeId (v0.8.3 rule).
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var resolved = await RepoResolver.ResolveAsync(repository, repoStore, ct);
            if (resolved is null)
                return (null,
                    $"Error: repository '{repository}' not found. " +
                    "Call ListRepositories() to see available repositories by name.");
            return (resolved.Value.ToString(), null);
        }

        // Fall back to scopeId UUID. Validate it's actually a UUID so a mistyped
        // non-UUID string doesn't silently become an orphan scope_id.
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            if (!Guid.TryParse(scopeId, out _))
                return (null,
                    $"Error: scopeId '{scopeId}' is not a valid UUID. " +
                    "Pass `repository` name instead (preferred), or a valid repositories.id UUID.");
            // Optional: could also verify the UUID actually exists in repositories.
            // We skip that check here to keep the tool fast; an orphan scope_id
            // simply means no recall will ever match it, not a fatal error.
            return (scopeId, null);
        }

        return (null, null);
    }
}
