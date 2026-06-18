# ADR-015: Content-aware index freshness (kill the time-based false-STALE)

**Status:** Proposed
**Date:** 2026-06-18

## Context

`list_repositories` and the search-style tools surface an index-freshness signal via
`StalenessLabel` (`src/CortexPlexus.App/Mcp/Tools/StalenessLabel.cs`). It is **purely
wall-clock based**:

```text
age = now - repositories.last_indexed
< 6h      → (no label, fresh)
6h..24h   → "(N hours ago)"
24h..7d   → "(N days ago) ⚠️ STALE"
> 7d      → "(N days ago) 🚨 VERY STALE"
```

`last_indexed` is written **only when an actual index run commits**
(`IndexingPipeline.IndexAsync` → `RepositoryStore.UpdateLastIndexedAsync`, or the agent
upload's final commit). The search tools also emit `StalenessLabel.SearchFooter`, which
for any age ≥ 24h tells the agent:

> "Results may miss recent code changes. Run ActivateAgent … **before relying on these
> results**."

### The failure

Index a repo to 100% health, then return after a few idle days **without changing any
code**. The index content is still 100% accurate, yet:

1. `age` is now > 7d → label "🚨 VERY STALE".
2. The footer actively instructs the model to distrust the result.
3. Claude Code falls back to manual `grep`/`Read` — abandoning the MCP it should be using.

This is a **false negative driven by a clock**. The thing that actually invalidates an
index is **content drift** (files changed since indexing), not elapsed time. A repo
indexed 30 days ago with zero changes is perfectly fresh; a repo indexed 1 hour ago then
heavily edited is stale. Time is only a weak proxy and here it actively misfires.

This is the same _class_ of bug as [ADR-008](008-kind-aware-health-metric.md): a metric
that false-alarms a healthy repo into looking broken. ADR-008 fixed false `PARTIAL`
*health*; this ADR fixes false `STALE` *freshness*.

### Why the watch agent alone does not fix it

The Local Agent's `watch` mode (`ProjectFileWatcher`) re-indexes on file changes, keeping
the index correct **while it runs**. But:

- During pure idle (no edits), the watcher does nothing → `last_indexed` stays old → the
  repo is **still labelled VERY STALE**. Watching does not, by itself, refresh the signal.
- If the watcher is **not** running (reboot, never auto-started), real drift during idle
  is missed and the index genuinely can be stale.

So the watch agent is the right vehicle for *correctness*, but the *signal* must be made
content-aware, and the watcher must be made reliable + observable.

## Decision

Shift the freshness model from **"how old is the index?"** to **"has anything changed
since the index?"**, delivered as four complementary levers. Lever 3 is the immediate
signal fix; Levers 1/2/4 make freshness provable and self-healing.

### Lever 1 — Content-drift freshness via git identity (the root fix)

Persist the **git commit indexed** and tree-clean state on the `repositories` row, set by
whoever performs the parse (the agent/pipeline runs where the working tree + `.git` live):

- `indexed_commit TEXT` — `git rev-parse HEAD` at index time.
- `indexed_tree_dirty BOOLEAN` — whether `git status --porcelain` was non-empty at index time.

Freshness is then decided by comparing the **current** working-tree identity (cheap, runs
on the dev machine where Claude Code itself runs) against the stored one:

| Current vs stored | Verdict |
|---|---|
| same `HEAD` **and** clean tree | **FRESH — verified current** (trust MCP, regardless of age) |
| `HEAD` differs, or tree dirty | **DRIFTED — N commits / dirty** (re-index recommended) |
| no git, or unknown | fall back to the (relabelled) time hint of Lever 3 |

This reframes the question from a clock to a fact. A skill/hook does
`git rev-parse HEAD` + `git status --porcelain` and asks the server (or reads the
`list_repositories` line) for `indexed_commit`; equality ⇒ provably fresh.

### Lever 2 — Watch agent as freshness heartbeat + reliable auto-start + observability

- **Heartbeat:** add `last_verified_at TIMESTAMPTZ`. A running watcher writes it on
  startup and on a periodic tick (e.g. hourly) **even when nothing changed**, meaning
  "an agent confirmed the index is in sync as of now". New lightweight endpoint
  `POST /api/repos/{id}/heartbeat` (or piggyback on the existing agent upload contract).
- **Watched flag:** `list_repositories` shows `🟢 watched (live-synced)` when
  `last_verified_at` is within a freshness TTL (e.g. ≤ 2× heartbeat interval). A watched
  repo is treated as fresh regardless of `last_indexed` age, because drift would have been
  captured in near-real-time.
- **Auto-start:** make the watcher start reliably (extend the v0.8.4 VS Code auto-start
  recipe; document a `systemd --user` unit) so "watched" is the normal state, not the
  exception.

### Lever 3 — Relabel: separate *age* (info) from *trust* (action) — ships first

Stop conflating index age with index trustworthiness. Concretely:

- `StalenessLabel` keeps an **informational** age hint but drops the alarmist wording.
- `SearchFooter` only emits the "results may miss recent changes / re-sync before relying"
  warning when there is **evidence of drift** (Lever 1 commit mismatch, dirty tree, or
  known changed-but-unindexed files) — **not** for age alone.
- Pure age with no drift evidence: at most a soft `(indexed N days ago)`; never a directive
  to abandon the MCP.

This is a small, self-contained change that immediately stops the false-STALE from pushing
Claude Code off the MCP, even before Levers 1/2/4 land.

### Lever 4 — On-resume auto-verify (close the loop in the hook)

The `cortex-mcp` SessionStart hook runs the Lever-1 git check at session start:

- same commit + clean ⇒ print "index verified current — use MCP", done.
- drift ⇒ trigger an **incremental** re-index (changed files only — fast) before the
  session proceeds, so by the time Claude queries, the index is genuinely fresh **and**
  labelled fresh.

This makes freshness self-healing instead of leaving the model to judge a clock.

## Schema changes

```sql
ALTER TABLE public.repositories
    ADD COLUMN IF NOT EXISTS indexed_commit     TEXT,
    ADD COLUMN IF NOT EXISTS indexed_tree_dirty BOOLEAN,
    ADD COLUMN IF NOT EXISTS last_verified_at   TIMESTAMPTZ;
```

All nullable/additive — existing rows degrade gracefully to Lever-3 time hints until the
next index populates the git columns.

## Rollout (incremental — separate work item from Phase 1 multi-language)

- **B1 (signal fix, ship first):** Lever 3 relabel + footer gating. No schema, no agent
  change. Kills the user-reported false-STALE.
- **B2 (provable freshness):** schema migration + agent sends `indexed_commit` /
  `indexed_tree_dirty`; `list_repositories` shows the git-verified verdict; `cortex-mcp`
  skill does the local git compare (Lever 1).
- **B3 (heartbeat + watched):** `last_verified_at` + heartbeat endpoint + watcher tick +
  `🟢 watched` flag + reliable auto-start (Lever 2).
- **B4 (self-healing):** SessionStart hook auto-verify + incremental reindex on drift
  (Lever 4).

## Consequences

**Positive**
- Freshness reflects reality (content), not a clock → no more distrusting an accurate index.
- Claude Code keeps using the MCP after idle periods, which is the whole point of the tool.
- Watch agent gains a clear, observable contract ("watched ⇒ trust"); drift becomes
  self-healing.
- Reuses existing machinery: `file_hashes` for incremental reindex, the agent upload path,
  the SessionStart hook.

**Negative / cost**
- New columns + an agent/server contract bump; mixed-version agents must tolerate the new
  fields (additive, so safe).
- Git-based freshness only applies to git repos; non-git working trees fall back to the
  time hint (acceptable — the alarmist wording is gone in B1 regardless).
- Heartbeat adds light periodic write traffic (one row update per repo per interval).
- "Watched ⇒ fresh" trusts the watcher; a silently-dead watcher could keep a repo looking
  fresh — mitigated by the `last_verified_at` TTL (stale heartbeat ⇒ drop the flag).

## Alternatives considered

- **Just raise the time thresholds.** Rejected — moves the false-positive, doesn't remove
  it; a long idle still trips it eventually.
- **Server-side working-tree hashing.** Rejected for agent-uploaded repos — the source
  isn't on the server; only the agent/Claude side can see the live tree. Git SHA is the
  cheap signal that lives on the right side.
- **Drop the staleness signal entirely.** Rejected — genuine drift (edited-but-not-indexed)
  is a real failure mode worth warning about; we want it *content-driven*, not removed.

## Verification / acceptance

- Repo indexed, then time advanced past 7d with **no** code change → `list_repositories`
  reports FRESH/verified (not VERY STALE); search footer emits **no** "don't rely" warning.
- Same repo with an uncommitted edit / new commit → reported DRIFTED; footer warns and (B4)
  triggers an incremental reindex that returns it to FRESH.
- With watcher running and idle → `🟢 watched`; killing the watcher past the TTL drops the
  flag back to the git-verified verdict.
- Unit coverage for the new `StalenessLabel` semantics (age vs drift) mirrors the ADR-008
  health-label tests.
