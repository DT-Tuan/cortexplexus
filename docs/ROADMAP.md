# CortexPlexus — Roadmap

## Phase Overview

| Phase | Name | Duration | Status |
|-------|------|----------|--------|
| **1** | MVP: Index & Query | 6 weeks | ✅ Complete |
| **2** | Watch & Search | 4 weeks | ✅ Complete |
| **3** | .NET Deep Analysis | 4 weeks | ✅ Complete |
| **4** | Community & Polish | Ongoing | ✅ Complete (core) |
| **5** | Search Intelligence | 3 weeks | ✅ Complete |
| **6** | Graph Visualization | 1 day | ✅ Complete |
| **7** | Remote Indexing & Composite Tools | 1 week | ✅ Complete |
| **8** | Local Agent & Slim Docker | 2 days | ✅ Complete |
| **9** | Multi-Language Expansion | 1 day | ✅ Complete |
| **10** | Code Intelligence: Docs, Summaries & Test Mapping | 1 day | ✅ Complete |
| **Tier B** | Multi-Language Framework Intelligence (ADR-016) | — | ✅ C1–C4 shipped (2026-06-21) |

---

## Phase 1–6: Previously Completed

See git history for details. Key milestones:
- Phase 1 (2026-04-03): Core pipeline, 7 MCP tools, Roslyn parser, PostgreSQL+AGE+pgvector
- Phase 2: Incremental indexing, HybridQueryRouter, RRF fusion, Ollama embedding
- Phase 3: EF Core, DI, ASP.NET route analysis, NuGet audit — 15 MCP tools
- Phase 4: Tree-sitter (TypeScript/JavaScript/Python), multi-project workspace
- Phase 5 (2026-04-04): Query expansion (HyDE + multi-query), weighted FTS
- Phase 6 (2026-04-04): Cytoscape.js web graph explorer, REST API

---

## Phase 7 — "Remote Indexing & Composite Tools"

**Goal:** Index projects from remote machines without mounting filesystem
**Status:** ✅ Complete (2026-04-05)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| 7.1 | POST `/api/index/push` (zip upload → index) | ✅ | Max 50MB, recommend `git archive` |
| 7.2 | POST `/api/index/git` (clone URL → index) | ✅ | HTTPS only, branch support |
| 7.3 | MCP tools: IndexFromLocal, IndexFromGit | ✅ | URL validation, command injection prevention |
| 7.4 | GetHelp MCP tool (self-documenting) | ✅ | 4 topics: quick-start, tools, indexing, strategies |
| 7.5 | ExploreTopic composite tool (search→callers→deps→callees) | ✅ | 1 call replaces 5 calls |
| 7.6 | OnboardProject composite tool (DI→endpoints→entities) | ✅ | 1 call replaces 4 calls |
| 7.7 | Type dependency extraction (DependsOn, UsesType edges) | ✅ | TypeDependencyExtractor |
| 7.8 | API data flow tracing (HandledBy edges) | ✅ | GetDataFlow tool |

**Deliverable:** 21 MCP tools. Remote indexing via zip/git. Composite tools reduce 4-5x tool calls.

---

## Phase 8 — "Local Agent & Slim Docker"

**Goal:** Source code never leaves dev machine. Slim server image.
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| 8.1 | CortexPlexus.Agent project (standalone CLI) | ✅ | `watch`, `index`, `stop`, `status`, `update` commands |
| 8.2 | LocalIndexer: parse locally → POST /api/index/results | ✅ | Roslyn + Tree-sitter, only metadata sent |
| 8.3 | ProjectFileWatcher: FileSystemWatcher + 3s debounce | ✅ | OS kernel events, ~0% CPU when idle |
| 8.4 | AgentUpdater: self-update with SHA256 verification | ✅ | Hash from `/api/agent/version` |
| 8.5 | PidManager: process lifecycle management | ✅ | PID files in `~/.cortexplexus/agent/pids/` |
| 8.6 | Server: `/api/index/results` endpoint | ✅ | Receive pre-parsed symbols + relationships |
| 8.7 | Server: `/api/agent/download`, install scripts (sh/ps1) | ✅ | Framework-dependent archive (62MB gzipped) |
| 8.8 | Server: `/api/index/{project}/hashes` for incremental sync | ✅ | File hash comparison |
| 8.9 | MCP tool: ActivateAgent | ✅ | AI auto-installs + starts agent on session start |
| 8.10 | Slim Docker: `aspnet:10.0-noble-chiseled` | ✅ | 906MB (was 1.97GB, -54%) |
| 8.11 | SDK detection: skip Roslyn when no SDK, actionable AI guidance | ✅ | TS/JS/Py still work on slim image |
| 8.12 | Smart repo naming: detect from .sln/package.json/pyproject.toml/git | ✅ | No more "workspace" as name |
| 8.13 | Security: relative paths, platform allowlist, content truncation | ✅ | OWASP-based audit |
| 8.14 | Docker image publish via GitHub Actions CI/CD | ✅ | `ghcr.io/dt-tuan/cortexplexus` |
| 8.15 | GitHub Actions: build-and-test, docker-publish, release | ✅ | CI on every push/PR |

