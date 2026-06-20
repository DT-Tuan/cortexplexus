using CortexPlexus.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CortexPlexus.App.Indexing;

public sealed class IndexingWorker(
    Channel<IndexingJob> jobChannel,
    IServiceScopeFactory scopeFactory,
    ILogger<IndexingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IndexingWorker started. Waiting for jobs...");

        await foreach (var job in jobChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("Processing indexing job: {FilePath} ({Change})", job.FilePath, job.ChangeType);

                using var scope = scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();

                // For file-level changes, re-index the containing project
                var projectPath = FindContainingProject(job.FilePath) ?? Path.GetDirectoryName(job.FilePath)!;
                logger.LogDebug("Resolved project root {Project} for {File}", projectPath, job.FilePath);
                await pipeline.IndexAsync(projectPath, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process indexing job: {FilePath}", job.FilePath);
            }
        }
    }

    // Files that mark a directory as a project/repository root, so a file change re-indexes the
    // WHOLE project as one repository instead of fragmenting it into one repo per sub-directory
    // (the language-agnostic equivalent of the .csproj walk — fixes the repo-splitting bug where
    // a watched Python/TS project registered "app", "tests", "schemas", … as separate repos).
    private static readonly string[] ProjectRootMarkerFiles =
    {
        "pyproject.toml", "setup.py", "setup.cfg",          // Python
        "package.json",                                     // npm / TS / JS
        "go.mod",                                           // Go
        "Cargo.toml",                                       // Rust
        "composer.json",                                    // PHP
        "pom.xml", "build.gradle", "build.gradle.kts",      // Java
    };

    /// <summary>
    /// Walk up from the changed file to the nearest directory that looks like a project/repository
    /// root: a C# solution/project, a VCS root (<c>.git</c>), or a language manifest. The nearest
    /// such ancestor wins, so a sub-package with its own manifest stays its own project while a
    /// plain package directory resolves up to the real project root. Returns null if none is found,
    /// in which case the caller falls back to the file's directory.
    /// </summary>
    internal static string? FindContainingProject(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir is not null)
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0
                || Directory.GetFiles(dir, "*.sln").Length > 0
                || Directory.GetFiles(dir, "*.slnx").Length > 0)
                return dir;

            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;

            foreach (var marker in ProjectRootMarkerFiles)
            {
                if (File.Exists(Path.Combine(dir, marker)))
                    return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
