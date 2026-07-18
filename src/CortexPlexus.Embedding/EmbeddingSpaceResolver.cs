using CortexPlexus.Core.Models;

namespace CortexPlexus.Embedding;

/// <summary>
/// Maps <see cref="EmbeddingOptions"/> to a pure <see cref="EmbeddingSpace"/> identity.
/// Lives in the Embedding project so Search/Core stay free of EmbeddingOptions.
/// </summary>
public static class EmbeddingSpaceResolver
{
    public static EmbeddingSpace FromOptions(EmbeddingOptions o) => o.Provider.ToLowerInvariant() switch
    {
        "ollama" => new("ollama", o.OllamaModel, o.Dimensions),
        "vertex" => new("vertex", o.VertexModelId, o.Dimensions),
        _        => new("gemini", o.GeminiModel, o.Dimensions),
    };
}
