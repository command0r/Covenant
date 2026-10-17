using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Budget wording (ADR-0014): period identity, warning state, enforcement — all from the server's
/// numbers; the console never computes spend.</summary>
public static class BudgetText
{
    public static string Period(string? period, string? periodId)
    {
        var p = (period ?? "").Trim().ToLowerInvariant() switch
        {
            "daily" => "Daily (UTC)",
            "monthly" => "Monthly (UTC)",
            "none" => "No period (cumulative)",
            "" => "Period not reported",
            _ => period!.Trim(),
        };
        return string.IsNullOrWhiteSpace(periodId) ? p : p + " · " + periodId;
    }

    public static string Scope(string? scope)
    {
        var s = (scope ?? "").Trim();
        if (s.Length == 0) return "Scope not reported";
        if (string.Equals(s, "global", StringComparison.OrdinalIgnoreCase)) return "Global";
        if (s.StartsWith("team:", StringComparison.OrdinalIgnoreCase)) return "Team " + s[5..];
        if (s.StartsWith("application:", StringComparison.OrdinalIgnoreCase)) return "Application " + s[12..];
        return s;
    }

    /// <summary>(label, tone) for the server's warning state: none | warn | critical | exceeded.</summary>
    public static (string Label, string Tone) Warning(string? warning) => (warning ?? "").Trim().ToLowerInvariant() switch
    {
        "none" => ("Within budget", "ok"),
        "warn" => ("Warning threshold reached", "warn"),
        "critical" => ("Critical threshold reached", "warn"),
        "exceeded" => ("Cap reached — new paid work blocked", "bad"),
        "" => ("Warning state not reported", "hollow"),
        _ => (warning!.Trim(), "hollow"),
    };

    public static string Enforcement(string? enforcement) => (enforcement ?? "").Trim().ToLowerInvariant() switch
    {
        "hard" => "Hard — refuses paid work that would exceed the cap (estimated bound)",
        "warn-only" => "Warn only — never blocks",
        "" => "Enforcement not reported",
        _ => enforcement!.Trim(),
    };

    public static string Thresholds(IReadOnlyList<decimal>? warnAt)
        => warnAt is null || warnAt.Count == 0
            ? "No warning thresholds"
            : string.Join(", ", warnAt.Select(w => (w * 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%"));

    /// <summary>Remaining as text; "Exceeded by $x" when negative (spend is never truncated to the cap).</summary>
    public static string Remaining(decimal remaining) => remaining >= 0 ? Money.Usd(remaining) : "Exceeded by " + Money.Usd(-remaining);

    /// <summary>0–100 for the meter; spend + reserved over cap. A zero cap with any use is full.</summary>
    public static int MeterPercent(decimal spent, decimal reserved, decimal cap)
    {
        if (cap <= 0) return spent + reserved > 0 ? 100 : 0;
        return (int)Math.Clamp((spent + reserved) / cap * 100m, 0m, 100m);
    }

    /// <summary>True when the server reports no spend and nothing reserved anywhere — the "local-only, nothing to
    /// chart" state that gets an explanation instead of a flat chart.</summary>
    public static bool NoSpendAnywhere(IReadOnlyList<BudgetStateDto>? budgets)
        => budgets is not null && budgets.All(b => b.SpentUsd == 0m && b.ReservedUsd == 0m);

    /// <summary>Parses "80, 95" (percent) into fractions [0.80, 0.95]; null when malformed or out of (0, 100].</summary>
    public static List<decimal>? ParseThresholds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<decimal>();
        foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim().TrimEnd('%');
            if (!decimal.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct <= 0 || pct > 100) return null;
            result.Add(Math.Round(pct / 100m, 4));
        }
        result.Sort();
        return result;
    }

    public static string FormatThresholds(IReadOnlyList<decimal>? warnAt)
        => warnAt is null ? "" : string.Join(", ", warnAt.Select(w => (w * 100m).ToString("0.##", CultureInfo.InvariantCulture)));
}
