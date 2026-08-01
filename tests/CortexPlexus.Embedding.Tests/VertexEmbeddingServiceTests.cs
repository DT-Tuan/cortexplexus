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

    // ======================================================================
    // ADR-029 — OAuth2 service-account auth
    // ======================================================================

    // === Auth-mode precedence: an explicit credential path wins over a key ===
    [Fact]
    public void VertexUsesOAuth_CredentialPathSet_WinsOverApiKey()
    {
        var opts = VertexOptions(o =>
        {
            o.VertexApiKey = "express-key";
            o.ApiKey = "gemini-key";
            o.VertexCredentialPath = "/etc/gcp/sa.json";
        });

        Assert.True(opts.VertexUsesOAuth);
    }

    // === Auth-mode precedence: an express key still selects query-string auth,
    // so existing express-mode deployments keep working untouched ===
    [Fact]
    public void VertexUsesOAuth_ApiKeyOnly_StaysExpressMode()
    {
        Assert.False(VertexOptions(o => o.VertexCredentialPath = null).VertexUsesOAuth);
    }

    // === The legacy VertexApiKey ⇒ ApiKey fallback also selects express mode:
    // the URL builder reads ApiKey when VertexApiKey is blank, so the predicate
    // must agree or the request would carry no credential at all ===
    [Fact]
    public void VertexUsesOAuth_GeminiApiKeyFallbackOnly_StaysExpressMode()
    {
        var opts = VertexOptions(o =>
        {
            o.VertexApiKey = null;
            o.ApiKey = "gemini-key";
        });

        Assert.False(opts.VertexUsesOAuth);
    }

    // === No credential of any kind ⇒ Application Default Credentials ===
    [Fact]
    public void VertexUsesOAuth_NoCredentialConfigured_FallsBackToAdc()
    {
        var opts = VertexOptions(o =>
        {
            o.VertexApiKey = null;
            o.ApiKey = null;
            o.VertexCredentialPath = null;
        });

        Assert.True(opts.VertexUsesOAuth);
    }

    // === Whitespace is not a credential — a blank-but-present env var must not
    // silently select express mode and send "?key= " ===
    [Fact]
    public void VertexUsesOAuth_WhitespaceApiKey_TreatedAsAbsent()
    {
        var opts = VertexOptions(o =>
        {
            o.VertexApiKey = "   ";
            o.ApiKey = "";
        });

        Assert.True(opts.VertexUsesOAuth);
    }

    // === OAuth mode: the URL carries NO credential. This is the load-bearing
    // assertion of the cutover — a leftover ?key= alongside a bearer header is
    // rejected, and it would also leak the key into access/proxy logs ===
    [Fact]
    public async Task EmbedAsync_OAuthMode_UrlHasNoKeyQueryString()
    {
        var handler = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var service = BuildService(handler, VertexOptions(o =>
        {
            o.VertexApiKey = null;
            o.ApiKey = null;
            o.VertexCredentialPath = "/etc/gcp/sa.json";
            o.VertexLocation = "us-central1";
        }));

        await service.EmbedAsync("x");

        var uri = handler.RequestsReceived[0].RequestUri!;
        Assert.Equal("", uri.Query);
        Assert.DoesNotContain("key=", uri.AbsoluteUri);
        // Everything else about the request shape is unchanged by the carrier.
        Assert.Equal("us-central1-aiplatform.googleapis.com", uri.Host);
        Assert.Equal(
            "/v1/projects/test-project/locations/us-central1/publishers/google/models/text-embedding-005:predict",
            uri.AbsolutePath);
    }

    // === The handler stamps Authorization: Bearer on the outgoing request ===
    [Fact]
    public async Task VertexAuthHandler_StampsBearerToken()
    {
        var inner = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var authHandler = new VertexAuthHandler(_ => Task.FromResult("tok-abc"))
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(authHandler);

        await client.PostAsync("https://us-central1-aiplatform.googleapis.com/v1/x:predict",
            new StringContent("{}"));

        var auth = inner.RequestsReceived[0].Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("tok-abc", auth.Parameter);
    }

    // === A token is fetched per request, not once per handler: the credential
    // library owns the ~1 h refresh, so caching here would pin an expired token ===
    [Fact]
    public async Task VertexAuthHandler_FetchesTokenPerRequest()
    {
        var calls = 0;
        var inner = FakeHttpMessageHandler.Ok(PredictBody(1, 768));
        var authHandler = new VertexAuthHandler(_ =>
        {
            calls++;
            return Task.FromResult($"tok-{calls}");
        })
        { InnerHandler = inner };
        using var client = new HttpClient(authHandler);

        await client.PostAsync("https://host/v1/x:predict", new StringContent("{}"));
        await client.PostAsync("https://host/v1/x:predict", new StringContent("{}"));

        Assert.Equal(2, calls);
        Assert.Equal("tok-2", inner.RequestsReceived[1].Headers.Authorization!.Parameter);
    }

    // === A failed credential load must NOT be memoised. A bind mount that is
    // not ready at container boot would otherwise poison the process until it
    // restarts — the second call here re-reads and reports the NEW failure ===
    [Fact]
    public async Task VertexTokenProvider_FailedLoad_IsRetriedNotCached()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-sa-{Guid.NewGuid():N}.json");
        var provider = new VertexTokenProvider(path);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.GetAccessTokenAsync());

        // The file now exists but is not a service-account key. A cached faulted
        // task would replay the missing-file error; a real re-read reports a
        // different one, which is what distinguishes the two.
        await File.WriteAllTextAsync(path, "{\"type\":\"authorized_user\"}");
        try
        {
            var second = await Record.ExceptionAsync(() => provider.GetAccessTokenAsync());

            Assert.NotNull(second);
            Assert.IsNotType<FileNotFoundException>(second);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // === Fingerprints are stable, short, and not the token itself ===
    [Fact]
    public void TokenFingerprint_IsStableAndDoesNotLeakTheToken()
    {
        const string token = "ya29.super-secret-bearer-value";

        var fp = VertexTokenProvider.TokenFingerprint(token);

        Assert.Equal(fp, VertexTokenProvider.TokenFingerprint(token));
        Assert.NotEqual(fp, VertexTokenProvider.TokenFingerprint(token + "x"));
        Assert.Equal(8, fp.Length);
        Assert.DoesNotContain(fp, token);
    }
}
