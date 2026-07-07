# ADR-018: Embedding-space versioning — stamp provider/model per repo & memory, guard cross-space search

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-1 / Tier 1 · T1.1

## Context

CortexPlexus supports three embedding providers (Ollama `nomic-embed-text`, Gemini
`gemini-embedding-001`, Vertex `text-embedding-005` — ADR-004, ADR-017). All three emit
768-dim vectors, so they are **column-compatible but semantically incompatible**: cosine
similarity between a Vertex query vector and an Ollama document vector is noise, not
signal (verified during the ADR-017 rollout — re-embedding was mandatory despite identical
dimensions).

### The failure (live in production today)

The tri-cortex server switched to `Provider=vertex` (2026-06-21). Since then repos have
been migrated by re-indexing one at a time. As of 2026-07-07 the fleet is **split**:
5 repos carry Vertex vectors, 7 still carry Ollama vectors. Every `semantic_search`
against an un-migrated repo now:

1. Embeds the query with **Vertex** (`HybridQueryRouter.SafeVectorSearchAsync`,
   `src/CortexPlexus.Search/HybridQueryRouter.cs:82-84`).
2. Compares it against **Ollama** vectors in pgvector
   (`VectorStore.SearchAsync`, `src/CortexPlexus.Graph/VectorStore.cs:187-259` —
   filters `repo_id`/`kind` only, no space check, `:194-198`).
3. Returns garbage-ranked results **with zero warning**.

`recall_memory(scope:"all")` is worse: it deliberately mixes memories across every repo
(`AgentMemoryStore.RecallAsync`, `src/CortexPlexus.Memory/AgentMemoryStore.cs:109-113`
ranks by `decay × cosine`), so a single recall blends two vector spaces in one ranked list.

### Why nothing catches it

Nothing in the schema records which provider/model produced a vector:

- `repositories` has exactly 5 columns (`id, name, path, created_at, last_indexed` —
  `src/CortexPlexus.Graph/Schema/Migrations.sql:11-17`).
- `code_symbols.embedding vector(768)` (`Migrations.sql:31`) and
  `agent_memories.embedding vector(768)` (`src/CortexPlexus.Memory/Schema/Migrations.sql:13`)
  carry no provenance; `indexed_at` is time-only.
- `IEmbeddingService` returns bare `float[]` — no metadata travels with the vector.
- A whole-`src` grep for `embedding_provider|embedding_model|vector_space` returns zero
  matches (verified 2026-07-07).

This violates VISION principle 3 (*"Đúng và tự biết mình sai"* — results must carry trust
metadata). It is the same bug class as ADR-008 (false health) and ADR-015 (false
staleness), except inverted: instead of a false alarm, it is a **missing alarm**.

## Decision

Record the **embedding space** — `(provider, model, dimensions)` — wherever vectors are
written, and make every vector-leg read **space-aware**.

### 1. Space identity

A space is the triple `provider:model:dim`, e.g. `vertex:text-embedding-005:768`,
`ollama:nomic-embed-text:768`. Derivable from config at any moment via a small pure
helper:

```csharp
// src/CortexPlexus.Embedding/EmbeddingSpace.cs
public sealed record EmbeddingSpace(string Provider, string Model, int Dimensions)
{
    public static EmbeddingSpace FromOptions(EmbeddingOptions o) => o.Provider.ToLowerInvariant() switch
    {
        "ollama" => new("ollama", o.OllamaModel, o.Dimensions),
        "vertex" => new("vertex", o.VertexModelId, o.Dimensions),
        _        => new("gemini", o.GeminiModel, o.Dimensions),
    };
    public string Key => $"{Provider}:{Model}:{Dimensions}";
}
```

No `IEmbeddingService` interface change — provider/model already live in
`EmbeddingOptions` (`ServiceCollectionExtensions.cs:37-48` selects the singleton from the
same options), so the space is config-derived. This keeps the wire type `float[]` and all
three service implementations untouched.

### 2. Schema (additive, idempotent — matches existing migration style)

