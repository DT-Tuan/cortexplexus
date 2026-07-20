using System.Text.RegularExpressions;
using CortexPlexus.Core.Abstractions;

namespace CortexPlexus.Core.Services;

public sealed partial class BasicSecretsScanner : ISecretsScanner
{
    public string Sanitize(string content)
    {
        var result = ConnectionStringRegex().Replace(content, "[REDACTED_CONNECTION_STRING]");
        result = BearerTokenRegex().Replace(result, "[REDACTED_TOKEN]");
        result = ApiKeyRegex().Replace(result, "[REDACTED_API_KEY]");
        result = Base64KeyRegex().Replace(result, match =>
        {
            var prefix = match.Groups[1].Value;
            return $"{prefix}[REDACTED]\"";
        });
        return result;
    }

    public bool ContainsSecrets(string content) => DetectSecret(content) is not null;

    public string? DetectSecret(string content)
    {
        if (ConnectionStringRegex().IsMatch(content)) return "connection string with a password";
        if (BearerTokenRegex().IsMatch(content)) return "bearer token";
        if (ApiKeyRegex().IsMatch(content)) return "well-known API key format";

        var keyword = KeywordAssignmentRegex().Match(content);
        if (keyword.Success) return $"'{keyword.Groups[1].Value}' assigned a value";

        return null;
    }

    [GeneratedRegex(@"(Server|Host|Data Source)=[^;""]+;[^""]*Password=[^;""]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringRegex();

    // Requires a token-shaped value, not just the word "Bearer" followed by anything.
    // Before issue #30 this was `Bearer\s+[A-Za-z0-9\-._~+/]+=*`, which matched the
    // prose "use an Authorization: Bearer header" — blocking save_memory and, worse,
    // silently redacting the word "header" out of any indexed documentation.
    //
    // Two guards now: >= 16 chars, and not a plain alphabetic word (negative lookahead).
    // Real bearer tokens are base64/hex/JWT and effectively always carry a digit or
    // separator; English words after "Bearer" ("authentication" = 14, "authorization"
    // = 13) are pure alpha and shorter. Known gap: a pure-alphabetic token is missed —
    // accepted, since ApiKeyRegex still catches every well-known issuer format.
    [GeneratedRegex(@"Bearer\s+(?![A-Za-z]+\b)[A-Za-z0-9\-._~+/]{16,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(""[^""]*(?:key|secret|token|password|apikey)[^""]*""\s*[:=]\s*)""[^""]{8,}""", RegexOptions.IgnoreCase)]
    private static partial Regex Base64KeyRegex();

    // Issue #30: a sensitive keyword only signals a secret when a VALUE follows it.
    // The old check was `SensitiveKeywords.Any(kw => lower.Contains(kw))` — a bare
    // substring test that rejected every memory *discussing* credential handling
    // ("the private_key field must never be logged"), which is exactly the class of
    // lesson most worth storing. Requiring an explicit ':' or '=' separator plus a
    // >= 3-char value keeps all previously-detected true positives (api_key=xyz,
    // Password=mypw, "user secret = abc") while letting prose through.
    //
    // 'pwd' is deliberately absent — see SecretsScannerSupplementTests, PWD= is too
    // common as a shell/path variable to treat as sensitive.
    [GeneratedRegex(
        @"\b(password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|bearer" +
        @"|private[_-]?key|connection[_-]?string|conn[_-]?str)\w*\s*[:=]\s*\S{3,}",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeywordAssignmentRegex();

    // Well-known API-key formats with deterministic prefixes. Chosen for low
    // false-positive rate — only matches strings that could not plausibly be
    // anything other than a real credential. Added in v0.8.2 after a smoke
    // test found "AIzaSyDmPk..." (Gemini key) wasn't detected by the old scanner.
    //
    // Covered:
    //   - Google API keys (Gemini, Maps, Firebase): AIzaSy[A-Za-z0-9\-_]{33}
    //   - OpenAI: sk-proj-* / sk-* with 20+ chars
    //   - Anthropic: sk-ant-* with 20+ chars
    //   - GitHub PAT / fine-grained / OAuth / user-to-server / refresh: gh[pousr]_[A-Za-z0-9]{36,}
    //   - AWS Access Key ID: AKIA[0-9A-Z]{16}
    //   - JWT (three base64-url segments): eyJ...eyJ...
    [GeneratedRegex(
        @"AIzaSy[A-Za-z0-9\-_]{33}" +
        @"|sk-(?:proj-|ant-)?[A-Za-z0-9\-_]{20,}" +
        @"|gh[pousr]_[A-Za-z0-9]{36,}" +
        @"|AKIA[0-9A-Z]{16}" +
        @"|eyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex ApiKeyRegex();
}
