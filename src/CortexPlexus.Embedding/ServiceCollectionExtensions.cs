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
    /// <item>Gemini → 4 (single 100-instance batch call; plenty under quota)</item>
    /// <item>Vertex → 8 (sequential 5-instance sub-batches ⇒ pipeline parallelism is the only concurrency; 26 texts/s measured, ADR-017)</item>
    /// </list>
    /// Public so unit tests and downstream consumers can apply the same defaults
    /// when constructing options manually (without going through DI).
    /// </summary>
    public static void ApplyProviderDefaults(EmbeddingOptions options)
    {
        if (options.MaxParallelBatches is null)
        {
            options.MaxParallelBatches = options.Provider.ToLowerInvariant() switch
            {
                // Ollama: single-thread CPU-bound — parallelism wastes work (R17).
                "ollama" => 1,
                // Vertex: each EmbedBatchAsync issues sub-batches of 5 SEQUENTIALLY,
                // so pipeline-level parallelism is the only concurrency. 8 concurrent
                // :predict calls measured 26.4 texts/s on us-central1 (>20 target,
                // 5.7× Ollama) vs 13.1 at 4 — ADR-017 benchmark 2026-06-21.
                "vertex" => 8,
                // Gemini: single 100-instance batch call; 4 is plenty under quota.
                _ => 4
            };
        }
    }
}
