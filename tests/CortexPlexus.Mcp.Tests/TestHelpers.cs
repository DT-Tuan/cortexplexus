using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Helper factory cho việc tạo mocked dependencies dùng cho MCP tool tests.
/// HybridQueryRouter là sealed class — không mock trực tiếp được, nên ta construct
/// bằng các mocked store/service.
/// </summary>
internal static class TestHelpers
{
    /// <summary>Default "current server space" for router tests (ADR-018).</summary>
    public static readonly EmbeddingSpace TestSpace = new("vertex", "text-embedding-005", 768);

    public static HybridQueryRouter BuildRouter(
        IVectorStore? vectorStore = null,
        IFullTextStore? fullTextStore = null,
        IEmbeddingService? embeddingService = null,
        IQueryExpander? queryExpander = null,
        IRepositoryStore? repositoryStore = null,
        EmbeddingSpace? currentSpace = null)
    {
        vectorStore ??= Substitute.For<IVectorStore>();
        fullTextStore ??= Substitute.For<IFullTextStore>();
        embeddingService ??= Substitute.For<IEmbeddingService>();
        repositoryStore ??= BuildRepoStore();

        // QUAN TRỌNG: chỉ set IsEnabled=false khi tạo mock mới.
        // Nếu test đã truyền queryExpander custom → tôn trọng config của test đó.
        if (queryExpander is null)
        {
            queryExpander = Substitute.For<IQueryExpander>();
            queryExpander.IsEnabled.Returns(false);
        }

        return new HybridQueryRouter(
            vectorStore,
            fullTextStore,
            embeddingService,
            queryExpander,
            repositoryStore,
            currentSpace ?? TestSpace,
            NullLogger<HybridQueryRouter>.Instance);
    }

    public static ContextCompressor BuildCompressor()
        => new();

    public static IRepositoryStore BuildRepoStore(params RepositoryInfo[] repos)
    {
        var store = Substitute.For<IRepositoryStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>(repos.ToList()));
        return store;
    }

    public static RepositoryInfo MakeRepo(
        string name,
        Guid? id = null,
        string? path = null,
        string? embeddingProvider = null,
        string? embeddingModel = null,
        int? embeddingDim = null)
        => new(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            Path: path ?? $"/test/{name}",
            CreatedAt: DateTimeOffset.UtcNow,
            LastIndexed: DateTimeOffset.UtcNow,
            EmbeddingProvider: embeddingProvider,
            EmbeddingModel: embeddingModel,
            EmbeddingDim: embeddingDim);

    public static SearchResult MakeResult(
        string fqn,
        string name,
        string kind = "method",
        string source = "TestSource")
        => new(
            Fqn: fqn,
            Name: name,
            Kind: kind,
            Signature: $"{name}()",
            FilePath: $"/repo/{name}.cs",
            StartLine: 10,
            Score: 0.9,
            Source: source);
}
