namespace CortexPlexus.Core.Abstractions;

public interface ISecretsScanner
{
    string Sanitize(string content);
    bool ContainsSecrets(string content);

    /// <summary>
    /// Returns a short human-readable description of the first credential pattern found,
    /// or <c>null</c> when the content is clean. Callers should surface this in rejection
    /// messages — a bare "contains secrets" reads as "reword it" and sends agents into a
    /// blind rewrite loop (issue #30).
    /// </summary>
    string? DetectSecret(string content);
}