### Architecture Changes

```
Developer Machine                          CortexPlexus Server (slim)
┌──────────────────────┐                   ┌────────────────────────┐
│ cortexplexus-agent   │                   │ aspnet:10.0-chiseled   │
│ ├─ Roslyn (C#)       │  POST metadata   │ ├─ MCP Server (21 tools)│
│ ├─ Tree-sitter (8)   │ ──────────────►  │ ├─ REST API (13 ep)    │
│ └─ FileSystemWatcher │  (no source code) │ ├─ Web UI (Cytoscape)  │
│                      │                   │ ├─ Embedding (Ollama)  │
│ Source code stays    │                   │ └─ PostgreSQL stores   │
│ on dev machine       │                   │   (AGE+pgvector+FTS)   │
└──────────────────────┘                   └────────────────────────┘
```

### Security Hardening
- File paths: absolute → project-relative (no dev machine info leaked)
- Agent download: platform allowlist (no path traversal)
- Agent update: SHA256 hash verification (no MITM)
- DocumentSection.Content: truncated to 500 chars
- Shell commands: quoted project names
- Docker: non-root chiseled image

**Deliverable:** ✅ Local Agent (source stays on dev machine), slim Docker (-54%), security audit skill, deploy script.

---

## Phase 9 — "Multi-Language Expansion"

**Goal:** Extend Tree-sitter support from 3 to 7 languages
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| 9.1 | JavaExtractor | ✅ | Package-based FQN, annotations, enums, inheritance |
| 9.2 | PhpExtractor | ✅ | Namespace `\` FQN, traits, dual `use` keyword |
| 9.3 | GoExtractor | ✅ | Receiver methods, struct fields, package FQN |
| 9.4 | RustExtractor | ✅ | `impl` blocks, trait impl, module path FQN |
| 9.5 | Update LanguageRegistry + TreeSitterCodeParser | ✅ | 4 new extensions, routing, excluded dirs |
| 9.6 | Tests: 23 new (81 parsing total, 125 all suites) | ✅ | 6 Java + 4 PHP + 6 Go + 7 Rust |
| 9.7 | Update excluded dirs: target/, .gradle/, vendor/ | ✅ | Java/Rust/Go/PHP build dirs |

### Language Support Matrix

| Language | Parser | Depth | File Extensions |
|----------|--------|-------|-----------------|
| **C#** | Roslyn (semantic) | Deep — call graph, DI, EF Core, API routes | `.cs` `.sln` `.csproj` |
| **TypeScript** | Tree-sitter | Moderate — classes, functions, imports, calls | `.ts` `.tsx` |
| **JavaScript** | Tree-sitter | Moderate — classes, functions, imports, calls | `.js` `.jsx` |
| **Python** | Tree-sitter | Moderate — classes, functions, decorators, imports | `.py` |
| **Java** | Tree-sitter | Moderate — classes, interfaces, enums, annotations | `.java` |
| **Go** | Tree-sitter | Moderate — structs, interfaces, receiver methods | `.go` |
| **Rust** | Tree-sitter | Moderate — structs, traits, impl blocks | `.rs` |
| **PHP** | Tree-sitter | Moderate — classes, interfaces, traits, namespaces | `.php` |
| **Markdown** | Custom | Heading-based sections, FTS searchable | `.md` |

**Deliverable:** ✅ 8 programming languages + Markdown. 125 tests. No new dependencies (TreeSitter.DotNet already includes all grammars).

---

## Phase 10 — "Code Intelligence: Docs, Summaries & Test Mapping"

**Goal:** Extract documentation, generate AI summaries, and map tests to production code
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| 10.1 (P0a) | Documentation extraction — XML doc, JSDoc, docstrings | ✅ | All 8 languages: C# XML doc, TS/JS JSDoc, Python docstrings, Java Javadoc, Go `//` comments, Rust `///`/`//!`, PHP `/** */` |
| 10.2 (P0b) | AI-generated method summaries via Ollama | ✅ | `ISummaryGenerator` interface, `OllamaSummaryGenerator` + `NoOpSummaryGenerator`, configurable model/concurrency |
| 10.3 (P1a) | Test-to-Code mapping — TestCovers edges | ✅ | Detects xUnit `[Fact]`/`[Theory]`, NUnit `[Test]`, pytest `test_`, Jest `it()`/`test()`, JUnit `@Test`, Go `Test*`, Rust `#[test]`, PHPUnit `test*` |
| 10.4 | MCP tool: GetTestCoverage | ✅ | Returns test methods linked via TestCovers edges |
| 10.5 | DocComment stored in `code_symbols.doc_comment` | ✅ | New column, included in FTS (weight D) |
| 10.6 | Summary stored in `code_symbols.summary` | ✅ | Generated on-demand via Ollama, cached in DB |
| 10.7 | Integration test fix: VectorStore logger param | ✅ | Added `ILogger<VectorStore>` to test constructors |

