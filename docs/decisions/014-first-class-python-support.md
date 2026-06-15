# ADR-014: First-Class Python Support (tree-sitter depth, framework analyzers deferred)

**Status:** Accepted
**Date:** 2026-06-14

## Context

Tree-sitter parsing for Python already exists and is wired into the pipeline:
`TreeSitterCodeParser` + `PythonExtractor` run inside `LocalIndexer` (full + incremental),
extracting classes, functions/methods, `import`→`DependsOn`, calls→`Calls`/`TestCovers`,
inheritance→`Inherits`, docstrings, test detection, plus config/http/event detectors. So
basic `search_code` / `semantic_search` / `get_callers` already work for a Python repo.

The trigger is a concrete repo: **`hive`** (Python 3.12, 81 `.py` files, `pyproject.toml`,
raw-SQL via psycopg + alembic + pydantic + apscheduler, **no web framework**; layout
`core/{intel,engine,publisher,loop,orchestrator}` + `cells/` + `storage/migrations`). It is
the first non-.NET repo to onboard. Probing the real gaps (not assumptions) surfaced three:

1. **No Python project-unit detection** — `LocalIndexer.FindSolutionsAndProjects` only
   understands `.csproj/.sln/.slnx`. Python projects (`pyproject.toml`/`requirements.txt`/
   `setup.py`) are invisible as a project unit.
2. **Call-graph records raw callee names** — `PythonExtractor.ExtractCall` emits the literal
   text (`self.foo`, `bar`), not a resolved FQN, so `get_callers/get_callees` is noisy.
3. **hive not onboarded** — and it lives on the Dev PC, not the LXC server filesystem, so
   `index_from_local` (server-side) cannot see it; the Local Agent `index` one-shot can.

The rich .NET analyzers (`DiContainerAnalyzer`, `AspNetRouteAnalyzer`, `EfCoreAnalyzer`,
`MiddlewarePipelineAnalyzer`, `ConfigurationAnalyzer`, `NuGetAuditAnalyzer`) are Roslyn-only
and have no Python equivalent, so `get_api_endpoints` / `get_di_registrations` /
`get_entity_mapping` / nuget-audit return empty for Python.

## Decision

Make Python a first-class indexed language **within tree-sitter** (consistent with
[ADR-003](003-roslyn-over-treesitter-csharp.md): Roslyn for C#, tree-sitter for the rest).
Three changes, scoped to onboard `hive`:

1. **Onboard `hive`** via Local Agent `cortexplexus-agent index <path> --server <url> --name hive`
   (then a watch instance under the CortexBridge ADR-023 reconcile model for 24/7, never
   hard-enabled always-on). Target server: PROD LXC `:8080`, same as every other repo.
2. **Python project-unit detection** — recognise `pyproject.toml` (primary; `hive` has it),
   fallback `requirements.txt`/`setup.py`/`setup.cfg`. Must not break the existing
   path→module FQN scheme (`core/intel/x.py` → `core.intel.x`).
3. **Best-effort call-graph FQN resolution** in `PythonExtractor` — resolve `self.` → the
   containing class FQN, and `import`/`from … import` aliases → module-qualified FQNs. This
   is heuristic, not Roslyn-grade.

**Deferred (out of scope):** Python framework-aware analyzers (FastAPI/Django/Flask routes,
SQLAlchemy/Django ORM entity mapping, pip/poetry dependency audit). Correct for `hive`,
which has no web framework. The `.NET-only` MCP tools stay .NET-only for now.

## Consequences

- **Pro:** First Python repo (`hive`) becomes queryable; validates the multi-language path
  end-to-end on real code, not a fixture.
- **Pro:** Project detection + FQN resolution lift `get_callers/get_callees` quality from
  "raw-name noise" toward usable — the highest-leverage polish for the cost.
- **Pro:** Scope stays in tree-sitter; no Roslyn/MSBuild coupling for Python.
- **Con:** FQN resolution is heuristic — Python is dynamically typed, so some calls stay
  unresolved; will not match Roslyn accuracy. Needs a "good enough" rubric, not 100%.
- **Con:** tree-sitter incremental re-parses the whole directory per change — fine at 81
  files, O(repo) if Python repos grow. Flagged, not fixed here.
- **Con:** Framework-blind — `get_api_endpoints` etc. stay empty for Python until a later ADR.
- **Risk:** `files_hive/` + `cells/` may hold data/generated content; a `.cortexplexusignore`
  may be needed so indexing doesn't ingest noise.

## Acceptance

1. Baseline probe: one-shot index `hive` → `list_repositories` shows `hive` with recorded
   N symbols / M embeddings (100% of embeddable kinds).
2. `search_code`/`semantic_search` scoped `repository:"hive"` return real `core/` symbols.
3. `get_callers/get_callees` on a known hive function return resolved callers (measured
   before/after the resolution change on a hand-picked call site).
4. Project-detection unit test: a `pyproject.toml` repo is recognised as a project unit;
   hive indexes without treating every `.py` as an orphan.
5. New unit tests (resolution + detection) GREEN; full suite GREEN.
6. `BENCHMARK.md` gains a hive-onboarding round.

## Related

- [ADR-003](003-roslyn-over-treesitter-csharp.md) — Roslyn for C#, tree-sitter for the rest
- CortexBridge ADR-023 — on-demand watch reconcile (per-project, session-gated)
