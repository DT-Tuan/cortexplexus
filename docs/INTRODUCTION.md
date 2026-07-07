# CortexPlexus — Turn your source code into a Knowledge Graph for AI

**Open-source Code Intelligence Platform. 100% self-hosted, free forever. Works with C#, TypeScript, JavaScript, Python, Java, Go, Rust, PHP.**

AI coding assistants (Claude, Cursor, Copilot) read your codebase as plain text — they don't understand the **structure** of your code. The result: agents `grep` and `read` dozens of files to infer class/method relationships, wasting tokens, missing context, giving confidently wrong answers.

**CortexPlexus fixes that — for every major language.** It parses your code with Tree-sitter (TypeScript, JavaScript, Python, Java, Go, Rust, PHP) plus a deep semantic tier for C# via Roslyn, builds a Knowledge Graph (classes/functions, call graph, API routes, DI wiring, test coverage, config usage, dependencies…), and serves it to any AI agent over the **Model Context Protocol** — **1 tool call instead of 10+ grep/read operations**.

---

## Core value — measurable, not marketing fluff

| Before CortexPlexus | With CortexPlexus | Benefit |
|---|---|---|
| Grep function name → read 5-10 files → manually assemble | `GetCallers("app.services.billing.charge")` — Python, TS, C#, Go… any language's FQN | **1 call replaces 10+** |
| Understand a service: read class + tests + deps (15+ files) | `ExploreTopic("PaymentService")` | **1 call replaces 15+** |
| Trace an API request: entrypoint → handler → downstream | `GetDataFlow("/api/orders")` | **1 call replaces 8+** |
| Impact analysis for refactor: grep → read callers → read caller-of-callers | `GetImpactAnalysis(fqn, depth: 3)` | **1 call replaces 10+** |
| Onboard new project: read 20+ files manually | `OnboardProject(repo)` | **1 call replaces 20+** |

**Real-world impact for AI agents:**
- 80-90% reduction in token/inference cost
- Accurate answers — agent works from structured context, not guesses based on snippets
- New-project onboarding in under 30 seconds
- **Cross-project memory**: a lesson learned by the agent in project A is recallable by the agent working in project B

---

## 34 MCP tools — covering five real needs

**Search & navigation (all 8 languages)** — `search_code` (hybrid full-text + vector), `semantic_search` (natural-language), `get_callers` / `get_callees`, `get_implementations`, `get_class_hierarchy` (directional, no sibling bleeding), `get_dependencies`, `get_impact_analysis`.

**Framework intelligence (multi-stack)** — `get_api_endpoints` (ASP.NET, FastAPI, Flask, NestJS, Express), `get_di_registrations` (ASP.NET, Spring, NestJS), `get_dependency_audit` (npm / pip / go / cargo / composer / maven / nuget), `get_config_usage` (`.env`, `appsettings.json`, `process.env`, `os.environ`, … across 8 languages), `get_architecture`.

**.NET deep tier (Roslyn)** — `get_entity_mapping` (DbContext → entity), `get_data_flow` (endpoint → handler → DB), `get_middleware_pipeline` (ASP.NET execution order). These three are C#-only; everything else above is multi-language.

**Quality & observability** — `get_test_coverage` (8 frameworks: xUnit, NUnit, pytest, Jest, JUnit, Go test, Rust `#[test]`, PHPUnit), `get_dead_code` (filters HTTP endpoints, event subscribers, test methods), `get_circular_dependencies` (DFS on `DependsOn` graph).

**Composite & memory** — `explore_topic` / `onboard_project` (one call replaces 5-20), `get_help` (self-documenting), and an opt-in **agent memory suite** (`save_memory` / `recall_memory` / `list_memories` / `forget_memory`) — a shared, decay-ranked knowledge store spanning every indexed repo.

---

## Measured performance on real projects

**R18 — HNSW bulk-load (benchmarked on pgvector + pg17):**
> Vector indexing phase: **51 minutes → 5.5 seconds (~556× speedup)** for batches ≥500 symbols.
> Strategy: drop HNSW → bulk INSERT → rebuild HNSW, instead of paying per-row HNSW maintenance.

