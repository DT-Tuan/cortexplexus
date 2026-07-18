using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Embedding;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CortexPlexus.Api.Tests;

/// <summary>
/// Tests cho stale-symbol removal trên POST /api/index/results (deletedFiles +
/// fullFileSnapshot sweep) và POST /api/agent/heartbeat (zombie-watch visibility).
///
/// Bối cảnh: bug report từ CortexFlow 2026-07-10 — graph không bao giờ quên symbol
/// của file đã xoá (upsert-by-FQN không revisit file không còn tồn tại), và watch
/// agent có thể "active" hàng tuần mà không upload gì (2026-07-17).
/// </summary>
public class AgentIndexResultsDeletionTests
{
    private static readonly Guid RepoId = Guid.NewGuid();

    private static RepositoryInfo Repo(string name = "TestProj") =>
        new(RepoId, name, $"_agent/{name}", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddHours(-1));

    private static (TestApiFactory factory, IRepositoryStore repoStore, IGraphStore graphStore, IVectorStore vectorStore)
        CreateFactory()
    {
        var repoStore = Substitute.For<IRepositoryStore>();
        var graphStore = Substitute.For<IGraphStore>();
        var vectorStore = Substitute.For<IVectorStore>();

        repoStore.GetByPathAsync("_agent/TestProj", Arg.Any<CancellationToken>())
            .Returns(Repo());
        vectorStore.UpsertAsync(
                Arg.Any<IEnumerable<CodeSymbol>>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Any<CancellationToken>())
            .Returns(new VectorUpsertResult(0, 0, 0));
        vectorStore.SweepMissingFilesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        repoStore.SweepFileHashesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var summaryGen = Substitute.For<ISummaryGenerator>();
        summaryGen.IsEnabled.Returns(false);

        var factory = TestApiFactory.Create(services =>
        {
            services.AddSingleton(repoStore);
            services.AddSingleton(graphStore);
            services.AddSingleton(vectorStore);
            services.AddSingleton(Substitute.For<ISecretsScanner>());
            services.AddSingleton(summaryGen);
            services.Configure<EmbeddingOptions>(_ => { });
        });

        return (factory, repoStore, graphStore, vectorStore);
    }

    [Fact]
    public async Task PostIndexResults_DeletionOnlyRequest_Accepted_AndDeletesEverywhere()
    {
        // Trước fix: request chỉ có deletedFiles bị reject (validation đòi symbols /
        // relationships / fileHashes) — deletion không có đường nào tới server.
        var (factory, repoStore, graphStore, vectorStore) = CreateFactory();
        using var _ = factory;

        var response = await factory.Client.PostAsJsonAsync("/api/index/results", new
        {
            projectName = "TestProj",
            symbols = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            fileHashes = new Dictionary<string, string>(),
            deletedFiles = new[] { "src/DirectorService.cs", "src/SandboxService.cs" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await vectorStore.Received(1).DeleteByFilePathsAsync(
            RepoId,
            Arg.Is<IReadOnlyCollection<string>>(f => f.Count == 2 && f.Contains("src/DirectorService.cs")),
            Arg.Any<CancellationToken>());
        await graphStore.Received(1).DeleteByFilePathsAsync(
            RepoId,
            Arg.Is<IReadOnlyCollection<string>>(f => f.Count == 2),
            Arg.Any<CancellationToken>());
        await repoStore.Received(1).DeleteFileHashesAsync(
            RepoId,
            Arg.Is<IReadOnlyCollection<string>>(f => f.Count == 2),
            Arg.Any<CancellationToken>());

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("staleFilesRemoved").GetInt32());
    }

    [Fact]
    public async Task PostIndexResults_FullSnapshot_SweepsMissingFiles()
    {
        // Snapshot sweep: full-index commit mang toàn bộ file hashes → mọi symbol
        // thuộc file ngoài snapshot bị xoá (kể cả khi hash cache đã bị force_reindex wipe).
        var (factory, repoStore, graphStore, vectorStore) = CreateFactory();
        using var _ = factory;

        vectorStore.SweepMissingFilesAsync(RepoId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(["src/Ghost.cs"]);
        repoStore.SweepFileHashesAsync(RepoId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(["src/Ghost.cs", "src/OrphanHash.cs"]);

        var response = await factory.Client.PostAsJsonAsync("/api/index/results", new
        {
            projectName = "TestProj",
            symbols = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            fileHashes = new Dictionary<string, string> { ["src/Kept.cs"] = "h1" },
            fullFileSnapshot = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await vectorStore.Received(1).SweepMissingFilesAsync(
            RepoId,
            Arg.Is<IReadOnlyCollection<string>>(s => s.Count == 1 && s.Contains("src/Kept.cs")),
            Arg.Any<CancellationToken>());
        // Graph cleanup nhận UNION của (symbol sweep ∪ hash sweep), distinct.
        await graphStore.Received(1).DeleteByFilePathsAsync(
            RepoId,
            Arg.Is<IReadOnlyCollection<string>>(f =>
                f.Count == 2 && f.Contains("src/Ghost.cs") && f.Contains("src/OrphanHash.cs")),
            Arg.Any<CancellationToken>());

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("staleFilesRemoved").GetInt32());
    }

    [Fact]
    public async Task PostIndexResults_IncrementalBatch_DoesNotSweep()
    {
        // Watch batch (fullFileSnapshot=false) chỉ mang hash của file thay đổi —
        // sweep trên set đó sẽ xoá nhầm cả repo. Phải KHÔNG sweep.
        var (factory, repoStore, _, vectorStore) = CreateFactory();
        using var _ = factory;

        var response = await factory.Client.PostAsJsonAsync("/api/index/results", new
        {
            projectName = "TestProj",
            symbols = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            fileHashes = new Dictionary<string, string> { ["src/Changed.cs"] = "h2" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await vectorStore.DidNotReceive().SweepMissingFilesAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await repoStore.DidNotReceive().SweepFileHashesAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostIndexResults_PureEmptyRequest_StillRejected()
    {
        var (factory, _, _, _) = CreateFactory();
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync("/api/index/results", new
        {
            projectName = "TestProj",
            symbols = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            fileHashes = new Dictionary<string, string>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // === /api/agent/heartbeat ===

    [Fact]
    public async Task PostHeartbeat_KnownProject_UpsertsStatus()
    {
        var (factory, repoStore, _, _) = CreateFactory();
        using var _1 = factory;

        var lastSync = DateTimeOffset.UtcNow.AddMinutes(-2);
        var response = await factory.Client.PostAsJsonAsync("/api/agent/heartbeat", new
        {
            projectName = "TestProj",
            agentVersion = "1.2.0",
            lastSyncUtc = lastSync,
            consecutiveFailures = 3,
            lastError = "MSBuildWorkspace exploded"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await repoStore.Received(1).UpsertWatchHeartbeatAsync(
            RepoId, "1.2.0",
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value == lastSync),
            3, "MSBuildWorkspace exploded",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostHeartbeat_UnknownProject_Returns404()
    {
        var (factory, _, _, _) = CreateFactory();
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync("/api/agent/heartbeat", new
        {
            projectName = "NoSuchProject",
            agentVersion = "1.2.0"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
