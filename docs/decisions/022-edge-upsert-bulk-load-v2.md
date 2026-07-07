# ADR-022: Edge upsert bulk-load v2 — stage chunked uploads, apply once, drop the size-threshold heuristic

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-5 / Tier 1 · T1.5
**Supersedes in part:** [ADR-009](009-age-edge-upsert-scaling.md) (the 20K threshold decision; the delete+CREATE mechanism itself is kept)

## Context

### The regression measurement (2026-07-02, MyFin first index, LXC production)

```
POST chunk [symbols 1/1 (485 items)]        → OK (18.9–82.9s)   455 rows + embeddings
POST chunk [relationships 1/1 (4402 items)] → OK (673.1s)       ← 87% of wall time
```

**4 402 edges in 673 s ≈ 6.5 edges/s ≈ 153 ms/edge** — a ~64× degradation from the
~2.4 ms/edge MERGE baseline ADR-009 measured in April. CortexFlow's full re-index
(2026-06-27) showed the same shape: 5 273 s total, dominated by 13 relationship chunks.

### Root cause: ADR-009 keyed the switch on the wrong variable

ADR-009 correctly diagnosed that AGE's edge `MERGE` existence check is a **sequential
scan on the edge label table** (no secondary indexes possible on edge labels), and added
a delete+CREATE bulk path. But the switch is:

```csharp
// AgeGraphStore.cs:148,158
private const int EdgeBulkLoadThreshold = 20_000;
var useBulkLoad = relList.Count >= EdgeBulkLoadThreshold;
```

Two compounding problems:

1. **The agent's chunked upload never trips it.** `LocalIndexer.RelationshipChunkSize = 5000`
   (`src/CortexPlexus.Agent/LocalIndexer.cs:250`), and each chunk is a separate
   `POST /api/index/results` → a separate `UpsertEdgesAsync` call
   (`AgentApiEndpoints.cs:214`). Every agent-indexed repo — which is **all 12 production
   repos** (`_agent/*`) — takes the MERGE path forever.
2. **MERGE cost scales with the *global* edge-label table, not the batch.** All repos
   share one AGE graph (`GraphName = "code_graph"`, `AgeGraphStore.cs:14`); edge label
   tables (`Calls`, `DependsOn`, …) hold every repo's edges. ADR-009's numbers were taken
   when the graph held ~21K edges (one repo); the fleet graph now holds an order of
   magnitude more, and each of MyFin's 4 402 MERGEs seq-scanned those global tables. The
   "break-even ~35K edges per call" analysis was valid only for the April graph size —
   **batch size was never the right control variable; target-table size is.**

### A lurking failure, not just slowness

Each 200-edge batch (`BatchSize = 200`) is one Cypher statement with **no
`CommandTimeout` override** (Npgsql default 30 s; `ExecuteCypher`,
`AgeGraphStore.cs:1587-1602`). At 153 ms/edge a batch already takes ~30.6 s — production
is at the edge of systematic batch timeouts. VectorStore sets 600 s for its heavy
operation (`VectorStore.cs:151`); the graph store never got the same treatment.

### Why we can't just lower the threshold

The bulk path first **deletes all outgoing edges for the batch's source-FQN set**
(`DeleteEdgesBySourceFqns`, `AgeGraphStore.cs:227-245`), then CREATEs. Applied per 5K
chunk, this corrupts data: a source vertex whose edges span a chunk boundary gets its
chunk-1 edges deleted by chunk-2's delete pass. ADR-009 §Consequences documented exactly
this contract ("full-index uploads the complete edge set") — the 20K threshold was the
guard that kept per-chunk calls off the destructive path. Any fix must restore the
"complete edge set per apply" invariant *before* widening the bulk path.

## Decision

Make the server **stage relationship chunks and apply them once**, then route every
full-index apply through delete+CREATE unconditionally.

### 1. Upload-session staging (wire protocol v1.2.0)

The agent's chunked upload already has session shape — N symbol chunks, M relationship
chunks, then **one final-commit chunk** carrying the file hashes
(`LocalIndexer.cs:325,343`). Formalize it:

- Agent adds to the `POST /api/index/results` payload: `uploadSessionId` (GUID, constant
  across one index run) and `isFinalChunk` (already implicit in the commit chunk). Bump
  `AgentInfo.Version` `1.1.0 → 1.2.0` (`src/CortexPlexus.Core/AgentInfo.cs:14` — the doc
  comment says wire-protocol fields are exactly what warrants a bump).
- Server (`AgentApiEndpoints`): symbol chunks process as today (embedding work benefits
  from streaming). **Relationship chunks are buffered** in an in-memory session store
  keyed by `uploadSessionId` (bounded: ~200 bytes/edge → 65K edges ≈ 13 MB; cap at
  256 MB, reject beyond). On the final-commit chunk, the server applies **all buffered
  edges in one `UpsertEdgesAsync` call**, then commits hashes as today.
