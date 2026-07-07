# ADR-027: Memory M4+M5 — code-graph binding (drift detection) & maintenance report

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [MEMORY-V2-ASSESSMENT](../research/MEMORY-V2-ASSESSMENT.md) §3.T3, §3.C1, §5.M4–M5
**Depends on:** ADR-025 (update/supersede, feedback), ADR-026 (status model)

## Context

### The unclaimed moat (M4)

CP is the only memory system that lives **next to a live code graph** — yet the two
never talk. `related_fqns TEXT[]` is a soft link that is:

- **never validated at save** — a typo'd FQN silently links to nothing (the exact bug
  class R25 fixed for search tools with parent-walk hints);
- **never re-checked afterwards** — when a re-index deletes or renames a symbol, every
  memory pointing at it keeps surfacing as if the code still existed. A memory saying
  "workaround for `Foo.Bar()` race" outlives `Foo.Bar()` itself with no signal — the
  memory-side twin of the false-freshness problem ADR-015 solved for the index.

Generic memory systems (Mem0, Zep, Letta) *cannot* do this — they have no ground truth
to check against. CP does: `code_symbols` is refreshed on every index run.

### No reflection tier (M5)

The store only accumulates raw entries (115 as of 2026-07-07). Known consolidation debt,
visible to any operator: the R17/R18/R19 embedding-throughput lessons exist as separate
fragments that should be one playbook. Generative-Agents and Letta both ship a
reflection/consolidation tier; CP has neither the mechanism nor even the *visibility*
(nothing reports near-duplicates, orphaned links, or never-recalled rows).

