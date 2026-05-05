namespace FunctionsMcpTool.Helpers;

public class OutputFormatter
{
    internal string Cell(string? s) =>
        (s ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    internal string Truncate(string s, int max) =>
        s.Length <= max ? s : "..." + s[^(max - 3)..];

    internal string SeverityLabel(int level) => level switch // [SPEC-12]
    {
        4 => "Critical",
        3 => "Error",
        2 => "Warning",
        1 => "Information",
        0 => "Verbose",
        _ => "—"
    };

    internal string TrendIndicator(long current, long previous) // [SPEC-08]
    {
        if (previous == 0) return current == 0 ? "→" : "↑ new";
        var pct = (double)(current - previous) / previous * 100;
        return pct switch
        {
            > 50 => $"↑↑ +{pct:F0}%",
            > 10 => $"↑ +{pct:F0}%",
            < -50 => $"↓↓ {pct:F0}%",
            < -10 => $"↓ {pct:F0}%",
            _ => $"→ {pct:+0;-0;0}%"
        };
    }

    internal TimeSpan ParseTimeRange(string s) // [SPEC-04] [SPEC-11]
    {
        if (string.IsNullOrWhiteSpace(s)) return TimeSpan.FromHours(24);
        var unit = s[^1..].ToLowerInvariant();
        var valueStr = s[..^1];
        if (!double.TryParse(valueStr, out var num)) return TimeSpan.FromHours(24);
        return unit switch // [SPEC-11]
        {
            "d" => TimeSpan.FromDays(num),
            "m" => TimeSpan.FromMinutes(num),
            _ => TimeSpan.FromHours(num)
        };
    }

    internal int[]? ParseSeverityFilter(string? severity) // [SPEC-20]
    {
        if (string.IsNullOrEmpty(severity)) return null;
        var levels = severity
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant() switch
            {
                "critical" => 4,
                "error" => 3,
                "warning" => 2,
                "information" or "info" => 1,
                "verbose" => 0,
                _ => -1
            })
            .Where(v => v >= 0)
            .Distinct()
            .ToArray();
        return levels.Length > 0 ? levels : null;
    }

    internal string ToKqlDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1 && ts.TotalDays == Math.Floor(ts.TotalDays))
            return $"{(int)ts.TotalDays}d";
        return $"{(int)Math.Ceiling(ts.TotalHours)}h";
    }

    internal string BuildSeverityWhereClause(int[]? levels) // [SPEC-20]
    {
        if (levels == null || levels.Length == 0) return "";
        return $"| where severityLevel in ({string.Join(", ", levels)})\n";
    }

    internal string BuildNoResourcesMessage(string? subscriptionNames, string? resourceGroup)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(subscriptionNames)) parts.Add($"subscription(s) '{subscriptionNames}'");
        if (!string.IsNullOrEmpty(resourceGroup)) parts.Add($"resource group '{resourceGroup}'");
        var filter = parts.Count > 0 ? $" matching {string.Join(" and ", parts)}" : "";
        return $"No Application Insights resources found{filter}.";
    }

    internal string SanitizeId(string id)
    {
        if (id.Length > 500 || id.Any(c => c is '"' or '\'' or '|' or ';' or '\n' or '\r'))
            throw new ArgumentException(
                $"Invalid characters in ID '{id}'. IDs must not contain quotes, pipes, semicolons, or newlines.");
        return id;
    }
}
