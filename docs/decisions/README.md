# Architecture Decision Records

| # | Decision | Status | Date |
|---|----------|--------|------|
| [001](001-postgresql-unified-store.md) | PostgreSQL unified store (AGE + pgvector + tsvector) | Accepted | 2026-04-03 |
| [002](002-monolith-architecture.md) | Monolith architecture (single .NET app) | Accepted | 2026-04-03 |
| [003](003-roslyn-over-treesitter-csharp.md) | Roslyn for C# instead of Tree-sitter | Accepted | 2026-04-03 |
| [004](004-google-gemini-embedding.md) | Google Gemini Embedding (free tier) as default | Accepted | 2026-04-03 |
| [005](005-mcp-dual-transport.md) | MCP dual transport (stdio + HTTP) | Accepted | 2026-04-03 |
| [008](008-kind-aware-health-metric.md) | Kind-aware Health metric | Accepted | 2026-04-15 |
| [009](009-age-edge-upsert-scaling.md) | AGE edge upsert: delete+CREATE for bulk, MERGE for incremental | Accepted | 2026-04-15 |
| [010](010-memory-storage-reuse-postgres.md) | Memory storage reuses existing PostgreSQL | Accepted | 2026-04-17 |
| [011](011-memory-scope-model.md) | Memory scope: session / project / global | Accepted | 2026-04-17 |
| [012](012-memory-decay-weibull.md) | Memory decay: Weibull curve (k=1.5) with per-topic λ | Accepted | 2026-04-17 |
| [013](013-memory-opt-in-default.md) | Memory system opt-in, default disabled | Accepted | 2026-04-17 |
| [014](014-first-class-python-support.md) | First-class Python support (tree-sitter call-graph FQN resolution) | Accepted | 2026-06-14 |
| [015](015-content-aware-index-freshness.md) | Content-aware index freshness (kill time-based false-STALE) | Accepted (B1 shipped) | 2026-06-18 |
| [016](016-multi-language-framework-intelligence.md) | Multi-language framework intelligence — Tier B (endpoints/DI/dependency-audit) | Accepted (C1–C4 shipped) | 2026-06-19 |
| [017](017-vertex-ai-embedding-provider.md) | Vertex AI embedding provider (opt-in, tri-cortex; Ollama stays default) | Accepted | 2026-06-21 |
| [018](018-embedding-space-versioning.md) | Embedding-space versioning — stamp provider/model, guard cross-space search | Proposed | 2026-07-07 |
| [019](019-context-pack-tool.md) | `get_context_pack` — one-call, token-budgeted orientation bundle | Proposed | 2026-07-07 |
| [020](020-readonly-graph-query-tool.md) | `graph_query` — read-only open Cypher MCP tool | Proposed | 2026-07-07 |
| [021](021-compact-tool-output.md) | Compact-by-default tool output + token measurement harness | Proposed | 2026-07-07 |
| [022](022-edge-upsert-bulk-load-v2.md) | Edge upsert bulk-load v2 — staged chunk apply, drop size threshold | Proposed | 2026-07-07 |
| [023](023-watch-lifecycle-self-service.md) | Watch lifecycle self-service — `agent install`, heartbeat, dead-watch surfacing | Proposed | 2026-07-07 |
| [024](024-memory-reliability-degraded-save.md) | Memory M0 — error envelope, degraded save, embedding backfill | Proposed | 2026-07-07 |
| [025](025-memory-write-reconciliation-hybrid-recall.md) | Memory M1+M2 — write reconciliation, `update_memory`, hybrid recall, selective reinforcement | Proposed | 2026-07-07 |
| [026](026-memory-lifecycle-invalidation.md) | Memory M3 — status lifecycle, invalidation over deletion, archive & export | Proposed | 2026-07-07 |
| [027](027-memory-code-binding-consolidation.md) | Memory M4+M5 — code-graph drift binding, maintenance report | Proposed | 2026-07-07 |
| [028](028-language-neutral-adoption-surface.md) | Language-neutral adoption surface — kill the ".NET-only" gestalt, description budget, languages-per-repo | Proposed | 2026-07-07 |

**Vision tiers (VISION.md):** ADRs 018–024, 028 form Tier 1 "Trust & Economy"; ADRs 025–027 are the Memory-v2 track (Tier 1.5/2 — see [MEMORY-V2-ASSESSMENT](../research/MEMORY-V2-ASSESSMENT.md)). Suggested order: **018 (P0 bug) → 024 (incident fix) → 028 (adoption, docs-heavy — parallelizable) → 022 (perf; shares agent-1.2.0 wire bump with 023) → 023 → 021 → 020 → 025 → 026 → 019 (consumes 018/021/023/025) → 027**. Implementation status tracked in [ROADMAP.md](../ROADMAP.md#vision-tier-1--trust--economy-2026h2).
