using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Parsing;
using CortexPlexus.Parsing.Markdown;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Agent;

/// <summary>
/// Parses source code locally using Roslyn + Tree-sitter, then sends
/// only metadata (symbols, relationships, file hashes) to the CortexPlexus server.
/// Source code never leaves the local machine.
/// </summary>
public sealed class LocalIndexer
{
    private readonly string _serverUrl;
    private readonly string _projectName;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ICodeParser _roslynParser;
    private readonly TreeSitterCodeParser _treeSitterParser;
    private readonly MarkdownParser _markdownParser;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // Server-side file hashes for incremental comparison
    private Dictionary<string, string> _serverHashes = new(StringComparer.OrdinalIgnoreCase);

    public LocalIndexer(string serverUrl, string projectName, ILogger logger)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _projectName = projectName;
        _logger = logger;
        // 30 minutes — large projects (100K+ relationships) can take 5-15 min server-side to persist
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // Build parsing services via DI
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddCortexPlexusParsing();
        var sp = services.BuildServiceProvider();

        _roslynParser = sp.GetRequiredService<ICodeParser>();
        _treeSitterParser = sp.GetRequiredService<TreeSitterCodeParser>();
        _markdownParser = sp.GetRequiredService<MarkdownParser>();
    }

    /// <summary>
    /// Last confirmed successful sync with the server: an index upload that was fully
    /// acknowledged, or a verified "nothing changed" check. Reported in heartbeats so
    /// the server can tell a healthy-idle watch from a zombie one.
    /// </summary>
    public DateTimeOffset? LastSyncUtc { get; private set; }

    /// <summary>
    /// Full index of a project directory. Fetches server hashes first for incremental detection.
    /// </summary>
    public async Task IndexAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        _rootPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger.LogInformation("Starting full index of {Path}...", path);

        // Fetch existing hashes from server
        await FetchServerHashesAsync();

        // Compute local hashes and diff against the server: changed files to re-parse,
        // plus files the server still has hashes for that no longer exist locally —
        // those must be reported so the server can drop their stale symbols.
        var localHashes = ComputeLocalHashes(path);
        var (changedFiles, deletedFiles) = DiffAgainstServer(localHashes, _serverHashes, _rootPath);

        if (changedFiles.Count == 0 && deletedFiles.Count == 0 && _serverHashes.Count > 0)
        {
            _logger.LogInformation("No files changed since last index. Skipping.");
            LastSyncUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (changedFiles.Count == 0 && deletedFiles.Count > 0)
        {
            // Deletion-only sync: nothing to parse, but the server must forget the
            // removed files. Previously this case silently skipped, which is exactly
            // how deleted symbols survived in the graph forever.
            _logger.LogInformation("No content changes, but {Count} file(s) were deleted — syncing deletions...", deletedFiles.Count);
            await PostResultsAsync([], [], localHashes, deletedFiles, fullFileSnapshot: true);
            LastSyncUtc = DateTimeOffset.UtcNow;
            sw.Stop();
            _logger.LogInformation("Deletion sync complete in {Duration:F1}s", sw.Elapsed.TotalSeconds);
            return;
        }

        var isFullIndex = _serverHashes.Count == 0;

        // Parse all languages
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();

        // C# (Roslyn)
        var solutions = FindSolutionsAndProjects(path);
        if (solutions.Count > 0)
        {
            foreach (var solutionPath in solutions)
            {
                ParseResult result;
                if (isFullIndex)
                {
                    _logger.LogInformation("Parsing C#: {Path}...", solutionPath);
                    result = await _roslynParser.ParseSolutionAsync(solutionPath);
                }
                else
                {
                    var csChanged = changedFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (csChanged.Count > 0)
                    {
                        _logger.LogInformation("Incremental C#: {Count} files", csChanged.Count);
                        result = await _roslynParser.ParseFilesAsync(csChanged, solutionPath);
                    }
                    else
                    {
                        continue;
                    }
                }
                allSymbols.AddRange(result.Symbols);
                allRelationships.AddRange(result.Relationships);
            }
        }

        // TypeScript/JavaScript/Python (Tree-sitter)
        var tsResult = await _treeSitterParser.ParseSolutionAsync(path);
        if (tsResult.Symbols.Count > 0)
        {
            allSymbols.AddRange(tsResult.Symbols);
            allRelationships.AddRange(tsResult.Relationships);
        }

        // Markdown
        var mdResult = _markdownParser.ParseDirectory(path);
        if (mdResult.Symbols.Count > 0)
        {
            allSymbols.AddRange(mdResult.Symbols);
            allRelationships.AddRange(mdResult.Relationships);
        }

        _logger.LogInformation("Parsed: {Symbols} symbols, {Rels} relationships", allSymbols.Count, allRelationships.Count);

        if (allSymbols.Count == 0 && deletedFiles.Count == 0)
        {
            _logger.LogWarning("No symbols extracted.");
            return;
        }

        // Send results to server. IndexAsync always hashes the FULL local tree, so this
        // is a complete snapshot — arm the server-side stale-symbol sweep with it.
        await PostResultsAsync(allSymbols, allRelationships, localHashes, deletedFiles, fullFileSnapshot: true);
        LastSyncUtc = DateTimeOffset.UtcNow;

        sw.Stop();
        _logger.LogInformation("Index complete in {Duration:F1}s", sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Incremental index of specific changed files (called by file watcher).
    /// </summary>
    public async Task IndexFilesAsync(string rootPath, IReadOnlyList<string> changedFiles)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(_rootPath))
            _rootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Split the batch: files that still exist get re-parsed; files that are gone
        // (delete events, rename sources) are reported to the server so their symbols
        // are dropped instead of lingering in the graph forever.
        var existingFiles = changedFiles.Where(File.Exists).ToList();
        var deletedFiles = changedFiles
            .Where(f => !File.Exists(f))
            .Select(ToRelativePath)
            .ToList();

        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();

        // Group by language
        var csFiles = existingFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var tsFiles = existingFiles.Where(f => IsTreeSitterFile(f)).ToList();
        var mdFiles = existingFiles.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();

        // C# incremental
        if (csFiles.Count > 0)
        {
            var solutions = FindSolutionsAndProjects(rootPath);
            if (solutions.Count > 0)
            {
                var result = await _roslynParser.ParseFilesAsync(csFiles, solutions[0]);
                allSymbols.AddRange(result.Symbols);
                allRelationships.AddRange(result.Relationships);
            }
        }

        // Tree-sitter: re-parse whole directory (tree-sitter is fast)
        if (tsFiles.Count > 0)
        {
            var result = await _treeSitterParser.ParseSolutionAsync(rootPath);
            allSymbols.AddRange(result.Symbols);
            allRelationships.AddRange(result.Relationships);
        }

        // Markdown
        if (mdFiles.Count > 0)
        {
            var result = _markdownParser.ParseDirectory(rootPath);
            allSymbols.AddRange(result.Symbols);
            allRelationships.AddRange(result.Relationships);
        }

        if (allSymbols.Count == 0 && deletedFiles.Count == 0)
        {
            _logger.LogInformation("No symbols extracted from changed files.");
            return;
        }

        // Compute hashes for changed files only
        var hashes = new Dictionary<string, string>();
        foreach (var file in existingFiles)
        {
            hashes[file] = ComputeFileHash(file);
        }

        // Incremental batch → NOT a full snapshot; only the explicit deletions apply.
        await PostResultsAsync(allSymbols, allRelationships, hashes, deletedFiles, fullFileSnapshot: false);
        LastSyncUtc = DateTimeOffset.UtcNow;

        sw.Stop();
        _logger.LogInformation(
            "Incremental index: {Symbols} symbols, {Deleted} deletion(s) in {Duration:F1}s",
            allSymbols.Count, deletedFiles.Count, sw.Elapsed.TotalSeconds);
    }

    private async Task FetchServerHashesAsync()
    {
        try
        {
            var url = $"{_serverUrl}/api/index/{Uri.EscapeDataString(_projectName)}/hashes";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                _serverHashes = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions)
                    ?? new Dictionary<string, string>();
                _logger.LogInformation("Server has {Count} file hashes", _serverHashes.Count);
            }
            else
            {
                _serverHashes = new Dictionary<string, string>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch server hashes, will do full index");
            _serverHashes = new Dictionary<string, string>();
        }
    }

    private string _rootPath = "";

    /// <summary>
    /// Threshold để bật chunking. Payloads nhỏ vẫn gửi 1 POST như cũ (faster).
    /// Lớn hơn threshold → chia thành nhiều POST để:
    /// - Tránh single-request HTTP timeout (R12: 18K symbols + 123K edges crash sau 30 phút)
    /// - Cho phép server commit incrementally (resume sau crash)
    /// - Visibility: log progress mỗi chunk
    /// </summary>
    private const int ChunkThreshold = 3000;

    private const int SymbolChunkSize = 2000;
    private const int RelationshipChunkSize = 5000;

    private async Task PostResultsAsync(
        IReadOnlyList<CodeSymbol> symbols,
        IReadOnlyList<Relationship> relationships,
        Dictionary<string, string> fileHashes,
        IReadOnlyList<string>? deletedFiles = null,
        bool fullFileSnapshot = false)
    {
        // Convert to DTOs — use project-relative paths to avoid leaking dev machine paths
        var symbolDtos = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s.Fqn))
            .Select(ToSymbolDto)
            .ToList();

        var relationshipDtos = relationships
            .Where(r => !string.IsNullOrWhiteSpace(r.FromFqn) && !string.IsNullOrWhiteSpace(r.ToFqn))
            .Select(r => new RelationshipDto
            {
                FromFqn = r.FromFqn,
                ToFqn = r.ToFqn,
                Type = r.Type.ToString(),
                Metadata = r.Metadata
            })
            .ToList();

        // Convert absolute file paths to project-relative to avoid leaking dev machine paths
        var relativeHashes = fileHashes.ToDictionary(
            kv => ToRelativePath(kv.Key),
            kv => kv.Value);

        // Deleted files travel with whichever request also carries the file hashes:
        // the single POST, or the final commit chunk. That keeps the server-side
        // deletion + snapshot sweep on the same request that proves the run finished.
        // Small payload → single POST như cũ (avoid chunking overhead)
        if (symbolDtos.Count + relationshipDtos.Count <= ChunkThreshold)
        {
            await PostChunkAsync(symbolDtos, relationshipDtos, relativeHashes, chunkLabel: "single",
                deletedFiles, fullFileSnapshot);
            return;
        }

        // Large payload → chunked upload
        _logger.LogInformation(
            "Large payload detected ({Symbols} symbols + {Rels} relationships) — chunking...",
            symbolDtos.Count, relationshipDtos.Count);

        // Phase 1: Symbols in batches of SymbolChunkSize
        var symbolBatches = symbolDtos.Chunk(SymbolChunkSize).ToList();
        for (var i = 0; i < symbolBatches.Count; i++)
        {
            var batch = symbolBatches[i];
            var label = $"symbols {i + 1}/{symbolBatches.Count} ({batch.Length} items)";
            await PostChunkAsync(
                batch.ToList(),
                new List<RelationshipDto>(),
                new Dictionary<string, string>(),
                chunkLabel: label);
        }

        // Phase 2: Relationships in batches of RelationshipChunkSize
        var relBatches = relationshipDtos.Chunk(RelationshipChunkSize).ToList();
        for (var i = 0; i < relBatches.Count; i++)
        {
            var batch = relBatches[i];
            var label = $"relationships {i + 1}/{relBatches.Count} ({batch.Length} items)";
            await PostChunkAsync(
                new List<object>(),
                batch.ToList(),
                new Dictionary<string, string>(),
                chunkLabel: label);
        }

        // Phase 3: Final commit chunk — empty content, only file hashes + deletions
        // (server marks lastIndexed and, on a full snapshot, sweeps stale symbols)
        await PostChunkAsync(
            new List<object>(),
            new List<RelationshipDto>(),
            relativeHashes,
            chunkLabel: $"final commit ({relativeHashes.Count} file hashes)",
            deletedFiles, fullFileSnapshot);

        _logger.LogInformation(
            "Chunked upload complete: {SymbolChunks} symbol chunks + {RelChunks} relationship chunks + 1 commit chunk",
            symbolBatches.Count, relBatches.Count);
    }

    private async Task PostChunkAsync(
        IReadOnlyList<object> symbols,
        IReadOnlyList<RelationshipDto> relationships,
        IReadOnlyDictionary<string, string> fileHashes,
        string chunkLabel,
        IReadOnlyList<string>? deletedFiles = null,
        bool fullFileSnapshot = false)
    {
        var payload = new
        {
            projectName = _projectName,
            symbols,
            relationships,
            fileHashes,
            // Pre-v0.9 servers ignore the two fields below (unknown JSON members).
            deletedFiles = deletedFiles is { Count: > 0 } ? deletedFiles : null,
            fullFileSnapshot
        };

        _logger.LogInformation("POST chunk [{Label}]...", chunkLabel);

        var url = $"{_serverUrl}/api/index/results";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _httpClient.PostAsJsonAsync(url, payload, JsonOptions);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("  → ERROR {Status}: {Error}", response.StatusCode, error);
            // Throw để abort chunked upload — partial state vẫn ở server, có thể resume
            throw new InvalidOperationException($"Chunk upload failed: {response.StatusCode} — {error}");
        }

        // HTTP 200 alone is not enough: the server will return 200 even when the
        // vector store silently dropped rows (historically hidden as WARN logs —
        // see issue #1). Parse the body and escalate any partial-persist failures
        // so the user's AI agent does not conclude "indexed" when it isn't.
        //
        // Wire-compat with two server generations:
        //   - v0.7.0+ servers emit symbolsPersisted / symbolsFailed / vectorRowsWritten
        //     plus the deprecated embeddingsPersisted / embeddingsFailed aliases.
        //   - older servers emit only the deprecated names.
        // We read whichever is present (Failed below picks new ?? old).
        var body = await response.Content.ReadFromJsonAsync<UploadAck>(JsonOptions);
        if (body is null)
        {
            _logger.LogWarning("  → OK but response body missing; cannot verify persist counts");
            return;
        }

        var failed = body.Failed;
        var persisted = body.Persisted;
        var vectorRows = body.VectorRows;

        if (failed > 0)
        {
            foreach (var w in body.Warnings ?? [])
                _logger.LogError("  → SERVER WARNING: {Warning}", w);

            _logger.LogError(
                "  → PARTIAL PERSIST: {Failed} of {Total} symbol rows failed for chunk [{Label}]. " +
                "Server logs have the stack trace. Aborting upload; queries will return stale / incomplete results.",
                failed, persisted + failed, chunkLabel);
            throw new InvalidOperationException(
                $"Server reported {failed} failed symbol upserts in chunk [{chunkLabel}]. " +
                "Do not query until the underlying issue is fixed and the chunk is re-uploaded.");
        }

        // Even with no hard failures, surface server warnings (e.g. embedding
        // pipeline produced 0 vectors despite N inputs — the loud-but-not-fatal
        // signal v0.7.0 added).
        if (body.Warnings is { Count: > 0 })
        {
            foreach (var w in body.Warnings)
                _logger.LogWarning("  → SERVER WARNING: {Warning}", w);
        }

        _logger.LogInformation(
            "  → OK ({Duration:F1}s) — {Persisted} symbol rows persisted, {VectorRows} with embedding",
            sw.Elapsed.TotalSeconds, persisted, vectorRows);
    }

    /// <summary>
    /// Trimmed view of the server's IndexResultsResponse — only the fields the
    /// agent uses to decide success/failure. Avoids a cross-project dependency
    /// on CortexPlexus.App's DTOs.
    /// </summary>
    /// <remarks>
    /// Field-naming wire-compat: v0.7.0 servers send the new symbolsPersisted /
    /// symbolsFailed names; older servers only sent embeddingsPersisted /
    /// embeddingsFailed. The Persisted / Failed / VectorRowsWritten properties
    /// below pick whichever the server actually serialized.
    /// </remarks>
    // Internal so CortexPlexus.Agent.Tests can exercise the wire-compat picker
    // (Persisted / Failed / VectorRows) directly. JSON-bound fields are nullable
    // because old servers omit some of them; the public accessors below default
    // to 0 so call-sites can use them as plain ints.
    internal sealed record UploadAck
    {
        public int Symbols { get; init; }
        public int Relationships { get; init; }
        public int Embeddings { get; init; }
        public IReadOnlyList<string>? Warnings { get; init; }
        public double DurationSeconds { get; init; }

        // v0.7.0+ canonical names (nullable so old servers that omit them still parse).
        public int? SymbolsPersisted { get; init; }
        public int? SymbolsFailed { get; init; }
        public int? VectorRowsWritten { get; init; }

        // Deprecated aliases — kept for one release. Old servers send only these.
        public int? EmbeddingsPersisted { get; init; }
        public int? EmbeddingsFailed { get; init; }

        // Convenience accessors used by call-sites + tests. New names win; deprecated
        // names fall back; absent fields default to 0.
        [System.Text.Json.Serialization.JsonIgnore]
        public int Persisted => SymbolsPersisted ?? EmbeddingsPersisted ?? 0;
        [System.Text.Json.Serialization.JsonIgnore]
        public int Failed => SymbolsFailed ?? EmbeddingsFailed ?? 0;
        [System.Text.Json.Serialization.JsonIgnore]
        public int VectorRows => VectorRowsWritten ?? 0;
    }

    /// <summary>
    /// Relationship DTO cho serialization. Dùng class thay vì anonymous type để
    /// có thể truyền giữa methods (chunking).
    /// </summary>
    private sealed class RelationshipDto
    {
        public string FromFqn { get; set; } = "";
        public string ToFqn { get; set; } = "";
        public string Type { get; set; } = "";
        public Dictionary<string, string>? Metadata { get; set; }
    }

    internal Dictionary<string, string> ComputeLocalHashes(string path)
    {
        var extensions = new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.py", "*.md" };
        var excludedDirs = new[] { "bin", "obj", "node_modules", "__pycache__", ".venv", ".git", ".vs", ".idea" };

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in extensions)
        {
            foreach (var file in Directory.GetFiles(path, ext, SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => excludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    continue;

                hashes[file] = ComputeFileHash(file);
            }
        }

        return hashes;
    }

    /// <summary>
    /// Diff local files against the server's stored hashes.
    /// Local keys are absolute paths; server keys are project-relative (v0.7+ servers)
    /// or absolute (legacy rows) — comparison happens on both forms so either
    /// generation matches. Returns:
    /// <list type="bullet">
    /// <item><b>Changed</b> — absolute paths of local files that are new or whose hash differs.</item>
    /// <item><b>Deleted</b> — server-side keys (exactly as stored) that no local file maps to;
    /// sent back verbatim so the server can delete the matching rows.</item>
    /// </list>
    /// The previous implementation compared the absolute local key against the relative
    /// server key directly — they never matched, so every watch start re-parsed the whole
    /// repo and file deletions were undetectable (the stale-symbol bug's agent half).
    /// </summary>
    internal static (List<string> Changed, List<string> Deleted) DiffAgainstServer(
        Dictionary<string, string> localHashes,
        Dictionary<string, string> serverHashes,
        string rootPath)
    {
        // Every local file under both its absolute and relative form → hash.
        var localByAnyForm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (absPath, hash) in localHashes)
        {
            localByAnyForm[absPath] = hash;
            localByAnyForm[ToRelativePath(absPath, rootPath)] = hash;
        }

        var changed = new List<string>();
        foreach (var (absPath, hash) in localHashes)
        {
            var relPath = ToRelativePath(absPath, rootPath);
            if (!serverHashes.TryGetValue(relPath, out var serverHash)
                && !serverHashes.TryGetValue(absPath, out serverHash))
            {
                changed.Add(absPath);
            }
            else if (!string.Equals(serverHash, hash, StringComparison.Ordinal))
            {
                changed.Add(absPath);
            }
        }

        var deleted = serverHashes.Keys
            .Where(serverKey => !localByAnyForm.ContainsKey(serverKey))
            .ToList();

        return (changed, deleted);
    }

    internal static string ComputeFileHash(string filePath)
    {
        // Retry on IOException (usually "file in use" / "sharing violation" / "access denied")
        // that fires transiently while the user's IDE is mid-save. 3 attempts with
        // 150-300ms backoff is enough to catch ~99% of IDE flush races without
        // noticeably slowing the batch.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                var hash = SHA256.HashData(bytes);
                return Convert.ToHexString(hash);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(150 * attempt);  // 150ms, 300ms
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    internal static IReadOnlyList<string> FindSolutionsAndProjects(string path)
    {
        if (path.EndsWith(".sln") || path.EndsWith(".slnx") || path.EndsWith(".csproj"))
            return File.Exists(path) ? [path] : [];

        if (!Directory.Exists(path)) return [];

        var excludedDirs = new[] { "bin", "obj", "node_modules" };

        var solutions = Directory.GetFiles(path, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(path, "*.slnx", SearchOption.AllDirectories))
            .Where(f => !IsExcludedPath(f, excludedDirs))
            .ToList();

        var allCsprojs = Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f, excludedDirs))
            .ToList();

        // No solution → every .csproj is a standalone unit.
        if (solutions.Count == 0)
            return allCsprojs;

        // R20 Issue #2 fix: some projects (commonly .Tests/) are not referenced by
        // any .sln but still contain code worth indexing. Discover .csproj files
        // that aren't mentioned in any solution file and add them alongside.
        var csprojInSolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sln in solutions)
            csprojInSolutions.UnionWith(ExtractCsprojPathsFromSln(sln));

        var orphanCsprojs = allCsprojs
            .Where(csproj => !csprojInSolutions.Contains(Path.GetFullPath(csproj)))
            .ToList();

        return solutions.Concat(orphanCsprojs).ToList();
    }

    /// <summary>
    /// Extract absolute .csproj paths from a solution file. Handles both formats:
    /// <list type="bullet">
    /// <item>Classic <c>.sln</c> text: <c>Project("{GUID}") = "Name", "rel\path.csproj", "{GUID}"</c></item>
    /// <item>Modern <c>.slnx</c> XML: <c>&lt;Project Path="rel/path.csproj" /&gt;</c> (possibly nested in &lt;Folder&gt;)</item>
    /// </list>
    /// Without .slnx support, every project in a .slnx-based repo is treated as an orphan
    /// standalone unit and re-indexed once per project on a full index (R27-followup: a
    /// 21-project repo was indexed 21× on each watch startup).
    /// </summary>
    internal static IEnumerable<string> ExtractCsprojPathsFromSln(string slnPath)
    {
        var results = new List<string>();
        if (!File.Exists(slnPath)) return results;
        var slnDir = Path.GetDirectoryName(slnPath) ?? "";

        void Add(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                !relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return;
            // Normalize Windows-style backslashes (also valid on Linux MSBuildWorkspace).
            var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar);
            results.Add(Path.GetFullPath(Path.Combine(slnDir, normalized)));
        }

        if (slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // <Project Path="..."/> elements, at any depth (top-level or inside <Folder>).
                var doc = System.Xml.Linq.XDocument.Load(slnPath);
                foreach (var proj in doc.Descendants("Project"))
                    Add(proj.Attribute("Path")?.Value ?? "");
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                // Malformed/unreadable .slnx → return whatever parsed (possibly empty).
            }
            return results;
        }

        string[] lines;
        try { lines = File.ReadAllLines(slnPath); }
        catch (IOException) { return results; }

        foreach (var line in lines)
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;
            // Three quoted strings: "ProjectName", "relative\path.csproj", "{GUID}"
            var parts = line.Split('"');
            if (parts.Length < 6) continue;
            Add(parts[5]);
        }

        return results;
    }

    private static bool IsExcludedPath(string path, string[] excludedDirs)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => excludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    internal static bool IsTreeSitterFile(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    private string ToRelativePath(string absolutePath) => ToRelativePath(absolutePath, _rootPath);

    internal static string ToRelativePath(string absolutePath, string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !absolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return absolutePath;

        return absolutePath[rootPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Report watch-agent liveness to the server (fire-and-forget semantics: failures
    /// are logged at debug level and never disturb the watch loop). Sent on watch
    /// start, after every flush outcome, and on a periodic idle timer, so
    /// list_repositories can tell a healthy-idle watch from a zombie one.
    /// </summary>
    public async Task PostHeartbeatAsync(int consecutiveFailures, string? lastError, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                projectName = _projectName,
                agentVersion = CortexPlexus.Core.AgentInfo.Version,
                lastSyncUtc = LastSyncUtc,
                consecutiveFailures,
                lastError
            };
            var url = $"{_serverUrl}/api/agent/heartbeat";
            using var response = await _httpClient.PostAsJsonAsync(url, payload, JsonOptions, ct);
            // 404 = repo not registered yet (first upload still pending) — expected, ignore.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Heartbeat POST failed (server unreachable?) — will retry on next beat");
        }
    }

    private object ToSymbolDto(CodeSymbol symbol)
    {
        var common = new Dictionary<string, object?>
        {
            ["fqn"] = symbol.Fqn,
            ["name"] = symbol.Name,
            ["kind"] = symbol.Kind,
            ["filePath"] = symbol.FilePath is not null ? ToRelativePath(symbol.FilePath) : null,
            ["startLine"] = symbol.StartLine,
            ["endLine"] = symbol.EndLine,
            ["documentation"] = symbol.Documentation,
            ["aiSummary"] = symbol.AiSummary
        };

        switch (symbol)
        {
            case ClassInfo c:
                common["accessibility"] = c.Accessibility;
                common["isAbstract"] = c.IsAbstract;
                common["isStatic"] = c.IsStatic;
                common["isSealed"] = c.IsSealed;
                common["isPartial"] = c.IsPartial;
                common["baseTypeFqn"] = c.BaseTypeFqn;
                common["interfaceFqns"] = c.InterfaceFqns;
                break;
            case MethodInfo m:
                common["signature"] = m.Signature;
                common["returnType"] = m.ReturnType;
                common["accessibility"] = m.Accessibility;
                common["isAsync"] = m.IsAsync;
                common["isStatic"] = m.IsStatic;
                common["isVirtual"] = m.IsVirtual;
                common["isOverride"] = m.IsOverride;
                common["isTestMethod"] = m.IsTestMethod;
                common["containingTypeFqn"] = m.ContainingTypeFqn;
                common["parameters"] = m.Parameters.Select(p => new { name = p.Name, type = p.Type, position = p.Position });
                break;
            case InterfaceInfo i:
                common["accessibility"] = i.Accessibility;
                common["memberFqns"] = i.MemberFqns;
                break;
            case PropertyInfo p:
                common["type"] = p.Type;
                common["hasGetter"] = p.HasGetter;
                common["hasSetter"] = p.HasSetter;
                common["containingTypeFqn"] = p.ContainingTypeFqn;
                break;
            case ConstructorInfo ct:
                common["signature"] = ct.Signature;
                common["accessibility"] = ct.Accessibility;
                common["containingTypeFqn"] = ct.ContainingTypeFqn;
                common["parameters"] = ct.Parameters.Select(p => new { name = p.Name, type = p.Type, position = p.Position });
                break;
            case DocumentSection d:
                common["level"] = d.Level;
                // Truncate content to first 500 chars — avoid sending full doc text
                common["content"] = d.Content.Length > 500 ? d.Content[..500] + "..." : d.Content;
                common["documentPath"] = d.DocumentPath;
                break;
            case DbContextInfo db:
                common["dbSets"] = db.DbSets.Select(s => new { entityTypeFqn = s.EntityTypeFqn, propertyName = s.PropertyName, tableName = s.TableName });
                break;
            case DiRegistrationInfo di:
                common["serviceTypeFqn"] = di.ServiceTypeFqn;
                common["implementationTypeFqn"] = di.ImplementationTypeFqn;
                common["lifetime"] = di.Lifetime;
                common["moduleName"] = di.ModuleName;
                break;
            case ApiEndpointInfo api:
                common["httpMethod"] = api.HttpMethod;
                common["routeTemplate"] = api.RouteTemplate;
                common["handlerMethodFqn"] = api.HandlerMethodFqn;
                common["endpointName"] = api.EndpointName;
                common["summary"] = api.Summary;
                common["moduleName"] = api.ModuleName;
                break;
        }

        return common;
    }
}
