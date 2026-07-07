# ADR-028: Language-neutral adoption surface — kill the ".NET-only" gestalt that makes agents refuse CP

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-9 / T1.8 · sibling of ADR-021 (021 = output economy, 028 = input surface)

## Context

### The failure mode: agents refuse to use CP on non-.NET projects

An AI agent decides whether to call an MCP tool from three inputs: the tool
descriptions in its context, the first-contact docs (CLAUDE.md template, get_help), and
what earlier calls returned. Today all three whisper ".NET" — so on a Python/TS repo the
agent rationally concludes "wrong tool for this project" and falls back to grep. This is
**observed behavior, not hypothesis**: the operator had to patch it *client-side* with a
UserPromptSubmit hook whose literal text is *"works for C# AND Python/TS/Go/etc."* — a
standing correction for a bias the server itself emits. ADR-016 C4 fixed the
ServerInstructions; the rest of the surface never got the pass.

### Evidence audit (2026-07-07)

| Surface | Finding | Effect on a non-.NET agent |
|---|---|---|
| `docs/AGENT-TEMPLATE.md` (pasted into downstream CLAUDE.md — the highest-leverage doc) | Step 1 prereq: *"Verify prereqs (**dotnet SDK**)"*, unqualified | Python project agent reads "needs dotnet SDK" → **refuses on the spot**. (Truth: the *agent binary* runs on the .NET runtime; the project's language is irrelevant.) |
| `get_callers`/`get_callees` param docs (`GraphTraversalTools.cs:127,148`) | FQN example: `'Namespace.Class.Method'` — C#-only idiom | "My FQNs look like `pkg.module.func` — not for me" |
| `docs/INTRODUCTION.md` lead | Roslyn first, `GetCallers("Method.FQN")`, EF Core in the flagship list | First-impression gestalt = .NET product with ports |
| Flat tool list | 4 .NET-only tools (`get_entity_mapping`, `get_middleware_pipeline`, `get_nu_get_audit`, EF/DI phrasing) interleaved with 30 universal ones | One biased description contaminates perception of the whole server |
| `get_nu_get_audit` | Mangled name (NuGet→`nu_get` casing artifact) **and** fully subsumed by `get_dependency_audit` (7 ecosystems incl. nuget, ADR-016 C1) | Legacy duplicate advertising the bias |
| Docs drift | Help/docs say "30 tools", actual 34; MCP-GUIDE troubleshooting assumes Ollama-only | Stale facts erode trust in all claims |
| Schema weight (measured) | 34 descriptions = **7 902 chars ≈ 2 000 tokens**, top-heavy (`SaveMemory` 187 tok, `ActivateAgent` 148 tok); with param docs + JSON scaffolding ≈ **5–7 K tokens injected into every session** of non-deferring clients (Cursor, Windsurf…) | Fixed context tax before the first call |

### The naming question, answered with numbers

Longest tool name: `GetCircularDependencies` (23 chars ≈ 6 tokens). Verb_noun names are
clear and self-describing; per-call cost of name length is noise. The measurable costs
live elsewhere: (a) the `mcp__cortexplexus__` **client-side prefix** (19 chars) is set by
the server alias in each client's config — not by us at runtime, but by our config
*examples*; (b) the **description bulk** above. Conclusion: **mass-renaming tools is the
wrong lever** — it breaks every downstream CLAUDE.md, doc, and model muscle-memory for a
~zero token win. The right levers are the alias we *recommend*, one mangled-name
deprecation, and description weight.

## Decision

Four moves, ordered by leverage:

### 1. Fix first-contact surfaces (highest leverage — zero code)

- **`AGENT-TEMPLATE.md` rewrite:** lead with *"works with C#, TypeScript, JavaScript,
  Python, Java, Go, Rust, PHP"* in the first line the downstream agent reads. Reframe the
  prereq truthfully: *"the CortexPlexus agent binary runs on the .NET runtime (like
  Node for many CLIs) — your project's language does not matter; install once, index
  anything."* Template examples use a TS or Python repo, not a C# one.
- **`INTRODUCTION.md` / `README.md` lead rewrite:** multi-language first (Tree-sitter 8
  languages up front), C#-depth presented as *the deepest tier* of a language matrix,
  not the product identity. Example calls alternate languages
  (`get_callers("app.services.billing.charge")`).
- **`MCP-GUIDE.md`:** mixed-language example table; troubleshooting updated for the
  three providers (not Ollama-only); tool count derived, not hand-written (ties into the
  ADR-021 drift-guard test: help count == scanned `[McpServerTool]` count).

### 2. Language-neutral tool-description pass (one PR, all 34 tools)

- **Universal tools:** neutral phrasing + a two-language FQN example, e.g.
  `"Fully qualified name — e.g. 'MyApp.Services.PaymentService.Charge' (C#) or 'app.services.payment.charge' (Python/TS)"`.
- **Framework tools:** explicit coverage sentence *first*
  (`"ASP.NET, FastAPI/Flask, NestJS/Express"` — already true per ADR-016, just unsaid
  per-tool), and .NET-only tools say exactly that (`"C#/.NET only — for other stacks use
  get_dependency_audit"`), so scoped truth replaces inferred generalization.
