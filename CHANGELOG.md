# Changelog

All notable changes to CortexPlexus are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versioning notes:

- The repo version covers the **server + stack** (Docker images, MCP tools, docs).
- The **Local Agent** has its own version string (`CortexPlexus.Core.AgentInfo.Version`); bumped independently when wire-protocol or CLI changes.
- Users on `ghcr.io/dt-tuan/cortexplexus:main` always get the latest commit to `main`. Users on a pinned tag (e.g. `:0.6.0` for exact, `:0.6` for latest patch in the 0.6.x series) get a stable release. Docker image tags follow OCI convention (bare semver, no `v` prefix); the git tag is `v0.6.0`.

## [Unreleased]

### Added

- **Vertex AI embedding provider** (ADR-017) — opt-in third provider branch
  (`Embedding:Provider=vertex`) for the tri-cortex deployment, alongside the
  default all-local Ollama and the existing Gemini branch. Moves embedding
  inference off the I/O-throttled LXC host onto Google Cloud's Vertex
  `:predict` endpoint.
  - `VertexEmbeddingService : IEmbeddingService` — express-mode API-key auth on
    the `?key=` query string (no OAuth); 768-dim via `outputDimensionality` (no
    DB schema change); Polly retry on 429/408/503/500 + transient, no retry on
    401, graceful-empty on persistent failure.
  - `EmbedBatchAsync` **sub-batches** to `VertexInstancesPerCall` (default 5) —
    one `:predict` call per sub-batch, matching Vertex's per-call instance cap
    for `text-embedding-004/005` (differs from Gemini's single 100-instance call).
  - `EmbeddingOptions` gains `VertexProjectId`, `VertexLocation` (default
    `global` ⇒ bare host, no region prefix), `VertexModelId`,
    `VertexInstancesPerCall`, `VertexApiKey`. `vertex ⇒ MaxParallelBatches=4`.
  - `Program.cs` now binds the `Embedding` configuration section (UserSecrets in
    Development, env vars in the container) in addition to env-var reads, so the
    Vertex API key stays runtime-only and is never committed.

### Notes

- Switching a repo to Vertex requires a **full re-embed** (different vector
  space than Ollama `nomic-embed-text`, even though both are 768-dim). The OSS
  all-local default (`Provider=ollama`) and the Gemini branch are unchanged.

## [0.8.4] — 2026-04-19

Focus: **help AI agents self-configure auto-start + sync public docs with v0.8.0–0.8.3 feature adds**.

Two operational findings from v0.8.3 smoke use drove this release:
(1) The VS Code tasks.json auto-start trick added to the runbook in v0.8.3 was never surfaced to AI assistants during `ActivateAgent` — they had no way to install it without the user first reading docs.
(2) A drift audit found 8 public-facing docs still referencing "26 tools" (the pre-v0.8.0 count) and none documenting the staleness labels or repository-name resolution for memory tools.

### Changed

- **Doc-sync pass** — public-facing docs caught up with v0.8.0–0.8.3 feature adds.
  Tool count references updated from **26 → 30** in: `README.md` (banner + ASCII
  diagram), `docs/MCP-GUIDE.md` section 5, `docs/INTRODUCTION.md`,
  `docs/INTRODUCTION-VI.md`, `docs/PITCH-DECK.md`.
  `MCP-GUIDE.md` now carries the **Memory tools sub-section** (4 tools, repository
  NAME vs UUID addressing, pointer to `MEMORY-SYSTEM.md`) and a new **Staleness
  warnings (§5b)** section explaining the `⚠️ STALE` / `🚨 VERY STALE` labels
  and the response pattern AI agents should follow.
  `docs/AGENT-TEMPLATE.md` rewritten: now a 5-step playbook (check MCP → activate
  agent → read staleness → use 30 tools → memory workflow) instead of the 3-step
  version that predated v0.8.0.
- **`smoke-release` skill** — Step 5 expected tool count updated to 30 (was 26,
  a false-fail as of v0.8.0).

### Added

- **`ActivateAgent` output now includes Step 8 — VS Code auto-start recipe**
  (`src/CortexPlexus.App/Mcp/Tools/AgentTools.cs`). When the AI client calls
  `ActivateAgent`, the returned recipe ends with a platform-aware `.vscode/tasks.json`
  snippet (pre-filled with the detected `serverUrl` and platform-correct
  `${env:USERPROFILE}` / `${env:HOME}` path), plus a step-by-step "read → merge or
  create → report" procedure so AI agents in other projects can wire up
  `runOn: folderOpen` auto-start without manual input. Closes the common pain of
  "I forgot to start watch in VS Code and now my index is 3 days stale" by letting
  the assistant install the fix once per project. Honest about limitations:
  points users at `docs/runbooks/agent-auto-start.md` for reboot-level supervisors
  (systemd / Task Scheduler / NSSM) on non-VS-Code editors. +5 unit tests.
- **`.vscode/tasks.json`** committed-local in CortexPlexus repo itself (`.vscode/`
  is already gitignored) so opening this repo in VS Code re-syncs the agent
  automatically — dogfooding the guidance above.

## [0.8.3] — 2026-04-19

Focus: **operational safety around stale indices + UX polish for memory tools**.
Smoke-test findings from v0.8.2 real usage drove two related improvements:
(1) forgetting to start the agent at VS Code session start → search tools silently
return stale results, (2) memory tools required a UUID for `scopeId` which was
error-prone (typo, wrong-repo-copy, name-collision).

### Added

- **`save_memory` / `recall_memory` / `list_memories` accept a `repository` NAME** as
  an alternative to `scopeId` UUID (Option A). Repository name is resolved via
  `RepoResolver.ResolveAsync` (the same R25 tie-break helper used by all search
  tools). When both are provided, `repository` wins. Removes the whole class of
  "wrong UUID" bugs; agents no longer need to maintain a name→UUID lookup table.
  Old `scopeId` path remains for power users and backward compat. UUIDs are now
  validated to fail fast on a mistyped non-UUID instead of becoming orphan
  scope_ids silently.
- **NEW `StalenessLabel` helper** in `src/CortexPlexus.App/Mcp/Tools/` — computes
  human-readable age labels (`"(3 days ago) ⚠️ STALE"`) and optional warning
  footers for search results. Thresholds: &lt;6h hidden, 6h–24h plain, 24h–7d
  STALE, &gt;7d VERY STALE.
- **`list_repositories` annotates each repo's `Last indexed:` line** with its
  staleness label when &gt;6h old. Agents see immediately if an index is stale.
- **`search_code` / `semantic_search` / `explore_topic` append a staleness
  footer** when the queried repo's index is &gt;24h old, with a clear recovery
  action (`"Run ActivateAgent ..."`). Cross-repo queries use the stalest
  relevant repo so a fresh repo doesn't mask a stale one.
- **`ActivateAgent` leads with an "Index freshness" block** when the named
  project is already in the repositories table and the last index is &gt;6h
  old. Explains that the agent will re-sync via SHA-256 file-hash diff and
  how long the user should expect that to take. Best-effort — a DB hiccup on
  the freshness probe never fails ActivateAgent.
- **Runbook: [`docs/runbooks/agent-auto-start.md`](docs/runbooks/agent-auto-start.md)** gains a VS Code
  `.vscode/tasks.json` section — opening the workspace auto-launches
  `cortexplexus-agent watch` in a silent dedicated terminal. Lowest-cost fix
  for the "forgot to start the agent" pain; per-workspace, commits with the
  repo, survives editor reloads, dies with the editor.

### Tests

- 4 new tests in `MemoryToolsTests` covering Option A: repo name resolves to UUID,
  non-existent name returns friendly error, name-takes-precedence-over-UUID,
  mistyped UUID is rejected with a clear message.
- 10 new `StalenessLabelTests` locking in all 4 severity bands + singular/plural
  age formatting + null-lastIndexed behavior.
- Total: 803 tests green (up from 789).

## [0.8.2] — 2026-04-18

Hotfix: `BasicSecretsScanner` now detects common well-known API-key formats (Google / OpenAI / Anthropic / GitHub / AWS / JWT), not only `api_key=` / `Bearer` / connection-string patterns. Caught by an end-to-end smoke test on v0.8.1 where a Gemini-format key (`AIzaSyD...`) was accepted by `save_memory`.

### Fixed

- **`BasicSecretsScanner.ContainsSecrets` now flags deterministic API-key formats**:
  - Google API keys (Gemini, Maps, Firebase): `AIzaSy` + 33 chars
  - OpenAI: `sk-proj-*` or `sk-*` with 20+ chars
  - Anthropic: `sk-ant-*` with 20+ chars
  - GitHub PAT / fine-grained / OAuth tokens: `ghp_` / `gho_` / `ghu_` / `ghs_` / `ghr_` + 36+ chars
  - AWS Access Key ID: `AKIA` + 16 uppercase alphanumerics
  - JWT (three base64-url segments): `eyJ...eyJ...`
- **`BasicSecretsScanner.Sanitize` now redacts the same formats** with the `[REDACTED_API_KEY]` marker.

Same patterns are already in the client-side `report-cortexplexus-bug` skill; this aligns the server-side scanner with it so `save_memory` can't leak credentials that were typed in natural-language form.

### Tests

- 13 new unit tests in `CortexPlexus.Core.Tests.SecretsScannerTests`: 6 positives (one per key format), 4 false-positive guards (short strings, keyword-alone, unrelated prose), 3 Sanitize redaction tests. 789 tests green.

## [0.8.1] — 2026-04-18

Follow-up to v0.8.0: expanded AI-agent-facing guidance so coding assistants know
**when to use** the memory tools, not just **how**. No code / schema changes.

### Added

- **NEW `get_help(topic: "memory")` — full playbook for AI agents**. Covers: verifying memory is enabled via `list_repositories`, picking the right scope (session vs project vs global decision guide), picking the right topic with its decay implications, workflow patterns ("resume work on a project", "user states a preference", "bug tied to a symbol"), and 5 common mistakes. ~130-line dedicated topic.
- **`when-to-use` topic extended** — new "Memory tools (v0.8.0, opt-in)" table mapping user intent → tool, plus a "DO NOT save as memory" list. Keeps agents from polluting the store with facts derivable from code.
- **`strategies` topic extended** — 4-step memory workflow pattern (recall at start, save during work, cleanup on exit, use relatedFqns for symbol-linked notes).
- **`quick-start` updated** — added a "Step 3: Recall prior context (if memory is enabled)" between "Verify indexing" and "Explore", so agents see memory as part of the normal onboarding flow.
- **All 4 memory tool `[Description]` strings rewritten** with explicit "USE for" / "DO NOT USE for" guidance, default scope recommendations, and cross-links to `GetHelp(topic: "memory")`. This is what agents see in the tool list at connection time.

### Tests

- `GetHelp` valid-topic Theory now includes `"memory"`.
- New `GetHelp_MemoryTopic_CoversScopesTopicsAndWorkflow` regression guard — ensures scope names, topic names, and all 3 of SaveMemory/RecallMemory/ForgetMemory stay in the playbook.
- New `GetHelp_AllIncludesMemoryGuide` — ensures the "all" default includes memory guidance.
- Total tests: 776 (up from 773).

## [0.8.0] — 2026-04-18

Focus: **agent memory system**. Opt-in (default off) semantic + graph-linked memory store for AI coding agents — replaces scattered per-agent `.md` dumps with a centralised, scoped, auto-decaying store that understands code at the FQN level. Competitive gap identified in Mem0/Zep/Letta/Serena/Quarry research (no self-hosted + code-native competitor). New MCP tools: SaveMemory / RecallMemory / ListMemories / ForgetMemory. Total MCP tool count: 26 → 30.

### Added (v0.8.0 Wave 1 — foundation)

- **NEW [`docs/MEMORY-SYSTEM.md`](docs/MEMORY-SYSTEM.md)** — user-facing spec for the opt-in agent memory system: scopes (session/project/global), topics, Weibull decay, enabling via config.
- **NEW [`docs/decisions/010-memory-storage-reuse-postgres.md`](docs/decisions/010-memory-storage-reuse-postgres.md)** — ADR: reuse existing Postgres DB for memory storage; rejected Redis / SQLite / dedicated-service alternatives.
- **NEW [`docs/decisions/011-memory-scope-model.md`](docs/decisions/011-memory-scope-model.md)** — ADR: three-tier scope (session/project/global); rejected flat/two-tier/five-tier/per-actor alternatives.
- **NEW [`docs/decisions/013-memory-opt-in-default.md`](docs/decisions/013-memory-opt-in-default.md)** — ADR: memory is disabled by default; opt-in via `Memory__Enabled=true` or `appsettings.json`. Rationale: trust + upgrade safety.
- **NEW `CortexPlexus.Memory` project** — `AgentMemoryStore` with save / recall (filter path) / list / forget / count, backed by the `agent_memories` table.
- **NEW `CortexPlexus.Core.Models.AgentMemory`** + `MemoryScope` + `MemoryTopic` constants (shared with the MCP tool layer in Wave 2).
- **NEW `CortexPlexus.Core.Abstractions.IAgentMemoryStore`** interface.
- **NEW `CortexPlexus.Memory.MemoryOptions`** — `Enabled` (default `false`), `ReapIntervalHours`, `MaxMemoriesPerScope`, `DefaultImportance`.
- **NEW schema migration** — `agent_memories` table with CHECK constraints (scope enum, scope_id-required-unless-global, importance 0..1, content 1..4000 chars), HNSW on `embedding vector(768)`, GIN on `related_fqns`, B-tree on `(scope, scope_id)`. Idempotent; runs on startup after the existing `IGraphStore.InitializeSchemaAsync()`.
- **NEW `CortexPlexus.Memory.Tests`** — 17 integration tests against a real pgvector container covering schema idempotency, scope validation, filter queries, access-count bumping on recall, forget semantics.

Wave 1 is **foundation only** — the store exists and the gate defaults to disabled, but no MCP tools are wired yet. Wave 2 (decay + 4 MCP tools) lands next. See [`docs/PLAN-v0.8.0.md`](docs/PLAN-v0.8.0.md).

### Added (v0.8.0 Wave 2 — decay + MCP surface)

- **NEW [`docs/decisions/012-memory-decay-weibull.md`](docs/decisions/012-memory-decay-weibull.md)** — ADR: Weibull decay (shape k=1.5) with per-topic λ (preference 365d, pattern/decision 180d, bug 90d, todo 30d, note 60d). Auto-forget at score < 0.1.
- **NEW `CortexPlexus.Memory.MemoryScoring`** — pure C# `Score` function + equivalent SQL expression (`ScoreSqlExpression`) inlined in `RecallAsync` and `ReapAsync` so ranking and pruning share one source of truth.
- **`IAgentMemoryStore.ReapAsync`** + `AgentMemoryStore.ReapAsync()` — deletes rows whose decay score is below the forget threshold. Single SQL `DELETE`, idempotent.
- **`RecallAsync` now ranks by decay × cosine similarity**. Rows below the forget threshold are filtered out even before the reaper runs. `ListAsync` still returns everything for audit.
- **NEW `CortexPlexus.Memory.MemoryReaper`** — `BackgroundService` that scans on an interval (`ReapIntervalHours`, default 24). Short-circuits when `Memory.Enabled=false`. Registered unconditionally; safe when disabled.
- **NEW 4 MCP tools** in `src/CortexPlexus.App/Mcp/Tools/MemoryTools.cs`:
  - `save_memory` — PII scan via `ISecretsScanner`, validates scope / scope_id / topic / importance, calls embedding service, persists. Returns JSON with the new memory's ID.
  - `recall_memory` — computes query embedding, combines with decay score for ranking. Bumps access count on returned rows.
  - `list_memories` — filter-only audit surface. Includes near-forgotten rows.
  - `forget_memory` — explicit delete by UUID.
  All four gate on `Memory.Enabled`; when disabled they return a clear "enable via config" message (ADR-013).
- **`list_repositories` now advertises memory state** — appends `Memory: enabled (N items).` or `Memory: disabled. Enable via Memory__Enabled=true ...` so users / agents see the feature state without having to probe.
- **`get_help` tool reference** updated: 26 → 30 tools. New "AGENT MEMORY (opt-in)" section lists the 4 tools with one-line usage examples.
- **Tests**: 19 `MemoryScoringTests` (pure, no DB — locks in decay formula), 7 `MemoryDecayTests` (integration: reap + decay-filtered recall), 19 `MemoryToolsTests` (NSubstitute mocks exercising every gate + validation branch). All 769 tests passing.

### Added (v0.8.0 Wave 3 — symbol-graph integration + cross-cutting docs)

- **`get_impact_analysis` gains `include_memories=false` parameter** — when true AND `Memory.Enabled=true`, memories linked to the analysed FQN surface under a new `--- Linked Memories ---` section alongside callers / implementations / hierarchy / references. Memory-store exceptions are swallowed so a glitch never breaks the core impact output.
- **`explore_topic` gains the same `include_memories` flag** — memories linked to the deepest-explored symbol surface under `**Linked Memories**` in the report. Opt-in, gated identically.
- **`docs/MEMORY-SYSTEM.md` §Symbol linking** updated with the final `include_memories=true` semantics on both tools.
- **NEW `docs/ARCHITECTURE.md` §5.5** — agent memory store as the 4th pillar alongside graph / vector / FTS, with schema summary, decay overview, and cross-links to ADRs 010/011/012/013.
- **`README.md` Features section** — tool count bumped 26 → 30, new bullet for the opt-in memory system, MCP Tools section header updated.
- **`get_help("tools")` tool reference** — "AGENT MEMORY (opt-in)" section added with one-line examples for the 4 tools.
- **Tests**: 4 new `MemoryIntegrationTests` covering `include_memories=false` (no recall), `includeMemories=true + disabled` (no recall), happy path (surfaces memory content with topic label), and memory-store exception (impact still returns). 773 tests passing.

## [0.7.1] — 2026-04-17

### Fixed

- **Locale-dependent percent format in Health label** — `FormatHealthLabel` used `:P0` which renders `"100 %"` (with non-breaking space U+00A0) on certain OS locales, breaking substring matches in tests and downstream consumers. Now forces `CultureInfo.InvariantCulture` and strips both U+00A0 and regular spaces for a stable `"100%"` output everywhere.

### Added

- **NEW [`docs/research/agent-memory-system.md`](docs/research/agent-memory-system.md)** — landscape survey of agent memory systems (Mem0, Zep, Letta, Serena, Quarry) with 6 reusable patterns, 5 anti-patterns, and a proposed design for CortexPlexus memory (TTL + scoped topics + Weibull decay). Research only — no implementation changes.

## [0.7.0] — 2026-04-15

### Changed

- **Docs**: corrected the `Requirements` section of [`README.md`](README.md) — actual idle memory is ~150 MB (app 99 MiB + postgres 25 MiB), not the 4 GB previously claimed. New guidance is tiered: 1 GB min / 2 GB recommended / 4 GB only for 20K+ symbol repos or co-located Ollama.

### Fixed

- **`list_repositories` Health metric is now kind-aware** ([ADR 008](docs/decisions/008-kind-aware-health-metric.md), [HEALTH-METRICS.md](docs/HEALTH-METRICS.md)). Previously every healthy .NET repo showed `PARTIAL` because the metric compared `embeddings / total_symbols`, but field/property/event/constructor are intentionally not embedded — so a typical .NET repo's ratio caps around 30-50%. CortexFlow showed `PARTIAL — 2130/5273 (40%)` despite being 100% healthy. Now compares `embeddings / embeddable_symbols` (where embeddable kinds = class, method, interface, struct, record, function, type, document, section). Output also shows both numerators ("100% of 2130 embeddable kinds") so the calculus is self-explanatory.

### Added

- **NEW [`docs/HEALTH-METRICS.md`](docs/HEALTH-METRICS.md)** — user-facing spec for the `Health:` line: every label, condition, and recovery action.
- **NEW [`docs/decisions/008-kind-aware-health-metric.md`](docs/decisions/008-kind-aware-health-metric.md)** — ADR explaining why embeddable kinds (not total) is the right denominator, with three rejected alternatives.
- **NEW [`docs/runbooks/agent-best-practices.md`](docs/runbooks/agent-best-practices.md)** — single-`.sln` indexing (3× faster than per-`.csproj`), watch-mode behavior, throughput tuning, force-reindex flow.
- **NEW `CortexPlexus.Core.EmbeddableKinds`** — single source of truth for the embeddable-kind allow-list, used by `IndexingPipeline`, `AgentApiEndpoints`, and `GraphTraversalTools.ListRepositories`. Replaces three duplicated `s.Kind is "class" or "method" or ...` literals.
- **`docs/ARCHITECTURE.md` §3.5** now enumerates embeddable vs non-embeddable kinds with rationale, replacing the implicit knowledge in `IndexingPipeline.cs` comments.
- **README + MCP-GUIDE** link to `HEALTH-METRICS.md` and `agent-best-practices.md` from the docs index and the "First 3 commands" section.

### Changed

- **`/api/index/results` response schema**: `embeddingsPersisted` and `embeddingsFailed` are now deprecated aliases (kept as computed properties for one release). Prefer the new `symbolsPersisted` / `symbolsFailed` — the historical names were misleading because the value counts symbol rows, not embedding rows. New `vectorRowsWritten` field counts symbols whose `embedding` column ended up non-null, distinguishing "row inserted with NULL embedding" (expected for non-embeddable kinds) from "row inserted with vector". Old (1.1.0) agents keep working against new servers; new (1.2.0+) agents keep working against old servers via the wire-compat picker in `LocalIndexer.UploadAck`. Removal target: v0.8.0. See [`docs/API.md`](docs/API.md).
- **`VectorUpsertResult` record** in `CortexPlexus.Core` gains a `VectorRowsWritten` member alongside `Persisted` / `Failed`. Used by the App-side `/api/index/results` handler to populate the new response field.

### Added

- **NEW [`docs/API.md`](docs/API.md)** — concise REST API reference for `/api/agent/version`, `/api/agent/download`, `/api/index/results`, and `/api/index/{name}/hashes`. Documents the new field names, the deprecated aliases, and the wire-compat policy.

### Fixed

- **AGE edge upsert scaling** ([ADR 009](docs/decisions/009-age-edge-upsert-scaling.md)). Edge phase on CortexFlow (19K edges / 4 chunks) slowed linearly: 11.8s → 20.9s → 28.5s → 30.4s (total 91.6s). Root cause: AGE's `MERGE (a)-[r:Type]->(b)` performs a sequential scan on the edge label table per edge, and the table grows with each chunk. Fix: for bulk indexing (≥500 edges), delete all outgoing edges for affected source vertices first, then `UNWIND + CREATE` (not MERGE) with `MATCH` on existing vertices. Projected improvement: ~15× (91s → ~6s). Incremental watch-mode still uses MERGE for small batches.

## [0.6.0] — 2026-04-15

Focus: indexing **correctness** (silent-failure bugs closed), indexing **UX** (progress + health), agent **reliability** (watch-mode resilience, auto-start), and deployment **hygiene** (pinning, image-bundled agent tarballs, runbooks).

### Added

- **MCP progress notifications** during server-side indexing — `index_from_local`, `index_from_git`, and the full pipeline emit phase events (`detect → parse → embed → graph → vector`) via `notifications/progress`. The embedding step reports per-batch sub-progress (`2 + done/total`) so the progress bar moves continuously during the slowest phase. Clients that don't send a `progressToken` silently ignore the reports.
- **`list_repositories` health probe** — each repo line now shows `Health: OK / PARTIAL / DEGRADED / EMPTY / UNKNOWN`, computed from a live join against `code_symbols`. Detects the "registered but empty" state that silently happened when `/api/index/results` returned 200 but the vector upsert rolled back.
- **`force_reindex(name)` MCP tool** — wipes the server-side file-hash cache for a given repo so the next `ActivateAgent` / `index_from_local` treats every file as changed. Symbols are not deleted in place; FQN upsert overwrites them cleanly.
- **Honest failure surfacing end-to-end** — `IVectorStore.UpsertAsync` now returns `VectorUpsertResult { Persisted, Failed }`. `/api/index/results` forwards these counts plus a `Warnings[]` string list. The Local Agent treats any non-zero `EmbeddingsFailed` as a hard error and aborts the upload. Replaces the old path where a 100% vector-upsert failure still returned HTTP 200 with input-side stats.
- **`IProgress<ProgressNotificationValue>` parameter** on `IndexingPipeline.IndexAsync` (non-MCP callers pass null).
- **`VectorUpsertResult` + `AgentInfo`** types in `CortexPlexus.Core` (SSOT for the agent version string shared between the agent CLI and the server endpoint).
- **Agent `watch` mode honors `.cortexplexusignore`** — `ProjectFileWatcher` loads user patterns at construction and filters events against them in addition to the built-in exclude list.
- **`FileSystemWatcher` overflow recovery** — on `OnError` (typically OS kernel buffer overflow during mass renames / git checkout), the watcher enumerates the watched tree and re-queues every eligible file instead of silently dropping the burst.
- **`ComputeFileHash` retry** — 3 attempts with 150/300ms backoff on `IOException` / `UnauthorizedAccessException` so the IDE's mid-save file-lock race no longer drops files from the batch.
- **Image-bundled agent tarballs** — `src/CortexPlexus.App/Dockerfile` now publishes the agent for `linux-x64` / `win-x64` / `osx-x64` and ships the tarballs in `/app/_agent/`. `/api/agent/download` resolves image-bundled first, then `${Workspace__Path}/_agent/` as an operator override. `docker compose pull` now ships the matching agent — no more manual SCP on version bumps.
- **`ActivateAgent` auto-detects server URL** from the incoming MCP request's `Host` header (multi-host friendly). Output now has a 7-step decision tree with explicit PASS/FAIL markers, a connectivity check (Step 2), a version check (Step 3), and clear branching for the AI assistant to follow.
- **`force_reindex` + `list_repositories` + 26-tool help** reflect the new tool count; the agent's `get_help("tools")` is updated.
- **Runbook: [`docs/runbooks/maintenance.md`](docs/runbooks/maintenance.md)** — disk & Docker housekeeping with a weekly cron template and two emergency-recovery variants (one that protects `cortexplexus_*` volumes).
- **Runbook: [`docs/runbooks/agent-auto-start.md`](docs/runbooks/agent-auto-start.md)** — systemd user unit (instance-parameterised), Windows Task Scheduler PowerShell, NSSM service for headless Windows, macOS LaunchAgent.
- **Marketing docs**: [`docs/INTRODUCTION.md`](docs/INTRODUCTION.md) (EN), [`docs/INTRODUCTION-VI.md`](docs/INTRODUCTION-VI.md) (VN), [`docs/PITCH-DECK.md`](docs/PITCH-DECK.md) — one-pagers + 8-slide outline grounded in real benchmarks (R18 HNSW 556× speedup, 26 MCP tools, 693 tests).
- **Antigravity MCP setup** in `README.md` + `docs/MCP-GUIDE.md` — uses `serverUrl` (not `url`); client table and schema differences documented so AI assistants don't copy the Claude Code pattern by accident.
- **MCP/IDE config gitignore hygiene**: `.mcp.json`, `.cursor/`, `.vscode/mcp.json`, etc. are git-ignored; `.mcp.json.example` is tracked as the template.

### Changed

- **Agent version bumped to `1.1.0`** (from `1.0.0`) — backward-compatible wire protocol; old servers accept new agents, new servers accept old agents (persist-counts default to zero).
- **Local Agent parses the `/api/index/results` response body** to check `EmbeddingsFailed`; on any persist error it throws with a clear message instead of silently succeeding.
- **Postgres base pinned** to `apache/age:release_PG17_1.6.0`; **pgvector pinned** to `v0.8.0` with `-march=x86-64-v2` so image builds run on CI with AVX-512 don't produce binaries that SIGILL on older host CPUs (Kaby Lake and similar).
- **`AgentInfo.Version` single source of truth**: moved to `CortexPlexus.Core`; server `/api/agent/version` reads it directly instead of duplicating the string.
- **`ActivateAgent` MCP tool signature** gained `IProgress<ProgressNotificationValue>` and `CancellationToken`; output completely rewritten as a 7-step recipe with verification hooks.
- **README `Why CortexPlexus?` section** links to the full marketing/pitch docs.
- **MCP-GUIDE "Developing on CortexPlexus itself"** clarifies that project-level `.mcp.json` shadows `--scope user`; documents the right escape hatches.

### Fixed

- **Issue [#1](https://github.com/DT-Tuan/cortexplexus/issues/1)** — pgvector type cache poisoned on fresh DB boot. Npgsql probed `pg_type` before `CREATE EXTENSION vector` ran, cached "vector type unknown", and every subsequent vector read/write failed while HTTP `/api/index/results` still returned 200. Fixed by running `CREATE EXTENSION IF NOT EXISTS vector; CREATE EXTENSION IF NOT EXISTS age` on a raw `NpgsqlConnection` **before** the `NpgsqlDataSource` singleton is built, with a 30×1s retry loop for startup races.
- **Postgres SIGILL on DELETE CASCADE / schema init** — CI rebuild produced pgvector binaries using AVX-512 paths that older Xeon/Core hosts (Intel Core i3-7100 / Kaby Lake) could not execute. Fixed by pinning `pgvector` to `v0.8.0` and compiling with `OPTFLAGS="-O2 -march=x86-64-v2"`.
- **`apache/age:latest` silent major bump** — upstream rolled `:latest` from PG17 to PG18 mid-session, breaking fresh deploys. Pinned to a release tag.
- **Test regression** — `GraphTraversalToolsTests.ListRepositories_*` were failing in CI because the new DI parameter `NpgsqlDataSource` was added as non-nullable; changed to `NpgsqlDataSource? = null` and guarded the health probe.
- **`ActivateAgent` recipe used to hand back `http://localhost:8080`** to AI clients connecting from a different host — recipe steps all failed with connection refused, the AI improvised wrong URLs. Now auto-detected from the MCP connection.
- **Stale `LocalIndexer` response parsing** — agent never looked at `EmbeddingsFailed`; partial uploads silently reported "done". Fixed alongside the server-side honest-response change.
- **`.gitignore` inline comments were interpreted as patterns** — rules for `.claude/`, `.git-backup/`, `deploy.sh` were never actually matched; they appeared as untracked in every `git status` despite the rule. Comments moved to their own lines; file re-audited.
- **Docker Compose image tags** use `:main` (CI publishes branch name, not `:latest`) for the default deployment.

### Security

- **`secrets sanitize` pass confirmed in place** in the embedding pipeline — symbols go through `ISecretsScanner.Sanitize` before text leaves the dev host for Ollama / Gemini.
- **Issue-report skill** (`~/.claude/skills/report-cortexplexus-bug/`) has hardened PII scrubbing: 11 regex rules (Gemini / OpenAI / GitHub PAT / JWT / Slack / email / Linux+macOS+Windows user paths / RFC1918 private IPs / public IPs). Rules moved out of a markdown table (where `\|` was silently interpreted as escaped pipe and broke alternation).

### Infrastructure

- **`/etc/cron.weekly/docker-prune`** installed on the reference LXC host (pruned 6.6 GB of stale layers + 118 MB dangling during install).
- **CI `docker-publish.yml`** now also builds and tags postgres image (`ghcr.io/dt-tuan/cortexplexus-postgres:main`) per commit; app image now bundles agent tarballs.
- **`.mcp.json.example`** at repo root for fresh clones; `.mcp.json` is git-ignored for per-developer URL/auth overrides.

### Breaking

None. All wire-protocol and MCP-tool changes are additive; old clients see new fields as ignored JSON, new clients on old servers see defaults.

## [0.5.0] — 2026-04-12

Initial public release.

- Open-source Code Intelligence Platform: Roslyn (C# deep) + Tree-sitter (TS / JS / Python / Java / Go / Rust / PHP) → PostgreSQL 17 + AGE + pgvector HNSW + tsvector BM25 → MCP.
- 26 MCP tools across search, navigation, .NET deep analysis, quality / observability, composite.
- Two-container Docker Compose deployment; Ollama (offline) or Google Gemini (free tier) for embeddings.
- 693 tests passing (~85% coverage).
- GitHub Release: agent tarballs for linux-x64 / win-x64 / osx-x64 + SHA256SUMS.

[Unreleased]: https://github.com/DT-Tuan/cortexplexus/compare/v0.8.4...HEAD
[0.8.4]: https://github.com/DT-Tuan/cortexplexus/compare/v0.8.3...v0.8.4
[0.8.3]: https://github.com/DT-Tuan/cortexplexus/compare/v0.8.2...v0.8.3
[0.8.2]: https://github.com/DT-Tuan/cortexplexus/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/DT-Tuan/cortexplexus/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/DT-Tuan/cortexplexus/compare/v0.7.1...v0.8.0
[0.7.1]: https://github.com/DT-Tuan/cortexplexus/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/DT-Tuan/cortexplexus/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/DT-Tuan/cortexplexus/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/DT-Tuan/cortexplexus/releases/tag/v0.5.0
