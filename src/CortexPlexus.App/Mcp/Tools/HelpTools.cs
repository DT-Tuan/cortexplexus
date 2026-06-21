using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class HelpTools
{
    private static readonly string[] ValidTopics =
    {
        "quick-start", "tools", "indexing", "strategies", "when-to-use", "memory", "all"
    };

    [McpServerTool, Description(
        "Get usage guide for CortexPlexus MCP tools. " +
        "Call this FIRST when connecting to CortexPlexus to learn how to use all available tools effectively.")]
    public static string GetHelp(
        [Description("Topic: 'quick-start', 'tools', 'indexing', 'strategies', 'when-to-use', 'memory', or 'all' (default)")] string topic = "all")
    {
        var normalized = topic.ToLowerInvariant();
        var content = normalized switch
        {
            "quick-start" => QuickStart,
            "tools" => ToolReference,
            "indexing" => IndexingGuide,
            "strategies" => Strategies,
            "when-to-use" => WhenToUse,
            "memory" => MemoryGuide,
            "all" => $"{QuickStart}\n\n{WhenToUse}\n\n{ToolReference}\n\n{IndexingGuide}\n\n{Strategies}\n\n{MemoryGuide}",
            _ => null
        };

        // R22 Fix #12: warn loudly when an unknown topic is passed instead of
        // silently returning the default. Avoids the user thinking their topic
        // filter was applied when it was ignored.
        if (content is null)
        {
            var validList = string.Join(", ", ValidTopics.Select(t => $"'{t}'"));
            return $"Unknown topic '{topic}'. Valid topics: {validList}.\n\n" +
                   $"Showing 'all' as default fallback:\n\n" +
                   $"{QuickStart}\n\n{WhenToUse}\n\n{ToolReference}\n\n{IndexingGuide}\n\n{Strategies}\n\n{MemoryGuide}";
        }

        return content;
    }

    private const string QuickStart = """
        # CortexPlexus — Quick Start

        CortexPlexus is a Code Intelligence MCP Server. It builds a Knowledge Graph
        from source code and provides structured context — call graphs, DI wiring,
        API routes, class hierarchy, semantic search — in 1 tool call instead of
        10+ grep/read operations.

        ## Step 1: Activate Local Agent (source code stays on your machine)
        → ActivateAgent(projectPath: "<your workspace path>")
        Follow the returned instructions to install + start the agent.
        The agent parses code locally and sends only metadata to server.

        ## Step 2: Verify indexing
        → ListRepositories()
        Also check the "Memory:" line — if enabled, you can use SaveMemory /
        RecallMemory to persist context across sessions (see GetHelp("memory")).

        ## Step 3: Recall prior context (if memory is enabled)
        → RecallMemory("<topic you're working on>", scope: "project", scopeId: "<repoId>", limit: 5)
        Read any hits before starting — avoids re-discovering what you already knew.

        ## Step 4: Explore
        → OnboardProject("repo-name")               # full project overview
        → ExploreTopic("ServiceName", depth:"deep")  # deep dive into a symbol

        ## Alternative indexing
        → IndexFromLocal("/path/to/project")        # code already on server
        → IndexFromGit("https://github.com/...")     # clone from Git

        Supported: C# (Roslyn), TypeScript, JavaScript, Python, Java, Go, Rust, PHP
        """;

    private const string WhenToUse = """
        # When to Use MCP Instead of Grep/Read

        ALWAYS prefer MCP tools over grep/read for these tasks:

        | You want to...                  | Use MCP tool                              | NOT this                    |
        |---------------------------------|-------------------------------------------|-----------------------------|
        | Find a class/method by name     | SearchCode("name")                        | Grep + Read files           |
        | Find code by concept            | SemanticSearch("payment logic")           | Grep guessing keywords      |
        | Understand a service deeply     | ExploreTopic("name", depth:"deep")        | Read 10+ files manually     |
        | See who calls a method          | GetCallers("FQN")                         | Grep method name            |
        | Trace what a method calls       | GetCallees("FQN")                         | Read method + follow calls  |
        | Check change impact             | GetImpactAnalysis("FQN", depth:3)         | Grep + manual trace         |
        | Find interface implementations  | GetImplementations("IService")            | Grep class name             |
        | See inheritance tree            | GetClassHierarchy("ClassName")            | Read multiple files         |
        | View DI registrations           | GetDiRegistrations("service")             | Read Startup.cs/Program.cs  |
        | List API endpoints              | GetApiEndpoints()                         | Grep "MapGet" / "@app.get"  |
        | Trace request flow              | GetDataFlow("/api/route")                 | Read endpoint → handler     |
        | Check test coverage             | GetTestCoverage("Method.FQN")             | Grep test files             |
        | Find config usage               | GetConfigUsage("KEY")                     | Grep "KEY" in all files     |
        | Find unused code                | GetDeadCode("repo")                       | Manual analysis             |
        | Check middleware order           | GetMiddlewarePipeline()                   | Read Program.cs             |
        | Detect circular dependencies    | GetCircularDependencies("repo")           | Manual DI analysis          |
        | Onboard new project             | OnboardProject("repo")                    | Read 20+ files              |

        Still use grep/read when:
        - Reading a specific file you know the path to
        - Making code edits (MCP is read-only)
        - Checking git history (git log, git blame)

        Rule of thumb: If you're about to grep for a class/method name, use SearchCode
        or ExploreTopic instead. If you're about to read multiple files to trace a flow,
        use a graph traversal tool.

        ## Memory tools (v0.8.0, opt-in) — when to use vs when NOT

        If Memory is enabled (see `ListRepositories()` → "Memory: enabled (N items)"):

        | You want to...                            | Use memory tool           | NOT memory |
        |-------------------------------------------|---------------------------|------------|
        | Remember a user preference across chats   | SaveMemory(topic:"preference") | Re-ask user next session |
        | Record a non-obvious project convention   | SaveMemory(topic:"pattern", scope:"project") | Keep in conversation only |
        | Note a bug/workaround tied to a symbol    | SaveMemory(topic:"bug", relatedFqns:["X.Y"]) | Comment in chat that's lost at end |
        | Recall prior context when resuming work   | RecallMemory("auth flow", scope:"project", scopeId:"<repoId>") | Re-read all docs from scratch |
        | Audit what you've saved                   | ListMemories()             | — |
        | Correct a wrong/stale memory              | ForgetMemory(id)           | Leave it to pollute recall |

        DO NOT save as memory:
        - Facts derivable from code (who calls X, what's in appsettings, etc.) — use SearchCode / GetCallers / GetConfigUsage instead.
        - Things already in CLAUDE.md, ADRs, or docs/ — those are versioned and authoritative.
        - Credentials, API keys, emails — the SecretsScanner will reject them anyway.
        - Current-conversation-only state ("user is asking about X right now") — just use the chat.

        Rule of thumb: If the fact would have been useful **in a future chat** and it's
        NOT derivable from running `search_code` / `get_impact_analysis` / etc., save it.
        Otherwise don't — memory pollution degrades recall quality for everyone.

        See `GetHelp(topic: "memory")` for the full memory playbook.
        """;

    private const string ToolReference = """
        # Tool Reference (30 tools)

        ## INDEXING (do this first)
        - ActivateAgent(projectPath, projectName?)
          Install + start local indexing agent. Source never leaves your machine.
          Example: ActivateAgent(projectPath: "/home/dev/myproject")

        - IndexFromLocal(path)
          Index a directory already on server.
          Example: IndexFromLocal("/workspace/myproject")

        - IndexFromGit(url, branch?, name?)
          Clone from Git URL and index.
          Example: IndexFromGit("https://github.com/org/repo.git", branch: "develop")

        - DeleteRepository(name)
          Delete an indexed repo + all its data (graph, symbols, embeddings). Irreversible.
          Example: DeleteRepository("old-test-repo")

        ## COMPOSITE (prefer these — 1 call replaces 4-5 calls)
        - ExploreTopic(query, repository?, depth?)
          Deep-explore a symbol: search → callers → deps → callees → impls.
          depth: "shallow" (search only) | "normal" (+callers/deps) | "deep" (everything)
          Example: ExploreTopic("OrderService", repository: "backend", depth: "deep")

        - OnboardProject(repository)
          Full project overview: DI registrations + API endpoints + EF entities.
          Example: OnboardProject("OpsFlow.Api")

        ## SEARCH
        - SearchCode(query, repository?, limit?, expand?)
          BM25 full-text search. Use when you know the exact name.
          Example: SearchCode("IPaymentService", repository: "backend", limit: 10)

        - SemanticSearch(query, repository?, limit?, expand?)
          Vector + BM25 hybrid. Use for natural language queries.
          Example: SemanticSearch("authentication and authorization logic")
          Tip: expand=true uses Ollama LLM for better recall (+2s latency)

        ## GRAPH TRAVERSAL
        - GetCallers(methodFqn, depth?)         — Who calls this? (depth 1-5)
        - GetCallees(methodFqn, depth?)         — What does this call? (depth 1-5)
        - GetDependencies(fqn, depth?)          — Type deps: DI injection, field types (depth 1-3)
        - GetImplementations(interfaceFqn)      — Classes implementing interface
        - GetClassHierarchy(classFqn)           — Inheritance tree (ancestors + descendants)
        - GetImpactAnalysis(fqn, depth?)        — Blast radius: callers + refs + impls + hierarchy

        Tip: Use SearchCode("partial-name") first to find the correct FQN,
        then pass FQN to graph tools.

        ## FRAMEWORK & ARCHITECTURE (multi-language unless noted)
        - GetApiEndpoints(moduleName?, repository?)      — HTTP routes: ASP.NET + Python FastAPI/Flask
        - GetDiRegistrations(serviceType?, repository?)  — DI: ASP.NET + Java Spring + NestJS @Injectable
        - GetDependencyAudit(path?, ecosystem?)          — Deps across npm/pip/go/cargo/composer/maven/nuget
        - GetDataFlow(endpointRoute)                     — Trace: endpoint → handler → services
        - GetArchitecture(repository?)                   — Architecture overview (modules + DI + endpoints)
        - GetEntityMapping(entityName?, repository?)     — EF Core entities (DbContext → DbSet → Entity) [.NET only]
        - GetNuGetAudit(path?)                           — NuGet package versions [.NET only — prefer GetDependencyAudit]

        ## CODE QUALITY & ARCHITECTURE
        - GetTestCoverage(methodFqn)                     — Which tests cover this production method?
        - GetConfigUsage(configKey?, repository?)         — Where is this config key used? (8 languages)
          Supports: appsettings.json, .env, IConfiguration, IOptions<T>, process.env,
          os.environ, System.getenv, os.Getenv, env::var, $_ENV
        - GetDeadCode(repository)                        — Public/internal methods with 0 callers
        - GetMiddlewarePipeline(repository?)              — ASP.NET middleware execution order
        - GetCircularDependencies(repository)            — Detect cyclic class dependencies (A→B→C→A)

        ## OVERVIEW
        - ListRepositories()    — All indexed repos with last index time
        - GetHelp(topic?)       — This guide (topics: quick-start, tools, indexing, strategies, when-to-use)

        ## AGENT MEMORY (opt-in — requires Memory__Enabled=true)
        Semantic, scoped, auto-decaying memory store. See docs/MEMORY-SYSTEM.md.
        - SaveMemory(content, scope, scopeId?, topic?, importance?, relatedFqns?)
          Store a memory. Scope: session|project|global. Topic shapes decay.
          Example: SaveMemory("Team prefers async/await", scope: "project", scopeId: "<repoId>", topic: "preference")
        - RecallMemory(query, scope?, scopeId?, topic?, relatedFqn?, limit?)
          Semantic recall with decay-weighted ranking.
          Example: RecallMemory("auth patterns", scope: "project", scopeId: "<repoId>")
        - ListMemories(scope?, scopeId?, topic?, limit?)
          Audit all saved memories (no semantic search; includes near-forgotten).
        - ForgetMemory(id)  — Delete a specific memory by UUID.
        """;

    private const string IndexingGuide = """
        # Indexing Guide

        ## When to index
        - First time connecting to a new project
        - After major code changes (new files, refactoring)
        - Not needed if local agent is already running (it auto-reindexes)

        ## Methods (in order of preference)
        1. LOCAL AGENT (best — source stays on your machine):
           → ActivateAgent("/path/to/project")
           Agent downloads automatically, watches files, indexes on save.

        2. Code on server:
           → IndexFromLocal("/path/to/project")

        3. Code on GitHub/Azure DevOps:
           → IndexFromGit("https://github.com/org/repo.git")

        4. Code on another machine (REST API):
           git archive -o project.zip HEAD
           curl -X POST http://server:8080/api/index/push -F "archive=@project.zip"

        ## Rules
        - 1 path = 1 repository (point to root directory)
        - Incremental: only changed files are re-parsed (SHA256 hash comparison)
        - Use 'repository' parameter in search/analysis tools to scope to specific project

        ## After indexing
        → ListRepositories()     — verify project appears
        → OnboardProject("repo") — get full overview
        """;

    private const string Strategies = """
        # Usage Strategies

        ## Session start (every session)
        1. ActivateAgent(projectPath: "<workspace>") — if not already running
        2. ListRepositories() — verify indexed

        ## Multi-repo workflow
        When working with multiple projects, ALWAYS pass repository parameter:
          SearchCode("UserService", repository: "backend")
          GetApiEndpoints(repository: "OpsFlow.Api")
        Tools using FQN (GetCallers, etc.) don't need repository — FQN is already unique.

        ## Understand a class/service (1 call)
        → ExploreTopic("ClassName", depth: "deep")
        Replaces: SearchCode + GetCallers + GetDependencies + GetCallees + GetImplementations

        ## Onboard new project (1 call)
        → OnboardProject("repo")
        Replaces: GetArchitecture + GetApiEndpoints + GetDiRegistrations + GetEntityMapping

        ## Evaluate change impact
        → GetImpactAnalysis("Method.FQN", depth: 3)
        → GetTestCoverage("Method.FQN")  — check if tests exist

        ## Debug request flow
        → GetDataFlow("/api/endpoint")
        → GetMiddlewarePipeline()  — check middleware order

        ## Code quality review
        → GetDeadCode("repo")                — unused methods
        → GetCircularDependencies("repo")    — tight coupling
        → GetConfigUsage("KEY")              — config impact analysis

        ## Priority: composite > individual tools
        Always try ExploreTopic or OnboardProject first.
        Use individual tools only for very specific queries.

        ## Memory workflow (when Memory is enabled — see GetHelp("memory"))

        1. At session start: after ListRepositories(), call RecallMemory with the topic
           you're about to work on. Example:
             RecallMemory("auth middleware", scope: "project", scopeId: "<repoId>", limit: 5)
           This gives you prior context before you start re-exploring the codebase.

        2. During work: if the user states a preference ("we always use X here") or you
           discover a non-obvious project convention, save it:
             SaveMemory("Team prefers result-type error handling over exceptions",
                        scope: "project", scopeId: "<repoId>", topic: "preference")

        3. At end of session: if you learned something the next agent session would waste
           time re-discovering, save it. Otherwise don't.

        4. Use relatedFqns when the memory is about a specific symbol — then future
           get_impact_analysis / explore_topic calls with include_memories=true surface it.
        """;

    private const string MemoryGuide = """
        # Agent Memory — Usage Playbook (opt-in, v0.8.0+)

        CortexPlexus memory is an opt-in, semantic, scoped, auto-decaying store for
        things AI agents want to remember across sessions — preferences, project
        conventions, bug notes, decisions — that are NOT derivable from the code itself.

        ## Before you use it: verify it's enabled

        Call `ListRepositories()`. Look for the "Memory:" line near the bottom:
          - `Memory: enabled (N items).`    → memory tools work.
          - `Memory: disabled. Enable via...` → tell the user, or skip memory entirely.

        If disabled, do NOT try to save — the tools will just return a clear error.

        ## The 3 scopes — pick the right one

        | Scope     | scope_id                          | When to use                                      |
        |-----------|-----------------------------------|--------------------------------------------------|
        | `session` | client-supplied session UUID      | State that should die when the chat ends         |
        | `project` | repository UUID (from ListRepos)  | Default for codebase-specific things (80% case)  |
        | `global`  | null                              | Rare: user-wide preferences across all projects  |

        Rule: default to `project` with the current repo's id. Only use `global`
        for user-wide truths. Only use `session` for truly transient state.

        ## The 6 topics — pick the right one (shapes decay)

        | Topic        | Half-life (~) | Use for                                           |
        |--------------|---------------|---------------------------------------------------|
        | `preference` | 1 year        | User likes / dislikes, style choices              |
        | `pattern`    | 6 months      | Code/design pattern specific to this project      |
        | `decision`   | 6 months      | Why X was chosen over Y (mini-ADR)                |
        | `bug`        | 3 months      | Known issue or workaround (fades after fix)       |
        | `todo`       | 1 month       | Short-lived follow-up                             |
        | `note`       | 2 months      | Default for unclassified memories                 |

        Wrong topic = wrong decay speed. If unsure, use `note`.

        ## Importance (0..1, default 0.5)

        - 0.9+ : "this is foundational; future sessions absolutely need this"
        - 0.5  : default — "probably useful later"
        - 0.2- : "low-signal, store only because user explicitly asked"

        Higher importance → survives decay longer. Don't inflate — it just delays the
        inevitable forget.

        ## Workflow pattern — resume work on a project

        ```
        1. ListRepositories()   → get repoId for your project, verify Memory: enabled
        2. RecallMemory(query: "<what you're about to work on>",
                        scope: "project", scopeId: "<repoId>", limit: 5)
        3. If hits: read them BEFORE running search_code / explore_topic.
           You might avoid re-discovering something you already knew.
        4. Do your work.
        5. If you learn something non-obvious: SaveMemory(...) with the right topic.
        6. If you find a stale memory: ForgetMemory(id).
        ```

        ## Workflow pattern — user states a preference

        User: "By the way, we always use Result<T> instead of throwing."

        → Immediately:
          SaveMemory(
            content: "Team prefers Result<T> over exceptions for domain errors",
            scope: "project",
            scopeId: "<current repoId>",
            topic: "preference",
            importance: 0.8)

        Don't wait until end of session. The act of stating a preference IS the signal.

        ## Workflow pattern — bug tied to a symbol

        You find: "PaymentProcessor.ProcessAsync has a race condition on retry"

        → Save with soft link:
          SaveMemory(
            content: "Race condition on retry — fixed in PR #42 by adding mutex on _retryLock",
            scope: "project",
            scopeId: "<repoId>",
            topic: "bug",
            relatedFqns: ["App.Payments.PaymentProcessor.ProcessAsync"])

        Later, when someone runs get_impact_analysis on that method with
        include_memories=true, this surfaces automatically.

        ## DO NOT save as memory

        - Facts derivable from code (use search_code / get_callers / get_config_usage instead)
        - Anything in CLAUDE.md, ADRs, or docs/ — those are versioned & authoritative
        - Secrets, credentials, API keys, emails (the SecretsScanner rejects these anyway)
        - Current-turn state ("user asked about X just now") — stay in the chat
        - Summaries of what you just did — the diff / commit message covers that
        - Duplicates of memories already in the store — run ListMemories() first if unsure

        ## Common mistakes

        1. "I'll just save everything, can't hurt" → WRONG. Recall is semantic + decay-ranked;
           too much low-signal noise buries the good stuff. Memory is curation, not dump.

        2. "I'll use `global` because it's easier" → WRONG. Global affects every project's
           recall. Use `project` and pass the current repo's id.

        3. "I'll make importance=1.0 to be safe" → WRONG. Inflation breaks ranking.
           Save with a realistic score; let the user raise it if they want.

        4. "I'll skip the topic" → OK but you get default 60-day decay. If this memory
           matters for 6+ months, set `topic: "pattern"` or `"preference"` explicitly.

        5. "Memory is disabled but I'll save anyway" → WRONG. The tool returns an error
           telling the user how to enable. Don't retry.

        ## The 4 tools (one-line each)

        - SaveMemory(content, scope, scopeId?, topic?, importance?, relatedFqns?)
          Stores a new memory after PII scan.

        - RecallMemory(query, scope?, scopeId?, topic?, relatedFqn?, limit?)
          Semantic + decay-ranked retrieval. Bumps access counter on hits.

        - ListMemories(scope?, scopeId?, topic?, limit?)
          Pure filter — no embedding cost. For audit / management.

        - ForgetMemory(id)
          Delete a specific memory by UUID.

        ## See also

        - docs/MEMORY-SYSTEM.md — full spec
        - docs/decisions/010..013 — ADRs (Postgres reuse, scope model, Weibull decay, opt-in)
        """;
}
