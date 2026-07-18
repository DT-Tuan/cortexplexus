namespace CortexPlexus.App.Api.Dto;

/// <summary>
/// Payload sent by CortexPlexus.Agent after local parsing.
/// Contains only metadata — no source code bodies.
/// </summary>
public sealed record IndexResultsRequest
{
    public required string ProjectName { get; init; }
    public required IReadOnlyList<SymbolDto> Symbols { get; init; }
    public required IReadOnlyList<RelationshipDto> Relationships { get; init; }
    public required Dictionary<string, string> FileHashes { get; init; }

    /// <summary>
    /// Project-relative paths of files the agent knows are gone (watch delete events,
    /// server-vs-local hash diff). The server drops their symbols, graph vertices and
    /// file hashes. NEW in agent v1.2.0 — older agents simply omit it.
    /// </summary>
    public IReadOnlyList<string>? DeletedFiles { get; init; }

    /// <summary>
    /// True when <see cref="FileHashes"/> is the COMPLETE file list of a successful full
    /// index run (not an incremental watch batch). Arms the server-side stale-symbol
    /// sweep: every stored symbol whose file is absent from the snapshot is deleted.
    /// This is what makes force_reindex actually shed symbols of deleted files — the
    /// hash-diff can't see them once the hash cache is wiped, the snapshot can.
    /// NEW in agent v1.2.0.
    /// </summary>
    public bool FullFileSnapshot { get; init; }
}

/// <summary>
/// Flat representation of any CodeSymbol subtype.
/// The 'kind' field determines which optional fields are relevant.
/// </summary>
public sealed record SymbolDto
{
    // --- Common (all symbols) ---
    public required string Fqn { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }

    // --- ClassInfo ---
    public string? Accessibility { get; init; }
    public bool? IsAbstract { get; init; }
    public bool? IsStatic { get; init; }
    public bool? IsSealed { get; init; }
    public bool? IsPartial { get; init; }
    public string? BaseTypeFqn { get; init; }
    public IReadOnlyList<string>? InterfaceFqns { get; init; }

    // --- MethodInfo / ConstructorInfo ---
    public string? Signature { get; init; }
    public string? ReturnType { get; init; }
    public bool? IsAsync { get; init; }
    public bool? IsVirtual { get; init; }
    public bool? IsOverride { get; init; }
    public bool? IsTestMethod { get; init; }
    public string? ContainingTypeFqn { get; init; }
    public IReadOnlyList<ParameterDto>? Parameters { get; init; }

    // --- InterfaceInfo ---
    public IReadOnlyList<string>? MemberFqns { get; init; }

    // --- PropertyInfo ---
    public string? Type { get; init; }
    public bool? HasGetter { get; init; }
    public bool? HasSetter { get; init; }

    // --- Common optional ---
    public string? Documentation { get; init; }
    public string? AiSummary { get; init; }

    // --- DocumentSection ---
    public int? Level { get; init; }
    public string? Content { get; init; }
    public string? DocumentPath { get; init; }

    // --- DbContextInfo ---
    public IReadOnlyList<DbSetDto>? DbSets { get; init; }

    // --- DiRegistrationInfo ---
    public string? ServiceTypeFqn { get; init; }
    public string? ImplementationTypeFqn { get; init; }
    public string? Lifetime { get; init; }
    public string? ModuleName { get; init; }

    // --- ApiEndpointInfo ---
    public string? HttpMethod { get; init; }
    public string? RouteTemplate { get; init; }
    public string? HandlerMethodFqn { get; init; }
    public string? EndpointName { get; init; }
    public string? Summary { get; init; }
}

public sealed record ParameterDto(string Name, string Type, int Position);

public sealed record DbSetDto(string EntityTypeFqn, string PropertyName, string? TableName);

public sealed record RelationshipDto
{
    public required string FromFqn { get; init; }
    public required string ToFqn { get; init; }
    public required string Type { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Liveness report from a `cortexplexus-agent watch` process. Sent on watch start,
/// after every flush (success or failure) and on a periodic idle timer.
/// </summary>
public sealed record AgentHeartbeatRequest
{
    public required string ProjectName { get; init; }
    public string? AgentVersion { get; init; }

    /// <summary>Last confirmed successful sync (upload or verified no-change check). Null when the agent has none to report.</summary>
    public DateTimeOffset? LastSyncUtc { get; init; }

    /// <summary>Consecutive flush failures at the time of the heartbeat. 0 = healthy.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Message of the most recent flush failure, if any.</summary>
    public string? LastError { get; init; }
}

public sealed record IndexResultsResponse
{
    public required string Project { get; init; }
    public required int Symbols { get; init; }
    public required int Relationships { get; init; }
    public required int Embeddings { get; init; }
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// Number of symbol rows that landed in <c>code_symbols</c> after this chunk
    /// (with or without an embedding column value). Equals <see cref="Symbols"/>
    /// after dedup minus <see cref="SymbolsFailed"/>. NEW in v0.7.0.
    /// </summary>
    public required int SymbolsPersisted { get; init; }

    /// <summary>
    /// Number of symbol rows whose batch upsert dropped them. Non-zero means
    /// the chunk is incomplete — the agent treats this as a hard error and
    /// aborts the chunked upload. NEW in v0.7.0.
    /// </summary>
    public required int SymbolsFailed { get; init; }

    /// <summary>
    /// Number of <c>code_symbols</c> rows where <c>embedding IS NOT NULL</c>
    /// landed in this chunk. Distinguishes "row inserted with NULL embedding"
    /// (expected for non-embeddable kinds) from "row inserted with vector".
    /// Always ≤ <see cref="SymbolsPersisted"/>. NEW in v0.7.0.
    /// </summary>
    public required int VectorRowsWritten { get; init; }

    /// <summary>Human-readable warning messages collected during persist. Empty on success.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Number of distinct files whose stale symbols/hashes were removed by this request
    /// (explicit <see cref="IndexResultsRequest.DeletedFiles"/> + snapshot sweep). NEW in v0.9.
    /// </summary>
    public int StaleFilesRemoved { get; init; }

    /// <summary>
    /// DEPRECATED in v0.7.0, removed in v0.8.0. Computed alias of
    /// <see cref="SymbolsPersisted"/>. The historical name was misleading because
    /// the value counts symbol rows, not embedding rows. Kept for one release so
    /// agent v1.1.0 binaries still parse responses from a v0.7.0 server.
    /// </summary>
    public int EmbeddingsPersisted => SymbolsPersisted;

    /// <summary>
    /// DEPRECATED in v0.7.0, removed in v0.8.0. Computed alias of
    /// <see cref="SymbolsFailed"/>.
    /// </summary>
    public int EmbeddingsFailed => SymbolsFailed;
}
