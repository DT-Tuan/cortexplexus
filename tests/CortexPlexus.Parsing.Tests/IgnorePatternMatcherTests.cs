using CortexPlexus.Parsing;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// Tests cho IgnorePatternMatcher (Issue #7 — vendored third-party leak).
///
/// Pattern types supported:
/// - Plain dirname: "claw-code-main" → match anywhere in path
/// - Glob suffix: "*.generated.cs" → match by extension
/// - Path prefix: "docs/legacy" → match relative path startsWith
/// </summary>
public sealed class IgnorePatternMatcherTests
{
    [Fact]
    public void LoadFromDirectory_NoIgnoreFile_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var patterns = IgnorePatternMatcher.LoadFromDirectory(tempDir);
            Assert.Empty(patterns);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDirectory_ValidFile_ReturnsPatternsExcludingCommentsAndBlanks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, ".cortexplexusignore"), """
                # Comment line
                claw-code-main
                vendor

                # Another comment
                *.generated.cs
                docs/legacy
                """);

            var patterns = IgnorePatternMatcher.LoadFromDirectory(tempDir);
            Assert.Equal(4, patterns.Count);
            Assert.Contains("claw-code-main", patterns);
            Assert.Contains("vendor", patterns);
            Assert.Contains("*.generated.cs", patterns);
            Assert.Contains("docs/legacy", patterns);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDirectory_StripsTrailingSlash_SoDirPatternMatches()
    {
        // Gitignore convention writes directories as "files_hive/". The trailing
        // slash must be stripped on load, otherwise the pattern lands in the
        // path-prefix branch and fails a doubled-slash startsWith check.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, ".cortexplexusignore"), """
                files_hive/
                docs/legacy/
                """);

            var patterns = IgnorePatternMatcher.LoadFromDirectory(tempDir);
            Assert.Contains("files_hive", patterns);
            Assert.Contains("docs/legacy", patterns);
            Assert.DoesNotContain(patterns, p => p.EndsWith('/'));

            Assert.True(IgnorePatternMatcher.Matches(
                $"{tempDir}/files_hive/recorder.py", tempDir, patterns));
            Assert.True(IgnorePatternMatcher.Matches(
                $"{tempDir}/files_hive/hive-doc/core/intel/agent.py", tempDir, patterns));
            Assert.True(IgnorePatternMatcher.Matches(
                $"{tempDir}/docs/legacy/old.md", tempDir, patterns));
            Assert.False(IgnorePatternMatcher.Matches(
                $"{tempDir}/core/intel/agent.py", tempDir, patterns));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    // Plain dirname matches anywhere in path
    [InlineData("/repo/claw-code-main/rust/lib.rs", "claw-code-main", true)]
    [InlineData("/repo/src/claw-code-main/test.rs", "claw-code-main", true)]
    [InlineData("/repo/src/main.cs", "claw-code-main", false)]
    // Don't match partial substring
    [InlineData("/repo/claw-code/lib.rs", "claw-code-main", false)]
    [InlineData("/repo/old-claw-code-main-backup/x.rs", "claw-code-main", false)]
    // Case insensitive
    [InlineData("/repo/CLAW-CODE-MAIN/file.rs", "claw-code-main", true)]
    public void Matches_PlainDirname(string filePath, string pattern, bool expected)
    {
        var result = IgnorePatternMatcher.Matches(filePath, "/repo", new[] { pattern });
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/repo/foo.generated.cs", "*.generated.cs", true)]
    [InlineData("/repo/sub/bar.generated.cs", "*.generated.cs", true)]
    [InlineData("/repo/foo.cs", "*.generated.cs", false)]
    [InlineData("/repo/foo.GENERATED.CS", "*.generated.cs", true)] // case insensitive
    public void Matches_GlobSuffix(string filePath, string pattern, bool expected)
    {
        var result = IgnorePatternMatcher.Matches(filePath, "/repo", new[] { pattern });
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/repo/docs/legacy/old.md", "docs/legacy", true)]
    [InlineData("/repo/docs/legacy/sub/file.md", "docs/legacy", true)]
    [InlineData("/repo/docs/current/new.md", "docs/legacy", false)]
    [InlineData("/repo/other/legacy/file.md", "docs/legacy", false)]
    public void Matches_PathPrefix(string filePath, string pattern, bool expected)
    {
        var result = IgnorePatternMatcher.Matches(filePath, "/repo", new[] { pattern });
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Matches_EmptyPatterns_AlwaysFalse()
    {
        Assert.False(IgnorePatternMatcher.Matches(
            "/repo/anything.cs", "/repo", Array.Empty<string>()));
    }

    [Fact]
    public void Matches_MultiplePatterns_AnyMatch()
    {
        var patterns = new[] { "claw-code-main", "vendor", "*.generated.cs" };

        Assert.True(IgnorePatternMatcher.Matches("/repo/claw-code-main/x.rs", "/repo", patterns));
        Assert.True(IgnorePatternMatcher.Matches("/repo/vendor/lib/y.go", "/repo", patterns));
        Assert.True(IgnorePatternMatcher.Matches("/repo/foo.generated.cs", "/repo", patterns));
        Assert.False(IgnorePatternMatcher.Matches("/repo/src/main.cs", "/repo", patterns));
    }

    [Fact]
    public void LoadFromDirectory_MalformedFile_ReturnsEmptyGracefully()
    {
        // Edge case: file exists but unreadable (e.g., directory name conflict).
        // Should return empty thay vì throw.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a directory với tên `.cortexplexusignore` thay vì file —
            // File.ReadAllLines sẽ throw, helper phải catch.
            Directory.CreateDirectory(Path.Combine(tempDir, ".cortexplexusignore"));
            var patterns = IgnorePatternMatcher.LoadFromDirectory(tempDir);
            Assert.Empty(patterns);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