```sql
-- src/CortexPlexus.Graph/Schema/Migrations.sql
ALTER TABLE public.repositories
    ADD COLUMN IF NOT EXISTS embedding_provider TEXT,
    ADD COLUMN IF NOT EXISTS embedding_model    TEXT,
    ADD COLUMN IF NOT EXISTS embedding_dim      INT;

-- src/CortexPlexus.Memory/Schema/Migrations.sql
ALTER TABLE agent_memories
    ADD COLUMN IF NOT EXISTS embedding_provider TEXT,
    ADD COLUMN IF NOT EXISTS embedding_model    TEXT,
    ADD COLUMN IF NOT EXISTS embedding_dim      INT;
```

Granularity is deliberate:
- **`repositories` = repo-level stamp.** A repo's symbols are (re-)embedded as a unit; a
  full index run overwrites the whole vector set, so one stamp per repo is truthful. A
  *partial* incremental sync after a provider switch would mix spaces inside one repo —
  the guard in §4 prevents that.
- **`agent_memories` = per-row stamp.** Memories are written continuously, one at a time,
  across provider eras; only per-row provenance is truthful there.

Three columns instead of one `TEXT` key so SQL can filter/aggregate by provider alone
(fleet dashboards, migration progress) without string parsing. `Key` is derived in C#.

### 3. Write-path stamping (all three vector producers)