- **Description token budget** (extends the ADR-021 harness with a schema-size test):
  query tools ≤ 60 tokens, workflow tools (`ActivateAgent`, memory suite) ≤ 100.
  Current worst offenders (187/148/132 tok) front-load their trigger sentence and move
  the playbook prose to `get_help` topics, which load on demand instead of every session.
  Target: total description weight ~2 000 → **≤ 1 200 tokens** with *better* first-line
  triggers.

### 3. Self-evidence beats description claims (the structural fix)

- **`list_repositories` shows detected languages per repo** — derived at no design cost
  from indexed file extensions (`GROUP BY` over `code_symbols.file_path` suffix, cached
  per index commit):
  `MyFin | 455 symbols | TypeScript` · `CortexFlow | 23 003 | C#` · `hive | 873 | Python`.
  An agent that *sees TypeScript repos in the fleet* cannot conclude "this server is
  .NET-only" — data outranks prose, the same self-evidence principle as the staleness
  labels (ADR-015). Also feeds ADR-019's trust header and the ADR-021 compact format
  (one `lang` column).
- **`get_help(topic: "languages")`:** the support matrix (language × parser × depth ×
  framework intelligence) generated from `LanguageRegistry` constants — cannot drift.

### 4. Naming: surgical, not sweeping

- **Deprecate `get_nu_get_audit`** (mangled + subsumed): description becomes
  `"DEPRECATED — use get_dependency_audit(ecosystem:'nuget')"`, delegates internally,
  removed after one minor release. 34 → 33 tools.
- **Recommend the short server alias `cortex`** in every config example
  (`.mcp.json.example`, template, guide): tool ids become `mcp__cortex__get_callers`
  (−12 chars on every mention; purely a client-config choice, zero server change,
  existing deployments unaffected).
- **No other renames.** Explicitly decided against — see Alternatives A.

## Alternatives considered

### A. Mass-rename to short names (`callers`, `impact`, `deps`…)
Breaks every downstream CLAUDE.md, doc, skill, and learned model behavior for a saving
already achieved by the alias recommendation; short names also lose the verb_noun
self-description that helps selection. **Rejected.**

### B. Split into two MCP servers (`cortex` universal + `cortex-dotnet` deep)
Cleanest possible gestalt fix — a Python agent never even sees EF tools. But it doubles
deployment/config/docs surface for every operator, and per-tool coverage sentences (+
§3 self-evidence) achieve the same perception at zero ops cost. **Rejected**; revisit
only if per-language tool counts grow enough to make the flat list unmanageable
(~60+ tools).

### C. Dynamic per-repo tool filtering (hide .NET tools when no C# repo is indexed)
MCP clients cache tool lists per session; appearing/disappearing tools confuse more than
help, and mixed fleets (this one: 12 repos, 2 languages today, more coming) make the
filter wrong most of the time. **Rejected.**

### D. Fix docs only, skip descriptions
Docs are read once; descriptions ride in *every* session context and are what the model
actually consults at call time. Both or the gestalt survives. **Rejected.**

## Consequences

**Positive**
- Directly attacks the adoption failure at its root: the agent's first three information
  sources all say multi-language, and the repo list *proves* it.
- ~40% description-weight reduction pays every session in every client (compounds with
  ADR-021's output work: input schemas + outputs both budgeted, both CI-guarded).
- The client-side hook workaround can eventually be retired — the server carries its own
  truth.
- One less tool, one less mangled name, zero breaking changes.

**Negative / cost**
- Docs rewrite touches marketing-adjacent files (INTRODUCTION/PITCH) — needs the
  operator's voice check, not just technical review.
- Trigger-first short descriptions risk under-explaining edge semantics; mitigated by
  moving depth to `get_help` topics (on-demand) and the R21-style self-correcting error
  messages already in place.
- Language detection by file extension is heuristic (mixed repos show top-2 by symbol
  count) — good enough for the perception job it does.
- Deprecation window means one release where both audit tools exist.

## Verification / acceptance

1. **Schema-budget test** (extends ADR-021 harness): sum of description tokens ≤ 1 200;
   per-tool caps enforced; fails CI on regression.
2. **Bias grep gate:** CI check that no *universal* tool description matches
   `\b(EF Core|NuGet|ASP\.NET|Roslyn|Namespace\.Class)\b` (allowlist for the declared
   .NET-only tools) — the gestalt cannot silently return.
3. **Languages line:** fixture repos in 3 languages → `list_repositories` shows correct
   per-repo languages; drift-guard: help matrix equals `LanguageRegistry`.
4. **Deprecation:** `get_nu_get_audit` returns the delegation result + deprecation note;
   removed in the following minor release.
5. **Behavioral acceptance (the one that matters):** fresh agent session on a
   TS-only repo (MyFin) and a Python-only repo, **without** the cortex-mcp hook active,
   asked "who calls X?" — the agent chooses CP tools over grep. Run as a scripted
   before/after eval (5 prompts × 2 repos); record in `docs/BENCHMARK.md` as the
   adoption round.

## References

- ADR-016 C4 (ServerInstructions pass this completes) · ADR-021 (shared budget harness,
  drift guards) · ADR-015 (self-evidence principle) · ADR-019 (consumes the languages
  signal)
- Operator's client-side hook text (the living proof of the bias) · measurements
  2026-07-07: 34 tools, 7 902 desc chars, SaveMemory 187 tok
- VISION.md §4 principle 2 ("agent là người dùng"), GAP-9 / T1.8
