# ADR-016: Multi-language framework intelligence (Tier B)

**Status:** Proposed
**Date:** 2026-06-19

## Context

Five tools are still effectively .NET-only because their data is produced exclusively by the
Roslyn analyzers:

| Tool | Source today | Non-.NET result |
|---|---|---|
| `get_api_endpoints` | Roslyn → `api_endpoint` nodes | empty |
| `get_di_registrations` | Roslyn → `di_registration` nodes | empty |
| `get_entity_mapping` | Roslyn → `dbcontext`/EF nodes | empty |
| `get_middleware_pipeline` | Roslyn → `middleware` nodes | empty |
| `get_nuget_audit` | `NuGetAuditAnalyzer` (.csproj) | empty |

The earlier work (ADR-014, GH #6, the dead-code rounds) made the *language-agnostic* tools
correct for Python/TS. Tier B is the remaining gap: bring framework-level intelligence to the
non-.NET stacks.

### The architectural lever

The graph tools query by **node-kind label**, not by language:

```cypher
get_api_endpoints      → MATCH (ep:api_endpoint)   RETURN ...
get_di_registrations   → MATCH (reg:di_registration) RETURN ...
```

So if the tree-sitter extractors **emit `api_endpoint` / `di_registration` nodes** (same FQN +
signature shape the Roslyn path uses, e.g. `API:POST:/users/{id}`), the **existing tools light
up for every language with zero new tool surface**. `SymbolDtoMapper` already round-trips the
`endpoint` and `di-registration` kinds, and the `ApiEndpointInfo` / `DiRegistrationInfo` models
already exist. Tier B is therefore **mostly new EXTRACTION + broadened descriptions**, not new
tools — mirroring the detector pattern already used for `ConfigAccessDetector` /
`HttpCallDetector` / `EventPatternDetector`.

`get_nuget_audit` is the exception: package manifests aren't AST, so it needs a sibling
file-parser, not a tree-sitter detector.

## Decision

Deliver framework intelligence for non-.NET stacks by:

1. **New tree-sitter detectors** that emit `api_endpoint` and `di_registration` nodes, wired
   into the existing extractors (one detector class per concern, language-dispatched like
   `ConfigAccessDetector`). The existing `get_api_endpoints` / `get_di_registrations` tools then
   return results unchanged.
2. **A multi-manifest dependency analyzer** (sibling to `NuGetAuditAnalyzer`) + a generalized
   `get_dependency_audit` tool (keep `get_nuget_audit` as a thin alias).
3. **Discoverability pass**: broaden the `[Description]` text + server instructions that say
   "ASP.NET" / "Minimal API" / "NuGet" to name the supported frameworks; widen the SessionStart
   reminder markers (already partly done — see memory `f3e42077`).

`get_entity_mapping` (ORM mapping) and `get_middleware_pipeline` are **deferred** — ORM/middleware
models vary far more per stack (SQLAlchemy/Prisma/GORM/Hibernate; Express/ASGI middleware) and
carry less cross-stack value than endpoints/DI/deps. Revisit after C1–C3 land.

### Detection matrix (framework → emitted node)

**API endpoints** (`api_endpoint`, fqn `API:<METHOD>:<route>`):

| Lang | Framework | Pattern |
|---|---|---|
| Python | FastAPI | `@app.get("/x")` / `@router.post(...)` decorators |
| Python | Flask | `@app.route("/x", methods=[...])` |
| Python | Django | `path("x/", view)` / `re_path` in `urls.py` |
| TS/JS | Express | `app.get("/x", h)` / `router.post(...)` calls |
| TS/JS | NestJS | `@Get("/x")` / `@Post()` method decorators |
| Go | Gin/Echo/net-http | `r.GET("/x", h)` / `mux.HandleFunc` calls |
| Java | Spring | `@GetMapping` / `@RequestMapping` annotations |

**DI registrations** (`di_registration`):

| Lang | Framework | Pattern |
|---|---|---|
| Java | Spring | `@Component`/`@Service`/`@Repository`/`@Bean` |
| TS | NestJS/Angular | `@Injectable()` + module `providers: [...]` |
| Python | FastAPI | `Depends(x)` provider wiring (best-effort) |

**Dependency audit** (`get_dependency_audit`):

| Ecosystem | Manifest |
|---|---|
| npm/pnpm/yarn | `package.json` |
| pip/poetry | `requirements.txt`, `pyproject.toml` |
| Go | `go.mod` |
| Rust | `Cargo.toml` |
| PHP | `composer.json` |
| Java | `pom.xml`, `build.gradle` |

## Rollout (priority by value ÷ effort) — each phase is its own PR series

- **C1 — Dependency audit** *(highest ROI, no AST, no graph)*: `PackageManifestAnalyzer` parsing
  the manifests above → `get_dependency_audit` (+`get_nuget_audit` alias). Pure file parsing,
  fully unit-testable offline.
- **C2 — API endpoints, Python + TS first**: `EndpointDetector` for FastAPI/Flask (Python) and
  Express/NestJS (TS) → `api_endpoint` nodes. Verify `get_api_endpoints(repository:"hive"/<ts>)`.
  Then extend Go/Java/Django.
- **C3 — DI registrations**: `DiDetector` for Spring + NestJS (the two with explicit DI) →
  `di_registration` nodes.
- **C4 — Discoverability**: broaden tool descriptions + `ServerInstructions` + reminder markers.

C1 ships value immediately and independently; C2 is the flagship; C3 is narrower (dynamic langs
rarely have explicit DI). C4 rides along with whichever lands first that changes a description.

## Consequences

**Positive**
- `get_api_endpoints` / `get_di_registrations` become genuinely multi-language with **no new tool
  surface** — pure extraction, reusing existing models/DTO mapping/queries.
- Reuses the proven detector pattern (`*Detector.DetectPython/DetectTypeScript/...` dispatched
  from each extractor) — consistent, individually testable, low blast radius.
- `get_dependency_audit` generalizes a capability users expect from any repo.

**Negative / risk**
- Decorator/annotation/route parsing is **heuristic per framework**; route templates and method
  lists vary (e.g. Flask `methods=[...]`, FastAPI default GET). Expect iteration via real-repo
  smoke tests, like the dead-code rounds.
- Endpoint FQN scheme (`API:<METHOD>:<route>`) must stay consistent with the Roslyn emitter so
  the shared tool output is uniform.
- Node kinds must survive the agent upload path — verify `SymbolDtoMapper` + the bulk-load edge
  path (lesson from GH #6 / Case D: confirm nodes/edges actually persist on the real graph, not
  just a synthetic fixture).
- DI for dynamic languages (FastAPI `Depends`) is genuinely fuzzy — keep it best-effort, mark
  clearly, don't over-promise.

## Alternatives considered

- **New per-language tools** (`get_fastapi_routes`, …). Rejected — explodes tool count; the
  kind-based query already unifies them under `get_api_endpoints`.
- **LLM-based endpoint extraction.** Rejected for the index path — non-deterministic, slow, costly
  at index time; tree-sitter patterns are deterministic and cheap.
- **Do entity-mapping/middleware now.** Deferred — highest per-stack variance, lowest shared
  value; not worth the surface until endpoints/DI/deps prove the pattern.

## Acceptance / verification

- C1: `get_dependency_audit` lists deps+versions for a Python (`pyproject.toml`/`requirements.txt`)
  and a JS (`package.json`) repo; unit tests per manifest format.
- C2: `get_api_endpoints(repository:"hive")` returns FastAPI/Flask routes if present (or a TS
  repo's Express/NestJS routes); fixture tests per framework + one real-repo smoke.
- C3: `get_di_registrations` returns Spring/NestJS providers on a real repo.
- C4: tool descriptions + server instructions no longer imply .NET-only for the covered tools.
- Every phase: CI green incl. AGE integration; live verification on a real indexed repo, not only
  fixtures.
