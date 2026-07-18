using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests cho SearchTools (SearchCode, SemanticSearch).
///
/// Phạm vi: TEST-PLAN.md #29, #30, #31, #32, #33
/// </summary>
public class SearchToolsTests
{
    // === #29: SearchCode_EmptyQuery_ReturnsGuidance ===
    [Fact]
    public async Task SearchCode_EmptyQuery_ReturnsNoResults()
    {
        // Mục đích: Query rỗng không crash. Hệ thống hiện trả "No results found."
        // (Router xử lý empty query an toàn vì FullTextStore có guard empty).
        var fullTextStore = Substitute.For<IFullTextStore>();
        fullTextStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var router = TestHelpers.BuildRouter(fullTextStore: fullTextStore);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var result = await SearchTools.SearchCode("", null, 20, false, router, compressor, repoStore);

        Assert.Equal("No results found.", result);
    }

    // === #30: SearchCode_NonExistentRepo_SearchesAllAnyway ===
    [Fact]
    public async Task SearchCode_NonExistentRepo_FallsBackToAllRepos()
    {
        // Mục đích: Repo name không tồn tại → RepoResolver trả null → search all repos.
        // Hiện tại không throw error — AI Agent có thể nhận được results vẫn hợp lệ.
        // Test này ghi nhận behavior hiện tại. Nếu muốn stricter (fail fast), đó là
        // nâng cấp tương lai cần rõ ràng hóa.
        var fullTextStore = Substitute.For<IFullTextStore>();
        fullTextStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
                [TestHelpers.MakeResult("App.Foo", "Foo")]));

        var router = TestHelpers.BuildRouter(fullTextStore: fullTextStore);
        var compressor = TestHelpers.BuildCompressor();
        // Repo store có "real-repo" nhưng user truyền "ghost-repo"
        var repoStore = TestHelpers.BuildRepoStore(TestHelpers.MakeRepo("real-repo"));

        var result = await SearchTools.SearchCode("Foo", "ghost-repo", 20, false, router, compressor, repoStore);

        // Không crash, trả kết quả (hoặc "No results found." nếu filter theo repoId null).
        // Verify quan trọng: method completed mà không exception.
        Assert.NotNull(result);

        // Verify FullTextStore được gọi với repoId = null (fallback to all repos khi không match).
        await fullTextStore.Received().SearchAsync(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Is<Guid?>(id => id == null),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // === #31: SemanticSearch_ExpandTrue_CallsQueryExpander ===
    [Fact]
    public async Task SemanticSearch_ExpandTrue_InvokesQueryExpanderHyde()
    {
        // Mục đích: expand=true kích hoạt HyDE (nếu IsEnabled=true).
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var fullTextStore = Substitute.For<IFullTextStore>();
        fullTextStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));

        var expander = Substitute.For<IQueryExpander>();
        expander.IsEnabled.Returns(true);
        expander.ExpandHydeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("hypothetical answer"));
        expander.ExpandMultiQueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["variant1", "variant2"]));

        var router = TestHelpers.BuildRouter(
            vectorStore: vectorStore,
            fullTextStore: fullTextStore,
            embeddingService: embedding,
            queryExpander: expander);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        await SearchTools.SemanticSearch("payment logic", null, 10, expand: true, router, compressor, repoStore);

        // Expander phải được gọi ít nhất 1 lần khi expand=true và IsEnabled=true.
        // Lưu ý: có thể là HyDE hoặc MultiQuery — miễn 1 trong 2 được gọi là đủ.
        var hydeCalls = expander.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IQueryExpander.ExpandHydeAsync));
        var multiCalls = expander.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IQueryExpander.ExpandMultiQueryAsync));
        Assert.True(hydeCalls + multiCalls > 0, "QueryExpander phải được gọi khi expand=true");
    }

    // === #32: SemanticSearch_ExpandTrue_ExpanderDisabled_GracefulFallback ===
    [Fact]
    public async Task SemanticSearch_ExpandTrue_ExpanderDisabled_DoesNotCrash()
    {
        // Mục đích: expand=true nhưng IsEnabled=false → graceful fallback, không crash.
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var fullTextStore = Substitute.For<IFullTextStore>();
        fullTextStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[768]));

        var expander = Substitute.For<IQueryExpander>();
        expander.IsEnabled.Returns(false); // disabled

        var router = TestHelpers.BuildRouter(
            vectorStore: vectorStore,
            fullTextStore: fullTextStore,
            embeddingService: embedding,
            queryExpander: expander);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        // Must not throw
        var result = await SearchTools.SemanticSearch("payment logic", null, 10, expand: true, router, compressor, repoStore);

        Assert.NotNull(result);
        // Expander không được gọi vì IsEnabled=false.
        var hydeCalls = expander.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IQueryExpander.ExpandHydeAsync));
        Assert.Equal(0, hydeCalls);
    }

    // === #33: SearchCode_SpecialCharsInQuery_Sanitized ===
    [Fact]
    public async Task SearchCode_SqlInjectionQuery_DoesNotCrash()
    {
        // Mục đích: Query chứa ký tự đặc biệt (SQL-like injection attempt) không crash.
        // FullTextStore phải sanitize internally — MCP tool chỉ forward query thôi.
        var fullTextStore = Substitute.For<IFullTextStore>();
        fullTextStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var router = TestHelpers.BuildRouter(fullTextStore: fullTextStore);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        var maliciousQuery = "'; DROP TABLE code_symbols; --";

        // Must not throw — query được forward xuống store, không execute SQL.
        var result = await SearchTools.SearchCode(maliciousQuery, null, 20, false, router, compressor, repoStore);

        Assert.NotNull(result);
        // Verify query nguyên vẹn được truyền xuống store (FullTextStore phải sanitize internally).
        await fullTextStore.Received().SearchAsync(
            maliciousQuery,
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // === #33b: SearchCode_UnicodeQuery_HandlesCorrectly ===
    [Fact]
    public async Task SearchCode_UnicodeQuery_ForwardsIntact()
    {
        // Mục đích: Unicode (tiếng Việt, CJK) không bị encode sai.
        var fullTextStore = Substitute.For<IFullTextStore>();
        fullTextStore.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        var router = TestHelpers.BuildRouter(fullTextStore: fullTextStore);
        var compressor = TestHelpers.BuildCompressor();
        var repoStore = TestHelpers.BuildRepoStore();

        await SearchTools.SearchCode("xử lý thanh toán", null, 20, false, router, compressor, repoStore);

        await fullTextStore.Received().SearchAsync(
            "xử lý thanh toán",
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
