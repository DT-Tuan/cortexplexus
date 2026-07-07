# ADR-023: Watch lifecycle self-service — `agent install`, heartbeat liveness, dead-watch surfacing

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-7 / Tier 1 · T1.6
**Completes:** [ADR-015](015-content-aware-index-freshness.md) Lever 2 / rollout B3 (heartbeat + watched flag + reliable auto-start)

## Context

The value of the index is proportional to its freshness (VISION principle 4), and
freshness in production depends entirely on per-repo watch agents that today must be
**manually supervised**. Three incidents in June–July 2026 traced to this gap:

1. **3-day total outage unnoticed** — the LXC host rebooted, containers had no restart
   policy, every watch agent's uploads failed. No signal surfaced anywhere until a user
   asked "is watch running?". (Fixed for the *server* by `restart: unless-stopped`,
   PR #25 — but the same blindness exists for every *agent*.)
2. **Watch never auto-started for a repo** — auto-start on the reference deployment runs
   through a hand-maintained reconcile script with a hardcoded repo allowlist; a repo
   missing from the list silently never gets watched.
3. **New repo indexed, watch forgotten** — MyFin was indexed one-shot (2026-07-02); watch
   had to be wired up manually afterwards (systemd unit, enable, verify — several manual
   steps an agent can get wrong).

### What exists today (verified 2026-07-07)

- **The agent cannot install itself as a service.** `cortexplexus-agent` has exactly six
  commands — `watch|index|status|stop|update|version` (`src/CortexPlexus.Agent/Program.cs:16-34`);
  a grep for `systemd|WindowsService|nssm|launchd|daemon|--install` across
  `src/CortexPlexus.Agent` returns zero hits. `watch` is a foreground process that relies
  on the caller to background/supervise it (`Program.cs:36-88`).
- **The runbook documents supervisors but automates nothing.**
  `docs/runbooks/agent-auto-start.md` covers systemd user units, Task Scheduler, NSSM,
  LaunchAgent, VS Code tasks — all with hand-edited placeholders (`<NAME>`, `<PATH>`,
  `<SERVER>`), and admits its own gaps (linger for headless Linux, `-AtLogOn` not firing
  on headless Windows, VS Code task dying with the editor).
- **`ActivateAgent` Step 8 only covers VS Code** (`AgentTools.cs:233-293`) — honest about
  not surviving reboot and not helping Rider/Neovim users.
- **The server has zero liveness signal.** The agent→server surface is three calls
  (hash fetch, results upload, version check) — no ping. The only liveness-adjacent
  datum is `repositories.last_indexed`, bumped **only when an upload lands**
  (`AgentApiEndpoints.cs:262`). A live-but-idle watcher and a dead watcher are
  **indistinguishable** server-side. `RepositoryInfo` has five fields — no `LastSeen`
  (`src/CortexPlexus.Core/Models/IndexingJob.cs:11-17`).
- ADR-015 already *designed* the fix's data model (Lever 2: `last_verified_at`,
  heartbeat endpoint, `🟢 watched` flag) but B3 never shipped. This ADR is its
  implementation vehicle, plus the install-automation piece ADR-015 scoped out.

## Decision

Three parts: make installing supervision **one command**, make liveness **observable**,
and make dead watches **visible where agents look**.

### 1. `cortexplexus-agent install` / `uninstall` (new CLI commands)

```
cortexplexus-agent install <path> --server <url> [--name <name>]
cortexplexus-agent uninstall [--name <name>]
```

`install` detects the real OS (`RuntimeInformation`, not the path heuristic
`AgentTools.cs:103` uses) and provisions the platform's native supervisor:

| Platform | Mechanism | Notes |
|---|---|---|
| Linux | systemd **user** unit `cortexplexus-watch@<name>.service` written to `~/.config/systemd/user/`, `daemon-reload`, `enable --now` | Prints a `loginctl enable-linger` hint (with explanation) when linger is off — the runbook's admitted gap, now surfaced at install time |
| macOS | LaunchAgent plist in `~/Library/LaunchAgents/`, `launchctl load` | `RunAtLoad` + `KeepAlive` |
| Windows | `schtasks` at-logon task with restart-on-failure | Prints NSSM guidance for headless servers (can't be silently automated — needs an external binary) |

The generated unit content mirrors the runbook's reference units (Restart=on-failure,
memory caps). `install` is **idempotent** — re-running updates the unit in place.
`uninstall` disables + removes. Unit generation is a pure, unit-testable function
(platform × params → file content + shell steps).

**`ActivateAgent` recipe change:** Step 6/8 collapse — the primary path becomes
`dotnet <agentDll> install <path> --server <url>` (one command replacing
nohup + tasks.json), with the VS Code task demoted to a fallback for environments
where a user-level supervisor is unavailable. The recipe keeps its verify step.

### 2. Heartbeat (implements ADR-015 B3 as specified)

