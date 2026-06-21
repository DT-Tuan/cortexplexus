using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Services;
using CortexPlexus.Embedding;
using CortexPlexus.Graph;
using CortexPlexus.Memory;
using CortexPlexus.Parsing;
using CortexPlexus.Search;
using CortexPlexus.App.Export;
using CortexPlexus.App.Indexing;
using CortexPlexus.App.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Threading.Channels;
using CortexPlexus.Core.Models;
using CortexPlexus.App.Api;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/cortexplexus-.log", rollingInterval: RollingInterval.Day)
    .MinimumLevel.Information()
    .CreateLogger();

try
{
    var command = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";

    switch (command)
    {
        case "init":
            await RunInit(args);
            break;
        case "index":
            await RunIndex(args);
            break;
        case "serve":
            await RunServe(args);
            break;
        case "status":
            await RunStatus(args);
            break;
        case "search":
            await RunSearch(args);
            break;
        case "export":
            await RunExport(args);
            break;
        default:
            PrintUsage();
            break;
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "CortexPlexus terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// --- Command Implementations ---

async Task RunInit(string[] args)
{
    Log.Information("Initializing CortexPlexus database schema...");
    using var services = BuildServices(args);
    var graphStore = services.GetRequiredService<IGraphStore>();
    await graphStore.InitializeSchemaAsync();
    var memoryStore = services.GetRequiredService<IAgentMemoryStore>();
    await memoryStore.InitializeSchemaAsync();
    Log.Information("Schema initialized successfully.");
}

async Task RunIndex(string[] args)
{
    var path = args.Length > 1 ? args[1] : ".";
    path = Path.GetFullPath(path);

    // 1 path = 1 repository (like GitHub)
    // All .sln/.csproj/.ts/.py inside are parsed as part of this repo
    Log.Information("Indexing repository at {Path}...", path);

    using var services = BuildServices(args);
    var pipeline = services.GetRequiredService<IndexingPipeline>();
    var stats = await pipeline.IndexAsync(path);

    Log.Information("Indexing complete: {Files} files, {Symbols} symbols, {Rels} relationships in {Duration:F1}s",
        stats.FilesProcessed, stats.SymbolCount, stats.RelationshipCount, stats.Duration.TotalSeconds);
}

async Task RunServe(string[] args)
{
    var useHttp = args.Contains("--http");
    var watch = args.Contains("--watch");
    var watchPath = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    ConfigureServices(builder.Services, args);

    if (watch)
    {
        builder.Services.AddHostedService<FileWatcherService>();
    }
    builder.Services.AddHostedService<IndexingWorker>();

    // MCP Server
    builder.Services.AddMcpServer(options =>
        {
            // Sent to every client on initialize. Counters the ".NET-only" first
            // impression: the code graph is multi-language, so agents in Python/TS/Go
            // repos should USE the graph tools instead of falling back to grep.
            options.ServerInstructions =
                "CortexPlexus is a MULTI-LANGUAGE code-intelligence graph + semantic search, " +
                "not .NET-only. Languages: C# (Roslyn, deepest), and Python, TypeScript, " +
                "JavaScript, Java, Go, Rust, PHP (tree-sitter). For ANY indexed repo in these " +
                "languages, prefer the graph/semantic tools over manual grep for structural " +
                "questions: search_code (exact name), semantic_search (concept), get_callers / " +
                "get_callees / get_impact_analysis (relationships). ALWAYS pass repository:\"<name>\" " +
                "(see list_repositories) to scope. Framework-aware tools now span multiple stacks: " +
                "get_api_endpoints (ASP.NET + Python FastAPI/Flask), get_di_registrations " +
                "(ASP.NET + Java Spring + NestJS), get_dependency_audit (npm/pip/go/cargo/composer/" +
                "maven/.NET), get_config_usage (8 languages). Still C#/.NET-only: get_entity_mapping, " +
                "get_middleware_pipeline, get_nuget_audit (use get_dependency_audit for other " +
                "ecosystems). A shared cross-project memory " +
                "store (recall_memory / save_memory, scope:\"all\") spans every indexed repo regardless " +
                "of language. Call get_help once for the full tool list and language support matrix.";
        })
        .WithHttpTransport()
        .WithToolsFromAssembly(typeof(Program).Assembly);

    var app = builder.Build();

    // Initialize schema on startup
    using (var scope = app.Services.CreateScope())
    {
        var graphStore = scope.ServiceProvider.GetRequiredService<IGraphStore>();
        await graphStore.InitializeSchemaAsync();
        var memoryStore = scope.ServiceProvider.GetRequiredService<IAgentMemoryStore>();
        await memoryStore.InitializeSchemaAsync();
    }

    // Web UI: static files + REST API
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Redirect("/index.html"));
    app.MapGraphApi();
    app.MapAgentApi();

    // MCP at /mcp (not root, to avoid conflict with web UI)
    app.MapMcp("/mcp");

    Log.Information("CortexPlexus MCP server starting on {Transport}...", useHttp ? "HTTP :8080" : "stdio");
    if (watch) Log.Information("File watcher enabled");

    await app.RunAsync();
}

async Task RunStatus(string[] args)
{
    using var services = BuildServices(args);
    var repoStore = services.GetRequiredService<IRepositoryStore>();
    var repos = await repoStore.ListAsync();

    if (repos.Count == 0)
    {
        Console.WriteLine("No repositories indexed yet. Run 'cortexplexus index <path>' first.");
        return;
    }

    Console.WriteLine($"{"Name",-30} {"Last Indexed",-25} {"Path"}");
    Console.WriteLine(new string('-', 80));
    foreach (var repo in repos)
    {
        var lastIndexed = repo.LastIndexed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never";
        Console.WriteLine($"{repo.Name,-30} {lastIndexed,-25} {repo.Path}");
    }
}

async Task RunSearch(string[] args)
{
    // Parse flags: --bm25, --vector, --expand
    var flags = args.Where(a => a.StartsWith("--")).Select(a => a.ToLower()).ToHashSet();
    var queryParts = args.Skip(1).Where(a => !a.StartsWith("--"));
    var query = string.Join(' ', queryParts);

    if (string.IsNullOrWhiteSpace(query))
    {
        Console.WriteLine("Usage: cortexplexus search [--bm25|--vector|--expand] <query>");
        return;
    }

    var searchType = flags.Contains("--bm25") ? SearchType.Bm25
        : flags.Contains("--vector") ? SearchType.Vector
        : SearchType.Hybrid;
    var expand = flags.Contains("--expand");

    using var services = BuildServices(args);
    var router = services.GetRequiredService<HybridQueryRouter>();
    var compressor = services.GetRequiredService<ContextCompressor>();

    var results = await router.SearchAsync(new SearchRequest(query, searchType, Expand: expand));
    if (results.Count == 0)
    {
        Console.WriteLine("No results found.");
        return;
    }

    Console.WriteLine(compressor.Compress(results));
}

async Task RunExport(string[] args)
{
    var outputPath = args.Length > 1 ? args[1] : "./docs/generated";
    Log.Information("Exporting knowledge to {Path}...", outputPath);
    using var services = BuildServices(args);
    var exporter = services.GetRequiredService<KnowledgeExporter>();
    await exporter.ExportAsync(outputPath);
    Log.Information("Export complete.");
}

void PrintUsage()
{
    Console.WriteLine("""
        CortexPlexus — Code Intelligence Platform

        Usage:
          cortexplexus init                   Initialize database schema
          cortexplexus index <path>           Index a repository (all languages)
          cortexplexus serve [--http] [--watch]  Start MCP server
          cortexplexus status                 Show indexed repositories
          cortexplexus search <query>         Search (hybrid: vector + BM25)
          cortexplexus search --bm25 <query> Search by keyword only (no embedding)
          cortexplexus search --vector <query> Search by semantic similarity
          cortexplexus search --expand <query> Search with query expansion (needs Ollama)
          cortexplexus export [path]          Export knowledge as Markdown (default: ./docs/generated)
        """);
}

// --- DI Setup ---

ServiceProvider BuildServices(string[] args)
{
    var services = new ServiceCollection();
    ConfigureServices(services, args);
    return services.BuildServiceProvider();
}

void ConfigureServices(IServiceCollection services, string[] args)
{
    services.AddLogging(b => b.AddSerilog());

    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? "Host=localhost;Database=cortexplexus;Username=postgres;Password=cortexplexus";

    services.AddCortexPlexusGraph(connectionString);
    services.AddCortexPlexusParsing();
    services.AddCortexPlexusSearch();

    // Memory system (opt-in; default disabled — see ADR-013, docs/MEMORY-SYSTEM.md)
    services.AddCortexPlexusMemory(options =>
    {
        options.Enabled = string.Equals(
            Environment.GetEnvironmentVariable("Memory__Enabled"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(Environment.GetEnvironmentVariable("Memory__ReapIntervalHours"), out var reap))
            options.ReapIntervalHours = reap;
        if (int.TryParse(Environment.GetEnvironmentVariable("Memory__MaxMemoriesPerScope"), out var max))
            options.MaxMemoriesPerScope = max;
        if (double.TryParse(Environment.GetEnvironmentVariable("Memory__DefaultImportance"), out var imp))
            options.DefaultImportance = imp;
    });

    var ollamaBaseUrl = Environment.GetEnvironmentVariable("Embedding__OllamaBaseUrl") ?? "http://localhost:11434";
    var ollamaModel = Environment.GetEnvironmentVariable("Embedding__OllamaModel") ?? "nomic-embed-text";

    // Query expansion (HyDE + multi-query via Ollama)
    var expandEnabled = Environment.GetEnvironmentVariable("QueryExpansion__Enabled") ?? "false";
    var expandModel = Environment.GetEnvironmentVariable("QueryExpansion__Model") ?? "phi3:mini";
    services.AddQueryExpansion(options =>
    {
        options.Enabled = expandEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);
        options.Provider = "ollama";
        options.OllamaBaseUrl = ollamaBaseUrl;
        options.OllamaModel = expandModel;
    });

    var embeddingProvider = Environment.GetEnvironmentVariable("Embedding__Provider") ?? "ollama";
    var embeddingApiKey = Environment.GetEnvironmentVariable("Embedding__ApiKey") ?? "";

    services.AddCortexPlexusEmbedding(options =>
    {
        options.Provider = embeddingProvider;
        options.ApiKey = embeddingApiKey;
        options.Dimensions = 768;
        options.OllamaBaseUrl = ollamaBaseUrl;
        options.OllamaModel = ollamaModel;
    });

    // AI summary generation (optional, uses LLM)
    var summaryEnabled = Environment.GetEnvironmentVariable("Summary__Enabled") ?? "false";
    var summaryProvider = Environment.GetEnvironmentVariable("Summary__Provider") ?? "ollama";
    var summaryModel = Environment.GetEnvironmentVariable("Summary__Model") ?? "phi3:mini";
    services.AddSummaryGeneration(options =>
    {
        options.Enabled = summaryEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);
        options.Provider = summaryProvider;
        options.OllamaBaseUrl = ollamaBaseUrl;
        options.OllamaModel = summaryModel;
        options.ApiKey = Environment.GetEnvironmentVariable("Summary__ApiKey");
        options.ApiBaseUrl = Environment.GetEnvironmentVariable("Summary__ApiBaseUrl");
        options.Model = Environment.GetEnvironmentVariable("Summary__CloudModel");
    });

    services.AddSingleton<ISecretsScanner, BasicSecretsScanner>();
    services.AddSingleton(Channel.CreateUnbounded<IndexingJob>());
    services.AddScoped<IndexingPipeline>();
    services.AddScoped<KnowledgeExporter>();
    services.AddMemoryCache();

    // Needed so MCP tool handlers (e.g. ActivateAgent) can look at the incoming
    // HTTP request and auto-detect the URL the client used to reach us — avoids
    // handing back a hard-coded `localhost:8080` to an agent that connected from
    // a different host.
    services.AddHttpContextAccessor();
}
