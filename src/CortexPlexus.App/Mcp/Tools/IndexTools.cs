using System.ComponentModel;
using CortexPlexus.App.Indexing;
using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class IndexTools
{
    [McpServerTool, Description(
        "Index a local project into the knowledge graph. " +
        "Use this when the project directory is accessible on the CortexPlexus server filesystem. " +
        "Note: C# projects (.sln/.csproj) require Local Agent — use ActivateAgent() instead. " +
        "TypeScript, JavaScript, Python, Markdown are indexed directly on server.")]
    public static async Task<string> IndexFromLocal(
        [Description("Absolute path to project directory on the server (e.g., /workspace, /opt/projects/MyApp)")] string path,
        IServiceScopeFactory scopeFactory = default!,
        IRepositoryStore repoStore = default!,
        // Auto-bound by the SDK: if the client passed a progressToken, reports are forwarded
        // as `notifications/progress`; otherwise the instance silently no-ops.
        IProgress<ProgressNotificationValue> progress = default!,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";

        if (!Directory.Exists(path))
            return $"Error: directory '{path}' does not exist on the server.";

        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();

        var stats = await pipeline.IndexAsync(path, ct, progress);
        var detectedName = IndexingPipeline.DetectProjectName(path);

        return FormatResult(detectedName, path, stats);
    }

    [McpServerTool, Description(
        "Index a project from a Git repository URL. " +
        "CortexPlexus will clone (or pull if already cloned) the repo and index it. " +
        "Note: C# projects (.sln/.csproj) require Local Agent — use ActivateAgent() instead. " +
        "TypeScript, JavaScript, Python, Markdown are indexed directly on server.")]
    public static async Task<string> IndexFromGit(
        [Description("Git repository URL (HTTPS). Example: https://github.com/org/repo.git")] string url,
        [Description("Branch to clone (default: main)")] string branch = "main",
        [Description("Optional project name (default: extracted from URL)")] string? name = null,
        IServiceScopeFactory scopeFactory = default!,
        IProgress<ProgressNotificationValue> progress = default!,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: Git URL is required.";

        // Validate URL to prevent command injection
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
            return "Error: Invalid Git URL. Only HTTPS URLs are supported.";

        // Sanitize branch name
        if (branch.Contains(' ') || branch.Contains(';') || branch.Contains('&'))
            return "Error: Invalid branch name.";

        var repoName = name ?? url.Split('/').LastOrDefault()?.Replace(".git", "") ?? "repo";
        var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
        var repoPath = Path.Combine(workspacePath, "_git", repoName);

        // Clone or pull
        try
        {
            if (Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                var pull = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git", Arguments = "pull",
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false
                });
                if (pull is not null) await pull.WaitForExitAsync();
            }
            else
            {
                var clone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --depth 1 --branch {branch} {url} {repoPath}",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false
                });
                if (clone is not null) await clone.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            return $"Error: Git operation failed — {ex.Message}";
        }

        if (!Directory.Exists(repoPath))
            return $"Error: Git clone failed — directory not created.";

        // Index
        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();
        var stats = await pipeline.IndexAsync(repoPath, ct, progress);

        return FormatResult(repoName, repoPath, stats, url, branch);
    }

    [McpServerTool, Description(
        "Delete an indexed repository and ALL its data — graph nodes/edges, code symbols, " +
        "embeddings, and file hashes. Irreversible: the project must be re-indexed to restore it. " +
        "Use to clean up test/stale/duplicate repositories (e.g. ones created by mis-indexing). " +
        "Requires the EXACT repository name from list_repositories (no fuzzy match, to avoid " +
        "deleting the wrong repo).")]
    public static async Task<string> DeleteRepository(
        [Description("Exact repository name to delete (must match list_repositories).")] string name,
        IGraphStore graphStore = default!,
        IRepositoryStore repoStore = default!,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Error: repository name is required.";

        // Exact (case-insensitive) match only — a destructive op must never fuzzy-resolve.
        var repos = await repoStore.ListAsync(ct);
        var matches = repos
            .Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return $"Repository '{name}' not found. Call list_repositories for valid names. " +
                   "Nothing was deleted.";
        if (matches.Count > 1)
            return $"Ambiguous: {matches.Count} repositories are named '{name}'. " +
                   "Nothing was deleted — resolve the duplicate names first.";

        var repo = matches[0];

        // 1) AGE graph vertices (+ their edges) by repo_id; 2) relational rows (cascades
        // code_symbols incl. embeddings + file_hashes).
        await graphStore.DeleteByRepoAsync(repo.Id, ct);
        var symbolCount = await repoStore.DeleteAsync(repo.Id, ct);

        return $"Deleted repository '{repo.Name}' ({repo.Path}). Removed {symbolCount} code symbols " +
               "plus their graph nodes, embeddings, and file hashes. Re-index to restore.";
    }

    private static string FormatResult(string projectName, string path, IndexingStats stats, string? gitUrl = null, string? branch = null)
    {
        var header = gitUrl is not null
            ? $"""
                Indexed from Git:
                  Repository: {projectName}
                  URL: {gitUrl}
                  Branch: {branch}
                  Path: {path}
                """
            : $"""
                Indexed:
                  Project: {projectName}
                  Path: {path}
                """;

        var body = $"""
              Files: {stats.FilesProcessed}
              Symbols: {stats.SymbolCount}
              Relationships: {stats.RelationshipCount}
              Duration: {stats.Duration.TotalSeconds:F1}s
            """;

        if (!stats.CSharpSkipped)
        {
            return $"""
                {header}
                {body}

                You can now use SearchCode, GetCallers, GetDependencies, ExploreTopic, etc.
                Use repository="{projectName}" parameter to scope queries to this project.
                """;
        }

        // C# was skipped — give AI clear, actionable next steps
        return $"""
            {header}
            {body}

            ⚠ C# PROJECTS DETECTED BUT NOT INDEXED (server does not have .NET SDK)
            TypeScript/JavaScript/Python/Markdown were indexed successfully.

            To index the C# code, you MUST use the Local Agent:

            → Step 1: Call ActivateAgent(projectPath: "<local path to this project on your machine>")
            → Step 2: Follow the returned instructions to install + start the agent
            → Step 3: The agent will parse C# locally (Roslyn) and send metadata to server

            The Local Agent runs on your dev machine where .NET SDK is installed.
            Source code never leaves your machine — only metadata (FQNs, signatures, relationships) is sent.
            """;
    }
}
