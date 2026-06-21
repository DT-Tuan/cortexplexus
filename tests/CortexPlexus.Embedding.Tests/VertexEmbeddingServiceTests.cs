using System.Net;
using System.Text.Json;
using CortexPlexus.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Embedding.Tests;

/// <summary>
/// Tests for <see cref="VertexEmbeddingService"/> (ADR-017) — Vertex AI
/// <c>:predict</c> embedding via HTTP mock (no real API calls).
///
/// Key behaviours asserted:
/// - Wire shape: host = {loc}-aiplatform.googleapis.com (global ⇒ bare host),
///   path .../models/{modelId}:predict, API key on ?key= query string.
/// - 768-dim output from predictions[].embeddings.values.
/// - EmbedBatchAsync SUB-BATCHES to VertexInstancesPerCall (batch > cap ⇒
///   multiple :predict calls), unlike Gemini's single 100-instance call.
/// - Graceful empty on 401/500; Polly retry on 429/500/transient.
/// </summary>
public class VertexEmbeddingServiceTests
{
    private static EmbeddingOptions VertexOptions(Action<EmbeddingOptions>? tweak = null)
    {
        var opts = new EmbeddingOptions
        {
            Provider = "vertex",
            VertexProjectId = "test-project",
            VertexLocation = "global",
            VertexModelId = "text-embedding-005",
            VertexInstancesPerCall = 5,
            VertexApiKey = "test-key",
            Dimensions = 768
        };
        tweak?.Invoke(opts);
        return opts;
    }

    private static VertexEmbeddingService BuildService(
        FakeHttpMessageHandler handler, EmbeddingOptions? options = null)
    {
        var factory = new FakeHttpClientFactory(handler);
        return new VertexEmbeddingService(
            factory, Options.Create(options ?? VertexOptions()),
            NullLogger<VertexEmbeddingService>.Instance);
    }

    /// <summary>Build a :predict response with <paramref name="count"/> predictions, each of <paramref name="dim"/> dims.</summary>
    private static string PredictBody(int count, int dim)
    {
        var values = "[" + string.Join(",", Enumerable.Repeat("0.01", dim)) + "]";
        var pred = "{\"embeddings\":{\"values\":" + values + "}}";
        var preds = string.Join(",", Enumerable.Repeat(pred, count));
        return "{\"predictions\":[" + preds + "]}";
    }

    // === Happy path: 768-dim output ===
    [Fact]
    public async Task EmbedAsync_Success_Returns768DimVector()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var service = BuildService(handler);

        var result = await service.EmbedAsync("def foo(): pass");

        Assert.Equal(768, result.Length);
        Assert.Equal(0.01f, result[0]);
        Assert.Equal(1, handler.CallCount);
    }

    // === Wire shape: global location ⇒ bare host, no region prefix ===
    [Fact]
    public async Task EmbedAsync_GlobalLocation_UsesBareHostAndPredictPath()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var service = BuildService(handler);

        await service.EmbedAsync("x");

        var uri = handler.RequestsReceived[0].RequestUri!;
        Assert.Equal("aiplatform.googleapis.com", uri.Host);
        Assert.Equal(
            "/v1/projects/test-project/locations/global/publishers/google/models/text-embedding-005:predict",
            uri.AbsolutePath);
        Assert.Contains("key=test-key", uri.Query);
    }

    // === Wire shape: regional location prefixes the host ===
    [Fact]
    public async Task EmbedAsync_RegionalLocation_PrefixesHost()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var service = BuildService(handler, VertexOptions(o => o.VertexLocation = "us-central1"));

        await service.EmbedAsync("x");

        var uri = handler.RequestsReceived[0].RequestUri!;
        Assert.Equal("us-central1-aiplatform.googleapis.com", uri.Host);
        Assert.Contains("/locations/us-central1/", uri.AbsolutePath);
    }

    // === Auth failure: 401 ⇒ empty, NO retry ===
    [Fact]
    public async Task EmbedAsync_401Unauthorized_ReturnsEmptyNoRetry()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "bad key");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("x");

        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount); // no retry
    }

    // === Retry: 429 ⇒ 1 + 3 retries = 4 calls, then empty ===
    [Fact]
    public async Task EmbedAsync_429RateLimit_RetriesThenEmpty()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.TooManyRequests, "rate limited");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("x");

        Assert.Empty(result);
        Assert.Equal(4, handler.CallCount);
    }

    // === Retry: 500 ⇒ 4 calls ===
    [Fact]
    public async Task EmbedAsync_500InternalError_Retries()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.InternalServerError, "boom");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("x");

        Assert.Empty(result);
        Assert.Equal(4, handler.CallCount);
    }

    // === Network exception ⇒ retried then empty ===
    [Fact]
    public async Task EmbedAsync_NetworkException_ReturnsEmptyGracefully()
    {
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("connection refused"));
        var service = BuildService(handler);

        var result = await service.EmbedAsync("x");

        Assert.Empty(result);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task EmbedAsync_MalformedJson_ReturnsEmpty()
    {
        var handler = FakeHttpMessageHandler.Ok("{not json}");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("x");

        Assert.Empty(result);
    }

    // === Sub-batch: batch > cap ⇒ ceil(n/cap) :predict calls ===
    [Fact]
    public async Task EmbedBatchAsync_OverCap_SubBatchesByInstancesPerCall()
    {
        // 12 texts, cap 5 ⇒ ceil(12/5) = 3 calls (5 + 5 + 2).
        var handler = FakeHttpMessageHandler.Ok(PredictBody(5, 768));
        var service = BuildService(handler);

        var texts = Enumerable.Range(1, 12).Select(i => $"text{i}").ToList();
        var results = await service.EmbedBatchAsync(texts);

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(12, results.Count); // never drop items
    }

    // === Sub-batch: under cap ⇒ single call ===
    [Fact]
    public async Task EmbedBatchAsync_UnderCap_SingleCall()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(3, 768));
        var service = BuildService(handler);

        var results = await service.EmbedBatchAsync(new[] { "a", "b", "c" });

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(3, results.Count);
    }

    // === Sub-batch wire-level: each :predict body carries ≤ cap instances ===
    [Fact]
    public async Task EmbedBatchAsync_EachRequestCarriesAtMostCapInstances()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(5, 768));
        var service = BuildService(handler);

        var texts = Enumerable.Range(1, 12).Select(i => $"text{i}").ToList();
        await service.EmbedBatchAsync(texts);

        var instanceCounts = new List<int>();
        foreach (var req in handler.RequestsReceived)
        {
            var body = await req.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            instanceCounts.Add(doc.RootElement.GetProperty("instances").GetArrayLength());
        }

        Assert.All(instanceCounts, c => Assert.True(c <= 5, $"sub-batch had {c} > cap 5"));
        Assert.Equal(12, instanceCounts.Sum());
    }

    // === Batch error ⇒ empty array per text, count preserved ===
    [Fact]
    public async Task EmbedBatchAsync_ServerError_ReturnsEmptyArrayPerText()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.InternalServerError);
        var service = BuildService(handler);

        var results = await service.EmbedBatchAsync(new[] { "a", "b", "c" });

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Empty(r));
    }

    // === outputDimensionality is sent in the request parameters ===
    [Fact]
    public async Task EmbedAsync_SendsOutputDimensionality()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var service = BuildService(handler);

        await service.EmbedAsync("x");

        var body = await handler.RequestsReceived[0].Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var dim = doc.RootElement.GetProperty("parameters").GetProperty("outputDimensionality").GetInt32();
        Assert.Equal(768, dim);
    }
}
