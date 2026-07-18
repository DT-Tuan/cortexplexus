using CortexPlexus.App.Api;
using CortexPlexus.App.Indexing;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CortexPlexus.Api.Tests;

/// <summary>
/// Minimal in-memory HTTP server để test REST API endpoints mà không cần
/// chạy full CortexPlexus.App (tránh phải connect PostgreSQL, Ollama, v.v.).
///
/// Cách dùng:
/// <code>
/// using var factory = TestApiFactory.Create(services =>
/// {
///     services.AddSingleton&lt;IGraphStore&gt;(mockGraphStore);
///     services.AddSingleton&lt;IRepositoryStore&gt;(mockRepoStore);
/// });
/// var client = factory.Client;
/// var response = await client.GetAsync("/api/repositories");
/// </code>
/// </summary>
public sealed class TestApiFactory : IDisposable
{
    private readonly IHost _host;
    public HttpClient Client { get; }

    private TestApiFactory(IHost host, HttpClient client)
    {
        _host = host;
        Client = client;
    }

    /// <summary>
    /// Build a test server with mockable services. Default: all store/router interfaces
    /// are mocked via NSubstitute. Caller có thể override bằng action configureServices.
    /// </summary>
    public static TestApiFactory Create(Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddFilter(_ => false));
                    services.AddRouting();

                    // Default mocks — tests có thể override.
                    services.AddSingleton<IGraphStore>(_ => Substitute.For<IGraphStore>());
                    services.AddSingleton<IRepositoryStore>(_ => Substitute.For<IRepositoryStore>());
                    services.AddSingleton<IVectorStore>(_ => Substitute.For<IVectorStore>());
                    services.AddSingleton<IFullTextStore>(_ => Substitute.For<IFullTextStore>());
                    services.AddSingleton<IEmbeddingService>(_ => Substitute.For<IEmbeddingService>());
                    services.AddSingleton<IQueryExpander>(_ =>
                    {
                        var e = Substitute.For<IQueryExpander>();
                        e.IsEnabled.Returns(false);
                        return e;
                    });
                    // ADR-018: router + endpoints need the server's current embedding space.
                    services.AddSingleton(new CortexPlexus.Core.Models.EmbeddingSpace("vertex", "text-embedding-005", 768));
                    services.AddSingleton<HybridQueryRouter>();
                    services.AddSingleton<ContextCompressor>();

                    // IndexingPipeline là concrete class — cần register nếu test endpoint /index/*.
                    // Cho phép caller override nếu muốn real/mock.
                    // (Mặc định chưa add — test gọi push/git sẽ fail với 500, muốn test thành công
                    //  phải override trong configureServices.)

                    configureServices?.Invoke(services);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGraphApi();
                        endpoints.MapAgentApi();
                    });
                });
            });

        var host = builder.Start();
        var testServer = host.GetTestServer();
        var client = testServer.CreateClient();

        return new TestApiFactory(host, client);
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
    }
}