### Key Components
- `DocCommentHelper` — Tree-sitter doc comment extraction for 7 languages
- `SymbolExtractor` — Roslyn XML doc extraction for C#
- `CallGraphExtractor` — TestCovers edge detection (8 test frameworks)
- `OllamaSummaryGenerator` — AI summary generation via local Ollama
- `SummaryOptions` — Configurable: model, max concurrency, enabled flag

**Deliverable:** ✅ Documentation extraction (8 languages), AI summaries (Ollama), test-to-code mapping (8 test frameworks), GetTestCoverage MCP tool verified working.

---

## Phase 10 (P1b) — "Configuration Mapping"

**Goal:** Trace configuration access from code to config files across all 8 languages
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P1b.1 | `ReadsConfig` relationship type + `ConfigKeyInfo` model | ✅ | New RelationshipType enum value, new CodeSymbol subclass |
| P1b.2 | `ConfigurationAnalyzer` (Roslyn) — IConfiguration, IOptions\<T\>, GetSection, GetValue, Environment.GetEnvironmentVariable | ✅ | CSharpSyntaxWalker, method-level granularity |
| P1b.3 | `ConfigAccessDetector` (Tree-sitter) — cross-language env var detection | ✅ | Python `os.environ`/`os.getenv`, JS/TS `process.env`, Java `System.getenv`/`getProperty`, Go `os.Getenv`, Rust `env::var`, PHP `$_ENV`/`getenv` |
| P1b.4 | All 6 Tree-sitter extractors integrated | ✅ | Config detection on function body nodes |
| P1b.5 | `ConfigFileParser` — appsettings.json, .env, docker-compose.yml | ✅ | JSON flattening, KEY=VALUE parsing, YAML environment section |
| P1b.6 | `QueryConfigUsageAsync` in AgeGraphStore | ✅ | Query config_key nodes + ReadsConfig edge readers |
| P1b.7 | `GetConfigUsage` MCP tool | ✅ | "Config này dùng ở đâu?" — 22nd MCP tool |
| P1b.8 | 18 new unit tests | ✅ | 6 languages × config detection + ConfigFileParser tests |

### Key Components
- `ConfigurationAnalyzer` — Roslyn CSharpSyntaxWalker for C# config patterns
- `ConfigAccessDetector` — Static helper for cross-language env var detection
- `ConfigFileParser` — Parses appsettings.json, .env, docker-compose.yml
- `GetConfigUsage` MCP tool — Find all code that reads a given config key

**Deliverable:** ✅ Configuration mapping (8 languages + 3 config file types), ReadsConfig edges, GetConfigUsage MCP tool, 143 total tests.

---

## P2a — "Fields/Events Extraction"

