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
    /// Vertex location. <c>"global"</c> (default) targets the bare
    /// <c>aiplatform.googleapis.com</c> host with NO region prefix; any other
    /// value (e.g. <c>"us-central1"</c>) prefixes the host as
    /// <c>{location}-aiplatform.googleapis.com</c>.
    /// </summary>
    public string VertexLocation { get; set; } = "global";

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
    /// </summary>
    public string? VertexApiKey { get; set; }

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
