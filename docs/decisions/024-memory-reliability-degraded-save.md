# ADR-024: Memory M0 — categorized error envelope, degraded save, embedding backfill

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) T1.7 · [MEMORY-V2-ASSESSMENT](../research/MEMORY-V2-ASSESSMENT.md) §3.R1–R2, §5.M0

## Context

### The incident that wrote this ADR (2026-07-07)

`save_memory` failed three consecutive times with exactly one string:
`"An error occurred invoking 'save_memory'."` Diagnosis required SSH access to the LXC,
raw JSON-RPC probing, docker log inspection, and eventually PVE-host forensics — ~40
minutes to discover the cause was a PostgreSQL crash-loop (host filesystem
`emergency_ro` after a SATA-link event). **An agent without infrastructure access had
zero chance**: no category, no recommended action, and — decisively — **the server
logged nothing** about any of the three failures.

Code-level cause: `SaveMemory` catches only `ArgumentException`
(`src/CortexPlexus.App/Mcp/Tools/MemoryTools.cs:111-114`). `NpgsqlException` (DB down),
`TimeoutException`, and everything else escape to the MCP SDK, which swallows them into
the generic string above with `isError:true` and no server-side trace. The same hole
exists in all five memory tools — and in most non-memory tools.

### The write/read asymmetry

- `recall_memory` **degrades gracefully** when embedding fails: falls back to
  filter-only recall (`MemoryTools.cs:160-163`), because `RecallAsync` already handles
  `NULL` query embeddings.
- `save_memory` **aborts** when embedding fails (`:87-93`) — "Memory NOT saved".

The asymmetry is backwards: the moment the embedding provider is down is precisely when
the agent most needs to *record* what it is learning about the outage. The store already
tolerates `embedding NULL` rows (`agent_memories.embedding vector(768) NULL`,
`src/CortexPlexus.Memory/Schema/Migrations.sql:13`; recall ranks them with a neutral
`COALESCE(…, 0.5)`, `AgentMemoryStore.cs:109-113`) — the write path just never uses that
tolerance.

This ADR is the R21/R25 "friendly, self-correcting errors" discipline (built for search
tools) extended to the memory surface, plus write-path resilience. It is deliberately
small: the schema/semantic upgrades live in ADR-025/026/027.

## Decision

### 1. Categorized error envelope for all five memory tools

Wrap each tool body in a shared handler that maps failures to a stable, machine-readable
category + recovery action, returned as compact JSON (per ADR-021):

| Category | Trigger | `action` returned to the agent |
|---|---|---|
| `validation` | bad scope/topic/UUID/length (existing friendly paths, unchanged) | fix the parameter (message already says how) |
| `secrets_detected` | `ISecretsScanner` hit | sanitize content, retry |
| `embedding_unavailable` | `IEmbeddingService` threw | for save: **proceeds degraded** (§2); for recall: proceeds filter-only (existing) — category reported either way |
| `storage_unavailable` | `NpgsqlException` / connection / timeout | "server datastore unreachable — likely infrastructure; retry later; report to operator" |
| `internal` | anything else | includes exception type name (not stack) |

Every non-`validation` failure is **logged server-side at Warning/Error with the
exception** — the zero-log blindness of the incident becomes impossible. Implementation
is one small helper (`MemoryToolGuard.ExecuteAsync(name, fn, logger)`) so the five tools
share a single seam; other tool families can adopt it later without redesign.

### 2. Degraded save (write-path parity with recall)

```
embed OK   → save with embedding                       (today's happy path)
embed FAIL → save with embedding = NULL,
             pending_embedding = TRUE,
             response: { stored: true, degraded: "no_embedding",
                         note: "semantic recall unavailable for this memory
                                until backfill; exact/filter recall works now" }
```

- One additive column: `pending_embedding BOOLEAN NOT NULL DEFAULT FALSE`
  (idempotent `ALTER TABLE`, matches the existing migration style).