**Goal:** Extract fields, constants, events from C# code + detect event subscriptions/publications
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P2a.1 | `FieldInfo` + `EventInfo` models | ✅ | New CodeSymbol subclasses: Type, IsStatic, IsReadOnly, IsConst, ConstantValue |
| P2a.2 | `HasField`, `HasEvent`, `Subscribes`, `Publishes` relationship types | ✅ | 4 new RelationshipType enum values |
| P2a.3 | `VisitFieldDeclaration` in SymbolExtractor | ✅ | Fields, readonly, const, static. Skips compiler-generated backing fields |
| P2a.4 | `VisitEventFieldDeclaration` in SymbolExtractor | ✅ | Event declarations with HasEvent edges |
| P2a.5 | Event subscription detection in CallGraphExtractor | ✅ | `+=` → Subscribes, `?.Invoke()` / direct call → Publishes |
| P2a.6 | 10 new unit tests | ✅ | 6 field + 3 event + 1 subscription |

**Deliverable:** ✅ Fields, constants, events extracted from C#. Event subscription/publish tracking. 153 total tests.

---

## P2b — "Exception / Error Flow"

**Goal:** Trace throw/catch chains through C# code
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P2b.1 | `Catches` relationship type | ✅ | New RelationshipType enum value (Throws already existed) |
| P2b.2 | `ExceptionFlowExtractor` (Roslyn) | ✅ | CSharpSyntaxWalker: throw new X → Throws edge, catch(X) → Catches edge |
| P2b.3 | Integrated into RoslynCodeParser (both full + incremental) | ✅ | |
| P2b.4 | 4 new unit tests | ✅ | throw, catch, multiple, dedup |

**Deliverable:** ✅ Exception flow tracing — Throws + Catches edges with full exception type FQN.

---

## P2c — "Code Metrics"

**Goal:** Calculate cyclomatic complexity, nesting depth, line count per method
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P2c.1 | `CyclomaticComplexity`, `MaxNestingDepth`, `LineCount` fields on MethodInfo | ✅ | Nullable int fields |
| P2c.2 | `CodeMetricsAnalyzer` — static analyzer | ✅ | if/for/foreach/while/do/switch/catch/ternary/&&/\|\|/?? |
| P2c.3 | Integrated into SymbolExtractor.VisitMethodDeclaration | ✅ | Computed from method body or expression body |
| P2c.4 | 8 new unit tests | ✅ | Simple, if-else, nested, switch, logical ops, ternary, try-catch, line count |

**Deliverable:** ✅ Per-method metrics: cyclomatic complexity, max nesting depth, line count.

---

## P2d — "Dead Code Detection"

**Goal:** Find public/internal methods with no callers
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P2d.1 | `QueryDeadCodeAsync` in IGraphStore + AgeGraphStore | ✅ | Relational query for candidates, graph check for Calls edges |
| P2d.2 | `GetDeadCode` MCP tool | ✅ | 23rd MCP tool — "Code nào có thể xóa an toàn?" |
| P2d.3 | Skips constructors, Main, entry points | ✅ | Reduces false positives |

**Deliverable:** ✅ Dead code detection via graph analysis. GetDeadCode MCP tool.

---

## P2e — "Cross-Service HTTP Call Tracing"

**Goal:** Detect outgoing HTTP calls in code across all 8 languages
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P2e.1 | `HttpCallExtractor` (Roslyn) — HttpClient, IHttpClientFactory | ✅ | GetAsync, PostAsync, PutAsync, DeleteAsync, SendAsync + URL extraction |
| P2e.2 | `HttpCallDetector` (Tree-sitter) — 6 languages | ✅ | JS/TS: fetch, axios. Python: requests, httpx. Go: http.Get/Post. Rust: reqwest. PHP: file_get_contents, curl_init. Java: URI.create, URL |
| P2e.3 | Integrated into RoslynCodeParser + all 6 Tree-sitter extractors | ✅ | HttpCalls edges with httpMethod + url metadata |
| P2e.4 | 6 new unit tests | ✅ | TS fetch/axios, Python requests, Go http.Get, Rust reqwest |

**Deliverable:** ✅ HTTP call tracing across 8 languages. HttpCalls edges with method + URL metadata.

---

## P3a — "Event / Delegate / Callback Tracking"

