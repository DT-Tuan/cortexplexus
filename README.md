# CortexPlexus

**Open-source Code Intelligence Platform** — turn your source code into a Knowledge Graph and serve structured context to AI assistants over the [Model Context Protocol](https://modelcontextprotocol.io/).

**Works with C#, TypeScript, JavaScript, Python, Java, Go, Rust, PHP.** Pull, `docker compose up`, connect your IDE, done. **100% free and self-hosted.**

[![Build & Test](https://github.com/DT-Tuan/CortexPlexus/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/DT-Tuan/CortexPlexus/actions/workflows/build-and-test.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-800%2B%20passing-brightgreen)](docs/TESTING.md)
[![MCP](https://img.shields.io/badge/MCP-34%20tools-blue)](https://modelcontextprotocol.io/)

---

## Why CortexPlexus?

AI coding assistants (Claude, Cursor, Copilot) read your files as plain text — they don't understand the structure of your code. CortexPlexus fixes that by:

1. **Parsing** source with Tree-sitter (TypeScript / JavaScript / Python / Java / Go / Rust / PHP) + a deep semantic tier for C# via Roslyn
2. **Building** a Knowledge Graph (classes, methods, call graph, DI registrations, API routes, EF Core entities, config keys, test coverage…)
3. **Searching** with hybrid Graph + Vector + BM25 fusion
4. **Serving** structured context to AI agents over MCP — one tool call instead of 10+ grep/read operations

**No other open-source tool combines** Roslyn-level C# semantic analysis with multi-language Tree-sitter inside a unified Knowledge Graph.

> **Want the full story?** See [docs/INTRODUCTION.md](docs/INTRODUCTION.md) (English) or [docs/INTRODUCTION-VI.md](docs/INTRODUCTION-VI.md) (Vietnamese) — real benchmark numbers, six concrete use cases, comparison vs Copilot / Cursor / Sourcegraph. For a talk-ready pitch: [docs/PITCH-DECK.md](docs/PITCH-DECK.md).

---

## Quick Start

```bash
# 1. Clone
git clone https://github.com/DT-Tuan/CortexPlexus.git
cd cortexplexus

# 2. (Optional) Configure embedding provider
cp .env.example .env
# Default = Ollama (offline). Set GEMINI_API_KEY in .env to use Gemini instead.

# 3. Start (2 containers: PostgreSQL + App)
docker compose up -d

# 4. Verify it's running
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/mcp
# 400 or 405 = OK (MCP rejects GETs — POST a JSON-RPC request to use it)

# 5. Connect your IDE (see below)
```

> **Running server and IDE on different machines?** Replace `localhost` with the server hostname or IP (e.g. `http://192.168.1.10:8080/mcp`) in every command above and in the IDE config below.

### Index your first project

You have three options:

**A. Local Agent (recommended — source never leaves your machine):**
```
ActivateAgent(projectPath: "/path/to/your/project")
```
Run from any AI client connected to CortexPlexus. The agent downloads from the server, parses locally, and uploads only metadata.

**B. Index code already on the server:**
```bash
docker exec cortexplexus-app dotnet CortexPlexus.App.dll index /workspace/your-project
```

**C. Index a Git URL:**
```
IndexFromGit(url: "https://github.com/org/repo.git", name: "myrepo")
```

### Connect your IDE

**Claude Code** — copy the template, then edit the URL if your server is not on `localhost`:
```bash
cp .mcp.json.example .mcp.json
```
`.mcp.json.example` contents (already points to `localhost:8080` — change the host if your CortexPlexus server runs elsewhere):
```json
{
  "mcpServers": {
    "cortexplexus": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```
> `.mcp.json` is git-ignored so your local URL/auth tweaks won't leak into the repo. If you're hacking on CortexPlexus itself, see [docs/MCP-GUIDE.md](docs/MCP-GUIDE.md#developing-on-cortexplexus-itself).

**Cursor** — `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

**VS Code** — `.vscode/mcp.json` (note: key is `"servers"`, not `"mcpServers"`):
```json
{
  "servers": {
    "cortexplexus": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

**Google Antigravity** — `~/.gemini/antigravity/mcp_config.json` (Windows: `C:\Users\<USERNAME>\.gemini\antigravity\mcp_config.json`). Antigravity uses `serverUrl`, **not** `url` — do not copy the Claude Code / Cursor schema here:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "serverUrl": "http://localhost:8080/mcp"
    }
  }
}
```
You can also open this file from inside Antigravity: Agent panel `...` → **MCP Servers** → **Manage MCP Servers** → **View raw config**. Close and reopen Antigravity after editing — it does not hot-reload.

**Windsurf / stdio-only clients** — bridge with [mcp-remote](https://www.npmjs.com/package/mcp-remote):
```json
{
  "mcpServers": {
    "cortexplexus": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:8080/mcp"]
    }
  }
}
```

After creating the file, **restart your IDE session** — no client hot-reloads MCP config.

> **Full MCP guide with all 34 tools, usage examples, and troubleshooting:**
> **[`docs/MCP-GUIDE.md`](docs/MCP-GUIDE.md)** — read this after connecting your IDE.

---

## Features

- **34 MCP tools** — search & navigation (all languages), framework intelligence (endpoints/DI/dependency-audit across stacks), a .NET deep tier, data flow, test coverage, dead code, circular deps, composite (`OnboardProject`, `ExploreTopic`), and opt-in cross-project agent memory (`SaveMemory`, `RecallMemory`, `ListMemories`, `ForgetMemory`)
- **Multi-language** — TypeScript / JavaScript / Python / Java / Go / Rust / PHP / Markdown via Tree-sitter + C# deep semantic via Roslyn
- **Framework intelligence across stacks** — API endpoints (ASP.NET, FastAPI, Flask, NestJS, Express), DI registrations (ASP.NET, Spring, NestJS), dependency audit (npm / pip / go / cargo / composer / maven / nuget), config usage (8 languages)
- **.NET deep tier** — EF Core entity mappings, Minimal API routes (with `[controller]` token expansion), middleware pipeline order, endpoint→DB data flow
- **Triple hybrid search** — Apache AGE Cypher (graph) + pgvector HNSW (vector) + tsvector BM25 (full-text) + RRF fusion
- **Context tracking edges** — `Calls`, `Implements`, `Inherits`, `DependsOn`, `UsesType`, `HandledBy`, `MapsTo`, `ReadsConfig`, `Throws`, `Catches`, `TestCovers`, `Subscribes`, `Publishes`, `HttpCalls`, `PipelineOrder`
- **Local indexing agent** — source code stays on your dev machine; only metadata is sent to the server
- **Incremental indexing** — SHA-256 file hashing, file watcher, re-index only changed files
- **HNSW bulk-load optimization** — drop/recreate index for fast initial indexing, ~250–1000× faster vector phase on large projects
- **Embedding** — Google Gemini API (free tier) or Ollama (offline, default)
- **Self-hosted** — 2 Docker containers, zero cloud dependency, zero cost
- **Agent memory (v0.8.0, opt-in)** — semantic, scoped (session/project/global), auto-decaying store backed by pgvector HNSW. Opt-in via `Memory__Enabled=true`; see [docs/MEMORY-SYSTEM.md](docs/MEMORY-SYSTEM.md)

## Tech Stack

| Layer | Technology | License |
|-------|-----------|---------|
| Runtime | .NET 10 | MIT |
| Database | PostgreSQL 17 + Apache AGE 1.7 + pgvector 0.8 | PostgreSQL / Apache 2.0 |
| Code parser (C#) | Roslyn (Microsoft.CodeAnalysis) | MIT |
| Code parser (TS / JS / Py / Java / Go / Rust / PHP) | TreeSitter.DotNet | MIT |
| Embedding | Google Gemini (free) / Ollama (offline) | Free / MIT |
| MCP SDK | ModelContextProtocol .NET SDK | MIT |
| Search | Apache AGE Cypher + pgvector HNSW + tsvector BM25 | — |

## MCP Tools (30)

### Search & navigation
- `search_code` — hybrid full-text + vector search with optional repo scope
- `semantic_search` — natural-language semantic search via embeddings
- `get_callers` / `get_callees` — call graph traversal with framework noise filter
- `get_implementations` — find all classes implementing an interface
- `get_class_hierarchy` — directional inheritance/implements traversal (no sibling bleeding)
- `get_dependencies` — what this class/method depends on
- `get_impact_analysis` — blast radius: what breaks if this changes?

### .NET deep analysis
- `get_di_registrations` — DI container: service → implementation
- `get_entity_mapping` — EF Core: DbContext → entity
- `get_api_endpoints` — API routes (with `moduleName` filter)
- `get_data_flow` — endpoint → handler → downstream methods
- `get_middleware_pipeline` — ASP.NET middleware execution order
- `get_nuget_audit` — NuGet packages and versions per project
- `get_architecture` — repository overview

### Quality / observability
- `get_test_coverage` — find tests covering a production method (8 frameworks: xUnit, NUnit, pytest, Jest, JUnit, Go, Rust, PHPUnit)
- `get_config_usage` — find code that reads a config key (`appsettings.json`, `.env`, `IConfiguration`, `IOptions<T>`, env-var APIs in 8 languages)
- `get_dead_code` — public/internal methods with no callers (excludes HTTP endpoints, event subscribers, test methods)
- `get_circular_dependencies` — DFS cycle detection on `DependsOn` graph

### Composite
- `explore_topic` — multi-step exploration (search + callers + deps + implementations) in 1 call
- `onboard_project` — full project overview in 1 call

### Indexing / agent / help
- `activate_agent` — install + run the local indexing agent (includes VS Code auto-start recipe for AI)
- `index_from_local` / `index_from_git` — server-side indexing
- `list_repositories` — indexed repos + staleness warnings (`⚠️ STALE` when >24h old, `🚨 VERY STALE` when >7d)
- `get_help` — usage guide (topics: quick-start, tools, indexing, strategies, memory)

### Agent memory (opt-in, v0.8.0)
Requires `Memory__Enabled=true`. Semantic, scoped, Weibull-decayed store. See [docs/MEMORY-SYSTEM.md](docs/MEMORY-SYSTEM.md).
- `save_memory` — store a preference / pattern / decision / bug note with per-topic decay
- `recall_memory` — semantic retrieval scoped to session / project / global
- `list_memories` — audit + management (no embedding cost)
- `forget_memory` — delete by id

All three project-scoped memory tools accept either `repository` NAME or `scopeId` UUID — repository name is resolved server-side and is the recommended form for AI agents.

## Architecture

```
┌────────────────────────────────────────┐
│      IDE / AI Agent                     │
│  Claude Code · Cursor · VS Code · …     │
│            MCP (HTTP)                   │
└────────────────┬────────────────────────┘
                 │
┌────────────────┴────────────────────────┐
│   CortexPlexus.App (.NET 10 monolith)   │
│                                          │
│  • MCP Server (34 tools)                 │
│  • REST API (10 endpoints)               │
│  • Roslyn parser (C# deep)               │
│  • Tree-sitter parsers (8 languages)     │
│  • Hybrid search (Graph + Vector + BM25) │
│  • Gemini / Ollama embedding             │
│  • File watcher + incremental indexing   │
└────────────────┬────────────────────────┘
                 │ Npgsql
┌────────────────┴────────────────────────┐
│   PostgreSQL                             │
│  Apache AGE (graph) + pgvector (vector)  │
│  + tsvector (BM25 full-text)             │
└─────────────────────────────────────────┘
```

## Project Structure

```
CortexPlexus/
├── src/
│   ├── CortexPlexus.Core/         # Domain models + abstractions (zero deps)
│   ├── CortexPlexus.Parsing/      # Roslyn + Tree-sitter parsers
│   ├── CortexPlexus.Graph/        # PostgreSQL + AGE + pgvector adapters
│   ├── CortexPlexus.Search/       # Hybrid search router + RRF fusion
│   ├── CortexPlexus.Embedding/    # Gemini + Ollama providers
│   ├── CortexPlexus.Agent/        # Local indexing agent CLI
│   └── CortexPlexus.App/          # Monolith entry: MCP server, REST API, CLI
├── tests/                          # 800+ tests across 12 projects
├── docs/
│   ├── ARCHITECTURE.md            # System architecture
│   ├── MCP-GUIDE.md               # AI agent connection guide
│   ├── BENCHMARK.md               # Round-by-round bench history
│   └── runbooks/                  # Setup + operations
├── docker-compose.yml             # 2-container deployment
└── .env.example                   # Config template
```

## Requirements

- **Docker + Docker Compose** (recommended), or .NET 10 SDK + PostgreSQL with AGE & pgvector for native runs
- **RAM**: ~150 MB idle, ~500 MB during a typical indexing run. Rule of thumb:
  - **1 GB** is enough for a small repo (under 2K symbols) and idle usage
  - **2 GB** recommended for everyday use (comfortable for indexing 10K-symbol projects with HNSW bulk-load)
  - **4 GB** if you index very large repos (20K+ symbols) or run an embedding model locally alongside
- **Disk**: ~1 GB for the two Docker images + growing storage for indexed metadata (roughly 1-2 MB per 1K symbols). Plan 2-5 GB.
- Optional: [Ollama](https://ollama.com/) for offline embedding (default) — budget another ~2-3 GB RAM if running it on the same host
- Optional: [Google Gemini API key](https://aistudio.google.com/apikey) (free tier) for cloud embedding — zero local cost

## Documentation

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — system architecture with diagrams
- [`docs/MCP-GUIDE.md`](docs/MCP-GUIDE.md) — connect AI clients to the MCP server
- [`docs/runbooks/development-setup.md`](docs/runbooks/development-setup.md) — local dev setup
- [`docs/runbooks/deployment.md`](docs/runbooks/deployment.md) — production deployment
- [`docs/BENCHMARK.md`](docs/BENCHMARK.md) — performance benchmarks and historical rounds
- [`CHANGELOG.md`](CHANGELOG.md) — version history and release notes
- [`docs/HEALTH-METRICS.md`](docs/HEALTH-METRICS.md) — what `Health: OK / PARTIAL / DEGRADED` means and how to interpret it
- [`docs/runbooks/agent-best-practices.md`](docs/runbooks/agent-best-practices.md) — single-`.sln` indexing, watch mode, throughput tuning
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — how to contribute
- [`SECURITY.md`](SECURITY.md) — how to report vulnerabilities

## Contributing

Contributions are welcome! Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) and our [Code of Conduct](CODE_OF_CONDUCT.md) before opening a PR.

## License

MIT — see [LICENSE](LICENSE).
