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
        if (PemPrivateKeyRegex().IsMatch(content)) return "PEM private key block";
        if (BearerTokenRegex().IsMatch(content)) return "bearer token";
        if (ApiKeyRegex().IsMatch(content)) return "well-known API key format";
        if (Base64KeyRegex().IsMatch(content)) return "a credential-named field with a long value";

        var keyword = KeywordAssignmentRegex().Match(content);
        if (keyword.Success) return $"'{keyword.Groups["kw"].Value}' assigned a value";

        return null;
    }

    // Keyword list shared by KeywordAssignmentRegex's two alternatives. const so the
    // concatenation stays a compile-time literal for [GeneratedRegex].
    //
    // 'pwd' is deliberately absent as a standalone keyword — PWD= is too common as a
    // shell/path variable (see SecretsScannerSupplementTests) — but it IS accepted
    // inside a connection string, where it is unambiguous.
    private const string Kw =
        @"password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|bearer" +
        @"|private[_-]?key|connection[_-]?string|conn[_-]?str";

    [GeneratedRegex(@"(Server|Host|Data Source)=[^;""]+;[^""]*(Password|Pwd)=[^;""]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringRegex();

    // Requires a token-shaped value, not just the word "Bearer" followed by anything.
    // Before issue #30 this was `Bearer\s+[A-Za-z0-9\-._~+/]+=*`, which matched the
    // prose "use an Authorization: Bearer header" — blocking save_memory and, worse,
    // silently redacting the word "header" out of any indexed documentation.
    //
    // The length floor alone does that job: the longest English word plausibly
    // following "Bearer" is "authentication" (14) / "authorization" (13). An earlier
    // draft also carried a `(?![A-Za-z]+\b)` "not a plain word" guard — it was removed
    // because it does NOT mean what it reads as: it fires on the FIRST alpha run, so
    // "Bearer myapptoken-v2-deadbeef" was rejected at "myapptoken". Length only.
    //
    // Known gap: opaque tokens under 16 chars ("Bearer tok_a1b2c3"). Accepted — the
    // floor cannot go much lower without swallowing prose again.
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]{16,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(""[^""]*(?:key|secret|token|password|apikey)[^""]*""\s*[:=]\s*)""[^""]{8,}""", RegexOptions.IgnoreCase)]
    private static partial Regex Base64KeyRegex();

    // "-----BEGIN RSA PRIVATE KEY-----". Never matched by the keyword list either
    // before or after #30 ("PRIVATE KEY" has a space, the list has private_key), yet
    // it is the least ambiguous credential there is. Zero false-positive risk.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PemPrivateKeyRegex();

    // Issue #30: a sensitive keyword only signals a secret when a VALUE is bound to it.
    // The old check was `SensitiveKeywords.Any(kw => lower.Contains(kw))` — a bare
    // substring test that rejected every memory *discussing* credential handling
    // ("the private_key field must never be logged"), which is exactly the class of
    // lesson most worth storing.
    //
    // Two binding forms, both unambiguous:
    //   1. keyword [quote] ':'|'=' value   — api_key=xyz, "password": "x", secret = abc
    //   2. --keyword value                 — CLI long option
    // The optional quote in form 1 matters: without it `{"password": "hunter2"}` slips
    // through, because the '"' sits between the keyword and the colon.
    //
    // Deliberately NOT covered: whitespace-only binding ("password is hunter2",
    // "| password | hunter2 |", CSV). Those are indistinguishable from the prose this
    // issue exists to unblock — "the password is never logged" must store. Also not
    // covered: XML element/attribute forms (<password>x</password>).
    [GeneratedRegex(
        @"(?:\b(?<kw>" + Kw + @")\w*[""'`\]]?\s*[:=]\s*" +
        @"|--(?<kw>" + Kw + @")\w*\s+)\S+",
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
