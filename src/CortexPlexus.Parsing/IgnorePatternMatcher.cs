namespace CortexPlexus.Parsing;

/// <summary>
/// Loads và matches gitignore-style patterns từ <c>.cortexplexusignore</c> file.
/// Đơn giản hóa gitignore: support exact directory names, glob patterns (*.ext, prefix/),
/// và line comments (#). KHÔNG support negation (!), nested .ignore files, hay full
/// gitignore semantics.
///
/// File format example:
/// <code>
/// # Vendored third-party (don't index)
/// claw-code-main
/// vendor
/// third_party
///
/// # Generated files
/// *.generated.cs
///
/// # Specific subdirectories
/// docs/legacy
/// </code>
///
/// Đây là utility shared giữa TreeSitter parser, Markdown parser, và LocalIndexer
/// để consistent exclusion across all parsers.
/// </summary>
public static class IgnorePatternMatcher
{
    private const string IgnoreFileName = ".cortexplexusignore";

    /// <summary>
    /// Load ignore patterns từ <c>{rootPath}/.cortexplexusignore</c>. Returns empty list
    /// nếu file không tồn tại. Comments (lines bắt đầu với #) và blank lines được skip.
    /// </summary>
    public static IReadOnlyList<string> LoadFromDirectory(string rootPath)
    {
        var ignoreFile = Path.Combine(rootPath, IgnoreFileName);
        if (!File.Exists(ignoreFile))
            return Array.Empty<string>();

        try
        {
            var lines = File.ReadAllLines(ignoreFile);
            var patterns = new List<string>(lines.Length);
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith('#')) continue;
                // Gitignore conventionally marks directories with a trailing slash
                // (e.g. "files_hive/"). Strip it so the pattern lands in the
                // dirname/prefix branches of Matches() instead of failing a
                // "startsWith(pattern + '/')" check on a doubled slash.
                trimmed = trimmed.TrimEnd('/');
                if (trimmed.Length == 0) continue;
                patterns.Add(trimmed);
            }
            return patterns;
        }
        catch
        {
            // Failed to read — treat as no patterns. Don't crash parser.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns true if the file path matches any ignore pattern.
    /// Patterns có thể là:
    /// - Plain dirname: <c>claw-code-main</c> → match nếu path chứa <c>/claw-code-main/</c>
    /// - Glob suffix: <c>*.generated.cs</c> → match nếu file ends với <c>.generated.cs</c>
    /// - Path prefix: <c>docs/legacy</c> → match nếu relative path bắt đầu với đó
    /// </summary>
    /// <param name="filePath">Absolute file path</param>
    /// <param name="rootPath">Absolute root directory (for computing relative path)</param>
    /// <param name="patterns">Ignore patterns loaded from .cortexplexusignore</param>
    public static bool Matches(string filePath, string rootPath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;

        var normalized = filePath.Replace('\\', '/');
        string? relativePath = null;
        try
        {
            relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        }
        catch
        {
            // Different volumes hoặc path issues — skip relative check
        }

        foreach (var pattern in patterns)
        {
            // Glob suffix: "*.generated.cs"
            if (pattern.StartsWith("*."))
            {
                var ext = pattern[1..]; // ".generated.cs"
                if (normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            // Path prefix: "docs/legacy" → match relative path startsWith
            if (pattern.Contains('/') && relativePath is not null)
            {
                if (relativePath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            // Plain dirname (most common case): "claw-code-main"
            // Match if any path segment equals this name (case-insensitive).
            if (normalized.Contains($"/{pattern}/", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith($"/{pattern}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