- **Schema:** `ALTER TABLE repositories ADD COLUMN IF NOT EXISTS last_verified_at TIMESTAMPTZ;`
  (exactly ADR-015's column).
- **Endpoint:** `POST /api/repos/{name}/heartbeat` — body `{ agentVersion, watchedPath }`;
  resolves the repo like the upload path does (`_agent/<name>`), stamps
  `last_verified_at = NOW()`. Cheap single-row UPDATE; no auth change (same trust model
  as the existing upload endpoint).
- **Agent:** `watch` mode POSTs the heartbeat (a) on startup after the initial sync,
  (b) on a periodic tick (default 15 min, `--heartbeat-interval` to override), and
  (c) after every debounce-triggered sync. Failure to heartbeat is logged but never
  interrupts watching (server may be briefly down — the agent must outlive server
  restarts, which it already does via retry on upload).
- Wire note: this rides the same `1.1.0 → 1.2.0` agent version bump as ADR-022 —
  ship both in one agent release so the fleet updates once.

### 3. Surfacing — put the signal where agents already look

`list_repositories` per-repo output (injection point: the existing per-repo loop,
`GraphTraversalTools.cs:428-458`, same seam as the staleness suffix) gains one line
derived from `last_verified_at` with TTL = 2× heartbeat interval (ADR-015's rule):

```
Watch: 🟢 watched (heartbeat 4 min ago)            — last_verified_at within TTL
Watch: 🔴 watch DOWN (last heartbeat 3 days ago)   — stamp exists but exceeded TTL
Watch: ⚪ not watched — index updates only when run manually
                                                   — stamp NULL (pre-1.2.0 agent or never watched)
```

- Per ADR-015: a `🟢 watched` repo is treated as fresh regardless of `last_indexed` age
  (drift would have been synced in near-real-time), which finally makes the
  "idle-but-current" repo look as trustworthy as it is.
- `🔴 watch DOWN` is the alarm that was missing in all three incidents: it names the
  repo, the silence duration, and the recovery command
  (`cortexplexus-agent install ...` or `systemctl --user start ...`).
- The search-footer seam (`SearchTools.AppendStalenessFooter`, `SearchTools.cs:50-63`)
  stays reserved for *content drift* (ADR-015 B2) — watch-down is a repo-level ops
  signal, not a per-result trust signal, so it lives in `list_repositories` and
  `ActivateAgent`'s freshness block only. One signal per surface; no alarm spam.

### Explicitly out of scope

- **Session-gated start/stop policies** (start watch when an IDE session opens, stop
  after idle — the reconcile-script pattern) stay **external**. The product ships
  always-on supervision; resource-frugal session gating is a deployment choice layered
  on top of the same systemd units. Documented as an advanced pattern in the runbook.
- **Auto-restarting dead agents from the server** — the server cannot reach into dev
  machines (and must never try; the Local Agent trust model is one-directional by
  design, ADR/Phase 8 security hardening).

## Alternatives considered

### A. Keep documentation-only (status quo + better runbook)
The runbook has existed since v0.8.x and the incidents happened anyway. Placeholder
editing is exactly the step humans and agents fumble. **Rejected.**

### B. PID-file sync to the server
Agent uploads its PID state; server shows it. PID files are already stale-prone locally
(`PidManager` deletes dead entries only when `status` runs) and say nothing after a
reboot. A heartbeat is simpler and self-expiring. **Rejected.**

### C. Heartbeat piggybacked on the hash-fetch call instead of a new endpoint
`GET /api/index/{name}/hashes` fires only when a sync starts — an idle watcher never
calls it, which is precisely the case that needs the heartbeat. **Rejected.**

### D. Full agent-daemon rewrite (built-in scheduler, self-update loop, IPC)
Solves the same problems plus in-place updates, at 10× the surface. The unused
`AgentUpdater` instance in watch mode (`Program.cs:78`) hints at this ambition — resist
it; supervisors are the OS's job. **Rejected** (revisit only if `install` proves
insufficient across platforms).

## Consequences

**Positive**
- New-repo onboarding becomes: `ActivateAgent` → one `install` command → watched forever,
  reboot-proof. The MyFin manual-setup path disappears.
- The three June/July incident classes each get a tripwire: dead agents are visible
  (`🔴`), unwatched repos are labeled (`⚪`), and installs are uniform (no allowlist to
  forget).
- ADR-015's freshness model finally gets its Lever-2 data; `🟢 watched` + B2's git
  verification together make "trust the index" provable end-to-end.

**Negative / cost**
- Platform-specific install code is a maintenance surface (3 OS paths × quirks); mitigated
  by generating from the already-battle-tested runbook units and keeping generation pure +
  unit-tested.
- Heartbeat writes: one row-UPDATE per repo per 15 min — negligible (12 repos ⇒ ~1 150
  writes/day).
- "Watched ⇒ fresh" trusts the watcher process; a wedged-but-alive watcher would keep the
  flag green. TTL bounds the damage window; ADR-015 B2 (git-verified freshness) is the
  independent cross-check.
- Agents ≤1.1.0 never heartbeat → their repos show `⚪ not watched` even if an old-style
  watch runs. Acceptable: it nudges the one-time fleet update, and the label is honest
  about what the server can *prove*.

## Verification / acceptance

1. **Unit:** unit-file/plist/schtasks generation — content snapshot tests per platform ×
   params; idempotent re-install.
2. **Unit:** heartbeat endpoint stamps `last_verified_at`; unknown repo → 404; TTL logic
   (`🟢`/`🔴`/`⚪`) boundary tests mirroring `StalenessLabelTests`.
3. **Integration (Linux CI):** `install` → unit exists + enabled; simulated reboot
   (`systemctl --user restart` of the unit) → watch resumes; `uninstall` → gone.
4. **Live (dev PC + LXC):** install for one repo; kill the agent process; within 2×
   interval `list_repositories` shows `🔴 watch DOWN` with recovery command; restart →
   `🟢` returns. Reboot the dev PC → watch self-starts (linger on), heartbeat resumes
   without any manual step.

## References

- ADR-015 (Lever 2 / B3 — the data model this implements) · ADR-022 (shared 1.2.0 wire bump)
- `docs/runbooks/agent-auto-start.md` (reference supervisor configs the generator emits)
- Incidents: LXC reboot outage 2026-06-30 (PR #25), MyFin manual watch setup 2026-07-02
- VISION.md §2.2 GAP-7, §4 principle 4, §6 T1.6
