# ADR-025: Memory M1+M2 — write-time reconciliation, `update_memory`, hybrid recall, selective reinforcement

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [MEMORY-V2-ASSESSMENT](../research/MEMORY-V2-ASSESSMENT.md) §3.W1–W3, §3.Q1–Q2, §3.T1, §5.M1–M2

## Context

Four defects share one root: the memory store treats every write and read as independent
events, with no model of *how knowledge evolves*.

1. **Blind INSERT (W1).** `save_memory` never checks whether the store already knows the
   fact. The dedup burden is pushed to the agent by convention ("check for an existing
   file first" lives in CLAUDE.md guidance, not in the tool). At 105+ memories the store
   demonstrably accumulates near-duplicates (R17/R18/R19 embedding lessons exist as
   separate fragments).
2. **No `update_memory` (W2).** Correcting a memory = `forget` (hard DELETE, loses
   access history) + `save` (new identity, no link to the old). There is no way to say
   "this supersedes that".
3. **Vector-only recall (Q1).** Code search is triple-hybrid with RRF (Phase 2/5);
   memory recall is cosine × decay only (`AgentMemoryStore.RecallAsync`,
   `AgentMemoryStore.cs:109-116`). Queries dominated by exact identifiers ("R27",
   "ef_search", an FQN, an error code) — BM25's home turf — systematically miss.
   The FTS/RRF machinery already exists in `CortexPlexus.Search`; memory never got it.
4. **Indiscriminate reinforcement (T1).** Recall bumps `last_accessed_at` on **every
   returned row** (`RecordAccessAsync`, `AgentMemoryStore.cs:120-122`), and decay is
   keyed on `last_accessed_at` (`MemoryScoring.ScoreSqlExpression`). A wrong-but-
   frequently-matching memory is therefore refreshed forever. Real damage on record:
   R27's two wrong hypotheses (both later disproven by static analysis) kept surfacing
   and misdirected two investigations.

Mem0's published write pipeline (compare candidate fact against similar memories →
ADD/UPDATE/DELETE/NOOP) and Zep/Graphiti's hybrid retrieval demonstrate the target
shapes. Constraint from VISION non-goal #3: **no LLM inside the server** — CP supplies
mechanics (similarity, links, ranking signals); the agent supplies judgment.

## Decision

### 1. Write-time reconciliation (server proposes, agent decides)

`save_memory` gains a pre-insert similarity probe against the same scope:

```
top-3 by cosine within scope, threshold ≥ 0.83 (initial; tuned via §Verification)
├─ no hit            → INSERT (today's behavior, zero extra round trips)
└─ hit(s)            → NO insert; return compact:
   { status: "similar_found",
     candidates: [ { id, content (first 200 chars), topic, score, updatedAt } ],
     options: "save_memory(force:true) | update_memory(id, …) | drop it" }
```

- `force: true` bypasses the probe (agent judged it genuinely new).
- The probe reuses the query-side embedding that the save already computes — **zero
  additional embedding calls**; one extra indexed vector lookup.
- Degraded saves (ADR-024, no embedding) skip the probe — reconciliation for them
  happens at backfill time as a report line, never as an auto-action.

### 2. `update_memory` + supersede links (knowledge gets a version chain)

```
update_memory(id, content?, topic?, importance?, relatedFqns?, appendContent?)
```

- In-place edit; re-embeds when content changed (through the ADR-024 degraded path if
  the provider is down); stamps `updated_at`; **preserves identity, access history,
  and provenance**.
- `save_memory(supersedes: <id>)` for replacement-shaped corrections: old row gets
  `status='superseded'` + the new row records `supersedes` (columns land in ADR-026's
  status model; until ADR-026 ships, `supersedes` is recorded and the old row is
  down-ranked, not re-statused).
- Provenance columns written on every save/update: `origin_session TEXT`,
  `origin_repo UUID` (the repo context the agent was working in — meaningful even for
  `global` memories), `source TEXT` (free-form: `benchmark`, `user-directive`,
  `hypothesis`, …). These are what make "trust this memory?" answerable — today's rows
  carry no origin at all.

### 3. Hybrid recall (give memory the same retrieval CP gives code)

- Additive generated column:
  `content_fts tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED`
  + GIN index.
