using CortexPlexus.Agent;
using CortexPlexus.Core;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("CortexPlexus.Agent");

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "watch":
        return await RunWatch(args, logger);
    case "index":
        return await RunIndex(args, logger);
    case "status":
        return RunStatus();
    case "stop":
        return RunStop(args);
    case "update":
        return await RunUpdate(args, logger);
    case "version":
        Console.WriteLine($"cortexplexus-agent v{AgentInfo.Version}");
        return 0;
    default:
        PrintUsage();
        return 1;
}

async Task<int> RunWatch(string[] args, ILogger log)
{
    var (path, server, name) = ParseArgs(args);
    if (path is null)
    {
        Console.Error.WriteLine("Usage: cortexplexus-agent watch <path> --server <url> --name <name>");
        return 1;
    }

    path = Path.GetFullPath(path);
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"Error: directory '{path}' does not exist.");
        return 1;
    }

    name ??= Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    server ??= "http://localhost:8080";

    // Write PID file for status/stop commands
    PidManager.WritePidFile(name, path, server);

    log.LogInformation("CortexPlexus Agent v{Version}", AgentInfo.Version);
    log.LogInformation("Project: {Name} ({Path})", name, path);
    log.LogInformation("Server: {Server}", server);

    // Initial full index
    var indexer = new LocalIndexer(server, name, log);
    await indexer.IndexAsync(path);

    // Start watching for changes
    log.LogInformation("Watching for file changes... (Ctrl+C to stop)");
    using var watcher = new ProjectFileWatcher(path, log);
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    // Also check for updates periodically
    var updater = new AgentUpdater(server, log);

    // Heartbeats: one now (right after the initial index), one after every flush
    // outcome, and one every 5 minutes while idle. The server renders these in
    // list_repositories as Watch: ACTIVE / FAILING / DISCONNECTED, so a watch that
    // is alive but not uploading is visible instead of silently serving stale data.
    await indexer.PostHeartbeatAsync(watcher.ConsecutiveFlushFailures, lastError: null, cts.Token);

    watcher.FlushCompleted += error =>
        _ = indexer.PostHeartbeatAsync(watcher.ConsecutiveFlushFailures, error?.Message, cts.Token);

    var heartbeatLoop = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), cts.Token); }
            catch (OperationCanceledException) { break; }
            await indexer.PostHeartbeatAsync(watcher.ConsecutiveFlushFailures, lastError: null, cts.Token);
        }
    });

    try
    {
        await watcher.WatchAsync(async changedFiles =>
        {
            log.LogInformation("Detected {Count} changed files, indexing...", changedFiles.Count);
            await indexer.IndexFilesAsync(path, changedFiles);
        }, cts.Token);
    }
    catch (WatchFailedException ex)
    {
        // Repeated flush failures — exit non-zero so systemd (Restart=on-failure /
        // always) recycles the agent instead of leaving a zombie watch running.
        log.LogCritical(ex, "Watch session failed: {Message}", ex.Message);
        await indexer.PostHeartbeatAsync(watcher.ConsecutiveFlushFailures, ex.InnerException?.Message ?? ex.Message);
        cts.Cancel();
        PidManager.RemovePidFile(name);
        return 2;
    }

    cts.Cancel();
    await heartbeatLoop;
    PidManager.RemovePidFile(name);
    return 0;
}

async Task<int> RunIndex(string[] args, ILogger log)
{
    var (path, server, name) = ParseArgs(args);
    if (path is null)
    {
        Console.Error.WriteLine("Usage: cortexplexus-agent index <path> --server <url> --name <name>");
        return 1;
    }

    path = Path.GetFullPath(path);
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"Error: directory '{path}' does not exist.");
        return 1;
    }

    name ??= Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    server ??= "http://localhost:8080";

    log.LogInformation("CortexPlexus Agent v{Version} — one-time index", AgentInfo.Version);
    var indexer = new LocalIndexer(server, name, log);
    await indexer.IndexAsync(path);
    return 0;
}

int RunStatus()
{
    var agents = PidManager.ListRunningAgents();
    if (agents.Count == 0)
    {
        Console.WriteLine("No CortexPlexus agents running.");
        return 0;
    }

    Console.WriteLine($"{"Name",-25} {"PID",-8} {"Path",-40} {"Server"}");
    Console.WriteLine(new string('-', 100));
    foreach (var agent in agents)
    {
        Console.WriteLine($"{agent.Name,-25} {agent.Pid,-8} {agent.Path,-40} {agent.Server}");
    }
    return 0;
}

int RunStop(string[] args)
{
    var stopAll = args.Contains("--all");
    var name = GetArgValue(args, "--name");

    if (!stopAll && name is null)
    {
        Console.Error.WriteLine("Usage: cortexplexus-agent stop --all | --name <name>");
        return 1;
    }

    var stopped = PidManager.StopAgents(stopAll ? null : name);
    Console.WriteLine($"Stopped {stopped} agent(s).");
    return 0;
}

async Task<int> RunUpdate(string[] args, ILogger log)
{
    var server = GetArgValue(args, "--server") ?? "http://localhost:8080";
    var updater = new AgentUpdater(server, log);
    var updated = await updater.CheckAndUpdateAsync();
    if (!updated)
        Console.WriteLine("Already up to date.");
    return 0;
}

(string? path, string? server, string? name) ParseArgs(string[] args)
{
    string? path = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
    string? server = GetArgValue(args, "--server");
    string? name = GetArgValue(args, "--name");
    return (path, server, name);
}

string? GetArgValue(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

void PrintUsage()
{
    Console.WriteLine($"""
        CortexPlexus Agent v{AgentInfo.Version} — Local Code Indexer

        Usage:
          cortexplexus-agent watch <path> --server <url> [--name <name>]
              Start watching a project for changes and auto-index.
              Source code stays on your machine — only metadata is sent to server.

          cortexplexus-agent index <path> --server <url> [--name <name>]
              One-time index of a project directory.

          cortexplexus-agent status
              Show running agent instances.

          cortexplexus-agent stop --all | --name <name>
              Stop running agent(s).

          cortexplexus-agent update --server <url>
              Update agent to latest version from server.

          cortexplexus-agent version
              Show version.
        """);
}
