using System.Diagnostics;
using CortexPlexus.Core.Services;

namespace CortexPlexus.Core.Tests;

/// <summary>
/// Edge case + performance tests cho BasicSecretsScanner.
///
/// Phạm vi: TEST-PLAN.md #105, #106, #107, #110
/// </summary>
public sealed class SecretsScannerSupplementTests
{
    private readonly BasicSecretsScanner _scanner = new();

    // === #105: SecretScanner_ConnectionStringVariants ===
    [Theory]
    [InlineData("Password=mypw", true, "lowercase Password keyword")]
    [InlineData("password=mypw", true, "all lowercase")]
    [InlineData("PWD=mypw", false, "PWD shorthand không phải sensitive keyword")]
    [InlineData("PASSWORD=mypw", true, "uppercase")]
    [InlineData("user secret = abc", true, "secret keyword")]
    [InlineData("apiKey: abc123", true, "apikey keyword")]
    [InlineData("api_key=xyz", true, "api_key snake")]
    [InlineData("api-key:value", true, "api-key kebab")]
    [InlineData("auth_token=abc", true, "auth_token")]
    // #30: these two flipped to false. A keyword must be adjacent to a VALUE to count —
    // "Bearer abc" is prose with no token shape, "private_key.pem" names a file.
    [InlineData("Bearer abc", false, "bare keyword, no token-shaped value")]
    [InlineData("private_key.pem", false, "filename mention, no assigned value")]
    [InlineData("publicData = 1", false, "no sensitive keyword")]
    public void ContainsSecrets_KeywordVariants(string content, bool expected, string reason)
    {
        // Mục đích: ContainsSecrets phát hiện đa dạng patterns dùng trong codebase thật.
        // Giúp AI agent biết khi nào không nên embed text này. (reason: {reason})
        _ = reason;
        Assert.Equal(expected, _scanner.ContainsSecrets(content));
    }

    [Fact]
    public void Sanitize_ConnectionStringWithSpecialCharsInPassword_StillRedacts()
    {
        // Mục đích: Password chứa ký tự đặc biệt vẫn bị redact.
        var input = "Server=db.com;Database=app;Password=p@ss!w0rd#$;Other=val";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_CONNECTION_STRING]", result);
        Assert.DoesNotContain("p@ss!w0rd", result);
    }

    [Fact]
    public void Sanitize_MultipleSecretsInOneInput_AllRedacted()
    {
        // Mục đích: 1 string chứa nhiều secrets — tất cả đều bị redact.
        var input = """
            Server=db1;Password=secret1;
            Authorization: Bearer eyJhbGciToken123
            """;
        var result = _scanner.Sanitize(input);

        Assert.Contains("[REDACTED_CONNECTION_STRING]", result);
        Assert.Contains("[REDACTED_TOKEN]", result);
        Assert.DoesNotContain("secret1", result);
        Assert.DoesNotContain("eyJhbGci", result);
    }

    [Fact]
    public void Sanitize_NullOrEmptyInput_DoesNotCrash()
    {
        // Mục đích: Edge case empty/whitespace — không crash.
        Assert.Equal(string.Empty, _scanner.Sanitize(string.Empty));
        Assert.Equal("   ", _scanner.Sanitize("   "));
    }

    // === #106: SecretScanner_Base64Key_MinLength ===
    [Theory]
    [InlineData("\"apikey\": \"abc\"", false, "value < 8 chars → not redacted")]
    [InlineData("\"apikey\": \"abcd1234\"", true, "value = 8 chars → redacted")]
    [InlineData("\"apikey\": \"abcdefghij1234567890\"", true, "value > 8 chars → redacted")]
    [InlineData("\"secretToken\": \"verylongsecretvalue123\"", true, "secret token long → redacted")]
    [InlineData("\"normalField\": \"normalvalue\"", false, "no secret keyword in field name")]
    public void Sanitize_Base64Key_RespectsMinLength(string input, bool shouldRedact, string reason)
    {
        // Mục đích: Base64Key regex yêu cầu value >= 8 chars để tránh false positives
        // (e.g., "apikey": "x" không có nghĩa là secret).
        _ = reason;
        var result = _scanner.Sanitize(input);
        if (shouldRedact)
            Assert.Contains("[REDACTED]", result);
        else
            Assert.DoesNotContain("[REDACTED]", result);
    }

    // === #107: SecretScanner_LargeInput_Performance ===
    [Fact]
    public void Sanitize_LargeInput_CompletesQuickly()
    {
        // Mục đích: 1MB input phải sanitize dưới 500ms (regex shouldn't blow up).
        // Generated regex của BasicSecretsScanner là source-generated → fast.
        var oneMb = new string('a', 1_000_000);

        var sw = Stopwatch.StartNew();
        var result = _scanner.Sanitize(oneMb);
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Sanitize(1MB) took {sw.Elapsed.TotalMilliseconds:F0}ms (budget: 500ms)");
    }

    [Fact]
    public void Sanitize_LargeInputWithSecrets_StillRedactsAll()
    {
        // Mục đích: Secrets nằm rải rác trong large input vẫn được redact đầy đủ.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            sb.AppendLine($"// normal code line {i}");
            if (i % 10 == 0)
                sb.AppendLine($"Server=db{i}.com;Database=test;Password=secret{i};");
        }

        var result = _scanner.Sanitize(sb.ToString());
        Assert.Contains("[REDACTED_CONNECTION_STRING]", result);

        // Tất cả secret values phải bị xoá.
        for (var i = 0; i < 100; i += 10)
            Assert.DoesNotContain($"secret{i};", result);
    }

    [Fact]
    public void Sanitize_UnicodeContent_PreservesNonAsciiText()
    {
        // Mục đích: Code Vietnamese/CJK không bị corrupt khi sanitize.
        var input = "// Tiếng Việt: xử lý thanh toán\npublic class XửLý { }";
        var result = _scanner.Sanitize(input);

        Assert.Equal(input, result); // unchanged
        Assert.Contains("xử lý", result);
        Assert.Contains("XửLý", result);
    }

    // === #110: EmbeddingInput_NoSourceCode (verification by inspection) ===
    // Note: Đây là design constraint hơn là code path test. Verify rằng tests khác
    // không test full source code embedding bằng cách check memory/contract.
    [Fact]
    public void Sanitize_BodyOfMethodWithSecrets_RedactsBeforeEmbedding()
    {
        // Mục đích: Method body chứa hardcoded secret → sau Sanitize không còn raw secret.
        // Đây là invariant quan trọng cho IEmbeddingService — chỉ signature + sanitized
        // documentation được gửi đi (theo CLAUDE.md security rule).
        var methodBody = """
            public void Init() {
                var conn = "Server=prod;Database=app;Password=hardcoded123";
                var token = "Bearer eyJsecrettoken12345abcdef";
            }
            """;

        var sanitized = _scanner.Sanitize(methodBody);

        Assert.DoesNotContain("hardcoded123", sanitized);
        Assert.DoesNotContain("eyJsecrettoken", sanitized);
        Assert.Contains("[REDACTED_CONNECTION_STRING]", sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
    }
}
