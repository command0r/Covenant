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

    /// <summary>Human name of a budget scope: an application's configured display name (else its id), a team, or Global.</summary>
    public static string ScopeName(string? scope, CovenantConfigDto? cfg)
    {
        var s = (scope ?? "").Trim();
        if (s.StartsWith("application:", StringComparison.OrdinalIgnoreCase))
        {
            var id = s[12..];
            var app = cfg?.Applications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            return "Application · " + (string.IsNullOrWhiteSpace(app?.DisplayName) ? id : app!.DisplayName!.Trim());
        }
        if (s.StartsWith("team:", StringComparison.OrdinalIgnoreCase)) return "Team · " + s[5..];
        return Scope(scope);
    }

    /// <summary>What a scope covers and how it relates to the others. Scopes are CEILINGS over the same charges — one charge
    /// counts against every applicable scope at once (Global, its team, its application); spend is never added up across scopes.</summary>
    public static string Coverage(string? scope, CovenantConfigDto? cfg)
    {
        var s = (scope ?? "").Trim();
        if (string.Equals(s, "global", StringComparison.OrdinalIgnoreCase))
            return "Covers every application and team. Every charge counts here.";
        if (s.StartsWith("team:", StringComparison.OrdinalIgnoreCase))
            return "Additional ceiling for all of team " + s[5..] + "'s applications, inside Global — the same charges count against both.";
        if (s.StartsWith("application:", StringComparison.OrdinalIgnoreCase))
        {
            var id = s[12..];
            var team = cfg?.Applications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))?.Team;
            return "Additional ceiling for this application" + (team is { Length: > 0 } t ? " (team " + t + ")" : "")
                   + ", inside Global — the same charge counts against both; it is never added as separate spend.";
        }
        return "";
    }

    /// <summary>A very low HARD cap on one application or team (≤ $0.01). Neutral fact only: the configuration carries no
    /// "demo" metadata, so intent is never inferred — a real team's low cap is labelled the same way.</summary>
    public static bool IsLowCap(string? scope, decimal capUsd, string? enforcement)
        => !string.Equals(enforcement, "warn-only", StringComparison.Ordinal) && capUsd <= 0.01m
           && ((scope ?? "").StartsWith("application:", StringComparison.OrdinalIgnoreCase) || (scope ?? "").StartsWith("team:", StringComparison.OrdinalIgnoreCase));

    /// <summary>One short line for the table: "All applications" / "Additional cap within Global" (the full same-charge
    /// explanation is <see cref="Coverage"/>, shown in the tooltip and the page's info tip).</summary>
    public static string ShortCoverage(string? scope)
    {
        var s = (scope ?? "").Trim();
        if (string.Equals(s, "global", StringComparison.OrdinalIgnoreCase)) return "All applications";
        if (s.StartsWith("team:", StringComparison.OrdinalIgnoreCase)) return "Additional cap for this team within Global";
        if (s.StartsWith("application:", StringComparison.OrdinalIgnoreCase)) return "Additional cap within Global";
        return "";
    }

    /// <summary>The application's owning team for the compact scope line, or null.</summary>
    public static string? TeamOf(string? scope, CovenantConfigDto? cfg)
    {
        var s = (scope ?? "").Trim();
        if (!s.StartsWith("application:", StringComparison.OrdinalIgnoreCase)) return null;
        var id = s[12..];
        return cfg?.Applications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))?.Team is { Length: > 0 } t ? t : null;
    }

    /// <summary>"Hard cap · alerts 80%, 95%" (the long enforcement text is in the tooltip).</summary>
    public static string StateLine(string? enforcement, IReadOnlyList<decimal>? warnAt)
    {
        var e = (enforcement ?? "").Trim().ToLowerInvariant() switch
        {
            "hard" => "Hard cap",
            "warn-only" => "Warn only",
            "" => "Enforcement not reported",
            _ => enforcement!.Trim(),
        };
        return e + " · " + (warnAt is null || warnAt.Count == 0 ? "no alerts" : "alerts " + Thresholds(warnAt));
    }

    /// <summary>Period name alone ("Monthly (UTC)"); the period id goes on its own line.</summary>
    public static string PeriodName(string? period) => Period(period, null);

    /// <summary>Tooltip for the low-cap badge: the real effect, no claim about intent.</summary>
    public static string LowCapEffect(decimal capUsd)
        => "Hard cap of " + Money.Cap(capUsd) + ": a paid request whose reserved cost would exceed what remains is refused before any provider call. Local $0-priced requests reserve nothing.";

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

/// <summary>Rules of the budget Add/Edit dialog (pure). The Host's validation stays authoritative.</summary>
public static class BudgetForm
{
    public static string Scope(string kind, string? name) => kind switch
    {
        "global" => "global",
        "application" => "application:" + (name ?? "").Trim(),
        _ => "team:" + (name ?? "").Trim(),
    };

    /// <summary>A free budget id derived from the scope ("team-research").</summary>
    public static string Id(string scope, CovenantConfigDto? cfg)
        => ConnectionRules.UniqueId(scope.Replace(':', '-'), cfg?.Budgets.Select(b => b.Id) ?? Enumerable.Empty<string>());

    /// <summary>Why the dialog cannot save yet (null = it can; the Host still validates).</summary>
    public static string? Problem(EntityEdit<BudgetConfigDto> e, string warnText, CovenantConfigDto? cfg)
    {
        var b = e.Working;
        if (e.IsNew)
        {
            if (b.Scope is "team:" or "application:") return "Enter the " + (b.Scope.StartsWith("team", StringComparison.Ordinal) ? "team name." : "application id.");
            if (b.Scope.Contains(':') && !ConnectionRules.IsValidId(b.Scope[(b.Scope.IndexOf(':') + 1)..]))
                return "Use letters, digits, '.', '_' or '-' for the name.";
            if (cfg?.Budgets.Any(x => string.Equals(x.Scope, b.Scope, StringComparison.OrdinalIgnoreCase)) == true)
                return "A budget for " + b.Scope + " already exists — edit it instead.";
        }
        if (b.CapUsd < 0m) return "The cap cannot be negative.";
        if (BudgetText.ParseThresholds(warnText) is null) return "Warn at: percentages between 0 and 100, separated by commas (e.g. 80, 95).";
        return null;
    }
}
