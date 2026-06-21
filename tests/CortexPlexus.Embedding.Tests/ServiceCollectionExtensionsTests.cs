using CortexPlexus.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Embedding.Tests;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions.AddCortexPlexusEmbedding"/>
/// — specifically the R19 provider-aware MaxParallelBatches auto-detection.
///
/// Context: R17 ground truth showed that Ollama on a single-thread CPU-bound
/// model has <c>zero</c> benefit from client-side parallelism (4 parallel
/// requests = 1 monolithic request, 6.91s vs 6.85s on the test server). R19
/// makes the default per-provider so users get the right behavior without
/// having to know the gory details.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static EmbeddingOptions ResolveOptions(Action<EmbeddingOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddCortexPlexusEmbedding(configure);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
    }

    [Fact]
    public void Ollama_Provider_AutoDefaults_To_SequentialParallelism()
    {
        // Ollama is CPU-bound single-thread on most local installs.
        // Client parallelism wastes work via queue contention (R17 ground truth).
        var opts = ResolveOptions(o =>
        {
            o.Provider = "ollama";
            o.OllamaModel = "nomic-embed-text";
            // No MaxParallelBatches set → should auto-default to 1
        });

        Assert.Equal(1, opts.MaxParallelBatches);
    }

    [Fact]
    public void Gemini_Provider_AutoDefaults_To_FourParallel()
    {
        // Gemini is request-count rate-limited; client parallelism is free
        // throughput within per-minute quotas. Default 4 keeps a healthy
        // headroom under typical limits (300 req/min for free tier).
        var opts = ResolveOptions(o =>
        {
            o.Provider = "gemini";
            o.ApiKey = "test-key";
        });

        Assert.Equal(4, opts.MaxParallelBatches);
    }

    [Fact]
    public void Vertex_Provider_AutoDefaults_To_EightParallel()
    {
        // Vertex issues sub-batches of 5 sequentially per EmbedBatchAsync, so
        // pipeline-level parallelism is the only concurrency. 8 measured 26.4
        // texts/s on us-central1 (>20 target) vs 13.1 at 4 — ADR-017 benchmark.
        var opts = ResolveOptions(o =>
        {
            o.Provider = "vertex";
            o.VertexProjectId = "p";
            o.VertexApiKey = "k";
        });

        Assert.Equal(8, opts.MaxParallelBatches);
    }

    [Fact]
    public void Vertex_Provider_ResolvesVertexEmbeddingService()
    {
        var services = new ServiceCollection();
        services.AddCortexPlexusEmbedding(o =>
        {
            o.Provider = "vertex";
            o.VertexProjectId = "p";
            o.VertexApiKey = "k";
        });
        var sp = services.BuildServiceProvider();

        var svc = sp.GetRequiredService<CortexPlexus.Core.Abstractions.IEmbeddingService>();
        Assert.IsType<VertexEmbeddingService>(svc);
    }

    [Fact]
    public void ExplicitValue_OverridesProviderDefault_OnOllama()
    {
        // Power user explicitly sets MaxParallelBatches=8 on Ollama
        // (e.g. running multi-GPU Ollama with OLLAMA_NUM_PARALLEL=8).
        // Auto-detection must NOT clobber the explicit value.
        var opts = ResolveOptions(o =>
        {
            o.Provider = "ollama";
            o.MaxParallelBatches = 8;
        });

        Assert.Equal(8, opts.MaxParallelBatches);
    }

    [Fact]
    public void ExplicitValue_OverridesProviderDefault_OnGemini()
    {
        // Conservative user wants sequential Gemini calls.
        var opts = ResolveOptions(o =>
        {
            o.Provider = "gemini";
            o.ApiKey = "k";
            o.MaxParallelBatches = 1;
        });

        Assert.Equal(1, opts.MaxParallelBatches);
    }

    [Fact]
    public void ProviderName_IsCaseInsensitive()
    {
        var opts = ResolveOptions(o =>
        {
            o.Provider = "OLLAMA"; // uppercase
        });

        Assert.Equal(1, opts.MaxParallelBatches);
    }

    [Fact]
    public void UnknownProvider_FallsBackTo_GeminiDefault()
    {
        // Anything that isn't "ollama" is treated as Gemini-compatible
        // (matches the existing service-registration branch in AddCortexPlexusEmbedding).
        var opts = ResolveOptions(o =>
        {
            o.Provider = "openai-compatible";
        });

        Assert.Equal(4, opts.MaxParallelBatches);
    }

    [Fact]
    public void ApplyProviderDefaults_DirectInvocation_IsIdempotent()
    {
        // Calling twice should not change the result on the second call.
        var opts = new EmbeddingOptions { Provider = "ollama" };
        ServiceCollectionExtensions.ApplyProviderDefaults(opts);
        var first = opts.MaxParallelBatches;
        ServiceCollectionExtensions.ApplyProviderDefaults(opts);
        var second = opts.MaxParallelBatches;

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }
}
