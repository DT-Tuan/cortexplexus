# ADR-020: `graph_query` — read-only open Cypher as an MCP tool

**Status:** Proposed
**Date:** 2026-07-07
**Vision ref:** [VISION.md](../VISION.md) GAP-3 / Tier 1 · T1.3

## Context

CortexPlexus answers structural questions through **fixed** tools: every question type is
one hand-written `Query*Async` on `IGraphStore` + one MCP tool + tests + a release. The
graph itself already holds far more answerable structure than the tool surface exposes —
20+ node labels (`Migrations.sql:117-123`), 25+ edge types (Calls, DependsOn, Throws,
Subscribes, TestCovers, ReadsConfig, HttpCalls, HandledBy, PipelineOrder, …).

Questions the data can answer today but no tool can:

- "Classes that `Publishes` events but have no `TestCovers` edge"
- "Methods with cyclomatic complexity > 15 that sit on a `HandledBy` chain from any endpoint"
- "Config keys read by more than one repo"
- "The 10 highest fan-in symbols that are not interfaces"

Each currently requires either a new C# tool (days + redeploy) or falling back to
grep — the exact failure mode CP exists to eliminate. Meta's Glean demonstrated the
alternative model at scale: expose the **fact store behind a query language**, and the
long tail of questions costs zero marginal tools.

### Codebase facts that shape the design (verified 2026-07-07)

1. **All reads funnel through a hardcoded 5-column projection.** `ExecuteCypherQuery`
   (`AgeGraphStore.cs:1469-1482`) wraps every query as
   `SELECT ... FROM cypher('code_graph', $$ <cypher> $$) AS (fqn agtype, name agtype, file_path agtype, start_line agtype, signature agtype)` —
   AGE requires the SQL `AS (...)` arity to match the Cypher `RETURN` arity, so an
   open-ended query with arbitrary columns cannot reuse this path.
2. **AGE has no parameterized Cypher** (comment at `AgeGraphStore.cs:1612-1613`); the
   query body is string-interpolated inside PostgreSQL dollar-quoting (`$$ ... $$`).
   A user-supplied query containing `$$` can therefore **break out of the quoting and
   inject raw SQL** — the existing `EscapeCypher` (`AgeGraphStore.cs:1615-1616`) only
   escapes quotes/backslashes in *values* and is useless for whole-statement input.
3. **No statement-level guard exists** — no read-only enforcement, no keyword filter,
   no timeout override (default 30 s), because until now all Cypher was machine-generated.
4. Multi-repo graph: all repos share `code_graph`; vertices carry a `repo_id` property,
   so repo scoping must happen inside the query.

## Decision

Ship one new MCP tool:

```
graph_query(query: string, repository?: string, limit?: int = 50)
```

**Contract:** `query` is an AGE-dialect Cypher *read* statement whose `RETURN` clause
yields **exactly one value per row** — a map literal for multi-field results:

```cypher
MATCH (c:class)-[:Publishes]->()
WHERE NOT EXISTS { MATCH ()-[:TestCovers]->(c) }
RETURN {fqn: c.fqn, file: c.file_path}
```

The single-column contract sidesteps fact #1: the server always wraps as
`AS (result agtype)`, JSON-parses each row, and returns a JSON array. Arbitrary
projections live *inside* the map, so no dynamic SQL column list is ever built.

### Defense in depth (all five layers, none optional)

| # | Layer | Mechanism |
|---|---|---|
| 1 | **Dollar-quote breakout kill** | Wrap the query in a **randomized dollar tag** (`$q_7f3a1c$ ... $q_7f3a1c$`, fresh per call). Reject the request outright if the user text contains the generated tag (probability ~0) or any `$...$` sequence forming a dollar-quote delimiter. This closes fact #2's injection hole structurally, not lexically. |
| 2 | **Read-only transaction** | Execute inside `BEGIN READ ONLY; … ROLLBACK;`. AGE mutations write through ordinary PostgreSQL writes, so a read-only transaction rejects them at the engine level — no reliance on parsing. |
| 3 | **Always-rollback** | Even the read runs in a transaction that is rolled back — belt-and-braces against any engine edge case layer 2 misses. |
| 4 | **Statement shape gate** | Before execution, a small validator tokenizes outside string literals and rejects mutation keywords (`CREATE`, `MERGE`, `DELETE`, `DETACH`, `SET`, `REMOVE`, `DROP`) and multiple statements (`;`). This is UX, not security (layers 1–3 are the security): it converts a would-be engine error into an instructive message. |
| 5 | **Resource caps** | `SET LOCAL statement_timeout = '5s'` inside the transaction; server-enforced `LIMIT` (min of user `limit` and hard cap 500) appended when the query lacks one; result payload truncated at 64 KB with an explicit `…truncated` marker. |

### Ergonomics (what makes it usable by an agent, not just safe)

