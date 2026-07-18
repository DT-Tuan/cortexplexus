using CortexPlexus.Embedding;

namespace CortexPlexus.Embedding.Tests;

/// <summary>
/// ADR-018: EmbeddingSpaceResolver maps EmbeddingOptions → the (provider, model, dim)
/// space identity that gets stamped on repos/memories and compared on every vector read.
/// The Key format is part of the wire/display contract — keep it stable.
/// </summary>
public class EmbeddingSpaceResolverTests
{
    [Fact]
    public void FromOptions_Ollama_MapsOllamaModel()
    {
        var space = EmbeddingSpaceResolver.FromOptions(new EmbeddingOptions
        {
            Provider = "ollama",
            OllamaModel = "nomic-embed-text",
            Dimensions = 768,
        });

        Assert.Equal("ollama", space.Provider);
        Assert.Equal("nomic-embed-text", space.Model);
        Assert.Equal(768, space.Dimensions);
    }

    [Fact]
    public void FromOptions_Vertex_MapsVertexModelId()
    {
        var space = EmbeddingSpaceResolver.FromOptions(new EmbeddingOptions
        {
            Provider = "vertex",
            VertexModelId = "text-embedding-005",
            Dimensions = 768,
        });

        Assert.Equal("vertex", space.Provider);
        Assert.Equal("text-embedding-005", space.Model);
    }

    [Fact]
    public void FromOptions_Gemini_IsDefaultBranch()
    {
        var space = EmbeddingSpaceResolver.FromOptions(new EmbeddingOptions
        {
            Provider = "gemini",
            GeminiModel = "gemini-embedding-001",
        });

        Assert.Equal("gemini", space.Provider);
        Assert.Equal("gemini-embedding-001", space.Model);
    }

    [Fact]
    public void FromOptions_ProviderCasing_Normalized()
    {
        // Config values arrive from env vars — casing must not create a "different" space.
        var space = EmbeddingSpaceResolver.FromOptions(new EmbeddingOptions
        {
            Provider = "Vertex",
            VertexModelId = "text-embedding-005",
        });

        Assert.Equal("vertex", space.Provider);
    }

    [Fact]
    public void Key_Format_IsProviderModelDim()
    {
        var space = EmbeddingSpaceResolver.FromOptions(new EmbeddingOptions
        {
            Provider = "ollama",
            OllamaModel = "nomic-embed-text",
            Dimensions = 768,
        });

        Assert.Equal("ollama:nomic-embed-text:768", space.Key);
    }
}