- The memory is immediately recallable by scope/topic/relatedFqn filters and (after
  ADR-025) by the BM25 leg; it simply lacks the semantic leg until backfill.

### 3. Embedding backfill

A small background loop (hosted service, piggybacking the existing `MemoryReaper`
schedule — every 24 h, plus an opportunistic pass on startup): select
`WHERE pending_embedding`, embed in batches through the current provider, stamp the
vector **and the embedding-space columns of ADR-018**, clear the flag. Failures leave
the flag set (retry next cycle); a counter in the log line
(`"memory backfill: 3 embedded, 1 still pending"`) keeps it observable.

Interaction with ADR-018: a backfilled memory gets the *current* space stamp — correct
by construction, since the vector was just produced by the current provider.

### 4. Health surfacing

`list_repositories`'s trailing memory line (`"Memory: enabled (105 items)"`,
`GraphTraversalTools.cs:470`) gains the pending count when non-zero:
`"Memory: enabled (105 items, 2 pending embedding)"` — cheap fleet-level visibility that
the backfill is (or is not) draining.

## Alternatives considered

### A. Queue failed saves client-side (agent retries later)
Agents lose turn-local state constantly (compaction, session end) — a "retry later" note
to an agent is a coin-flip. The server is the durable party; it should accept the write
degraded. **Rejected.**

### B. Catch-all that retries the embedding N times before degrading
Polly retry already exists *inside* each embedding service for transient HTTP failures
(ADR-017). A second retry layer here just multiplies latency during outages.
**Rejected** — degrade immediately, backfill later.

### C. Fix only save_memory, skip the envelope
The incident's worst property was silence, not the failed write. Envelope + logging is
the part that converts the next infrastructure failure from a 40-minute forensic session
into one self-describing tool response. **Rejected.**

### D. Dead-letter table for failed saves instead of NULL-embedding rows
More moving parts (second table, drain logic, duplicate-on-drain risk) for the same
outcome. The main table already tolerates NULL embeddings. **Rejected.**

## Consequences

**Positive**
- Memory tools can no longer fail silently — every failure has a category, an action,
  and a server log line.
- Writes survive embedding-provider outages; knowledge capture no longer depends on the
  weakest external dependency.
- Foundation for ADR-025/026 (the guard/envelope is where reconciliation responses and
  status semantics will also surface).

**Negative / cost**
- Degraded rows are semantically invisible until backfill (bounded by the 24 h cycle +
  startup pass; acceptable vs losing the write).
- One more background responsibility on the reaper schedule (trivial load: pending rows
  are rare).
- Envelope changes the failure-response *shape* for memory tools; agents pattern-matching
  the old plain-text errors need the (already planned) ADR-021 release-notes treatment.

## Verification / acceptance

1. **Unit:** each category path returns its envelope; `NpgsqlException` → `storage_unavailable`
   + logger received the exception (the incident's regression test).
2. **Unit:** embed-failure save → row persisted with `pending_embedding=TRUE`, response
   carries `degraded`; recall by filter finds it; semantic recall does not.
3. **Unit:** backfill embeds pending rows, stamps ADR-018 space columns, clears flag;
   failed backfill leaves flag set.
4. **Integration:** stop postgres → `save_memory` returns `storage_unavailable` envelope
   (not the SDK generic string); server log contains the failure. Stop embedding provider
   → save succeeds degraded → restart provider → backfill drains → semantic recall finds it.
5. **Live (LXC):** re-run the exact three saves that failed on 2026-07-07; then kill the
   embedding provider and repeat — observe degraded save + next-cycle backfill.

## References

- Incident 2026-07-07 (postgres crash-loop / `emergency_ro`) — full chain in
  MEMORY-V2-ASSESSMENT §3.R1
- R21/R25 friendly-error precedent · ADR-017 (provider retry layer) · ADR-018 (space
  stamping on backfill) · ADR-021 (compact envelope shape)