- `RecallAsync` runs two legs — existing vector×decay, new BM25 (`ts_rank`) ×decay —
  fused with the existing RRF implementation (k=60, same constants as code search).
  Embedding-unavailable recall degrades to the BM25 leg alone (strictly better than
  today's filter-only fallback).
- **Explainable score:** each hit returns
  `{ score, factors: { semantic, exact, decay, importance } }` (compact, per ADR-021)
  instead of one opaque number — the agent can see *why* something surfaced and weigh
  trust accordingly.
- **Context-aware boost:** multiply a small factor by origin — same-repo ×1.2,
  global ×1.1, foreign-repo ×1.0 (initial values; measured then tuned). Cross-project
  recall stays fully enabled — the boost orders, never filters.

### 4. Selective reinforcement (stop refreshing wrong knowledge)

- `RecallAsync` **stops auto-bumping** `last_accessed_at` on return.
- New lightweight tool: `memory_feedback(ids: uuid[], useful: bool)` —
  `useful:true` bumps `last_accessed_at`/`access_count` (exactly the old effect, now
  earned); `useful:false` records a `misleading_count` (input to ADR-026's refute flow
  and ADR-027's maintenance report).
- Client-side automation note (not server scope): the cortex-mcp hook can auto-call
  feedback at turn end for memories the agent actually cited — keeping the burden off
  the agent's discipline.
- Transition guard: rows never fed back neither gain nor lose vs today — decay simply
  runs from their last genuine access. The Weibull curve and λ values (ADR-012) are
  unchanged.

## Alternatives considered

### A. Server-side auto-merge on high similarity (full Mem0 UPDATE/NOOP)
Requires judging whether two texts are the *same fact* — an LLM call. VISION non-goal #3.
The candidates-back-to-agent design gets the same dedup outcome with the intelligence
where it already lives. **Rejected.**

### B. Cross-encoder / LLM reranker on recall
Same non-goal; RRF + explainable factors first. Revisit only with eval-harness evidence
(ADR-021 §2 discipline) that fusion ranking is the bottleneck. **Deferred.**

### C. Reinforcement via implicit signals (recall count) instead of explicit feedback
That is exactly today's touch-on-read bug — being returned is not being useful.
**Rejected** (it's the defect, not a design option).

### D. Separate `memory_versions` history table
Full audit trail, but the supersede *chain* (old row retained + link) already provides
recoverable history with zero new tables. **Rejected** for now.

## Consequences

**Positive**
- Duplicate growth stops at the source; corrections become first-class (identity-
  preserving) instead of delete-and-retype.
- Exact-identifier recall (the systematic miss class) is fixed with infrastructure CP
  already trusts; recall quality becomes explainable rather than oracular.
- Wrong memories stop being self-refreshing; usefulness becomes an earned, recorded
  signal that ADR-026/027 build on.
- Provenance makes cross-project trust decisions ("learned where? from what?") possible.

**Negative / cost**
- `save_memory` becomes two-step when similars exist — one extra round trip, only in the
  case where blind insert was doing damage. `force:true` keeps automation paths unblocked.
- Without feedback discipline (or the client hook), reinforcement signal goes quiet;
  acceptable — quiet decay is strictly safer than false reinforcement.
- FTS column + GIN index on a ~100–1000-row table: negligible storage; English-config
  tokenization is imperfect for Vietnamese content (memories are mixed-language) — noted;
  `simple` config fallback is a one-line change if measured recall favors it.
- Threshold 0.83 will need tuning; shipped behind a config knob
  (`Memory__ReconcileThreshold`) with the eval harness deciding, not vibes.

## Verification / acceptance

1. **Unit:** probe returns candidates ≥ threshold, same-scope only; `force` bypasses;
   degraded save skips probe. `update_memory` re-embeds on content change, preserves
   `access_count`/provenance; `supersedes` links both directions.
2. **Unit:** RRF fusion — a query matching a memory only lexically (e.g. `"R27"`) must
   surface it top-3 (fails today); semantic-only match still surfaces; factors sum
   coherently.
3. **Unit:** recall does not touch `last_accessed_at`; `memory_feedback(useful:true)`
   does; `useful:false` increments `misleading_count`.
4. **Eval (golden set):** ~30 real queries mined from session history (mix: exact ids,
   concepts, Vietnamese phrasing) with expected-hit labels; assert recall@5 ≥ baseline
   +30% with hybrid on; threshold sweep for 0.83 documented in the PR.
5. **Live:** save the ADR-022 lesson twice with different wording → second save returns
   `similar_found` with the first as candidate (the exact dup-class that motivated W1).

## References

- Mem0 write-pipeline pattern (reconcile-at-write, agent-adjudicated here) ·
  Zep/Graphiti hybrid retrieval · Generative-Agents scoring (already ≈ ADR-012)
- `AgentMemoryStore.cs:91-149` (recall + touch-on-read), `MemoryScoring.cs` (decay)
- R27 wrong-hypothesis incidents (the T1 evidence) · ADR-012 (decay unchanged) ·
  ADR-018 (space stamps) · ADR-021 (output shape) · ADR-024 (degraded-save interplay)