Constraint (VISION non-goal #3): no LLM in the server. Consolidation *judgment*
(what to merge, how to phrase a synthesis) belongs to the agent; the server's job is the
*measurement* that makes a maintenance session cheap and targeted.

## Decision

### 1. Validate `relatedFqns` at write (M4a)

`save_memory` / `update_memory` check each FQN against `code_symbols` (one indexed
`= ANY(@fqns)` query — `idx_symbols_fqn` exists):

- exact hit → link stored as today;
- near-miss → warning in the response + up to 3 suggestions (reusing the R25
  parent-walk / anchored-match helpers); stored anyway (the agent may be linking code
  that isn't indexed yet — a warning, not a gate).

### 2. Drift detection at re-index (M4b)

New column: `code_drifted BOOLEAN NOT NULL DEFAULT FALSE`.

After an index commit for repo R (both pipeline and agent-upload paths), one set-based
SQL pass re-resolves the `related_fqns` of R-scoped memories against the fresh
`code_symbols`:

- any linked FQN vanished → `code_drifted = TRUE`;
- all resolve again (rename reverted, symbol restored) → flag clears.

Recall output renders the flag as `⚠️ linked code changed since this was saved — verify
before trusting` — same honesty pattern as ADR-015's freshness verdicts and the
`.claude` file-memory's "point-in-time observation" reminder, now automated by ground
truth instead of prompted by convention. Drifted memories also get a mild rank penalty
(×0.9), never exclusion — drift is a caution, not a verdict.

Cost note: the pass touches only memories whose scope_id = R (tens of rows) with one
array-resolve query — unmeasurable next to the index run it piggybacks on.

### 3. Working-set recall (M4c)

`recall_memory(nearFqns: string[])` — optional parameter that boosts memories whose
`related_fqns` intersect (or share a namespace prefix with) the given symbols. This is
the memory leg of ADR-019's `get_context_pack` (§sections table: "Memories") — the pack
passes its task-relevant symbols, and memory recall becomes *location-aware*: bug notes
about the code you are touching outrank generic lessons.

### 4. Maintenance report (M5) — server measures, agent thinks

New tool: `get_memory_maintenance_report(repository?)` returning a compact digest:

| Section | Mechanism (all SQL/pgvector — no LLM) |
|---|---|
| **Near-duplicate clusters** | pairwise cosine over the (small) store, greedy clustering at ≥0.80; emits cluster members + sizes |
| **Drifted** | `WHERE code_drifted` |
| **Misleading** | `misleading_count > 0` (ADR-025 feedback) — refute candidates |
| **Stale-unused** | never recalled (or no feedback) in N days AND score sinking — archive-or-synthesize candidates |
| **Promotion candidates** | project memories with useful-feedback from ≥2 distinct `origin_repo`s (ADR-025 provenance) — suggest `scope='global'` (MEMORY-V2 §M6.20) |

The report is the input to a periodic agent-driven maintenance session (the `/reflect`
ritual): the agent reads clusters, writes syntheses via `update_memory` + `supersedes`,
refutes with reasons, promotes cross-project lessons. Server-side auto-merge is
explicitly **not** performed.

## Alternatives considered

### A. Hard FK from `related_fqns` to `code_symbols`
Symbols are deleted/recreated wholesale on every full re-index — an FK would either
cascade-destroy memory links (data loss) or block indexing (the R27-1 bug class).
Soft links + a reconciliation pass is the correct coupling. **Rejected.**

### B. Auto-refute/auto-archive drifted memories
Code drift ≠ knowledge invalid (a lesson about a deleted class often generalizes).
Only the agent can judge; the server only flags. **Rejected** (same server/agent split
as ADR-025's reconciliation).

### C. LLM-powered consolidation in the server (Letta sleep-time style)
VISION non-goal #3. The report achieves the targeting; the intelligence already sits in
the client. **Rejected.**

### D. Embedding-cluster maintenance as an offline script instead of a tool
Scripts rot and need operator hands; a tool makes maintenance available to the agent in
any session, which is who performs it anyway. **Rejected.**

## Consequences

**Positive**
- Memory gains what no competing system has: links that *know* when the code moved
  under them. Trust framing (ADR-015 for the index, ADR-018 for vectors) now covers the
  third leg — memories.
- Consolidation debt becomes visible and cheap to service; the store trends toward
  fewer, denser, better-linked entries instead of monotonic fragment growth.
- Location-aware recall closes the loop with `get_context_pack` — memories arrive
  exactly where they apply.

**Negative / cost**
- One more post-index step (bounded, set-based); one more flag to render everywhere
  memory rows are shown.
- Near-duplicate clustering is O(n²) cosine on the store — fine at 10³ rows; the report
  caps at the top clusters and the tool documents the bound (revisit with an index-
  assisted approach if stores reach 10⁵).
- Namespace-prefix matching in `nearFqns` boosting can over-match monorepo-style giant
  namespaces; boost weights stay small and tunable.
- Maintenance still requires an agent session to happen — the report makes it cheap,
  not automatic. (Client-side: a periodic `/reflect` cadence; out of server scope.)

## Verification / acceptance

1. **Unit:** FQN validation — exact pass-through, near-miss suggestions, unknown-but-
   stored warning; drift pass sets/clears flag correctly on vanish/restore; rank penalty
   applied.
2. **Unit:** `nearFqns` boosts exact-link > namespace-prefix > unlinked; report sections
   each fire on seeded fixtures (dup cluster, drifted row, misleading row, promotion
   candidate with 2 origin repos).
3. **Integration:** index repo → save memory linked to a real method → delete the method
   → re-index → recall shows the ⚠️ drift warning; restore method → re-index → warning
   gone.
4. **Live drill:** run the maintenance report against the real 115-row store; perform
   one full `/reflect` consolidation session off it (merge the R17/R18/R19 fragments
   into one playbook memory with supersedes) — the session itself is the acceptance test,
   and its before/after counts seed MEMORY-V2 §7's duplicate-rate baseline.

## References

- ADR-015 (freshness-honesty pattern this extends to memories) · ADR-019 (context-pack
  consumer of `nearFqns`) · ADR-025/026 (feedback, provenance, status machinery)
- R25 parent-walk hints (reused for FQN suggestions) · R27 (drift/wrongness evidence)
- Generative Agents & Letta reflection tiers (the pattern, minus in-server LLM)
- MEMORY-V2-ASSESSMENT §3.T3, §3.C1, §5.M4–M5, §7 (metrics this work must move)
