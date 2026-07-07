# ADR-026: Memory M3 — status lifecycle, invalidation over deletion, archive & export

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [MEMORY-V2-ASSESSMENT](../research/MEMORY-V2-ASSESSMENT.md) §3.T1–T2, §3.S3, §5.M3

## Context

Every destruction path in the memory system is a **hard DELETE**:

- `forget_memory` → `DELETE` by UUID.
- The `MemoryReaper` → `DELETE WHERE score < 0.1` on its 24 h cycle (ADR-012, which
  explicitly acknowledges: *"no tombstones … a future release may add `archived` flag
  if requested"* — this ADR is that request, with evidence).

Two failure classes follow:

1. **Negative knowledge is destroyed.** "Hypothesis X was tested and disproven" is the
   most expensive knowledge a fleet of agents produces — and the *only* way to record it
   today is to delete the wrong memory, leaving nothing. R27's two wrong hypotheses had
   to be corrected in a session-notes file because the store had no way to say
   *refuted*; the wrong memories kept surfacing until manually forgotten, and the
   refutation itself lives outside the system.
2. **Silent, irreversible loss.** The reaper permanently deletes whatever decayed —
   including memories that decayed only because ADR-025's reinforcement signal was quiet
   (agent forgot to feedback), or because a long project pause outlasted λ. Combined
   with **no export/backup path** (memories exist solely inside `pgdata`), the
   2026-07-07 filesystem incident was nearly a total-loss drill: a less recoverable
   failure would have destroyed 115 memories with no second copy anywhere.

Zep/Graphiti demonstrates the correct primitive: contradiction and obsolescence
**invalidate** (with history) rather than delete. ADR-025 already introduces
`supersedes`; this ADR provides the status model it lands on.

## Decision

### 1. `status` lifecycle (additive column, default preserves today's semantics)

```sql
ALTER TABLE agent_memories
  ADD COLUMN IF NOT EXISTS status         TEXT NOT NULL DEFAULT 'active',
  ADD COLUMN IF NOT EXISTS refuted_reason TEXT,
  ADD COLUMN IF NOT EXISTS supersedes     UUID;   -- lands here if ADR-025 ships second
```

| Status | Meaning | In default recall? |
|---|---|---|
| `active` | normal knowledge | ✅ |
| `superseded` | replaced by a newer memory (`supersedes` chain, ADR-025) | ❌ (successor surfaces instead) |
| `refuted` | tested and found wrong — **kept as negative knowledge** | ✅ **last, clearly labeled** |
| `archived` | decayed below threshold — parked, not destroyed | ❌ (opt-in via list) |

`refuted` rows surface at the *bottom* of recall with
`⛔ refuted: <reason> (<date>)` — an agent about to re-explore a dead end gets warned
instead of finding silence where the warning should be. This inverts the R27 failure.

### 2. Re-pointed tools (no new destructive surface)

- `forget_memory(id, reason)` → default action becomes `status='refuted'` +
  `refuted_reason` (reason **required** — "wrong" without why is noise).
  `forget_memory(id, hard: true)` keeps true DELETE for the legitimate case: secrets/PII
  that must not persist. Tool description rewritten accordingly.
- **Reaper → Archiver.** Below-threshold rows get `status='archived'` instead of DELETE.
  True DELETE happens only after a long quarantine (`Memory__ArchivePurgeDays`, default
  180) — recoverable window measured in months, unbounded growth still prevented.
- `list_memories(includeArchived: true, status: <filter>)` — audit/resurrection surface
  (resurrect = `update_memory` setting `status='active'`, via ADR-025's tool).
- All recall/scoring queries gain `status`-awareness; the existing
  `WHERE score >= 0.1` live-filter continues to hide archived rows by construction.

### 3. Export / import (close the single-copy hole)

- `GET /api/memories/export` → JSONL stream: full rows **minus embeddings** (re-derivable;
  keeps exports small and provider-portable) — includes status/provenance/space columns.
- `POST /api/memories/import` → upsert by `id` (idempotent restore), `pending_embedding=TRUE`
  on rows whose stored space ≠ current space (ADR-024's backfill re-embeds them — restore
  into a different provider "just works").
- Runbook: `docs/runbooks/memory-backup.md` — nightly cron `curl … | gzip` to an
  off-host destination + restore drill. The 2026-07-07 incident is the motivating
  reference and the drill scenario.

## Alternatives considered

### A. Keep hard delete, add a `deleted_log` audit table
Preserves *that* something was deleted, not the knowledge itself; refuted-hypothesis
warnings impossible. **Rejected.**

### B. Soft-delete flag only (`is_deleted`), no status taxonomy
One bit can't distinguish "wrong" (surface as warning!) from "obsolete" (hide) from
"decayed" (park). The taxonomy is exactly four values — not speculative generality.
**Rejected.**

### C. Full bi-temporal model (Graphiti-style `valid_at`/`invalid_at` intervals)
Strictly more expressive (facts true *during* a period), but doubles the mental model
for a store whose facts are overwhelmingly "currently believed / no longer believed".
**Deferred** — the status model is forward-compatible (intervals can be derived from
`created_at`/`updated_at`/status transitions if Tier-3 temporal work wants them).

### D. pg_dump as the backup story
Backs up everything but restores nothing selectively; ties memory recovery to full-DB
restore mechanics and one Postgres major version. The JSONL export is provider- and
version-portable, diffable, and small. **Complementary, not sufficient** — runbook
mentions both.

## Consequences

**Positive**
- Negative knowledge becomes first-class: dead ends warn instead of vanish (directly
  kills the R27 failure class, completing ADR-025's `misleading_count` loop).
- Nothing valuable is more than one status-flip from recovery; catastrophic-loss
  exposure drops from "everything since ever" to "since last export".
- Store hygiene improves *without* the destruction anxiety that made the reaper's
  hard-delete feel dangerous to tune.

**Negative / cost**
- Table keeps archived rows ~180 days longer — at memory-store scale (hundreds of rows,
  text-sized) this is noise.
- `refuted`-in-recall costs a few result slots; bounded (they rank last, capped count)
  and it is precisely the feature.
- Import-by-id trusts the export's UUIDs; a malicious/corrupt file could overwrite rows
  — mitigated by the same single-operator trust model as every other endpoint (Phase 11
  revisits if multi-user lands).
- One more config knob (`ArchivePurgeDays`) and a runbook to keep honest.

## Verification / acceptance

1. **Unit:** status transitions (active→refuted with reason required; active→archived by
   scorer; archived→active via update; superseded excluded from recall while successor
   surfaces); `hard:true` really deletes.
2. **Unit:** refuted rows rank last + carry the labeled reason; archived rows invisible
   to recall, visible to `list_memories(includeArchived:true)`.
3. **Unit:** export excludes embeddings, includes all metadata; import is idempotent
   (double-import = no dupes); cross-space import sets `pending_embedding`.
4. **Integration (restore drill):** export 115-row store → wipe a scratch DB → import →
   recall parity (modulo embedding backfill) — this test **is** the runbook's drill.
5. **Live:** refute one of the residual R27-era memories with a real reason; confirm a
   related query surfaces the ⛔ warning at the bottom.

## References

- ADR-012 (reaper + the acknowledged tombstone gap this closes) · ADR-024 (backfill used
  by import) · ADR-025 (supersedes, misleading_count, update_memory)
- Zep/Graphiti edge-invalidation pattern · Incident 2026-07-07 (single-copy near-miss)
- MEMORY-V2-ASSESSMENT §3.T1–T2, §3.S3
