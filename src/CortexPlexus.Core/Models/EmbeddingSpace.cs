namespace CortexPlexus.Core.Models;

/// <summary>
/// Identity of an embedding vector space — provider + model + dimensions.
/// Vectors from different spaces are column-compatible (same dim) but
/// semantically incompatible (cosine is noise). See ADR-018.
/// </summary>
public sealed record EmbeddingSpace(string Provider, string Model, int Dimensions)
{
    public string Key => $"{Provider}:{Model}:{Dimensions}";
}