**Goal:** Trace pub/sub patterns: MediatR, EventEmitter, Django signals, delegate invocation
**Status:** ✅ Complete (2026-04-08)

### Tasks

| # | Task | Status | Notes |
|---|------|--------|-------|
| P3a.1 | `EventPatternExtractor` (Roslyn) | ✅ | MediatR Send/Publish → Publishes edge, IRequestHandler/INotificationHandler → HandledBy edge, delegate invocation, generic Raise/Dispatch/Emit |
| P3a.2 | `EventPatternDetector` (Tree-sitter) | ✅ | JS/TS: .on()→Subscribes, .emit()→Publishes, addEventListener. Python: signal.connect()→Subscribes, signal.send()→Publishes |
| P3a.3 | Integrated into RoslynCodeParser + TS + Python extractors | ✅ | |
| P3a.4 | 6 new unit tests | ✅ | TS on/emit/addEventListener/dedup, Python connect/send |

**Deliverable:** ✅ Event/messaging pattern detection (MediatR, EventEmitter, Django signals). Subscribes/Publishes/HandledBy edges.

---

## P4a — "Middleware Pipeline Order"

**Goal:** Extract ASP.NET middleware pipeline execution order
**Status:** ✅ Complete (2026-04-08)

| # | Task | Status | Notes |
|---|------|--------|-------|
| P4a.1 | `MiddlewarePipelineAnalyzer` (Roslyn) | ✅ | Scans app.UseXxx() calls, extracts order + creates MiddlewareInfo symbols |
| P4a.2 | `PipelineOrder` edges (middleware[n] → middleware[n+1]) | ✅ | Preserves execution order |
| P4a.3 | `GetMiddlewarePipeline` MCP tool | ✅ | 24th MCP tool |
| P4a.4 | 3 new unit tests | ✅ | Order extraction, pipeline edges, empty case |

---

## P4b — "API Contract Mapping"

**Goal:** Link API endpoints to their request/response DTOs
**Status:** ✅ Complete (2026-04-08)

| # | Task | Status | Notes |
|---|------|--------|-------|
| P4b.1 | `ApiContractAnalyzer` (Roslyn) | ✅ | Analyzes controller action params → AcceptsDto, return type → ReturnsDto |
| P4b.2 | Unwraps Task\<T\>, ActionResult\<T\>, IResult | ✅ | Gets actual DTO type |
| P4b.3 | Skips primitives, DI services, HttpContext | ✅ | Only complex DTOs |
| P4b.4 | 3 new unit tests | ✅ | Request DTO, response DTO, skip primitives |

---

## P4c — "Circular Dependency Detection"

**Goal:** Detect circular dependencies in class dependency graph
**Status:** ✅ Complete (2026-04-08)

| # | Task | Status | Notes |
|---|------|--------|-------|
| P4c.1 | `QueryCircularDependenciesAsync` in AgeGraphStore | ✅ | DFS cycle detection on DependsOn edges |
| P4c.2 | `GetCircularDependencies` MCP tool | ✅ | 25th MCP tool |

**Deliverable:** ✅ Middleware pipeline, API contracts, circular dependency detection. 26 MCP tools, 183 tests.

---

## Tier B — Multi-Language Framework Intelligence (ADR-016)

**Goal:** Make the framework-level tools (API endpoints, DI, dependency audit) work for non-.NET
stacks — not just C#/.NET. Lever: the graph tools query by **node-kind label**, so emitting the same
`api_endpoint` / `di_registration` nodes (identical `API:<METHOD>:<route>` / `DI:<svc>-><impl>` FQN +
`HandledBy`) from the tree-sitter path lights up the existing tools with **zero new tool surface**.
**Status:** ✅ C1–C4 shipped + deployed + live-verified (2026-06-21).
**Reference:** [ADR-016](decisions/016-multi-language-framework-intelligence.md).

### Phases

