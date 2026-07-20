using CortexPlexus.Core.Services;

namespace CortexPlexus.Core.Tests;

public sealed class SecretsScannerTests
{
    private readonly BasicSecretsScanner _scanner = new();

    [Theory]
    [InlineData("Server=myserver;Database=mydb;User Id=admin;Password=s3cret123;", true)]
    [InlineData("Host=localhost;Password=test123;", true)]
    [InlineData("Data Source=srv;Password=abc;", true)]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0", true)]
    [InlineData("public class MyService { }", false)]
    [InlineData("var x = 42;", false)]
    public void ContainsSecrets_DetectsCorrectly(string content, bool expected)
    {
        Assert.Equal(expected, _scanner.ContainsSecrets(content));
    }

    [Fact]
    public void Sanitize_RedactsConnectionString()
    {
        var input = "Server=prod.db.com;Database=app;Password=SuperSecret;";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_CONNECTION_STRING]", result);
        Assert.DoesNotContain("SuperSecret", result);
    }

    [Fact]
    public void Sanitize_RedactsBearerToken()
    {
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.longtoken";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_TOKEN]", result);
        Assert.DoesNotContain("eyJhbGci", result);
    }

    [Fact]
    public void Sanitize_PreservesNormalCode()
    {
        var input = "public void ProcessPayment(decimal amount) { }";
        var result = _scanner.Sanitize(input);
        Assert.Equal(input, result);
    }

    // v0.8.2: well-known API-key formats. Smoke test on 2026-04-18 caught
    // a Gemini key "AIzaSyDmPk..." being stored verbatim because only
    // keyword-based detection ran. These tests guard the regression.

    [Theory]
    [InlineData("My Gemini key is AIzaSyDmPkX1Y2Z3A4B5C6D7E8F9G0H1I2J3K4L5M for project X", true)]
    [InlineData("openai uses sk-proj-abc123DEF456ghi789JKL0mnoPQR3stu for completion", true)]
    [InlineData("Anthropic client reads sk-ant-api03-zzZZaaBBccDDeeFFggHH for chat", true)]
    [InlineData("github pat: ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("AWS access: AKIAIOSFODNN7EXAMPLE is the old sample", true)]
    [InlineData("JWT: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0Hk5Q", true)]
    // False-positive guards — similar-looking strings that are NOT keys
    [InlineData("Result<T> where T : class", false)]
    [InlineData("Consider pattern AIzaSy in this random sentence", false)]  // AIzaSy alone, no 33 chars after
    [InlineData("sk-abc", false)] // too short for OpenAI
    [InlineData("the quick brown fox jumps over the lazy dog", false)]
    public void ContainsSecrets_DetectsApiKeyFormats(string content, bool expected)
    {
        Assert.Equal(expected, _scanner.ContainsSecrets(content));
    }

    // Issue #30: prose that DISCUSSES credentials must store. Before the fix every one
    // of these was rejected by a bare substring test on the keyword list, which blocked
    // precisely the credential-handling lessons most worth keeping.

    [Theory]
    [InlineData("use an Authorization: Bearer header instead of a query param")]
    [InlineData("this endpoint rejects API-key auth; only OAuth2 works")]
    [InlineData("store the value in the secret vault, never in the repo")]
    [InlineData("the private_key field must never be logged")]
    [InlineData("Repro probe A: this sentence contains the word bearer and nothing else of note.")]
    [InlineData("rotate the access_token on every deploy; connection_string lives in env")]
    public void ContainsSecrets_AllowsProseAboutCredentials(string content)
    {
        Assert.False(_scanner.ContainsSecrets(content));
        Assert.Null(_scanner.DetectSecret(content));
    }

    // Bypass guards. Relaxing the keyword gate (#30) must not open a hole the OLD
    // bare-substring check would have closed — every case below was found by an
    // adversarial pass over the first draft of the fix and pins a real regression.
    [Theory]
    [InlineData("{\"password\": \"P@ssw0rd!\"}", "quoted JSON key — the '\"' sits between keyword and ':'")]
    [InlineData("{\"api_key\": \"my-custom-service-key-99\"}", "quoted JSON key, no well-known issuer prefix")]
    [InlineData("mysql -u root --password Winter2024!", "CLI long option binds by whitespace")]
    [InlineData("password=42", "short value still a value")]
    [InlineData("password=\"a b\"", "quoted value containing a space")]
    [InlineData("Authorization: Bearer myapptoken-v2-deadbeefcafe", "opaque token starting with an alpha run")]
    [InlineData("Authorization: Bearer SuperSecretTokenXX", "pure-alphabetic token >= 16")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAxxxx", "PEM block, no keyword at all")]
    [InlineData("Server=localhost;User Id=sa;Pwd=SuperSecret1", "connection string using Pwd= not Password=")]
    public void ContainsSecrets_ClosesBypasses(string content, string reason)
    {
        _ = reason;
        Assert.True(_scanner.ContainsSecrets(content));
    }

    [Fact]
    public void Sanitize_LeavesBearerProseIntact()
    {
        // The old BearerTokenRegex redacted the word after "Bearer", so indexed docs
        // came back as "use an Authorization: [REDACTED_TOKEN] instead of a query param".
        var input = "use an Authorization: Bearer header instead of a query param";
        Assert.Equal(input, _scanner.Sanitize(input));
    }

    [Fact]
    public void DetectSecret_NamesTheTrigger()
    {
        // A rejection the caller can act on — the whole point of issue #30's second half.
        Assert.Contains("api_key", _scanner.DetectSecret("api_key=xyz"));
        Assert.Equal("bearer token", _scanner.DetectSecret("Authorization: Bearer eyJhbGciToken123"));
        Assert.Equal("connection string with a password",
            _scanner.DetectSecret("Server=db;Database=app;Password=hunter2;"));
    }

    [Fact]
    public void Sanitize_RedactsGoogleApiKey()
    {
        var input = "Use AIzaSyDmPkX1Y2Z3A4B5C6D7E8F9G0H1I2J3K4L5M in the config";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_API_KEY]", result);
        Assert.DoesNotContain("AIzaSyDmPk", result);
    }

    [Fact]
    public void Sanitize_RedactsOpenAiKey()
    {
        var input = "sk-proj-abc123DEF456ghi789JKL0mnoPQR3stu is the key";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_API_KEY]", result);
        Assert.DoesNotContain("sk-proj-abc", result);
    }

    [Fact]
    public void Sanitize_RedactsGithubPat()
    {
        var input = "token=ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_API_KEY]", result);
        Assert.DoesNotContain("ghp_abc", result);
    }
}