| Producer | Where | Change |
|---|---|---|
| Server indexing pipeline | `IndexingPipeline.cs:239` (right after `vectorStore.UpsertAsync:235`) | `UpdateLastIndexedAsync` gains the space triple (single UPDATE, same round-trip) |
| Agent upload final-commit | `AgentApiEndpoints.cs:262` | same extended call (embeddings are produced server-side on this path too, so the server's current space is authoritative) |
| Memory save | `MemoryTools.SaveMemory` (`MemoryTools.cs:85`) → `AgentMemoryStore.SaveAsync` (`AgentMemoryStore.cs:54-72`) | INSERT includes the space triple of the vector just produced |

**Incremental-sync guard:** on an agent incremental upload, if the repo row's stamped
space is non-null and ≠ server current space, the server must **refuse the partial upsert
of embeddings** (respond with an actionable error: *"repo X carries ollama vectors but
server now embeds with vertex — run force_reindex + full re-index to migrate"*). Without
this, one file save after a provider switch silently poisons a repo with mixed spaces
that no repo-level stamp can describe.

### 4. Read-path guards

**`semantic_search` / hybrid search** — the guard lives where the vector leg fans out
(`HybridQueryRouter`), because only the vector leg is space-sensitive; FTS and graph legs
remain valid for any repo:

- Single-repo query, space mismatch → run FTS/graph legs normally, **skip the vector
  leg**, and append a footer through the existing seam
  (`SearchTools.AppendStalenessFooter`, `SearchTools.cs:50-63` — same "append footer if
  non-null" pattern as ADR-015):
  `⚠️ semantic leg skipped: repo carries ollama:nomic-embed-text vectors, server queries with vertex:text-embedding-005. Re-index to migrate.`
- Cross-repo query (repoId = null) → vector leg adds `WHERE` on repos whose space matches
  the current one; footer reports `N repos excluded from semantic ranking (space mismatch: <names>)`.
- Repo with NULL stamp (legacy, pre-migration rows) → treated as **unknown**: vector leg
  runs (behavior unchanged) but the footer flags `space unknown — stamp by re-indexing`.
  This keeps the rollout non-breaking.

**`recall_memory`** — mismatched-space memories must not be *dropped* (their content is
still valid knowledge!); they are ranked as if they had no embedding. The existing order
clause already handles NULL embeddings with a neutral 0.5
(`COALESCE((1.0 - (embedding <=> @q)), 0.5)`, `AgentMemoryStore.cs:109-113`); extend it:

```sql
ORDER BY (decay_score) *
  CASE WHEN embedding_provider IS NOT DISTINCT FROM @curProvider
        AND embedding_model    IS NOT DISTINCT FROM @curModel
       THEN COALESCE((1.0 - (embedding <=> @q)), 0.5)
       ELSE 0.5   -- foreign/unknown space: neutral, content-recall only
  END DESC
```

Footer on recall output: `N memories ranked without semantic score (foreign embedding space)`.

**`list_repositories`** — each repo line gains its space, and a mismatch marker vs the
server's current space:

```
Name: CortexFlow  ... Embedding: vertex/text-embedding-005 (768d)
Name: iTAS        ... Embedding: ollama/nomic-embed-text (768d) ⚠️ mismatch — semantic_search degraded until re-index
```

This makes migration progress self-documenting (the exact pain of the 2026-06/07 rollout,
where fleet state had to be tracked in session notes).

### 5. Backfill (operator action, not automatic)

Historic truth can't be derived from the DB. The operator (who knows the migration
history) may stamp legacy rows once:

```sql
-- Example: everything not re-indexed since the Vertex cutover is Ollama
UPDATE repositories SET embedding_provider='ollama', embedding_model='nomic-embed-text', embedding_dim=768
 WHERE embedding_provider IS NULL AND last_indexed < '2026-06-21';
UPDATE agent_memories  SET embedding_provider='ollama', embedding_model='nomic-embed-text', embedding_dim=768
 WHERE embedding_provider IS NULL AND created_at   < '2026-06-21';
```

Documented in the runbook; never run automatically (the server cannot know cutover dates).

## Alternatives considered

### A. One vector column per space (`embedding_ollama`, `embedding_vertex`, …)
Every provider switch doubles storage and HNSW index count; queries must pick a column;
migration means populating a new column fleet-wide anyway. **Rejected** — cost without
removing the need for provenance.

### B. Hard single-space server: refuse to serve any repo whose space ≠ current
Simplest guard, but blocks the *gradual* migration that is the actual operating mode
(12 repos, re-indexed one at a time over weeks). FTS/graph legs would be needlessly
blocked too. **Rejected** — the degraded-but-honest mode of §4 serves reality better.

### C. Auto re-embed on mismatch detection
Turns a read into a surprise bulk write (hours of wall-time, Vertex billing). Violates
the "no surprise cost" expectation of a query tool. **Rejected** as automatic behavior;
the footer *recommends* the explicit `force_reindex` path instead.

### D. Encode space into the pgvector index / partial indexes per space
Partial HNSW indexes per space keyed on the new columns would speed mixed-fleet vector
scans, but adds index-management complexity for a transient migration state. **Deferred**
— revisit only if mixed fleets become permanent rather than transitional.

## Consequences

**Positive**
- Kills a live silent-wrongness bug; every vector read is now either valid or labeled.
- Migration progress becomes visible in `list_repositories` (no more session-notes bookkeeping).
- Mixed-space repos become impossible (incremental guard), so "repo space" stays a
  truthful single value.
- Additive schema; legacy rows degrade to today's behavior + an "unknown space" hint.

**Negative / cost**
- Provider switch now *requires* full re-index per repo before incremental sync resumes
  for embeddings (guard in §3). This was already the de-facto contract (ADR-017 notes);
  now it's enforced, which may surprise an operator mid-migration — the error message
  must carry the exact recovery command.
- Order-clause `CASE` on recall adds negligible per-row cost (no new index needed; the
  HNSW index still serves the common matched-space case).
- Three new columns × two tables of migration surface; mixed-version agents unaffected
  (server-side only).

## Verification / acceptance

1. **Unit:** `EmbeddingSpace.FromOptions` maps all three providers; `Key` stable.
2. **Unit:** `VectorStore`/router — mismatch ⇒ vector leg skipped/filtered, footer text
   exact; NULL stamp ⇒ unchanged behavior + unknown-space hint.
3. **Unit:** `AgentMemoryStore.RecallAsync` — foreign-space memory ranks with 0.5 neutral
   factor, never by garbage cosine; matched-space unchanged.
4. **Integration:** index a repo (Ollama config) → switch config to Vertex → (a)
   `list_repositories` shows ⚠️ mismatch on that repo; (b) `semantic_search` on it returns
   FTS/graph results + skip-footer; (c) incremental agent upload is refused with the
   recovery message; (d) full re-index clears the mismatch.
5. **Live (LXC):** after deploy, `list_repositories` must show the real fleet split
   (5 vertex / 7 ollama) without any manual backfill — legacy repos show `space unknown`
   until backfill SQL is run, then show `ollama ⚠️ mismatch`.

## References

- ADR-004 (Gemini default), ADR-017 (Vertex provider — "full re-embed required" note this ADR enforces)
- ADR-008 / ADR-015 — the "metric must not lie" precedents; this ADR extends the same
  principle to vector provenance
- VISION.md §2.2 GAP-1, §4 principle 3, §6 T1.1