**Scale (CortexFlow — real full-stack .NET solution, production fleet):**
> 23,000+ symbols / 15,700+ embeddings indexed and kept live-synced by the watch agent.
> Embedding providers: **Ollama** (all-local default), **Gemini** (free tier), **Vertex AI** (measured 26.4 texts/s — ADR-017).

**Search quality (hybrid fusion):**
> Apache AGE Cypher (graph) + pgvector HNSW (vector, ef_search=100 for ~99% recall) + tsvector BM25 (full-text), fused via Reciprocal Rank Fusion, with HyDE + multi-query expansion.

**Incremental indexing:**
> SHA-256 content hash + file watcher → only re-index files that changed. Edit-to-reindex loop < 1 second per file.

---

## Six practical use cases

1. **Onboard a new project** — agent has a full architectural map on first connect, no need to "learn" by reading README + wandering files. Works the same on a React PWA, a FastAPI service, or an enterprise .NET solution.
2. **Debug a complex bug** — `get_data_flow("/api/failing-endpoint")` returns the full handler → service → repository → DB chain, pinpointing the stage that needs a breakpoint.
3. **Pre-merge impact analysis** — `get_impact_analysis(method, depth: 3)` lists exactly how many callers will break if a signature changes.
4. **Test-coverage audit** — find tests for any production method across 8 test frameworks; surface untested hot paths in CI.
5. **Codebase cleanup** — `get_dead_code` + `get_circular_dependencies` surface removable zones in one call; replaces expensive standalone tools (NDepend, SonarQube).
6. **Cross-project knowledge** — the memory suite lets your agent in project B recall the workaround your agent discovered in project A last month, instead of re-deriving it.

---

## Why you can trust it

| Criteria | CortexPlexus |
|---|---|
| **Tests** | 800+ passing (unit + integration + performance), ~85% coverage |
| **Languages** | 8 — TypeScript, JavaScript, Python, Java, Go, Rust, PHP via Tree-sitter; C# deep tier via Roslyn |
| **Deployment** | 2 Docker containers, < 2 GB RAM, < 2 GB disk |
| **License** | MIT (commercial use, fork, rebrand — all free) |
| **External dependencies** | Zero required (Ollama offline is the default; Gemini / Vertex AI are optional) |
| **DB stack** | 1 PostgreSQL 17 + AGE + pgvector + tsvector — no Redis, RabbitMQ, Elasticsearch |

---

## Quick comparison with alternatives

| | GitHub Copilot | Cursor search | Sourcegraph | **CortexPlexus** |
|---|:---:|:---:|:---:|:---:|
| Open source | No | No | Partial | **Yes (MIT)** |
| Self-hosted | No | No | Yes (Enterprise) | **Yes (free)** |
| Multi-language code graph | No | No | Yes | **Yes (8 languages)** |
| Roslyn deep C# | No | No | No | **Yes** |
| Cross-project agent memory | No | No | No | **Yes** |
| MCP native | No | Partial | No | **Yes (34 tools)** |
| Annual cost / 20 devs | ~$5,000 | ~$5,000 | $10,000+ | **$0** |

---

## Get started in 3 commands

```bash
git clone https://github.com/DT-Tuan/CortexPlexus.git
cd cortexplexus
docker compose up -d
```

Drop a `.mcp.json` at your project root pointing at `http://localhost:8080/mcp`, restart your IDE, and your agent gets 34 code-intelligence tools. Your source code stays on your machine — the Local Agent only uploads metadata.

---

## Who it's for

- **Polyglot teams and solo devs** — one self-hosted server indexes your React frontend, Python services, and Go tooling into a single queryable graph your AI agent actually uses.
- **C# / .NET teams** that want the deepest tier: DI containers, EF Core, middleware stacks understood semantically, without paying a SaaS subscription.
- **Tech leads** who need CI-integrated impact analysis / dead code / test-coverage audits without buying $10K/year tools.
- **Researchers and OSS contributors** looking for a platform to experiment with RAG-on-code, Knowledge Graphs, and hybrid search.

---

**Repo**: https://github.com/DT-Tuan/CortexPlexus · **License**: MIT · **Stack**: .NET 10 + PostgreSQL 17 (AGE + pgvector + tsvector) + Roslyn + Tree-sitter + Ollama/Gemini/Vertex embeddings + ModelContextProtocol SDK.

> Also available in Vietnamese: [INTRODUCTION-VI.md](./INTRODUCTION-VI.md).
