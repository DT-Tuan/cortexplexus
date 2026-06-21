using System.ComponentModel;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Parsing;
using CortexPlexus.Search;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class DotNetTools
{
    /// <summary>
    /// Resolves the repository scope for a DotNet tool, returning a guard message the caller
    /// must return verbatim when scoping can't proceed safely:
    /// <list type="bullet">
    /// <item>a repository name was given but doesn't resolve → "not found";</item>
    /// <item>repository omitted, NO content filter, and &gt;1 repo indexed → require it (this is the
    /// cross-repo token-dump trap, GH #3).</item>
    /// </list>
    /// Otherwise <c>RepoId</c> is the scope (null only when 0 repos indexed, or when a content
    /// filter is present and no repo was given — a bounded cross-repo lookup that stays useful).
    /// </summary>
    private static async Task<(Guid? RepoId, string? Guard)> ResolveScopeAsync(
        string? repository, bool hasContentFilter, IRepositoryStore repoStore, string noun)
    {
        if (repository is not null)
        {
            var id = await RepoResolver.ResolveAsync(repository, repoStore);
            return id is null
                ? (null, $"Repository '{repository}' not found. Call list_repositories for valid names.")
                : (id, null);
        }

        var repos = await repoStore.ListAsync();
        if (!hasContentFilter && repos.Count > 1)
            return (null,
                $"{repos.Count} repositories are indexed ({string.Join(", ", repos.Select(r => r.Name))}). " +
                $"Pass repository:\"<name>\" to scope {noun} — omitting it returns every repo and is token-heavy.");

        // 0 repos → null (queries return empty anyway). 1 repo → scope to it implicitly.
        return (repos.Count == 1 ? repos[0].Id : (Guid?)null, null);
    }

    [McpServerTool, Description(
        "Get DI container service registrations across languages — ASP.NET " +
        "(AddScoped/AddTransient/AddSingleton), Spring (@Component/@Service/@Repository/@Controller/" +
        "@Configuration beans) and NestJS/Angular (@Injectable providers).")]
    public static async Task<string> GetDiRegistrations(
        [Description("Filter by service type name (optional)")] string? serviceType = null,
        [Description("Repository name to scope results (optional)")] string? repository = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var (repoId, guard) = await ResolveScopeAsync(repository, serviceType is not null, repoStore, "DI registrations");
        if (guard is not null) return guard;
        var results = await graphStore.QueryDiRegistrationsAsync(serviceType, repoId);

        if (results.Count == 0)
            return serviceType is not null
                ? $"No DI registrations found for service type '{serviceType}'."
                : "No DI registrations found. Index a project with DI first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DI Registrations ({results.Count}):");
        sb.AppendLine(compressor.Compress(results));
        return sb.ToString();
    }

    [McpServerTool, Description("Get EF Core entity mappings (DbContext → DbSet → Entity)")]
    public static async Task<string> GetEntityMapping(
        [Description("Filter by entity name (optional)")] string? entityName = null,
        [Description("Repository name to scope results (optional)")] string? repository = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var (repoId, guard) = await ResolveScopeAsync(repository, entityName is not null, repoStore, "entity mappings");
        if (guard is not null) return guard;
        var results = await graphStore.QueryEntityMappingsAsync(entityName, repoId);

        if (results.Count == 0)
            return entityName is not null
                ? $"No entity mapping found for '{entityName}'."
                : "No entity mappings found. Index a project with EF Core first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Entity Mappings ({results.Count}):");
        sb.AppendLine(compressor.Compress(results));
        return sb.ToString();
    }

    [McpServerTool, Description(
        "List HTTP API endpoints across languages — ASP.NET (Minimal API MapGet/MapPost, MVC " +
        "[HttpGet]), Python (FastAPI/Flask route decorators) and TypeScript (NestJS @Get/@Post " +
        "controllers, Express app.get/router.post). Returns each route as METHOD + path with its " +
        "source file.")]
    public static async Task<string> GetApiEndpoints(
        [Description("Filter by module name (optional)")] string? moduleName = null,
        [Description("Repository name to scope results (optional)")] string? repository = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var (repoId, guard) = await ResolveScopeAsync(repository, moduleName is not null, repoStore, "API endpoints");
        if (guard is not null) return guard;
        var results = await graphStore.QueryApiEndpointsAsync(moduleName, repoId);

        if (results.Count == 0)
            return moduleName is not null
                ? $"No API endpoints found for module '{moduleName}'."
                : "No API endpoints found. Index an ASP.NET, FastAPI/Flask, NestJS, or Express project first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"API Endpoints ({results.Count}):");
        foreach (var ep in results)
        {
            sb.AppendLine($"  {ep.Signature ?? ep.Fqn}");
            if (ep.FilePath is not null) sb.AppendLine($"    File: {ep.FilePath}:{ep.StartLine}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Trace data flow from API endpoint through service layer to database")]
    public static async Task<string> GetDataFlow(
        [Description("API route to trace (e.g., '/api/tasks')")] string? endpointRoute = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        if (string.IsNullOrWhiteSpace(endpointRoute))
            return "Missing required parameter 'endpointRoute'. Pass an API route to trace. " +
                   "Example: GetDataFlow(endpointRoute: '/api/chat/completion')";

        var rawResults = await graphStore.QueryDataFlowAsync(endpointRoute);
        // R23 N3+N4: drop System.String.IsNullOrEmpty, ILogger.LogWarning, etc.
        // — these are framework noise, not "data flow downstream of an endpoint".
        var results = GraphTraversalTools.StripFrameworkNoise(rawResults);
        if (results.Count == 0)
            return $"No data flow found for endpoint '{endpointRoute}'.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Data Flow for: {endpointRoute}");
        sb.AppendLine($"Downstream methods ({results.Count}):");
        sb.AppendLine(compressor.Compress(results));
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Find where a configuration key is used in code. " +
        "Traces ReadsConfig edges from code symbols to config keys (appsettings.json, .env, IConfiguration, IOptions<T>, process.env, os.environ, System.getenv, os.Getenv, env::var, $_ENV).")]
    public static async Task<string> GetConfigUsage(
        [Description("Configuration key to search for (e.g., 'ConnectionStrings', 'DATABASE_URL', 'Logging'). Omit to list all config keys.")] string? configKey = null,
        [Description("Repository name to scope results (optional)")] string? repository = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var (repoId, guard) = await ResolveScopeAsync(repository, configKey is not null, repoStore, "config usage");
        if (guard is not null) return guard;
        var results = await graphStore.QueryConfigUsageAsync(configKey, repoId);

        if (results.Count == 0)
            return configKey is not null
                ? $"No config usage found for key '{configKey}'. The project may not use this config key, or it hasn't been indexed yet."
                : "No configuration keys found. Index a project with config files (appsettings.json, .env) first.";

        var sb = new System.Text.StringBuilder();

        // Split into config definitions and code readers
        var configKeys = results.Where(r => r.Kind == "config_key").ToList();
        var readers = results.Where(r => r.Kind != "config_key").ToList();

        if (configKeys.Count > 0)
        {
            sb.AppendLine($"Config Keys ({configKeys.Count}):");
            foreach (var ck in configKeys)
            {
                sb.AppendLine($"  {ck.Fqn}");
                if (ck.FilePath is not null) sb.AppendLine($"    Defined in: {ck.FilePath}:{ck.StartLine}");
            }
            sb.AppendLine();
        }

        if (readers.Count > 0)
        {
            sb.AppendLine($"Code Reading This Config ({readers.Count}):");
            sb.AppendLine(compressor.Compress(readers));
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Audit NuGet packages — list dependencies with versions")]
    public static string GetNuGetAudit(
        [Description("Path to project or directory to audit")] string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";

        // Agent-uploaded projects exist only as graph metadata — the actual
        // .csproj files are NOT on the server filesystem. Explain the limitation
        // instead of throwing DirectoryNotFoundException (R20 fix).
        if (!Directory.Exists(path))
            return $"Path '{path}' does not exist on the server. " +
                   "Note: agent-uploaded projects (/workspace/_agent/...) contain only " +
                   "graph metadata, not source files. NuGet audit requires access to " +
                   ".csproj files on the server filesystem. Run audit on the local " +
                   "checkout of that project, or point at a path inside /workspace " +
                   "that contains source code.";

        var analyzer = new NuGetAuditAnalyzer();
        var packages = analyzer.AnalyzeDirectory(path);

        if (packages.Count == 0)
            return $"No NuGet packages found in '{path}'. " +
                   "The directory exists but contains no .csproj files.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"NuGet Dependencies ({packages.Count}):");
        sb.AppendLine();

        var grouped = packages.GroupBy(p => p.ProjectName ?? "Unknown");
        foreach (var group in grouped)
        {
            sb.AppendLine($"  [{group.Key}]");
            foreach (var pkg in group.OrderBy(p => p.PackageId))
            {
                sb.AppendLine($"    {pkg.PackageId} {pkg.Version}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Audit project dependencies across ecosystems — npm (package.json), pip " +
        "(requirements.txt / pyproject.toml), Go (go.mod), Rust (Cargo.toml), PHP (composer.json), " +
        "Maven (pom.xml) and .NET (.csproj). Lists declared dependencies with versions, grouped by " +
        "manifest. Generalizes get_nuget_audit to every supported language.")]
    public static string GetDependencyAudit(
        [Description("Path to project or directory to audit. Defaults to the server workspace.")] string? path = null,
        [Description("Filter to one ecosystem (optional): npm | pip | go | cargo | composer | maven | nuget")] string? ecosystem = null)
    {
        path ??= Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";

        // Agent-uploaded projects exist only as graph metadata — manifest files are NOT on the
        // server filesystem (same limitation as get_nuget_audit). Explain instead of throwing.
        if (!Directory.Exists(path))
            return $"Path '{path}' does not exist on the server. " +
                   "Note: agent-uploaded projects (/workspace/_agent/...) contain only graph " +
                   "metadata, not source files. Dependency audit reads manifest files " +
                   "(package.json, requirements.txt, go.mod, Cargo.toml, composer.json, pom.xml, " +
                   ".csproj) from disk. Run it on a local checkout, or point at a path inside " +
                   "/workspace that contains source.";

        var analyzer = new PackageManifestAnalyzer();
        var deps = analyzer.AnalyzeDirectory(path);

        if (ecosystem is not null)
            deps = deps
                .Where(d => d.Ecosystem.Equals(ecosystem, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (deps.Count == 0)
            return ecosystem is not null
                ? $"No '{ecosystem}' dependencies found in '{path}'."
                : $"No dependencies found in '{path}'. " +
                  "No recognized manifest (package.json, requirements.txt, pyproject.toml, go.mod, " +
                  "Cargo.toml, composer.json, pom.xml, .csproj) was present.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Dependencies ({deps.Count}):");
        sb.AppendLine();

        foreach (var group in deps.GroupBy(d => d.Manifest).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var eco = group.Select(d => d.Ecosystem).Distinct().FirstOrDefault() ?? "?";
            sb.AppendLine($"  [{group.Key}] ({eco})");
            foreach (var dep in group.OrderBy(d => d.IsDev).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var devTag = dep.IsDev ? "  (dev)" : "";
                sb.AppendLine($"    {dep.Name} {dep.Version}{devTag}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Get architecture overview — modules, layers, key components")]
    public static async Task<string> GetArchitecture(
        [Description("Repository name to focus on (optional — omit for all)")] string? repository = null,
        IGraphStore graphStore = default!,
        IRepositoryStore repoStore = default!)
    {
        var (repoId, guard) = await ResolveScopeAsync(repository, hasContentFilter: false, repoStore, "the architecture overview");
        if (guard is not null) return guard;

        var allRepos = await repoStore.ListAsync();
        var repos = repoId is { } rid ? allRepos.Where(r => r.Id == rid).ToList() : allRepos;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Architecture Overview:");
        sb.AppendLine();

        sb.AppendLine($"Repositories ({repos.Count}):");
        foreach (var repo in repos)
            sb.AppendLine($"  - {repo.Name} (indexed: {repo.LastIndexed?.ToString("yyyy-MM-dd HH:mm") ?? "never"})");
        sb.AppendLine();

        // DI Registrations — grouped by module
        var diRegs = await graphStore.QueryDiRegistrationsAsync(repoId: repoId);
        if (diRegs.Count > 0)
        {
            sb.AppendLine($"DI Registrations ({diRegs.Count}):");
            var grouped = diRegs.GroupBy(r => ExtractModule(r.FilePath));
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                sb.AppendLine($"  [{group.Key}]");
                foreach (var reg in group)
                    sb.AppendLine($"    {reg.Fqn}");
            }
            sb.AppendLine();
        }

        // API Endpoints — with routes
        var endpoints = await graphStore.QueryApiEndpointsAsync(repoId: repoId);
        if (endpoints.Count > 0)
        {
            sb.AppendLine($"API Endpoints ({endpoints.Count}):");
            foreach (var ep in endpoints)
                sb.AppendLine($"  {ep.Signature ?? ep.Fqn}  →  {ep.FilePath}:{ep.StartLine}");
            sb.AppendLine();
        }

        // EF Core Entities
        var entities = await graphStore.QueryEntityMappingsAsync(repoId: repoId);
        if (entities.Count > 0)
        {
            sb.AppendLine($"EF Core Entities ({entities.Count}):");
            foreach (var e in entities)
                sb.AppendLine($"  {e.Name} ({e.Fqn})");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Get the ASP.NET middleware pipeline order. Shows the sequence of app.UseXxx() middleware calls " +
        "and their execution order.")]
    public static async Task<string> GetMiddlewarePipeline(
        [Description("Repository name to scan")] string? repository = null,
        IGraphStore graphStore = default!,
        IRepositoryStore repoStore = default!)
    {
        // Query middleware nodes from graph (kind = "middleware")
        var (repoId, guard) = await ResolveScopeAsync(repository, hasContentFilter: false, repoStore, "the middleware pipeline");
        if (guard is not null) return guard;

        var allRepos = await repoStore.ListAsync();
        var repos = repoId is { } rid ? allRepos.Where(r => r.Id == rid).ToList() : allRepos;

        if (repos.Count == 0)
            return "No repositories found. Index a project first.";

        var allMiddlewares = new List<SearchResult>();
        foreach (var repo in repos)
        {
            var overview = await graphStore.GetGraphOverviewAsync(repo.Id, 100, ["middleware"]);
            foreach (var node in overview.Nodes)
                allMiddlewares.Add(new Core.Models.SearchResult(node.Fqn, node.Name, node.Kind, node.Signature, node.FilePath, node.StartLine, 1.0, "Graph:Middleware"));
        }

        if (allMiddlewares.Count == 0)
            return "No middleware pipeline found. Index an ASP.NET project with app.UseXxx() calls first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Middleware Pipeline ({allMiddlewares.Count} stages):");
        sb.AppendLine("Request flows through middleware in this order:");
        sb.AppendLine();

        // Sort by FQN which contains the middleware name
        var sorted = allMiddlewares.OrderBy(m => m.StartLine ?? 0).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var m = sorted[i];
            sb.AppendLine($"  {i + 1}. {m.Name}");
            if (m.FilePath is not null) sb.AppendLine($"     File: {m.FilePath}:{m.StartLine}");
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Detect circular dependencies between classes. Finds classes that depend on each other " +
        "in a cycle (A → B → C → A), which can indicate tight coupling.")]
    public static async Task<string> GetCircularDependencies(
        [Description("Repository name to scan")] string repository,
        IGraphStore graphStore = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        if (repoId is null)
            return $"Repository '{repository}' not found.";

        var cycles = await graphStore.QueryCircularDependenciesAsync(repoId.Value);
        if (cycles.Count == 0)
            return $"No circular dependencies found in '{repository}'. The dependency graph is acyclic.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Circular Dependencies in '{repository}' ({cycles.Count} cycles):");
        sb.AppendLine();

        for (var i = 0; i < cycles.Count; i++)
        {
            sb.AppendLine($"  Cycle {i + 1}: {string.Join(" → ", cycles[i])}");
        }

        sb.AppendLine();
        sb.AppendLine("Consider breaking these cycles by introducing interfaces or restructuring dependencies.");
        return sb.ToString();
    }

    private static string ExtractModule(string? filePath)
    {
        if (filePath is null) return "Unknown";
        // Extract module name from path like /workspace/src/CortexPlexus.Graph/...
        var parts = filePath.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Contains('.') && !part.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return part;
        }
        return Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "Unknown";
    }
}
