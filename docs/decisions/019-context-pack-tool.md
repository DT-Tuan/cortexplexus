# ADR-019: `get_context_pack` — one-call, token-budgeted orientation bundle

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-2 / Tier 1 · T1.2 (highest-ROI item of the tier)

## Context

Every working session an AI agent runs against a CP-indexed repo starts with the same
ritual: `list_repositories` (≈1.3K tokens of prose for 12 repos) → `recall_memory` →
2–3 exploratory `search_code`/`semantic_search` calls. Measured across recent sessions:
**3–5 round trips and 4–6K tokens before the first line of real work** — repeated after
every context compaction too. VISION's north-star metric targets <1.5K tokens for this.

The ingredients all exist server-side; nothing composes them:

- **Task-relevant code:** `HybridQueryRouter.SearchAsync` (hybrid + HyDE/multi-query,
  Phase 5).
- **Repo skeleton:** `IGraphStore.GetGraphOverviewAsync(repoId, nodeLimit, kindFilter)`
  (`IGraphStore.cs:37`, impl `AgeGraphStore.cs:1290-1371`) returns nodes + raw edges +
  total count with kind filtering — **but it is only wired to the Web UI REST endpoints**
  (`GraphApiEndpoints.cs:31,45`); no MCP tool calls it.
- **Framework anchors:** `onboard_project` fetches DI registrations, endpoints, entities
  (`ExploreTools.cs:190-261`).
- **Memories:** `AgentMemoryStore.RecallAsync` with decay × cosine ranking, `relatedFqn`
  filtering (`AgentMemoryStore.cs:91-149`).
- **Budget primitive:** `ContextCompressor` — token budget (default 4000, chars/4
  estimate), 3 verbosity levels L0/L1/L2, hard break-on-budget
  (`src/CortexPlexus.Search/ContextCompressor.cs`).

The closest composite, `explore_topic` (`ExploreTools.cs:24-181`), is *symbol*-centric
(drills into one top hit) not *task*-centric, applies per-section `.Take(10)` caps with
**no overall budget and no cross-section dedup**, and skips the repo skeleton entirely.

Prior art: Aider's repo-map solves cold-start orientation with a token-budgeted map whose
symbols are chosen by graph centrality — but from a tags file. CP has a *real* code graph
to build a better one from.

### Two latent bugs this work must fix on the way

1. `onboard_project` scopes results by **substring match on file path**
   (`FilePath.Contains(repository)`, `ExploreTools.cs:211-243`) — not by `repo_id`. A
   repo named `Core` would match half the fleet.
2. Its tool description promises "…and NuGet packages" which the implementation never
   fetches (`ExploreTools.cs:188-189`) — description drift.

## Decision

One new MCP tool:

```
get_context_pack(
    repository: string,          // required — the repo to orient in
    task?: string,               // optional — what the agent is about to do
    budget_tokens: int = 2000,   // hard cap on the response
    include?: string[]           // optional section filter, default all
)
```

### Sections (assembled server-side, one call)

| # | Section | Source | Default share of budget |
|---|---|---|---|
| 0 | **Trust header** | staleness label + embedding space (ADR-018) + watch status (ADR-023) + symbol/embedding counts | fixed ~50 tokens |
| 1 | **Repo skeleton** | `GetGraphOverviewAsync` kinds `[namespace, class, interface]`; rank by **degree** (fan-in + fan-out computed from the returned edge set); render as an indented tree of top-degree symbols | 30% |
| 2 | **Task-relevant symbols** | hybrid search on `task` (skipped when `task` omitted; budget reallocated to skeleton) | 35% |
| 3 | **Framework anchors** | DI registrations + API endpoints + entities — the `onboard_project` internals, **re-scoped to `repo_id`** (fixing bug 1) | 20% |
| 4 | **Memories** | `RecallAsync(query: task ?? repo name, scope: project + global, limit 5)` | 15% |

Assembly rules:

- **Global budget, not per-section caps.** Unused section budget spills to the next
  section in order (a repo with no endpoints gives its share to the skeleton).
  `ContextCompressor` levels degrade L2→L0 as pressure rises — reusing its existing
  level-selection logic with the pack's budget instead of the 4000 default.
- **Cross-section FQN dedup** — a symbol surfaced in the skeleton is not repeated in
  task results (the known `explore_topic` gap).