| # | Phase | Status | PR | Notes |
|---|-------|--------|----|-------|
| C1 | Dependency audit — `PackageManifestAnalyzer` + `get_dependency_audit` | ✅ | #13 | npm/pip/go/cargo/composer/maven/.NET |
| C2 | API endpoints — Python (FastAPI/Flask) + TypeScript (NestJS/Express) | ✅ | #14, #21 | `api_endpoint` nodes + `HandledBy` |
| C3 | DI registrations — Java Spring + NestJS/Angular `@Injectable` | ✅ | #15 | `di_registration` nodes |
| C4 | Discoverability — ServerInstructions + GetHelp + tool descriptions | ✅ | #20 | drop stale ".NET-only" |
| — | Go/Java/Django endpoints + Spring `@Bean` DI | ⏳ Backlog | — | the multi-framework tail |
| — | `get_entity_mapping` / `get_middleware_pipeline` for non-.NET | ⏸ Deferred | — | high per-stack variance (ADR-016) |

### Infra fixes (surfaced during live verification)

| Item | Status | PR |
|------|--------|----|
| Repo-splitting — watcher fragmented a multi-package project into one repo per directory | ✅ | #16 |
| `delete_repository` MCP tool — relational cascade + AGE `DETACH DELETE` | ✅ | #17 |
| Empty-path phantom guard — don't register a repo for a missing/empty path | ✅ | #18 |
| Workspace-root phantom guard — watcher must not auto-index the mount root | ✅ | #19 |

### Live verification (on the LXC deployment, via host-clone + `index_from_local`)
- `get_api_endpoints`: FastAPI-realworld (10 routes), NestJS starter (`@Get`), Express (`app.get`/`router.post`)
- `get_di_registrations`: NestJS `@Injectable`
- `get_dependency_audit` / `delete_repository`: verified via raw JSON-RPC (new tools need a fresh MCP session)

**Deliverable:** ✅ Framework tools now span stacks — `get_api_endpoints` (ASP.NET/FastAPI/Flask/NestJS/
Express), `get_di_registrations` (ASP.NET/Spring/NestJS), `get_dependency_audit` (7 ecosystems),
`get_config_usage` (8 languages) — plus the `delete_repository` tool and watcher/phantom hardening.

---

## Deployment Info

Default endpoints when running locally with `docker compose up -d`:

| Component | URL | Status |
|-----------|-----|--------|
| PostgreSQL (AGE 1.7 + pgvector 0.8.2) | `localhost:5432` | inside `cortexplexus-postgres` container |
| CortexPlexus App (Web UI + MCP + REST API) | `http://localhost:8080` | inside `cortexplexus-app` container |
| Docker image | `aspnet:10.0-noble-chiseled` | 906MB (slim) |
| Web Graph Explorer | `http://localhost:8080/` | served by App |
| MCP Endpoint | `http://localhost:8080/mcp` | JSON-RPC over HTTP |
| REST API | `http://localhost:8080/api/*` | 13 endpoints |
| Agent binary | `http://localhost:8080/api/agent/download` | win-x64, linux-x64 |

### Stats (2026-04-08)

