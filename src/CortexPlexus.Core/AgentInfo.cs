namespace CortexPlexus.Core;

/// <summary>
/// Single source of truth for the Local Agent version string. Used by:
///  - CortexPlexus.Agent CLI's `version` command
///  - CortexPlexus.App's `/api/agent/version` endpoint response
///  - ActivateAgent MCP tool's version-check step
/// Bump whenever an agent-visible change ships — new wire-protocol fields,
/// changed CLI args, or tightened error handling that users on older builds
/// should re-download to get.
/// </summary>
public static class AgentInfo
{
    // 1.2.0: stale-symbol fix (relative-path hash diff, deletedFiles + fullFileSnapshot
    // wire fields) + zombie-watch fix (flush re-queue/backoff, fail-fast exit,
    // /api/agent/heartbeat liveness reporting).
    public const string Version = "1.2.0";
}
