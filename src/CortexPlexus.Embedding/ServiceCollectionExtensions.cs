using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CortexPlexus.Embedding;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCortexPlexusEmbedding(
        this IServiceCollection services,
        Action<EmbeddingOptions> configure)
    {
        // Configure with provider-aware defaults applied AFTER the user's configure runs.
        // This lets us auto-detect MaxParallelBatches based on Provider when the user
        // hasn't set it explicitly (R19).
        services.Configure<EmbeddingOptions>(opts =>
        {
            configure(opts);
            ApplyProviderDefaults(opts);
        });

        var options = new EmbeddingOptions();
        configure(options);
        ApplyProviderDefaults(options);

        services.AddHttpClient(nameof(GeminiEmbeddingService));

        // Ollama on CPU-constrained servers can take minutes per request when the queue
        // is saturated (observed 4.6+ min on a 3-core LXC with parallelism=4 clients).
        // Default HttpClient timeout is 100s which is far too aggressive — raise to 10 min.
        services.AddHttpClient(nameof(OllamaEmbeddingService), client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        services.AddHttpClient(nameof(VertexEmbeddingService));

        if (options.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
        }
        else if (options.Provider.Equals("vertex", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmbeddingService, VertexEmbeddingService>();
        }
        else
        {
            services.AddSingleton<IEmbeddingService, GeminiEmbeddingService>();
        }

        return services;
    }

    /// <summary>
    /// Apply provider-aware defaults for options that the user did not explicitly set.
    /// Currently handles <see cref="EmbeddingOptions.MaxParallelBatches"/>:
    /// <list type="bullet">
    /// <item>Ollama → 1 (single-thread CPU-bound model; parallelism wastes work via queue contention)</item>
    /// <item>Gemini / Vertex → 4 (managed API, request-throughput bound; parallelism is free throughput)</item>
    /// </list>
    /// Public so unit tests and downstream consumers can apply the same defaults
    /// when constructing options manually (without going through DI).
    /// </summary>
    public static void ApplyProviderDefaults(EmbeddingOptions options)
    {
        if (options.MaxParallelBatches is null)
        {
            options.MaxParallelBatches =
                options.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) ? 1 : 4;
        }
    }
}
