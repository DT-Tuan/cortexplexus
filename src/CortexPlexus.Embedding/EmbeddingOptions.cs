namespace CortexPlexus.Embedding;

public sealed record EmbeddingOptions
{
    public string Provider { get; set; } = "gemini";
    public string? ApiKey { get; set; }
    public int Dimensions { get; set; } = 768;
    public string GeminiModel { get; set; } = "gemini-embedding-001";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "nomic-embed-text";
    public string TaskType { get; set; } = "CODE_RETRIEVAL_QUERY";
    public int MaxBatchSize { get; set; } = 100;

    // --- Vertex AI provider (ADR-017, opt-in; Ollama stays default) ---

    /// <summary>Google Cloud project id for the Vertex <c>:predict</c> endpoint. Required when <see cref="Provider"/> == "vertex".</summary>
    public string? VertexProjectId { get; set; }

    /// <summary>
    /// Vertex location. Default <c>"us-central1"</c>, which prefixes the host as
    /// <c>{location}-aiplatform.googleapis.com</c>.
    /// <para>
    /// <b>Do not use <c>"global"</c> for embeddings.</b> The global endpoint
    /// (bare <c>aiplatform.googleapis.com</c>, no region prefix) is still
    /// supported, but measured ~11.5 s per <c>:predict</c> call on
    /// <c>text-embedding-005</c> vs ~1.55 s on <c>us-central1</c> (ADR-017
    /// benchmark, 2026-06-21) — a 7.5× regression that drops throughput below
    /// even the local Ollama baseline.
    /// </para>
    /// </summary>
    public string VertexLocation { get; set; } = "us-central1";

    /// <summary>Vertex embedding model id (e.g. <c>text-embedding-005</c>).</summary>
    public string VertexModelId { get; set; } = "text-embedding-005";

    /// <summary>
    /// Max instances per <c>:predict</c> call. Vertex caps this per model
    /// (<c>text-embedding-004/005</c> = 5; <c>gemini-embedding-001</c> via Vertex
    /// may be 1). <see cref="VertexEmbeddingService.EmbedBatchAsync"/> sub-batches
    /// to this cap — differs from Gemini's single 100-instance batch call.
    /// </summary>
    public int VertexInstancesPerCall { get; set; } = 5;

    /// <summary>
    /// Vertex API key (express-mode, sent on the <c>?key=</c> query string — NOT
    /// OAuth/bearer). Supplied at runtime only (UserSecrets / env var); never
    /// committed. Falls back to <see cref="ApiKey"/> if unset.
    /// <para>
    /// Express-mode keys can only be minted through the Vertex AI Studio
    /// console. On an account where you hold just a downloaded service-account
    /// key file, leave this empty and set <see cref="VertexCredentialPath"/> —
    /// see <see cref="VertexUsesOAuth"/> (ADR-029).
    /// </para>
    /// </summary>
    public string? VertexApiKey { get; set; }

    /// <summary>
    /// Path to a Google service-account JSON key file, for OAuth2 bearer auth
    /// (ADR-029). Setting this selects OAuth2 and takes precedence over
    /// <see cref="VertexApiKey"/>.
    /// <para>
    /// Deploy the file as a read-only bind mount and point this at the mount
    /// path — never bake a credential into a container image, because image
    /// layers are immutable and the file stays recoverable from image history
    /// even if a later layer deletes it.
    /// </para>
    /// <para>
    /// Leave empty on an account with an express-mode key (the OSS default), or
    /// to fall back to Application Default Credentials when no key is
    /// configured at all — see <see cref="VertexUsesOAuth"/>.
    /// </para>
    /// </summary>
    public string? VertexCredentialPath { get; set; }

    /// <summary>
    /// True when Vertex should authenticate with an OAuth2 bearer token
    /// (service account / ADC) instead of an express-mode API key on the query
    /// string. Precedence, in order:
    /// <list type="number">
    /// <item><see cref="VertexCredentialPath"/> set ⇒ OAuth2 from that key file.</item>
    /// <item>else an API key is configured (<see cref="VertexApiKey"/>, or
    /// <see cref="ApiKey"/> as the legacy fallback) ⇒ express-mode query string.</item>
    /// <item>else ⇒ OAuth2 via Application Default Credentials
    /// (<c>GOOGLE_APPLICATION_CREDENTIALS</c>, or the ambient identity inside GCP).</item>
    /// </list>
    /// <para>
    /// The API-key branch sits in the middle deliberately: it keeps every
    /// existing express-mode deployment working untouched, while an explicit
    /// credential path still wins for an operator migrating off one.
    /// </para>
    /// <para>
    /// This is the SINGLE source of truth for the auth mode —
    /// <see cref="VertexEmbeddingService"/> reads it to decide whether to append
    /// <c>?key=</c>, and <c>ServiceCollectionExtensions</c> reads it to decide
    /// whether to put <see cref="VertexAuthHandler"/> in the HTTP pipeline. The
    /// two MUST agree: a bearer header plus a query key is a 400, and neither is a 401.
    /// </para>
    /// </summary>
    public bool VertexUsesOAuth =>
        !string.IsNullOrWhiteSpace(VertexCredentialPath)
        || (string.IsNullOrWhiteSpace(VertexApiKey) && string.IsNullOrWhiteSpace(ApiKey));

    /// <summary>
    /// How many embedding batches to issue in parallel during indexing.
    /// <para>
    /// <c>null</c> (default) = auto-detect by provider: <c>1</c> for Ollama
    /// (CPU-bound single-thread inference makes parallelism counter-productive
    /// — confirmed in R17 ground truth on the LXC server), <c>4</c> for Gemini
    /// (request-count rate limited, parallelism is free throughput).
    /// </para>
    /// <para>
    /// Set explicitly to override auto-detection. Use <c>1</c> to force
    /// sequential behavior on any provider.
    /// </para>
    /// </summary>
    public int? MaxParallelBatches { get; set; }
}
