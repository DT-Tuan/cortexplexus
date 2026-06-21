# ADR-017: Vertex AI Embedding Provider (opt-in, tri-cortex deployment)

**Status:** Accepted
**Date:** 2026-06-21

## Context

CortexPlexus default embedding (Ollama, all-local) on the LXC `192.168.50.14`
host is **I/O-throttled to ~4.6 texts/s** (vs ~29 texts/s in the R17 isolated
baseline). Root cause is Proxmox host-level I/O contention **outside** the LXC —
not fixable in CortexPlexus code (see memory `25623bbd`, `808d7898`). Ollama's
mmap model load thrashes under the host's I/O pressure.

For the **tri-cortex** deployment we want embedding inference moved **off the
box** to a managed API so throughput is bounded by the API, not by LXC disk
contention. Two managed options already proven elsewhere:

- **Gemini** (`gemini-embedding-001`) — already CP's non-Ollama branch (ADR-004).
  Uses `generativelanguage.googleapis.com`, 100 instances/batch call.
- **Vertex AI** (`text-embedding-005`) — Google Cloud's `:predict` endpoint,
  proven in CortexFlow's `VertexAIEmbeddingService` (ADR-054 there). Caps **5
  instances per `:predict` call** for `text-embedding-004/005`. Code-native
  embedding model tuned for retrieval.

CortexPlexus is **public on GitHub**: the OSS default must remain all-local
Ollama (no API key, no cloud dependency). Vertex is an **explicit opt-in branch**
for the tri-cortex operator only.

## Decision

Add **`VertexEmbeddingService : IEmbeddingService`** as a third, opt-in provider
branch. Ollama stays the default; Gemini stays as-is.

- **Wire shape** (mirrors CortexFlow's proven impl): `POST` to host
  `{location}-aiplatform.googleapis.com` (location `global` ⇒ bare
  `aiplatform.googleapis.com`, **no** region prefix), path
  `/v1/projects/{projectId}/locations/{location}/publishers/google/models/{modelId}:predict`.
  Body `{ "instances": [{ "content": <text> }], "parameters": { "outputDimensionality": 768 } }`.
  Response `predictions[].embeddings.values ⇒ float[]`.
- **Auth = API key on the `?key=` query string** (Vertex express-mode), **not**
  OAuth/bearer — same model as CortexFlow's Vertex service. The key is supplied
  **at runtime only** (UserSecrets in Development, or `Embedding__VertexApiKey`
  env var in the deployed container). It is **never** written to
  `appsettings.json` or any committed file.
- **`VertexInstancesPerCall` (default 5)** — a config knob, because the per-call
  instance cap is model-dependent (`text-embedding-004/005` = 5;
  `gemini-embedding-001` via Vertex may be 1). `EmbedBatchAsync` **sub-batches**
  the input to this cap and issues one `:predict` per sub-batch. This differs
  from Gemini's single 100-instance `batchEmbedContents` call.
- **768-dim** via `outputDimensionality` — matches the existing pgvector HNSW
  column, **no schema change**.
- **`ApplyProviderDefaults`**: `vertex ⇒ MaxParallelBatches = 4` (managed API is
  request-throughput bound, parallelism is free — same rationale as Gemini).
- **Resilience**: Polly retry on `429/408/503/500` + transient network
  exceptions (mirrors `GeminiEmbeddingService`); **no retry on 401**; graceful
  **empty** array on persistent failure (never throw into the indexing pipeline).
- **Config binding**: `EmbeddingOptions` gains `VertexProjectId`,
  `VertexLocation` (default `"global"`), `VertexModelId`,
  `VertexInstancesPerCall`, `VertexApiKey`. `Program.cs` now binds the
  `"Embedding"` section from `IConfiguration` (so UserSecrets load) in addition
  to the existing env-var reads.

## Re-embed requirement (vector-space change)

`text-embedding-005` produces a **different 768-dim vector space** than Ollama
`nomic-embed-text`. Even though the dimension matches, the vectors are **not
comparable** across models. Switching a repo to Vertex therefore **requires a
full re-embed** of that repo: wipe the three repo-keyed stores (AGE graph
`DETACH DELETE` + `DELETE code_symbols` + `DELETE file_hashes`) then re-index
(memory `3a96706c`). There is no in-place model swap.

**Cross-repo caveat:** `recall_memory(scope:"all")` and cross-repo
`semantic_search` compare vectors across repositories. A fleet where some repos
are Ollama-768 and others Vertex-768 yields **meaningless cross-boundary
distances**. The operator must either keep one provider fleet-wide or accept that
cross-repo semantic queries only make sense within a same-provider cohort.

## Consequences

- **Pro:** Embedding throughput decoupled from LXC I/O contention — target
  >20 texts/s vs 4.6 texts/s all-local on the same host.
- **Pro:** OSS all-local path untouched — `Provider=ollama` (default) still runs
  with zero cloud dependency; `Provider=gemini` unchanged.
- **Pro:** No DB schema change (768-dim preserved).
- **Con:** Vertex branch needs a Google Cloud API key + project — runtime-only
  secret, deployment-specific.
- **Con:** Method signatures/summaries are sent to Google (same trade-off
  already accepted for the Gemini default in ADR-004).
- **Con:** Switching provider mandates a full re-embed; mixed-provider fleets
  break cross-repo semantic comparisons.
