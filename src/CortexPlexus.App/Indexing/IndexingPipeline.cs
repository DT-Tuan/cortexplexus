using CortexPlexus.Core;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Embedding;
using CortexPlexus.Parsing.Markdown;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace CortexPlexus.App.Indexing;

public sealed class IndexingPipeline(
    ICodeParser parser,
    TreeSitterCodeParser treeSitterParser,
    MarkdownParser markdownParser,
    IGraphStore graphStore,
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    ISecretsScanner secretsScanner,
    IRepositoryStore repositoryStore,
    ISummaryGenerator summaryGenerator,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<IndexingPipeline> logger)
{
    private readonly EmbeddingOptions _embeddingOptions = embeddingOptions.Value;

    public async Task<IndexingStats> IndexAsync(
        string path,
        CancellationToken ct = default,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        // 5 fixed phases: detect → parse → embed → graph → vector.
        // "embed" is typically 60-80% of wall time (Ollama single-thread, 25-30s/batch),
        // so each phase tick in the progress bar is coarse — good enough for the user to
        // know the run isn't hung. Per-batch progress inside embedding is added below.
        const int totalPhases = 5;
        void Report(int phase, string message) => progress?.Report(new ProgressNotificationValue
        {
            Progress = phase,
            Total = totalPhases,
            Message = message
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Report(0, "Detecting project + changed files");

        // Do NOT register a repository for a path that no longer exists or holds no indexable
        // source. The file watcher re-runs IndexAsync on delete events (a directory being
        // removed), which would otherwise register an EMPTY phantom repo — and a phantom that
        // happens to share a real repo's name can hijack repository-scoped resolution
        // (observed: a 0-symbol "CortexPlexus" at /workspace). Bail before touching the repo store.
        if (!Directory.Exists(path))
        {
            logger.LogInformation("Index path no longer exists, skipping (no repo registered): {Path}", path);
            return new IndexingStats(sw.Elapsed, 0, 0, 0);
        }
        if (!HasAnyIndexableFile(path))
        {
            logger.LogInformation("No indexable source under {Path}, skipping (no repo registered).", path);
            return new IndexingStats(sw.Elapsed, 0, 0, 0);
        }

        // Repository = root directory (like a GitHub repo)
        // All sub-projects inside belong to the same repository
        var repoName = DetectProjectName(path);
        var repo = await repositoryStore.GetByPathAsync(path, ct)
            ?? await repositoryStore.RegisterAsync(repoName, path, ct);

        logger.LogInformation("Repository: {Name} ({Path})", repo.Name, repo.Path);

        // Check for changed files (incremental indexing)
        var changedFiles = await GetChangedFilesAsync(path, repo.Id, ct);
        if (changedFiles is { Count: 0 })
        {
            logger.LogInformation("No files changed since last index. Skipping.");
            return new IndexingStats(sw.Elapsed, 0, 0, 0);
        }

        // Parse ALL C# solutions/projects in the repo (not just the first one)
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();
        var totalFiles = 0;
        var totalErrors = 0;

        Report(1, "Parsing source files");
        var solutionPaths = FindAllSolutionsAndProjects(path);

        // C# parsing requires .NET SDK (Roslyn MSBuildWorkspace)
        var hasDotnetSdk = HasDotnetSdk();
        var csharpSkipped = false;

        if (solutionPaths.Count > 0 && !hasDotnetSdk)
        {
            csharpSkipped = true;
            logger.LogWarning(
                "C# projects found ({Count} .sln/.csproj) but .NET SDK is not available. " +
                "Skipping Roslyn parsing. Use CortexPlexus Local Agent to index C# projects — " +
                "it runs on your dev machine where SDK is installed.",
                solutionPaths.Count);
        }

        if (solutionPaths.Count > 0 && hasDotnetSdk)
        {
            // Auto-restore NuGet packages so Roslyn can resolve symbols
            await RestoreNuGetAsync(solutionPaths, ct);

            foreach (var solutionPath in solutionPaths)
            {
                ParseResult csharpResult;
                if (changedFiles is null)
                {
                    logger.LogInformation("Parsing C#: {SolutionPath}...", solutionPath);
                    csharpResult = await parser.ParseSolutionAsync(solutionPath, ct);
                }
                else
                {
                    var csChanged = changedFiles.Where(f => f.EndsWith(".cs")).ToList();
                    if (csChanged.Count > 0)
                    {
                        logger.LogInformation("Incremental C#: {Count} files changed", csChanged.Count);
                        csharpResult = await parser.ParseFilesAsync(csChanged, solutionPath, ct);
                    }
                    else
                    {
                        csharpResult = new ParseResult([], [], TimeSpan.Zero, 0, 0);
                    }
                }
                allSymbols.AddRange(csharpResult.Symbols);
                allRelationships.AddRange(csharpResult.Relationships);
                totalFiles += csharpResult.FilesProcessed;
                totalErrors += csharpResult.ErrorCount;
            }
        }

        // Parse TypeScript/JavaScript/Python (Tree-sitter) — scans entire repo directory
        var tsResult = await treeSitterParser.ParseSolutionAsync(path, ct);
        if (tsResult.Symbols.Count > 0)
        {
            logger.LogInformation("Tree-sitter: {Symbols} symbols, {Rels} relationships from {Files} files",
                tsResult.Symbols.Count, tsResult.Relationships.Count, tsResult.FilesProcessed);
            allSymbols.AddRange(tsResult.Symbols);
            allRelationships.AddRange(tsResult.Relationships);
            totalFiles += tsResult.FilesProcessed;
        }

        // Parse Markdown documents
        var mdResult = markdownParser.ParseDirectory(path);
        if (mdResult.Symbols.Count > 0)
        {
            allSymbols.AddRange(mdResult.Symbols);
            allRelationships.AddRange(mdResult.Relationships);
            totalFiles += mdResult.FilesProcessed;
        }

        totalErrors += tsResult.ErrorCount + mdResult.ErrorCount;
        var parseResult = new ParseResult(allSymbols, allRelationships, sw.Elapsed, totalFiles, totalErrors);
        logger.LogInformation("Parsed {Files} files: {Symbols} symbols, {Rels} relationships, {Errors} errors",
            parseResult.FilesProcessed, parseResult.Symbols.Count, parseResult.Relationships.Count, parseResult.ErrorCount);

        if (parseResult.Symbols.Count == 0)
        {
            logger.LogWarning("No symbols extracted.");
            return new IndexingStats(sw.Elapsed, parseResult.FilesProcessed, 0, 0);
        }

        // Set RepoId on symbols
        var symbols = parseResult.Symbols
            .Select(s => SetRepoId(s, repo.Id))
            .ToList();

        // Generate embeddings for the kinds enumerated in CortexPlexus.Core.EmbeddableKinds.
        // Single source of truth — also used by the agent-upload endpoint and the kind-aware
        // Health metric (ADR 008 / docs/HEALTH-METRICS.md).
        var embeddable = symbols
            .Where(s => EmbeddableKinds.Contains(s.Kind))
            .ToList();

        Report(2, $"Generating embeddings for {embeddable.Count} symbols");
        logger.LogInformation("Generating embeddings for {Count} symbols...", embeddable.Count);

        // Sub-progress: each batch finishing nudges the top-level progress value
        // smoothly from 2 → 3 (fractional). Makes the progress bar move continuously
        // during the longest phase (embedding, typically 60-80% of wall time).
        var embedProgress = progress is null ? null : new Progress<(int Done, int Total)>(p =>
        {
            if (p.Total == 0) return;
            progress.Report(new ProgressNotificationValue
            {
                Progress = 2f + (float)p.Done / p.Total,
                Total = totalPhases,
                Message = $"Embedding batch {p.Done}/{p.Total}"
            });
        });
        var embeddings = await GenerateEmbeddingsAsync(embeddable, ct, embedProgress);

        var embeddedCount = embeddings.Count;
        // Count failures against DISTINCT FQNs: method overloads (the Roslyn FQN omits
        // parameters) and partial classes share an FQN and collapse into one embedding
        // entry, so the raw embeddable.Count over-reports failures (R27-2). embeddings is
        // keyed by FQN, so embeddedCount is already distinct.
        var distinctEmbeddable = embeddable.Select(s => s.Fqn).Distinct().Count();
        var failedEmbeddings = distinctEmbeddable - embeddedCount;
        logger.LogInformation("Generated {Count} embeddings.", embeddedCount);
        if (failedEmbeddings > 0)
            logger.LogWarning("{Count} symbols failed to embed — semantic search will not cover them", failedEmbeddings);

        // Generate AI summaries for methods (optional, requires LLM)
        if (summaryGenerator.IsEnabled)
        {
            symbols = await GenerateSummariesAsync(symbols, ct);
        }

        // Write to stores with verification
        var validSymbols = symbols.Where(s => !string.IsNullOrWhiteSpace(s.Fqn)).ToList();
        var validRelationships = parseResult.Relationships
            .Where(r => !string.IsNullOrWhiteSpace(r.FromFqn) && !string.IsNullOrWhiteSpace(r.ToFqn)).ToList();

        var skippedSymbols = symbols.Count - validSymbols.Count;
        var skippedRels = parseResult.Relationships.Count - validRelationships.Count;
        if (skippedSymbols > 0)
            logger.LogWarning("Skipped {Count} symbols with empty FQN", skippedSymbols);
        if (skippedRels > 0)
            logger.LogWarning("Skipped {Count} relationships with empty FQN", skippedRels);

        Report(3, $"Upserting graph: {validSymbols.Count} nodes, {validRelationships.Count} edges");
        logger.LogInformation("Writing to graph store: {Nodes} nodes, {Edges} edges...",
            validSymbols.Count, validRelationships.Count);
        await graphStore.UpsertNodesAsync(validSymbols, ct);
        await graphStore.UpsertEdgesAsync(validRelationships, ct);

        Report(4, $"Upserting vectors: {embeddings.Count} embeddings");
        logger.LogInformation("Writing to vector + FTS store...");
        await vectorStore.UpsertAsync(validSymbols, embeddings, ct);

        // Update file hashes + last indexed
        await UpdateFileHashesAsync(path, repo.Id, ct);
        await repositoryStore.UpdateLastIndexedAsync(repo.Id, ct);

        Report(5, $"Indexed {parseResult.FilesProcessed} files, {validSymbols.Count} symbols");
        sw.Stop();
        return new IndexingStats(sw.Elapsed, parseResult.FilesProcessed, validSymbols.Count, validRelationships.Count, csharpSkipped);
    }

    /// <summary>
    /// Returns null if no hashes exist (first index), empty list if nothing changed, or list of changed file paths.
    /// </summary>
    private async Task<IReadOnlyList<string>?> GetChangedFilesAsync(string path, Guid repoId, CancellationToken ct)
    {
        var csFiles = IndexableExtensions
            .SelectMany(ext => Directory.GetFiles(path, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}"))
            .ToList();

        var changed = new List<string>();
        var hasAnyHash = false;

        foreach (var file in csFiles)
        {
            var hash = ComputeFileHash(file);
            var isChanged = await repositoryStore.IsFileChangedAsync(file, repoId, hash, ct);

            if (!isChanged)
            {
                hasAnyHash = true; // At least one file has a stored hash
                continue;
            }

            changed.Add(file);
        }

        // If no file has any stored hash, this is first index
        if (!hasAnyHash && changed.Count == csFiles.Count)
            return null;

        return changed;
    }

    private async Task UpdateFileHashesAsync(string path, Guid repoId, CancellationToken ct)
    {
        var allFiles = IndexableExtensions
            .SelectMany(ext => Directory.GetFiles(path, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}"));

        foreach (var file in allFiles)
        {
            var hash = ComputeFileHash(file);
            await repositoryStore.UpdateFileHashAsync(file, repoId, hash, ct);
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        var bytes = System.IO.File.ReadAllBytes(filePath);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private Task<Dictionary<string, float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<CodeSymbol> symbols, CancellationToken ct,
        IProgress<(int Done, int Total)>? batchProgress = null) =>
        EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols,
            embeddingService,
            secretsScanner,
            logger,
            _embeddingOptions.MaxParallelBatches ?? 1,
            ct,
            batchProgress);

    private async Task<List<CodeSymbol>> GenerateSummariesAsync(List<CodeSymbol> symbols, CancellationToken ct)
    {
        var methods = symbols
            .Where(s => s is MethodInfo && s.AiSummary is null)
            .ToList();

        if (methods.Count == 0) return symbols;

        logger.LogInformation("Generating AI summaries for {Count} methods...", methods.Count);

        var items = methods.Select(s =>
        {
            var sig = s is MethodInfo m ? m.Signature : s.Name;
            return (Signature: sig, Documentation: s.Documentation);
        }).ToList();

        var summaries = await summaryGenerator.SummarizeBatchAsync(items, ct);

        var result = new List<CodeSymbol>(symbols.Count);
        var summaryIndex = 0;
        var generated = 0;

        foreach (var symbol in symbols)
        {
            if (symbol is MethodInfo m && m.AiSummary is null)
            {
                var summary = summaries[summaryIndex++];
                result.Add(summary is not null ? m with { AiSummary = summary } : m);
                if (summary is not null) generated++;
            }
            else
            {
                result.Add(symbol);
            }
        }

        logger.LogInformation("Generated {Count} AI summaries.", generated);
        return result;
    }

    // internal (not private) so IndexingPipelineTests can assert every CodeSymbol
    // subtype gets a RepoId. The arms below must cover ALL concrete subtypes:
    // a fall-through to `_ => symbol` leaves RepoId null, which VectorStore then
    // coerces to Guid.Empty → code_symbols_repo_id_fkey violation that takes down
    // the whole 200-row batch (R27-1; was previously masked by the missing
    // FieldInfo/EventInfo/MiddlewareInfo/ConfigKeyInfo arms).
    internal static CodeSymbol SetRepoId(CodeSymbol symbol, Guid repoId) => symbol switch
    {
        ClassInfo c => c with { RepoId = repoId },
        MethodInfo m => m with { RepoId = repoId },
        InterfaceInfo i => i with { RepoId = repoId },
        PropertyInfo p => p with { RepoId = repoId },
        ConstructorInfo c => c with { RepoId = repoId },
        FieldInfo f => f with { RepoId = repoId },
        EventInfo e => e with { RepoId = repoId },
        NamespaceInfo n => n with { RepoId = repoId },
        DbContextInfo d => d with { RepoId = repoId },
        DiRegistrationInfo d => d with { RepoId = repoId },
        ApiEndpointInfo a => a with { RepoId = repoId },
        MiddlewareInfo m => m with { RepoId = repoId },
        ConfigKeyInfo c => c with { RepoId = repoId },
        DocumentSection d => d with { RepoId = repoId },
        _ => symbol
    };

    /// <summary>
    /// Find all .sln/.slnx files in the repo. If none found, find all .csproj files.
    /// Solutions are preferred because they contain all projects in the solution.
    /// Each .sln is parsed once (Roslyn opens all projects inside it).
    /// </summary>
    internal static IReadOnlyList<string> FindAllSolutionsAndProjects(string path)
    {
        if (path.EndsWith(".sln") || path.EndsWith(".slnx") || path.EndsWith(".csproj"))
            return File.Exists(path) ? [path] : [];

        if (!Directory.Exists(path)) return [];

        // Prefer .sln files — each .sln includes all its projects
        var solutions = Directory.GetFiles(path, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(path, "*.slnx", SearchOption.AllDirectories))
            .Where(f => !IsExcludedPath(f))
            .ToList();

        if (solutions.Count > 0)
            return solutions;

        // No solutions — find individual .csproj files
        return Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f))
            .ToList();
    }

    /// <summary>
    /// Run dotnet restore on solutions/projects so Roslyn can resolve all symbols.
    /// Without restore, MSBuildWorkspace produces compilation errors and call graph edges are lost.
    /// </summary>
    private async Task RestoreNuGetAsync(IReadOnlyList<string> solutionPaths, CancellationToken ct)
    {
        foreach (var solutionPath in solutionPaths)
        {
            // Skip if obj directory already has project.assets.json (already restored)
            var dir = Path.GetDirectoryName(solutionPath)!;
            if (solutionPath.EndsWith(".csproj"))
            {
                var assetsFile = Path.Combine(dir, "obj", "project.assets.json");
                if (File.Exists(assetsFile)) continue;
            }
            else
            {
                // For solutions, check if any project has been restored
                var anyRestored = Directory.GetFiles(dir, "project.assets.json", SearchOption.AllDirectories)
                    .Any(f => !IsExcludedPath(f));
                if (anyRestored) continue;
            }

            logger.LogInformation("Restoring NuGet packages for: {Path}", solutionPath);
            try
            {
                // Find dotnet CLI — may not exist in slim Docker images (aspnet-chiseled)
                var dotnetPath = FindDotnetSdk();
                if (dotnetPath is null)
                {
                    logger.LogDebug("dotnet SDK not found, skipping NuGet restore (Roslyn will use bundled MSBuild)");
                    return;
                }

                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dotnetPath,
                    Arguments = $"restore \"{solutionPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process.Start();
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(ct);
                    logger.LogWarning("NuGet restore failed (exit {Code}): {Error}", process.ExitCode, stderr);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "NuGet restore failed for {Path}, Roslyn may report compilation errors", solutionPath);
            }
        }
    }

    private static bool HasDotnetSdk() => FindDotnetSdk() is not null;

    private static string? FindDotnetSdk()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            // If any SDK is listed, dotnet CLI is usable
            return !string.IsNullOrWhiteSpace(output) ? "dotnet" : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsExcludedPath(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}")
            || path.Contains($"{sep}node_modules{sep}");
    }

    // Source/doc extensions the pipeline can index. Single list so the "is there anything to
    // index?" guard, change detection, and hash bookkeeping stay in sync.
    private static readonly string[] IndexableExtensions =
        { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.py", "*.md" };

    /// <summary>
    /// True if the directory tree contains at least one indexable source/doc file (ignoring
    /// build/dependency dirs). Used to avoid registering an empty phantom repository for a
    /// missing/emptied path — enumerates lazily and returns on the first match.
    /// </summary>
    internal static bool HasAnyIndexableFile(string path)
    {
        if (!Directory.Exists(path)) return false;

        var sep = Path.DirectorySeparatorChar;
        foreach (var ext in IndexableExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(path, ext, SearchOption.AllDirectories))
            {
                if (!file.Contains($"{sep}bin{sep}") && !file.Contains($"{sep}obj{sep}")
                    && !file.Contains($"{sep}node_modules{sep}") && !file.Contains($"{sep}__pycache__{sep}")
                    && !file.Contains($"{sep}.venv{sep}"))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Smart project name detection. Priority:
    /// 1. .sln/.slnx file name (C# solution)
    /// 2. package.json "name" field (Node.js)
    /// 3. pyproject.toml [project] name (Python)
    /// 4. Single .csproj RootNamespace or project file name
    /// 5. Git remote origin repo name
    /// 6. Fallback: folder name
    /// </summary>
    public static string DetectProjectName(string path)
    {
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(path))
            return Path.GetFileName(path) ?? "unknown";

        // 1. .sln / .slnx → best for C# projects
        var sln = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(path, "*.slnx", SearchOption.TopDirectoryOnly))
            .FirstOrDefault();
        if (sln is not null)
            return Path.GetFileNameWithoutExtension(sln);

        // 2. package.json → Node.js / TypeScript
        var packageJson = Path.Combine(path, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                var json = File.ReadAllText(packageJson);
                var nameMatch = System.Text.RegularExpressions.Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (nameMatch.Success)
                {
                    var name = nameMatch.Groups[1].Value;
                    // Strip org scope: "@org/my-app" → "my-app"
                    if (name.Contains('/'))
                        name = name[(name.LastIndexOf('/') + 1)..];
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch { /* ignore parse errors */ }
        }

        // 3. pyproject.toml → Python
        var pyproject = Path.Combine(path, "pyproject.toml");
        if (File.Exists(pyproject))
        {
            try
            {
                var toml = File.ReadAllText(pyproject);
                var nameMatch = System.Text.RegularExpressions.Regex.Match(toml, "name\\s*=\\s*\"([^\"]+)\"");
                if (nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value))
                    return nameMatch.Groups[1].Value;
            }
            catch { /* ignore */ }
        }

        // 4. Single .csproj at root → use project file name
        var csproj = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csproj.Length == 1)
            return Path.GetFileNameWithoutExtension(csproj[0]);

        // 5. Git remote origin → repo name
        try
        {
            var gitConfig = Path.Combine(path, ".git", "config");
            if (File.Exists(gitConfig))
            {
                var config = File.ReadAllText(gitConfig);
                var urlMatch = System.Text.RegularExpressions.Regex.Match(config, "url\\s*=\\s*.*?([^/]+?)(?:\\.git)?\\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (urlMatch.Success && !string.IsNullOrWhiteSpace(urlMatch.Groups[1].Value))
                    return urlMatch.Groups[1].Value;
            }
        }
        catch { /* ignore */ }

        // 6. Fallback: folder name
        return Path.GetFileName(path) ?? "unknown";
    }
}

public sealed record IndexingStats(
    TimeSpan Duration, int FilesProcessed, int SymbolCount, int RelationshipCount,
    bool CSharpSkipped = false);
