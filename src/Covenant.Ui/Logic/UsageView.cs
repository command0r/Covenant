using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Presentation rules for the Usage page (pure, unit-tested): window/group choices, the honest charge line (unknown is
/// never $0, local is not free), outcome split and a bounded per-day trend.</summary>
public static class UsagePresentation
{
    public static readonly IReadOnlyList<(string Value, string Label)> Windows =
        [("1h", "Last hour"), ("24h", "Last 24 hours"), ("7d", "Last 7 days"), ("30d", "Last 30 days"), ("all", "Whole segment")];

    public static readonly IReadOnlyList<(string Value, string Label)> Groups =
        [("team", "Team (who pays)"), ("application", "Application (caller)"), ("profile", "Workload")];

    public static string GroupLabel(string group) => group switch
    {
        "application" => "Application",
        "profile" => "Workload",
        _ => "Team",
    };

    /// <summary>"$0.0123 · 2 estimated · 1 unknown (not counted)" — the known sum plus what it does not cover.</summary>
    public static string ChargeLine(UsageRowDto r)
    {
        var s = Money.Usd(r.TotalChargeUsd);
        if (r.EstimatedChargeRequests > 0) s += " · " + N(r.EstimatedChargeRequests) + " estimated";
        if (r.UnknownChargeRequests > 0) s += " · " + N(r.UnknownChargeRequests) + " unknown (not counted)";
        return s;
    }

    public static string Split(UsageRowDto r)
        => Money.Usd(r.InferenceChargeUsd) + " inference · " + Money.Usd(r.SearchChargeUsd) + " search";

    public static string Outcomes(UsageRowDto r)
    {
        var parts = new List<string> { N(r.Served) + " served" };
        if (r.Refused > 0) parts.Add(N(r.Refused) + " refused");
        if (r.Failed > 0) parts.Add(N(r.Failed) + " failed");
        if (r.Cancelled > 0) parts.Add(N(r.Cancelled) + " cancelled");
        return string.Join(" · ", parts);
    }

    public static string Calls(UsageRowDto r)
    {
        var s = N(r.LocalCalls) + " local · " + N(r.ExternalCalls) + " external";
        if (r.UnknownBoundaryCalls > 0) s += " · " + N(r.UnknownBoundaryCalls) + " unknown";
        if (r.ExternalRetrievals > 0) s += " · " + N(r.ExternalRetrievals) + " web retrievals";
        return s;
    }

    public static string Sensitive(UsageRowDto r)
        => N(r.SensitiveDetected) + " detected · " + N(r.SensitiveBaseline) + " sensitive workload"
           + (r.RedactionsApplied + r.RedactionsRefused > 0
               ? " · " + N(r.RedactionsApplied) + " sanitized, " + N(r.RedactionsRefused) + " sanitization refused"
               : "");

    /// <summary>Bar heights 0–100 for each day (relative to the busiest day of THIS row). Empty when fewer than 2 days.</summary>
    public static IReadOnlyList<(string Date, int Requests, int Height)> Trend(UsageRowDto r)
    {
        if (r.Daily.Count < 2) return [];
        int max = Math.Max(1, r.Daily.Max(d => d.Requests));
        return r.Daily.Select(d => (d.Date, d.Requests, (int)Math.Round(100.0 * d.Requests / max))).ToList();
    }

    public static string Window(UsageReportDto rep)
        => rep.From.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " – "
           + rep.To.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
