namespace CortexPlexus.App.Mcp.Tools;

/// <summary>
/// Renders how old a repository's last-index timestamp is. Used by
/// <c>list_repositories</c> and search-type tools.
///
/// ADR-015 (B1): the age is reported as <b>information only</b> — it does NOT imply the index
/// is wrong. What actually invalidates an index is <i>content drift</i> (files changed since
/// indexing), a separate content-based signal (ADR-015 B2). The previous time-based escalation
/// ("⚠️ STALE" / "🚨 VERY STALE" + a "don't rely on these results, re-index first" footer) fired
/// purely on elapsed time and pushed agents off a fresh-but-old index — exactly the
/// false-negative this removes.
///
///   &lt; 6h : no label (effectively fresh)
///   ≥ 6h  : "(indexed N hours/days ago)"   — neutral, informational
///   null  : "(never)"                       — genuinely not indexed yet
/// </summary>
public static class StalenessLabel
{
    private static readonly TimeSpan FreshWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Short suffix for the "Last indexed: ..." line. Returns <c>null</c> when the index is
    /// fresh (&lt; 6h); otherwise a neutral age note — never an alarm (ADR-015 B1).
    /// </summary>
    public static string? Format(DateTimeOffset? lastIndexed, DateTimeOffset now)
    {
        if (lastIndexed is null) return "(never)";
        var age = now - lastIndexed.Value;
        if (age < FreshWindow) return null;
        return $"(indexed {FormatAge(age)} ago)";
    }

    /// <summary>
    /// Footer for search-style tools. ADR-015 B1 removed the age-based "results may be stale,
    /// re-index before relying" warning — it nagged on a clock and pushed agents off the MCP
    /// for an index that was old but still 100% accurate. A content-drift-based footer
    /// (git HEAD mismatch / dirty working tree) is reintroduced by ADR-015 B2; until then this
    /// returns <c>null</c>. Signature kept stable for B2.
    /// </summary>
    public static string? SearchFooter(DateTimeOffset? lastIndexed, DateTimeOffset now)
    {
        _ = lastIndexed;
        _ = now;
        return null;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1.0)
        {
            var days = (int)age.TotalDays;
            return days == 1 ? "1 day" : $"{days} days";
        }
        var hours = (int)age.TotalHours;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
