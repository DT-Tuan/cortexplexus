namespace CortexPlexus.Core.Models;

public sealed record IndexingJob(
    string FilePath,
    Guid RepoId,
    ChangeType ChangeType
);

public enum ChangeType { Created, Modified, Deleted }

public sealed record RepositoryInfo(
    Guid Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastIndexed
);

/// <summary>
/// Last-reported state of the watch agent feeding a repository. A row exists only for
/// repos that a `cortexplexus-agent watch` process has ever heartbeated. LastSync is
/// the agent's last confirmed successful sync (upload or verified no-change check) —
/// a fresh LastHeartbeat with an old LastSync is the zombie-watch signal.
/// </summary>
public sealed record WatchStatusInfo(
    Guid RepoId,
    string AgentVersion,
    DateTimeOffset LastHeartbeat,
    DateTimeOffset? LastSync,
    int ConsecutiveFailures,
    string? LastError
);