- **`repository` parameter** resolves via the existing `RepoResolver` and is exposed to
  the query as a pre-bound `WHERE` hint: the tool description documents the idiom
  `MATCH (n {repo_id: $repo})` and the server substitutes the resolved UUID via the
  escaped-literal path (`EscapeCypher`) — the *only* server-side interpolation into the
  user query, and it is a server-generated value.
- **Self-documenting failure:** on validator rejection or AGE error, the tool returns
  the error plus a cheat sheet — available labels, edge types, and the single-column
  RETURN contract — so the agent can self-correct in one round trip (mirrors the R21/R25
  "friendly param errors" precedent).
- **`get_help(topic: "graph-query")`** documents the schema: node labels + their key
  properties, edge types + directions. Generated from the same constants the migration
  uses, so it cannot drift from reality.
- Output is a JSON array (one element per row), not prose — this tool is for precision
  questions; token-lean by construction (aligns with ADR-021).

### Position in the tool hierarchy

`graph_query` is the **escape hatch, not the front door**. Tool description states:
"Prefer the purpose-built tools (get_callers, get_impact_analysis, …) — they encode
traversal semantics (edge direction, polymorphism, framework filters) you would have to
reimplement in raw Cypher. Use graph_query when no dedicated tool answers your question."
The R21–R25 smoke-test history shows how much correctness tuning those tools embed
(anchored FQN matching, directional traversal, CLR-primitive filters); raw Cypher gets
none of it, by design.

## Alternatives considered

### A. Keep adding dedicated tools per question
The status quo. Each costs days and grows the tool list the client must reason over
(30 already). The long tail is unbounded — this is a treadmill, not a strategy.
**Rejected** as the *only* mechanism; dedicated tools remain for the high-frequency 90%.

### B. Text-to-Cypher inside the server (LLM translates natural language)
Doubles failure modes (wrong Cypher *and* wrong answer), needs an LLM dependency in the
server, and the MCP client is already a stronger Cypher author than any small local
model. The agent writes Cypher; CP executes it. **Rejected** (VISION non-goal #3:
"đừng biến CP thành agent thứ hai").

### C. Expose raw SQL over the relational tables instead
`code_symbols` + relational metadata cover some questions, but relationship traversal —
the whole point — lives in AGE. Two query languages, and SQL injection surface over the
*entire* database rather than one graph. **Rejected.**

### D. GraphQL/JSON query DSL designed by us
Safer to validate but a new language nobody (human or model) knows; Cypher is in every
model's training data. **Rejected.**

### E. Separate read-only PostgreSQL role instead of READ ONLY transactions
Stronger isolation (connection-level), but requires managing a second connection string /
data source through DI and deployment. The transaction guard achieves the same effect
with zero deployment surface. **Deferred** — adopt if graph_query ever runs against
untrusted multi-user input (Phase 11 world).

## Consequences

**Positive**
- The 31st question costs zero new code — VISION north-star "1 call cho câu hỏi bất kỳ".
- Doubles as a debugging/analytics surface for CP development itself (fleet dashboards,
  edge-type audits) without psql access.
- The five-layer guard design is reusable if a raw-SQL analytics tool is ever wanted.

**Negative / cost**
- Raw Cypher bypasses the semantic corrections baked into dedicated tools — answers can
  be *subtly less right* (e.g., missing polymorphic call resolution). Mitigated by the
  hierarchy note in the description; accepted as inherent to an escape hatch.
- AGE's Cypher dialect has gaps vs Neo4j (no `shortestPath`, limited functions) — agent
  frustration risk; the cheat sheet must state the dialect explicitly.
- A pathological-but-valid read query can still burn 5 s of CPU per call; the timeout
  bounds it, and MCP clients serialize calls, so worst case is bounded nuisance.

## Verification / acceptance

1. **Security tests (the gate for shipping):** attempted mutation via each keyword →
   rejected by validator; mutation smuggled past validator (e.g. via exotic casing/
   whitespace) → blocked by READ ONLY tx; `$$` and custom dollar-tag payloads → request
   rejected, no SQL escape (assert via absence of any write + audit of executed SQL);
   `;`-chained statement → rejected.
2. **Contract tests:** multi-column RETURN → instructive error naming the map idiom;
   missing LIMIT → capped at 500; >64 KB result → truncation marker present.
3. **Functionality:** the four Context example queries run against the CortexPlexus
   self-index and return correct, spot-checked results.
4. **Live (LXC):** run the "Publishes without TestCovers" query across the fleet;
   confirm 5 s timeout by running an intentional cartesian query.

## References

- Glean (Meta) — facts + query language model this adopts for the long tail
- ADR-009/R21/R25 — the correctness tuning history that justifies keeping dedicated tools primary
- `AgeGraphStore.cs:1469-1482` (projection constraint), `:1587-1616` (execution + escaping facts)
- VISION.md §2.2 GAP-3, §3 (Glean row), §6 T1.3