- Session GC: buffers older than 30 min without a new chunk are dropped (agent crash /
  network loss ⇒ next index run starts a fresh session; no partial edge state was ever
  written, which is *better* than today's partially-applied chunks).
- **Backward compat:** a payload without `uploadSessionId` (agent ≤1.1.0) takes the
  legacy per-chunk path unchanged. No forced agent upgrade; upgraded agents get the fast
  path via the existing self-update flow (`AgentUpdater`, ActivateAgent Step 3/5).

### 2. Full-index ⇒ delete+CREATE, unconditionally

With the complete edge set guaranteed in one call, replace the size heuristic:

```csharp
// was: var useBulkLoad = relList.Count >= EdgeBulkLoadThreshold;   // 20_000
// now:
var useBulkLoad = isFullIndexApply;   // session-applied or pipeline full-index
```

- Full-index applies (staged session apply; server pipeline `IndexingPipeline.cs:231`)
  always take delete+CREATE. CREATE's flat ~7.8 ms/edge (ADR-009 measurement) beats a
  global-table MERGE scan at any realistic fleet size; for a small repo the worst case
  vs MERGE-on-empty-graph is a few seconds — irrelevant next to the 10-minute failure
  mode it removes.
- **Incremental watch path keeps MERGE** (1–10 edges per file-save; idempotency matters
  more than speed there — unchanged from ADR-009). Note: incremental MERGE also pays the
  global seq-scan (~150 ms/edge today → ~1.5 s per save for 10 edges). Acceptable now;
  if fleet growth pushes it past ~5 s/save, the escape hatch is per-repo graphs
  (Alternative D) — revisit then.

### 3. Operational hardening (independent of the protocol change)

- `ExecuteCypher` gains a `commandTimeoutSeconds` parameter; edge delete/CREATE/MERGE
  statements run at **300 s** (mirroring VectorStore's 600 s precedent for heavy ops,
  `VectorStore.cs:151`) so a slow batch degrades gracefully instead of throwing at 30 s.
- Per-phase timing at Information level inside `UpsertEdgesAsync`: delete ms, apply ms,
  edges/s, path taken (merge|create). Today only the delete pass is timed
  (`AgeGraphStore.cs:172-178`) and per-request node/edge split lives in
  `AgentApiEndpoints.cs:210-217`; the 6.5 edges/s figure had to be reverse-engineered
  from agent-side logs — that observability gap cost us two months of not noticing.
- Sort edges by `(src, dst)` before UNWIND (ADR-009 Alternative B, "recommended but not
  sufficient") — free GIN-lookup locality on the vertex MATCHes.

## Projected impact

*(Projections, to be validated by benchmark before acceptance-flip — per the R17
"measure before projecting" lesson.)*

| Scenario | Today (measured) | Projected | Basis |
|---|---|---|---|
| MyFin edge phase (4 402 edges) | 673 s | ~35–40 s | 7.8 ms/edge CREATE + delete overhead |
| CortexFlow full re-index (~65K edges, 13 chunks) | ~88 min total, edge-dominated | edge phase ~8–10 min | flat CREATE, one delete pass |
| Watch incremental (10 edges) | ~1.5 s | unchanged | MERGE kept |

## Alternatives considered

### A. Lower the threshold / per-chunk delete+CREATE
Corrupts edges across chunk boundaries (see Context). **Rejected** — correctness.

### B. Have the agent send everything in one giant request
Removes chunking that exists for good reasons (50 MB request-size ceilings, retry
granularity, memory on both ends — R13 introduced chunking to fix real timeouts).
**Rejected.**

### C. Staging in a relational temp table instead of memory
Survives server restart mid-upload and has no RAM ceiling, but adds a write+read round
trip for every edge and schema surface for a transient artifact. A crashed upload simply
re-runs today. **Rejected for now** — revisit if edge sets outgrow the memory cap.

### D. One AGE graph per repository
Shrinks every label table to repo size (fixing MERGE *and* making repo deletion
`drop_graph`), but breaks all cross-repo queries — including the Tier-2 cross-repo
service topology (VISION T2.2) which needs one traversable graph — and multiplies
schema/index management by repo count. **Deferred** — reconsider only if the shared
graph's scale becomes untenable even on the CREATE path.

### E. B-tree/GIN indexes on edge label tables
Re-examined and still not supported by AGE without touching internals (ADR-009 Alt A).
**Rejected.**

## Consequences

**Positive**
- Edge phase returns to flat scaling regardless of fleet size; the dominant indexing
  bottleneck (87% of MyFin wall time) drops ~18×.
- Partial-application window disappears: today a failed chunk N leaves chunks 1..N-1
  applied (edges without their hash commit); staged apply is closer to atomic.
- Timeout cliff removed; timing logs make the next regression visible in one log line.

**Negative / cost**
- Wire-protocol bump: mixed-version fleets run two code paths until agents update
  (self-update exists but is manual/ActivateAgent-driven — see ADR-023's liveness work).
- Server holds edge buffers in memory (bounded, GC'd); a monolith restart mid-upload
  discards a session (agent's next run redoes it — same recovery as today's failures).
- `isFullIndexApply` plumbs one more flag through `IGraphStore.UpsertEdgesAsync` —
  interface change, all fakes/tests touched.

## Verification / acceptance

1. **Perf test (the one ADR-009's test missed):** seed the graph with ≥100K *foreign*
   edges (other repos), then index a 5K-edge repo through the chunked agent path —
   assert edge phase < 60 s (fails today at ~700 s) and edges/s ≥ 50 in the new timing log.
2. **Correctness test:** source vertex with edges spanning two 5K chunks → after staged
   apply, *all* its edges exist (fails under per-chunk delete+CREATE, guards Alternative A).
3. **Compat test:** payload without `uploadSessionId` → legacy path, results identical
   to v1.1.0 behavior.
4. **Session GC test:** orphaned session buffer dropped after TTL; no edge writes occur.
5. **Live (LXC):** re-index MyFin end-to-end; compare the `Graph upsert:` log line
   against the 2026-07-02 baseline (673 s); update `docs/BENCHMARK.md` with a new round
   (per the benchmark-update policy).

## References

- ADR-009 (mechanism kept, threshold superseded) · ADR-002 (monolith — single-instance
  assumption the in-memory staging relies on)
- Measurements: MyFin index 2026-07-02 (`673.1s / 4402 edges`); CortexFlow re-index
  2026-06-27 (~88 min); ADR-009 April baselines (2.4 ms MERGE / 7.8 ms CREATE)
- VISION.md §2.2 GAP-5, §6 T1.5, §8 north-star "full-index 20k symbols <15 min"
