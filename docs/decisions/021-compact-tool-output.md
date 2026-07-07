# ADR-021: Compact-by-default tool output + token measurement harness

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-6 / Tier 1 · T1.4

## Context

VISION principle 1: *token là đơn vị tiền tệ* — a tool that returns 2 000 tokens for a
100-token answer is a broken tool. Today's output layer was written for human
readability, and the bill is paid by every agent on every call:

- **All 34 tools return prose `string`s** (31 `Task<string>` + 3 `string`; verified
  2026-07-07). `list_repositories` renders 12 repos as ~100 lines of labeled fields
  (`Name:` / `Path:` / `Last indexed:` / `Health:` per repo — `GraphTraversalTools.cs:428-458`)
  ≈ **1.3K tokens for information a dense format carries in ~250**.
- Decorative headers, repeated field labels, and per-item blank lines are systematic
  (`ExploreTools`, `OnboardProject`, help texts).
- The only budget primitive, `ContextCompressor`
  (`src/CortexPlexus.Search/ContextCompressor.cs` — 4000-token default, chars/4
  estimate, L0/L1/L2 levels), is applied **only to search-result lists**; everything
  else is unbudgeted `StringBuilder` prose.
- **No token measurement exists anywhere** — output cost regressions land silently (the
  help text still says "30 tools" while 34 exist; nobody noticed because nothing counts).
- The resolved MCP SDK (`ModelContextProtocol.AspNetCore` 1.0.0 — csproj floats `0.*`,
  `project.assets.json` resolves 1.0.0) **supports** `UseStructuredContent` /
  `Tool.OutputSchema` / `CallToolResult.StructuredContent`, but no tool opts in; the
  memory tools hand-serialize *indented* JSON strings (`MemoryTools.cs:25-29`,
  `WriteIndented=true` — paying tokens for whitespace).

## Decision

Three moves, ordered by leverage. The unit of success is **tokens per answered
question**, measured, not eyeballed.

### 1. Compact-by-default rendering convention (the big win)

A shared formatter (`Mcp/Tools/OutputFormat.cs`) + a rendering convention applied to
every list-shaped tool:

- **One line per record**, positional, `|`-separated, with a single legend line naming
  the fields once:

```
# repos (12) — fields: name | symbols | embeddings | space | last indexed | watch
CortexFlow | 23003 | 15781 (100%) | vertex/768 | 5d ago | 🟢
CortexPlexus | 3642 | 3010 (100%) | vertex/768 | 2h ago | 🟢
iTAS | 1236 | 795 (100%) | ollama/768 ⚠️ | 71d ago | ⚪
```

- No decorative banners, no repeated labels, no per-record blank lines, no
  `WriteIndented` JSON.
- Terse-but-complete: nothing informational is dropped — only formatting overhead.
  (The ADR-015/018/023 trust signals *gain* prominence in the dense format: one column
  instead of a buried suffix.)
- **`format: "full"` opt-in parameter** on converted tools restores today's verbose
  prose for humans debugging via MCP inspector. Default is compact — agents are the
  primary user (VISION principle 2), and defaults are what agents use.
- `ContextCompressor` L0/L1/L2 stays the verbosity ladder for search results; its
  levels adopt the same single-line record shape (L0 already is one).

Conversion order by measured traffic × verbosity: `list_repositories` →
`explore_topic` → `onboard_project` → search result rendering → graph tools → help
texts (rewritten tersely; also fix the stale "30 tools" count by deriving it from the
tool registry at startup instead of a hand-edited constant).

### 2. Token measurement harness (make cost a tested property)

- `OutputFormat.EstimateTokens(string)` — chars/4, same heuristic `ContextCompressor`
  already uses; consistency matters more than precision.
- **Golden-call budget tests** in `CortexPlexus.Mcp.Tests`: a fixture set of ~20
  representative calls against seeded stores; each asserts
  `EstimateTokens(output) <= budget` with per-call budgets checked into the test
  (e.g. `list_repositories(12 repos) ≤ 350`, `explore_topic(normal) ≤ 1200`).
  A PR that bloats output **fails CI** — token cost becomes a regression-guarded
  contract, the same way R17's lesson made performance claims measurement-backed.
