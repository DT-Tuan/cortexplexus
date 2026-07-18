using System.Diagnostics;
using System.Text.Json;
using CortexPlexus.App.Api.Dto;
using CortexPlexus.App.Indexing;
using CortexPlexus.Core;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CortexPlexus.App.Api;

public static class AgentApiEndpoints
{
    // Version is a const from CortexPlexus.Core.AgentInfo — single source of
    // truth shared with the agent CLI. Bump AgentInfo.Version, not this file.
    private const string AgentVersion = AgentInfo.Version;

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // --- Agent Download & Version ---

        api.MapGet("/agent/version", () =>
        {
            // Compute SHA256 of each available archive/binary for integrity verification.
            // Resolve order (first hit wins per RID):
            //   1. /app/_agent — bundled in the Docker image at build time. This is the
            //      "ships with CortexPlexus" copy; users never have to SCP a tarball.
            //   2. ${Workspace__Path}/_agent — explicit override. Drop a newer / custom
            //      tarball here to override the bundled one without rebuilding the image.
            var hashes = new Dictionary<string, string>();
            foreach (var rid in new[] { "win-x64", "linux-x64", "osx-x64" })
            {
                var archivePath = ResolveAgentArchive(rid);
                if (archivePath is not null && File.Exists(archivePath))
                {
                    var bytes = File.ReadAllBytes(archivePath);
                    hashes[rid] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                }
            }
            return Results.Ok(new
            {
                version = AgentVersion,
                platforms = new[] { "win-x64", "linux-x64", "osx-x64" },
                sha256 = hashes
            });
        });

        api.MapGet("/agent/download", (string? platform) =>
        {
            var rid = platform ?? DetectPlatform();

            // Validate against allowlist to prevent path traversal
            string[] allowedPlatforms = ["win-x64", "linux-x64", "osx-x64"];
            if (!allowedPlatforms.Contains(rid))
                return Results.BadRequest(new { error = $"Invalid platform '{rid}'. Allowed: {string.Join(", ", allowedPlatforms)}" });

            // Try archive first — image-bundled, then user-override. Serving the
            // same file either way.
            var archivePath = ResolveAgentArchive(rid);
            if (archivePath is not null && File.Exists(archivePath))
            {
                var archiveName = Path.GetFileName(archivePath);
                var stream = File.OpenRead(archivePath);
                return Results.File(stream, "application/gzip", archiveName);
            }

            // Fallback: single binary (self-contained). Only searched under the
            // user-override location — the image never ships single binaries.
            var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
            var agentDir = Path.Combine(workspacePath, "_agent", rid);
            var binaryName = rid.StartsWith("win") ? "cortexplexus-agent.exe" : "cortexplexus-agent";
            var binaryPath = Path.Combine(agentDir, binaryName);
            if (File.Exists(binaryPath))
            {
                var stream = File.OpenRead(binaryPath);
                return Results.File(stream, "application/octet-stream", binaryName);
            }

            return Results.NotFound(new { error = $"Agent not found for platform '{rid}'. Available: win-x64, linux-x64" });
        });

        // Install scripts
        api.MapGet("/agent/install.sh", (HttpRequest request) =>
        {
            var serverUrl = $"{request.Scheme}://{request.Host}";
            var script = $"""
                #!/bin/bash
                set -e
                INSTALL_DIR="$HOME/.cortexplexus/agent"
                mkdir -p "$INSTALL_DIR"

                # Detect platform
                OS=$(uname -s | tr '[:upper:]' '[:lower:]')
                ARCH=$(uname -m)
                case "$OS-$ARCH" in
                    linux-x86_64) RID="linux-x64" ;;
                    darwin-x86_64) RID="osx-x64" ;;
                    darwin-arm64) RID="osx-x64" ;;
                    *) echo "Unsupported: $OS-$ARCH"; exit 1 ;;
                esac

                echo "Downloading CortexPlexus Agent ($RID)..."
                curl -sL "{serverUrl}/api/agent/download?platform=$RID" -o "/tmp/cortexplexus-agent.tar.gz"
                tar -xzf /tmp/cortexplexus-agent.tar.gz -C "$INSTALL_DIR"
                rm /tmp/cortexplexus-agent.tar.gz
                chmod +x "$INSTALL_DIR/cortexplexus-agent"

                # Save version
                curl -s "{serverUrl}/api/agent/version" | grep -o '"version":"[^"]*"' | cut -d'"' -f4 > "$INSTALL_DIR/version.txt"

                echo "Installed to $INSTALL_DIR/"
                echo "Run: dotnet $INSTALL_DIR/cortexplexus-agent.dll watch /path/to/project --server {serverUrl}"
                """;
            return Results.Text(script, "text/plain");
        });