- **Compact rendering per ADR-021** — single-line records, no decorative headers.
- **Deterministic order** (trust → skeleton → task → anchors → memories) so agents can
  rely on positional skimming after the first use.
- `task` omitted ⇒ a pure orientation pack (skeleton + anchors + memories) — the
  "restore my bearings after compaction" call.

### Centrality: degree now, PageRank later

Confirmed absent: no PageRank/community/degree aggregation exists anywhere in
`CortexPlexus.Graph` (grep verified). First-order **degree centrality is computable
today, client-free, from `GraphOverview.Edges`** at zero new graph queries — and for
"which symbols matter in this repo" it is a good first proxy (Aider itself shipped
degree-ranked maps before PageRank). PageRank/Louvain land with GraphRAG-lite
(VISION T2.4) and upgrade this section without changing the tool contract.

## Alternatives considered

### A. Status quo — client composes orientation from N tool calls
The measured 4–6K-token, 3–5-round-trip baseline. No server-side dedup or budget is
possible across separate calls. **Rejected** — this ADR's raison d'être.

### B. Static repo-map generated at index time, served from cache
Cheaper reads, but: stale between indexes, can't incorporate `task`, and pack sections
(memories, trust header) are inherently query-time. **Partially adopted:** the skeleton
section's degree ranking may be cached per index run as an optimization if profiling
shows `GetGraphOverviewAsync` + edge-degree computation exceeding ~200 ms on
CortexFlow-scale repos — cache invalidation is trivial (bust on index commit).

### C. Extend `explore_topic` with more flags instead of a new tool
`explore_topic` answers "tell me about X"; the pack answers "arm me for this task in
this repo". Different budget models (per-section caps vs global), different centering
(symbol vs repo). Overloading one tool with mode flags produces exactly the parameter
soup that makes agents misuse tools. **Rejected.**

### D. Let the client pass a full conversation context for relevance-tuning
MCP-stateless purity says no; the `task` string is the right-sized interface (the agent
summarizes its own intent in one line). **Rejected** (matches the Thread-Scoped analysis
in `docs/research/ADVANCED-RAG-TECHNIQUES.md` §5 — the client owns conversation state).

## Consequences

**Positive**
- Session orientation: 3–5 calls / 4–6K tokens → **1 call / ≤2K tokens** (budget-capped
  by construction). Directly moves the VISION north-star metric.
- Post-compaction recovery becomes one cheap call — the single biggest quality-of-life
  fix for long-running agent sessions on large repos.
- Fixes the `onboard_project` repo-scoping bug and description drift as groundwork.
- `GetGraphOverviewAsync` finally earns an MCP surface (it was UI-only).

**Negative / cost**
- One more composite to keep honest as underlying queries evolve — mitigated by
  section-level integration tests with golden outputs.
- Degree centrality can over-rank utility/god classes (high fan-in ≠ importance);
  acceptable for v1, PageRank upgrade path named above.
- A vague `task` string yields a noisy section 2; bounded harm (its 35% budget), and the
  agent can re-call with a sharper task — still cheaper than the old ritual.
- Server does more work per call (~4 store queries + compression); all are existing
  indexed reads; budget caps the serialization cost.

## Verification / acceptance

1. **Unit:** budget respected within ±5% across section mixes; spill logic; FQN dedup;
   `include` filtering; `task`-omitted mode.
2. **Unit:** anchors section returns only `repo_id`-scoped rows (regression test for the
   substring bug — a fixture with two repos whose names substring-overlap).
3. **Integration (self-index):** `get_context_pack(repository:"CortexPlexus", task:"add a
   new MCP tool")` — pack contains `Mcp/Tools` classes in skeleton or task section,
   `Program.cs` DI anchor, and stays ≤2K estimated tokens.
4. **Live (LXC):** measure the real orient flow before/after on a fresh session against
   CortexFlow (the largest repo): assert ≥50% token reduction vs the
   list+recall+2-searches baseline; record in `docs/BENCHMARK.md`.

## References

- Aider repo-map (token-budgeted, centrality-ranked map — the pattern, improved by a real graph)
- `docs/research/ADVANCED-RAG-TECHNIQUES.md` §5 (client owns conversation state)
- ADR-018 (trust header space field) · ADR-021 (compact rendering) · ADR-023 (watch status field)
- VISION.md §2.2 GAP-2, §6 T1.2, §8 metric "orient <1.5k tokens"
