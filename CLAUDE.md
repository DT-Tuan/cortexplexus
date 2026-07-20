# CortexPlexus — AI Agent Context

_.NET project. Mở rộng context khi cần — phần dưới là directive dùng code-intelligence._

## Code intelligence — cortexplexus MCP

> **Cortexplexus repository for this repo**: `CortexPlexus` — pass as `repository:` parameter to scoped MCP calls (`search_code` / `get_callers` / `recall_memory(scope:"project")` / `save_memory(scope:"project")`). Workspace folder name may differ from cortexplexus name.

**Active orient + remind:** type `/cortex-mcp` for a one-shot health-check + cheat sheet + cross-project `recall_memory`. The global SessionStart hook also prints a 1-line reminder in any .NET repo where cortexplexus is wired (see `~/.claude/skills/cortex-mcp/`). Treat the rules below as policy from here.

This is a .NET codebase indexed by the **cortexplexus** MCP server (code graph + .NET-aware queries + semantic search). For STRUCTURAL questions about this repo, prefer its tools over manual grep/read:

- "who calls X / what breaks if I change X" → `get_callers`, `get_impact_analysis` (run BEFORE editing a shared symbol)
- "where is this endpoint / DI reg / EF entity / config key / middleware order" → `get_api_endpoints`, `get_di_registrations`, `get_entity_mapping`, `get_config_usage`, `get_middleware_pipeline`
- "find code about <concept>" → `semantic_search`; exact name → `search_code`
- onboarding / architecture / dead code / cycles → `onboard_project`, `get_architecture`, `get_dead_code`, `get_circular_dependencies`

If unsure, call `get_help` once per session. If a query tool returns nothing, the repo isn't indexed yet — run `index_from_local` once, then retry.

**Guard (don't over-call):** for a small edit to a file you already have open or whose path you know, plain Read/grep is fine — don't round-trip the MCP for trivial lookups. Use cortexplexus when the question is about relationships/impact/whereabouts across the codebase.

### Memory (cross-project learning) — models forget this; don't

cortexplexus also hosts a **shared memory store** (`recall_memory` / `save_memory` / `list_memories`) that spans EVERY indexed .NET repo. The query tools above are useless for this — these are separate, and models routinely skip them. Two habits, both required:

- **Recall first.** At session start, or before re-investigating a non-trivial problem, call `recall_memory(query: <what you're about to do>, scope: "all")` — another repo may have already solved it. Read the hits before grepping.
- **Save transferable lessons.** The moment you distill a durable lesson, hit a non-obvious bug/workaround, or make a decision **another .NET repo could reuse**, call `save_memory` (scope `project` = this repo; topic `decision`/`bug`/`pattern`/`preference`). The act of distilling IS the trigger — don't defer to end-of-session, and do it even when you also write a local note.
- **Guard (curation, not dump):** save ONLY *transferable* knowledge. Never save code-derivable facts (use the graph tools), ADR/CLAUDE.md/doc duplicates, secrets, project-internal trivia, or current-turn state. Wrong topic ⇒ wrong decay.