| Metric | Value |
|--------|-------|
| MCP Tools | 25 |
| Languages | 8 + Markdown |
| Unit Tests | 183 (Parsing 139, Search 35, Core 9) |
| Test Frameworks | 8 (xUnit, NUnit, pytest, Jest, JUnit, Go test, Rust #[test], PHPUnit) |
| REST Endpoints | 13 |
| Docker Image | 906MB (aspnet-chiseled) |
| Transfer Size | 113MB (gzipped) |
| CortexPlexus self-index | 2070 symbols, 6525 relationships |

---

## Future Backlog (Uncommitted)

### P5 — Test Coverage & Quality Assurance (Next priority)

**Goal:** Tăng test coverage từ ~35% lên ~85% — bổ sung 120 tests cho data layer, MCP tools, API, agent, E2E.
**Reference:** [TEST-PLAN.md](TEST-PLAN.md) — Chi tiết 125 tests, ưu tiên P0-P3, roadmap 5 sprints.

| Sprint | Focus | Tests | Priority |
|--------|-------|:-----:|:--------:|
| 1 | Graph Store (AGE) + MCP Search/Graph Traversal | 26 | P0 |
| 2 | MCP DotNet/Explore/Index + Repository Store + REST API | 32 | P0+P1 |
| 3 | Embedding Services + Store supplements | 21 | P1 |
| 4 | Agent Components + Indexing Pipeline | 21 | P2 |
| 5 | E2E Workflows + Security + AI Agent UX | 20 | P2+P3 |

**Key gaps identified (0 tests currently):**
- AgeGraphStore — Core data layer, Cypher injection risk
- All 26 MCP tool handlers — AI Agent interface
- REST API endpoints — Security surface
- Gemini/Ollama embedding services — External dependencies
- RepositoryStore — Incremental indexing correctness
- Agent components (FileWatcher, LocalIndexer, Updater)

---

### Completed Capabilities (for reference)
> Items below were previously in backlog but are now **done**:
> - ✅ Documentation extraction (P0a) — XML doc, JSDoc, docstrings for 8 languages
> - ✅ AI-generated method summaries (P0b) — Ollama, ISummaryGenerator, cached in DB
> - ✅ Test-to-code mapping (P1a) — TestCovers edges, 8 test frameworks
> - ✅ Configuration mapping (P1b) — ReadsConfig edges, appsettings.json/.env/docker-compose.yml, 8 languages
> - ✅ Fields/Events extraction (P2a) — FieldInfo, EventInfo, Subscribes/Publishes edges
> - ✅ Exception flow (P2b) — Throws + Catches edges
> - ✅ Code metrics (P2c) — Cyclomatic complexity, nesting depth, line count
> - ✅ Dead code detection (P2d) — GetDeadCode MCP tool, graph-based caller analysis
> - ✅ HTTP call tracing (P2e) — HttpCallExtractor + HttpCallDetector, 8 languages
> - ✅ Event/messaging patterns (P3a) — MediatR, EventEmitter, Django signals
> - ✅ Middleware pipeline order (P4a) — app.UseXxx() extraction + GetMiddlewarePipeline tool
> - ✅ API contract mapping (P4b) — AcceptsDto/ReturnsDto edges
> - ✅ Circular dependency detection (P4c) — DFS cycle detection + GetCircularDependencies tool

### P2 — Code Intelligence: Deeper Analysis (Next up)

| Priority | Feature | Effort | Value |
|----------|---------|--------|-------|
| ~~P2a~~ | ~~Fields/Events extraction~~ | ~~Low~~ | ✅ **Done** |
| ~~P2b~~ | ~~Exception / Error flow~~ | ~~Medium~~ | ✅ **Done** |
| ~~P2c~~ | ~~Code metrics~~ | ~~Low~~ | ✅ **Done** |
| ~~P2d~~ | ~~Dead code detection~~ | ~~Low~~ | ✅ **Done** |
| ~~P2e~~ | ~~Cross-service HTTP call tracing~~ | ~~Medium~~ | ✅ **Done** |

### P3 — Advanced Analysis

| Priority | Feature | Effort | Value |
|----------|---------|--------|-------|
| ~~P3a~~ | ~~Event/delegate/callback tracking~~ | ~~High~~ | ✅ **Done** |
| **P3b** | Intra-method data flow — variable → transform → output tracking | Very High | Deep data tracing |
| **P3c** | Cross-file constant / magic string tracking — hardcoded URLs, SQL table names | Medium | Rename impact analysis |
| **P3d** | SQL query analysis — from EF Core generated SQL | High | DB query optimization |

### Phase 11 — Multi-User & Access Control
- Role-based filtering: user/operator/admin/owner
- API key authentication for MCP + REST endpoints
- `min_role` column on code_symbols + pre-filtering

### Phase 12 — Knowledge Expansion
- Export auto-generated architecture docs (knowledge graph → Markdown)
- CLI: `cortexplexus export /output/path`
- Cross-project reference tracking

### Other Backlog
- Community detection (Louvain/Leiden algorithm)
- Docker Hub image publish (when GitHub repo created)
- WPF/MVVM analyzer
- Temporal graph (code state at any point in time)
- Code review suggestions based on graph patterns
- Graph explorer enhancements: hierarchical layouts, edge labels, namespace grouping
- DiContainerAnalyzer: factory patterns (AddScoped with lambda)
- Deep analysis for Java — Spring DI `@Component/@Service` ✅ (Tier B C3); endpoints `@GetMapping` + JPA entities ⏳
- Deep analysis for Go — gin/echo/net-http routes ⏳ (Tier B tail); GORM models ⏳
- Deep analysis for Rust (actix-web routes, diesel models) ⏳
- Deep analysis for PHP (Laravel DI, Eloquent models) ⏳
- Python Django routes (`urls.py` `path()`/`re_path`) ⏳ (Tier B tail)