- Log line per tool call at Debug: tool name + estimated output tokens — enables the
  VISION §8 quarterly metric without external tooling.

### 3. StructuredContent: adopt at the edge, don't migrate the world

`UseStructuredContent = true` **only** for tools whose consumers parse rather than read:
`graph_query` (ADR-020 — returns a JSON array by contract) and the memory tools
(already JSON-shaped; switch their hand-rolled strings to compact serialization,
`WriteIndented=false`, via the SDK's typed path). Prose-shaped tools stay text — a
compact text line is *cheaper* than JSON for record data (no repeated keys, no quoting),
and the primary client reads text natively. Revisit wholesale migration only if MCP
clients grow schema-aware result handling worth paying JSON overhead for.

## Alternatives considered

### A. Migrate everything to structuredContent/JSON
JSON repeats keys per record — for `list_repositories` it is *more* tokens than the
compact text format unless keys are stripped, at which point it's a worse CSV. Structure
helps parsers, not budgets. **Rejected** as a blanket move; adopted at the edge (§3).

### B. Client-side compression (agent asks for less via `limit` params)
Already possible, doesn't touch the fixed overhead (banners, labels, indentation) which
dominates small responses — `list_repositories` has no `limit` at all. **Rejected** as
sufficient; limits stay as the volume knob, this ADR fixes the per-record cost.

### C. A global response middleware that post-compresses any tool's string
Tempting single seam, but lossy transforms applied blind to prose risk mangling meaning
(truncating mid-record, stripping load-bearing whitespace in code snippets). Formatting
is a per-tool authoring concern with a shared helper, not a middleware. **Rejected.**

### D. Real tokenizer instead of chars/4
Accurate budgets, but adds a tokenizer dependency to the server for a number only used
as a regression guard. chars/4 is consistent, fast, and already the codebase convention.
**Rejected** (revisit if budget tests prove flaky near thresholds).

## Consequences

**Positive**
- Every session, every agent, every call gets cheaper — compounding savings with zero
  client changes. Projected ≥60% reduction on the orient-flow calls
  (`list_repositories` alone: ~1.3K → ~300 tokens), converging on the VISION §8 target
  together with ADR-019.
- Token cost becomes CI-visible; output bloat and doc drift (the "30 tools" class of
  rot) get tripwires.
- ADR-019's pack and ADR-020's query tool are born compact — conventions exist before
  their implementation.

**Negative / cost**
- Dense formats are less pleasant for humans reading raw MCP output — mitigated by
  `format:"full"` and the Web UI remaining the human surface.
- Every existing output-shape test (`McpToolsTests`, snapshot-style assertions) needs
  updating in the same PR as each tool's conversion — staged rollout keeps PRs reviewable.
- Downstream prompts/skills that regex today's output (e.g. anything grepping
  `Last indexed:` lines) break on conversion; the compact format ships in a minor
  release with the old format one `format:"full"` away, and release notes call out
  each converted tool.
- chars/4 misestimates CJK/Vietnamese-heavy content (~2 chars/token); budgets are set
  with headroom, and the harness compares like-for-like, so regressions still surface.

## Verification / acceptance

1. **Golden-call budget tests** (the §2 harness) green in CI, with the 20-call fixture
   committed and per-call budgets documented inline.
2. **Before/after measurement** on the real orient flow (list + recall + 2 searches)
   against the LXC deployment: ≥50% total token reduction, recorded in
   `docs/BENCHMARK.md` as a numbered round (per the benchmark-update policy).
3. **No information loss:** for each converted tool, a review checklist asserts every
   field present in `full` is present or deliberately dropped (documented) in compact.
4. **Drift guard:** help/tool-count derived from the registry — test asserts the help
   text count equals the scanned `[McpServerTool]` count (would have caught 30-vs-34).

## References

- `ContextCompressor` (existing budget/level machinery this generalizes)
- SDK capability: `ModelContextProtocol.Core` 1.0.0 `UseStructuredContent`/`OutputSchema` (unused today)
- R17 "measure before projecting" → §2 makes output cost a measured property
- ADR-019 (consumes the convention) · ADR-020 (structuredContent edge case)
- VISION.md §2.2 GAP-6, §4 principle 1, §6 T1.4, §8 metrics