        api.MapGet("/agent/install.ps1", (HttpRequest request) =>
        {
            var serverUrl = $"{request.Scheme}://{request.Host}";
            var script = $"""
                $ErrorActionPreference = 'Stop'
                $installDir = "$env:USERPROFILE\.cortexplexus\agent"
                New-Item -ItemType Directory -Force -Path $installDir | Out-Null

                Write-Host "Downloading CortexPlexus Agent (win-x64)..."
                Invoke-WebRequest -Uri "{serverUrl}/api/agent/download?platform=win-x64" -OutFile "$env:TEMP\cortexplexus-agent.tar.gz"
                tar -xzf "$env:TEMP\cortexplexus-agent.tar.gz" -C $installDir
                Remove-Item "$env:TEMP\cortexplexus-agent.tar.gz"

                $version = (Invoke-RestMethod -Uri "{serverUrl}/api/agent/version").version
                Set-Content -Path "$installDir\version.txt" -Value $version

                Write-Host "Installed to $installDir\"
                Write-Host "Run: dotnet $installDir\cortexplexus-agent.dll watch <path> --server {serverUrl}"
                """;
            return Results.Text(script, "text/plain");
        });

        // --- Index Results (receive pre-parsed data from agent) ---

        api.MapPost("/index/results", async (
            IndexResultsRequest request,
            IServiceScopeFactory scopeFactory,
            ILogger<IndexingPipeline> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProjectName))
                return Results.BadRequest(new { error = "projectName is required" });

            // Relaxed validation cho chunked uploads: chấp nhận empty symbols nếu request mang
            // relationships, file hashes (intermediate hoặc final chunk) hoặc deleted files
            // (deletion-only sync). Pure-empty request vẫn bị reject (kẻ tấn công gửi rác).
            if (request.Symbols.Count == 0 && request.Relationships.Count == 0 && request.FileHashes.Count == 0
                && request.DeletedFiles is not { Count: > 0 })
                return Results.BadRequest(new { error = "Request must contain at least one of: symbols, relationships, fileHashes, deletedFiles" });

            var sw = Stopwatch.StartNew();

            using var scope = scopeFactory.CreateScope();
            var repoStore = scope.ServiceProvider.GetRequiredService<IRepositoryStore>();
            var graphStore = scope.ServiceProvider.GetRequiredService<IGraphStore>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
            var secretsScanner = scope.ServiceProvider.GetRequiredService<ISecretsScanner>();
            var embeddingOptions = scope.ServiceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

            // Register/get repository (use project name as path for remote agents)
            var repoPath = $"_agent/{request.ProjectName}";
            var repo = await repoStore.GetByPathAsync(repoPath, ct)
                ?? await repoStore.RegisterAsync(request.ProjectName, repoPath, ct);

            logger.LogInformation("Receiving index results from agent: {Project} ({Symbols} symbols, {Rels} relationships)",
                request.ProjectName, request.Symbols.Count, request.Relationships.Count);

            // Convert DTOs to domain models
            var symbols = request.Symbols
                .Select(dto => SymbolDtoMapper.ToModel(dto, repo.Id))
                .Where(s => !string.IsNullOrWhiteSpace(s.Fqn))
                .ToList();

