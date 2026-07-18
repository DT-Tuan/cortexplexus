using System.Net;
using System.Net.Http.Json;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Embedding;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CortexPlexus.Api.Tests;

/// <summary>
/// ADR-018 write-path guard trên POST /api/index/results:
/// - incremental upload (fullFileSnapshot=false) vào repo stamped KHÁC space hiện tại
///   → 409 với recovery message, TRƯỚC mọi store write (mixed-space repo là bất khả);
/// - full-snapshot commit → được nhận + stamp lại space (đường migrate);
/// - incremental + space khớp → hoạt động bình thường.
/// Server hiện tại configure Provider=vertex → space vertex:text-embedding-005:768.
/// </summary>
public class AgentIndexResultsSpaceGuardTests
{
    private static readonly Guid RepoId = Guid.NewGuid();

    private static RepositoryInfo Repo(string? provider, string? model) =>
        new(RepoId, "TestProj", "_agent/TestProj",
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddHours(-1),
            provider, model, provider is null ? null : 768);

    private static (TestApiFactory factory, IRepositoryStore repoStore, IGraphStore graphStore, IVectorStore vectorStore)
        CreateFactory(string? stampedProvider, string? stampedModel)
    {
        var repoStore = Substitute.For<IRepositoryStore>();
        var graphStore = Substitute.For<IGraphStore>();
        var vectorStore = Substitute.For<IVectorStore>();

        repoStore.GetByPathAsync("_agent/TestProj", Arg.Any<CancellationToken>())
            .Returns(Repo(stampedProvider, stampedModel));
        vectorStore.UpsertAsync(
                Arg.Any<IEnumerable<CodeSymbol>>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Any<CancellationToken>())
            .Returns(new VectorUpsertResult(1, 0, 0));
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
            services.Configure<EmbeddingOptions>(o =>
            {
                o.Provider = "vertex";
                o.VertexModelId = "text-embedding-005";
                o.Dimensions = 768;
            });
        });

        return (factory, repoStore, graphStore, vectorStore);
    }

    // The guard + stamp key on fullReindex (all symbols re-embedded), NOT fullFileSnapshot
    // (complete file-hash list, for the sweep). A real full re-index sends both true; an
    // incremental IndexAsync sends fullFileSnapshot=true but fullReindex=false.
    private static object EmbeddableSymbolPayload(bool fullReindex, bool fullFileSnapshot = false) => new
    {
        projectName = "TestProj",
        symbols = new[]
        {
            new { fqn = "App.OrderService", name = "OrderService", kind = "class" },
        },
        relationships = Array.Empty<object>(),
        fileHashes = new Dictionary<string, string> { ["src/OrderService.cs"] = "h1" },
        fullFileSnapshot,
        fullReindex,
    };

    [Fact]
    public async Task IncrementalUpload_StampedForeignSpace_Returns409_BeforeAnyWrite()
    {
        var (factory, repoStore, graphStore, vectorStore) = CreateFactory("ollama", "nomic-embed-text");
        using var _ = factory;

        var response = await factory.Client.PostAsJsonAsync(
            "/api/index/results", EmbeddableSymbolPayload(fullReindex: false));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ollama:nomic-embed-text", body);
        Assert.Contains("vertex:text-embedding-005", body);
        Assert.Contains("force_reindex", body);

        // Atomic refusal: nothing may have been persisted anywhere.
        await graphStore.DidNotReceive().UpsertNodesAsync(Arg.Any<IEnumerable<CodeSymbol>>(), Arg.Any<CancellationToken>());
        await vectorStore.DidNotReceive().UpsertAsync(
            Arg.Any<IEnumerable<CodeSymbol>>(), Arg.Any<IReadOnlyDictionary<string, float[]>>(), Arg.Any<CancellationToken>());
        await repoStore.DidNotReceive().UpdateFileHashAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repoStore.DidNotReceive().UpdateLastIndexedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncrementalWithFullSnapshot_StampedForeignSpace_Still409_AndDoesNotRestamp()
    {
        // The exact bug the finder sweep caught: an incremental IndexAsync sends
        // fullFileSnapshot=true (for the sweep) while embedding only a subset. It must NOT
        // be mistaken for a full re-index — the guard must still fire and the stamp must
        // not be rewritten to a false "pure" space.
        var (factory, repoStore, _, _) = CreateFactory("ollama", "nomic-embed-text");
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync(
            "/api/index/results", EmbeddableSymbolPayload(fullReindex: false, fullFileSnapshot: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await repoStore.DidNotReceive().UpdateEmbeddingSpaceAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FullReindex_StampedForeignSpace_Accepted_AndRestamped()
    {
        // Đây chính là đường migrate: force_reindex + full re-index phải ĐƯỢC nhận
        // dù stamp cũ lệch, và stamp được ghi lại theo space hiện tại.
        var (factory, repoStore, _, _) = CreateFactory("ollama", "nomic-embed-text");
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync(
            "/api/index/results", EmbeddableSymbolPayload(fullReindex: true, fullFileSnapshot: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await repoStore.Received(1).UpdateEmbeddingSpaceAsync(
            RepoId, "vertex", "text-embedding-005", 768, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncrementalUpload_MatchedSpace_Accepted_NoRestamp()
    {
        var (factory, repoStore, _, _) = CreateFactory("vertex", "text-embedding-005");
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync(
            "/api/index/results", EmbeddableSymbolPayload(fullReindex: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Incremental không được stamp lại — stamp chỉ đổi trên full re-index.
        await repoStore.DidNotReceive().UpdateEmbeddingSpaceAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncrementalUpload_UnstampedRepo_Accepted()
    {
        // Legacy repo chưa stamp: incremental vẫn chạy như trước (non-breaking rollout).
        var (factory, _, _, vectorStore) = CreateFactory(stampedProvider: null, stampedModel: null);
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync(
            "/api/index/results", EmbeddableSymbolPayload(fullReindex: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await vectorStore.Received(1).UpsertAsync(
            Arg.Any<IEnumerable<CodeSymbol>>(), Arg.Any<IReadOnlyDictionary<string, float[]>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletionOnlyIncremental_ForeignSpace_StillAccepted()
    {
        // Deletion-only sync mang 0 embeddable symbol — không đụng vector space nào,
        // guard không được chặn (agent phải dọn được file xoá kể cả giữa migration).
        var (factory, repoStore, _, _) = CreateFactory("ollama", "nomic-embed-text");
        using var _1 = factory;

        var response = await factory.Client.PostAsJsonAsync("/api/index/results", new
        {
            projectName = "TestProj",
            symbols = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            fileHashes = new Dictionary<string, string>(),
            deletedFiles = new[] { "src/Gone.cs" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await repoStore.Received(1).DeleteFileHashesAsync(
            RepoId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }
}