            var relationships = request.Relationships
                .Select(SymbolDtoMapper.ToRelationship)
                .Where(r => !string.IsNullOrWhiteSpace(r.FromFqn) && !string.IsNullOrWhiteSpace(r.ToFqn))
                .ToList();

            // Same allow-list as IndexingPipeline + the Health metric. See
            // CortexPlexus.Core.EmbeddableKinds and docs/HEALTH-METRICS.md.
            var embeddable = symbols
                .Where(s => EmbeddableKinds.Contains(s.Kind))
                .ToList();

            // ADR-018 incremental-sync guard: refuse before any store write so the agent
            // can force_reindex + full re-index without leaving a mixed-space partial.
            // Keyed on FullReindex (all symbols re-embedded), NOT FullFileSnapshot (only
            // the file-hash list is complete): an incremental IndexAsync sends a full
            // snapshot for the sweep yet embeds a subset, so it must still be blocked on a
            // space mismatch.
            var currentSpace = EmbeddingSpaceResolver.FromOptions(embeddingOptions);
            if (!request.FullReindex
                && repo.EmbeddingProvider is not null
                && (repo.EmbeddingProvider != currentSpace.Provider
                    || repo.EmbeddingModel != currentSpace.Model)
                && embeddable.Count >= 1)
            {
                var msg =
                    $"repo '{repo.Name}' carries {repo.EmbeddingProvider}:{repo.EmbeddingModel} vectors " +
                    $"but the server now embeds with {currentSpace.Provider}:{currentSpace.Model} — " +
                    "run force_reindex then a full re-index (agent watch restart) to migrate. " +
                    "Incremental embedding sync is blocked to prevent a mixed-space repo.";
                logger.LogWarning("Rejecting incremental agent upload: {Message}", msg);
                return Results.Json(new { error = msg }, statusCode: StatusCodes.Status409Conflict);
            }

            // Generate AI summaries (optional, requires LLM)
            var summaryGen = scope.ServiceProvider.GetRequiredService<ISummaryGenerator>();
            if (summaryGen.IsEnabled)
            {
                var methods = symbols.OfType<MethodInfo>().Where(m => m.AiSummary is null).ToList();
                if (methods.Count > 0)
                {
                    var items = methods.Select(m => (m.Signature, m.Documentation)).ToList();
                    var summaries = await summaryGen.SummarizeBatchAsync(items, ct);
                    for (var i = 0; i < methods.Count; i++)
                    {
                        if (summaries[i] is not null)
                        {
                            var idx = symbols.IndexOf(methods[i]);
                            if (idx >= 0) symbols[idx] = methods[i] with { AiSummary = summaries[i] };
                        }
                    }
                    logger.LogInformation("Generated {Count} AI summaries", summaries.Count(s => s is not null));
                }
            }

            // Store in graph (timed separately to diagnose perf issues)
            var graphSw = Stopwatch.StartNew();
            await graphStore.UpsertNodesAsync(symbols, ct);
            var nodeTime = graphSw.Elapsed;
            await graphStore.UpsertEdgesAsync(relationships, ct);
            graphSw.Stop();
            logger.LogInformation("Graph upsert: {Nodes} nodes in {NodeTime:F1}s + {Edges} edges in {EdgeTime:F1}s",
                symbols.Count, nodeTime.TotalSeconds, relationships.Count, (graphSw.Elapsed - nodeTime).TotalSeconds);

            // Generate embeddings server-side (from signatures only, no source code)
            var embedSw = Stopwatch.StartNew();
            var embeddings = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
                embeddable, embeddingService, secretsScanner, logger,
                embeddingOptions.MaxParallelBatches ?? 1, ct);
            embedSw.Stop();

            // Store in vector + FTS — keep the return value so we can surface
            // partial-persist failures to the agent (HTTP 200 is not enough
            // information when the vector path silently drops rows; see issue #1).
            var vectorSw = Stopwatch.StartNew();
            var vectorResult = await vectorStore.UpsertAsync(symbols, embeddings, ct);
            vectorSw.Stop();

            var warnings = new List<string>();
            if (vectorResult.HasFailures)
            {
                warnings.Add(
                    $"vector_upsert: {vectorResult.Failed} of {vectorResult.Total} symbols failed to persist. " +
                    "Check server logs for stack trace. Re-run indexing after resolving the root cause.");
            }
            else if (embeddings.Count > 0 && vectorResult.VectorRowsWritten == 0)
            {
                // Embedding pipeline produced vectors but none landed → quieter failure
                // mode that wouldn't trigger HasFailures (e.g. all batches succeed but
                // every embedding column ended up NULL). Surface it loudly.
                warnings.Add(
                    $"vector_upsert: {embeddings.Count} embeddings sent, 0 rows with non-null embedding column. " +
                    "Vector pipeline appears non-functional even though no exception was thrown. " +
                    "Check server logs for `[WRN] Vector upsert` lines.");
            }

            // Update file hashes
            foreach (var (filePath, hash) in request.FileHashes)
            {
                await repoStore.UpdateFileHashAsync(filePath, repo.Id, hash, ct);
            }

            // ADR-018: stamp space only on the final commit of a genuine full re-index
            // (FullReindex + the FileHashes-bearing chunk). Every symbol was re-embedded in
            // the current space, so the stamp is truthful. Intermediate full-reindex chunks
            // set FullReindex but carry empty hashes, so FileHashes.Count > 0 keeps the
            // stamp on the final commit only; an incremental IndexAsync sets FullReindex=false
            // and never restamps (which would falsely claim a subset-embed repo is pure).
            if (request.FullReindex && request.FileHashes.Count > 0)
            {
                await repoStore.UpdateEmbeddingSpaceAsync(
                    repo.Id, currentSpace.Provider, currentSpace.Model, currentSpace.Dimensions, ct);
            }

            // --- Stale-symbol removal (the "graph never forgets deleted files" fix) ---
            // 1. Explicit deletions the agent observed (watch delete events / hash diff).
            var staleFilesRemoved = 0;
            if (request.DeletedFiles is { Count: > 0 })
            {
                var deadFiles = request.DeletedFiles
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (deadFiles.Count > 0)
                {
                    var deadRows = await vectorStore.DeleteByFilePathsAsync(repo.Id, deadFiles, ct);
                    await graphStore.DeleteByFilePathsAsync(repo.Id, deadFiles, ct);
                    await repoStore.DeleteFileHashesAsync(repo.Id, deadFiles, ct);
                    staleFilesRemoved += deadFiles.Count;
                    logger.LogInformation(
                        "Removed {Rows} stale symbol rows across {Files} deleted file(s) for {Project}",
                        deadRows, deadFiles.Count, request.ProjectName);
                }
            }

            // 2. Snapshot sweep: on a full-index commit, anything not in the snapshot is
            // gone from disk — drop it. Catches deletions the hash diff cannot see
            // (e.g. right after force_reindex wiped the hash cache). Runs only after all
            // upserts of this request succeeded, so a failed run never triggers a sweep.
            if (request.FullFileSnapshot && request.FileHashes.Count > 0)
            {
                var snapshot = request.FileHashes.Keys.ToList();
                var sweptSymbolFiles = await vectorStore.SweepMissingFilesAsync(repo.Id, snapshot, ct);
                var sweptHashFiles = await repoStore.SweepFileHashesAsync(repo.Id, snapshot, ct);
                var sweptFiles = sweptSymbolFiles.Concat(sweptHashFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sweptFiles.Count > 0)
                {
                    await graphStore.DeleteByFilePathsAsync(repo.Id, sweptFiles, ct);
                    staleFilesRemoved += sweptFiles.Count;
                    logger.LogInformation(
                        "Snapshot sweep for {Project}: removed stale entries of {Files} file(s) no longer on disk",
                        request.ProjectName, sweptFiles.Count);
                }
            }

            await repoStore.UpdateLastIndexedAsync(repo.Id, ct);

            sw.Stop();
            logger.LogInformation(
                "Agent index results stored: {Symbols} symbols, {Rels} relationships, {Embeds} embeddings ({VectorOk}/{VectorTotal} persisted) in {Duration:F1}s " +
                "(graph={GraphTime:F1}s, embed={EmbedTime:F1}s, vector={VectorTime:F1}s)",
                symbols.Count, relationships.Count, embeddings.Count,
                vectorResult.Persisted, vectorResult.Total,
                sw.Elapsed.TotalSeconds,
                graphSw.Elapsed.TotalSeconds, embedSw.Elapsed.TotalSeconds, vectorSw.Elapsed.TotalSeconds);

            return Results.Ok(new IndexResultsResponse
            {
                Project = request.ProjectName,
                Symbols = symbols.Count,
                Relationships = relationships.Count,
                Embeddings = embeddings.Count,
                SymbolsPersisted = vectorResult.Persisted,
                SymbolsFailed = vectorResult.Failed,
                VectorRowsWritten = vectorResult.VectorRowsWritten,
                Warnings = warnings,
                StaleFilesRemoved = staleFilesRemoved,
                DurationSeconds = sw.Elapsed.TotalSeconds
                // EmbeddingsPersisted / EmbeddingsFailed serialize automatically as
                // computed-property aliases of SymbolsPersisted / SymbolsFailed
                // (see IndexResultsResponse). Drop in v0.8.0.
            });
        });

        // --- Watch-agent heartbeat (zombie-watch visibility) ---
        // The watch agent reports liveness + last successful sync + consecutive upload
        // failures. list_repositories renders this as Watch: ACTIVE / FAILING /
        // DISCONNECTED so a watch that is alive-but-uploading-nothing is visible in one
        // call instead of silently serving weeks-stale results.

        api.MapPost("/agent/heartbeat", async (
            AgentHeartbeatRequest request,
            IRepositoryStore repoStore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProjectName))
                return Results.BadRequest(new { error = "projectName is required" });

            var repo = await repoStore.GetByPathAsync($"_agent/{request.ProjectName}", ct);
            if (repo is null)
                return Results.NotFound(new
                {
                    error = $"No agent-indexed repository named '{request.ProjectName}'. " +
                            "The heartbeat is recorded only after the first index upload."
                });

            await repoStore.UpsertWatchHeartbeatAsync(
                repo.Id,
                string.IsNullOrWhiteSpace(request.AgentVersion) ? "unknown" : request.AgentVersion,
                request.LastSyncUtc,
                Math.Max(0, request.ConsecutiveFailures),
                request.LastError,
                ct);

            return Results.Ok(new { ok = true });
        });

        // --- File Hashes (for incremental sync) ---

        api.MapGet("/index/{projectName}/hashes", async (
            string projectName,
            IRepositoryStore repoStore,
            CancellationToken ct) =>
        {
            var repoPath = $"_agent/{projectName}";
            var repo = await repoStore.GetByPathAsync(repoPath, ct);

            if (repo is null)
                return Results.Ok(new Dictionary<string, string>());

            var hashes = await repoStore.GetFileHashesAsync(repo.Id, ct);
            return Results.Ok(hashes);
        });

        return app;
    }

    // Image-bundled location (from src/CortexPlexus.App/Dockerfile's agent
    // publish step) takes precedence; operators can still drop a newer tarball
    // at ${Workspace__Path}/_agent/ to override without rebuilding the image.
    private static string? ResolveAgentArchive(string rid)
    {
        var archiveName = $"cortexplexus-agent-{rid}.tar.gz";
        var bundledPath = Path.Combine("/app", "_agent", archiveName);
        if (File.Exists(bundledPath)) return bundledPath;

        var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
        var workspaceArchivePath = Path.Combine(workspacePath, "_agent", archiveName);
        return File.Exists(workspaceArchivePath) ? workspaceArchivePath : null;
    }

    private static string DetectPlatform()
    {
        if (OperatingSystem.IsWindows()) return "win-x64";
        if (OperatingSystem.IsMacOS()) return "osx-x64";
        return "linux-x64";
    }
}
