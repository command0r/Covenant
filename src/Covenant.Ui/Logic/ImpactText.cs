using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>One changed route in the impact preview, ready to display.</summary>
public sealed record ImpactLine(string Scope, string Before, string After, bool NowRefused, bool WasRefused);

/// <summary>Formats the Host's impact analysis (POST /admin/config/validate → <c>impact</c>). The Host computes it
/// with the real engine against the candidate; the console only words it. Nothing here guesses an impact.</summary>
public static class ImpactText
{
    /// <summary>"Public" or "Public · profile public-tickets" (profile "*" / empty = any profile).</summary>
    public static string Scope(ConfigImpactRowDto row)
    {
        var cls = PolicyText.ClassificationLabel(row.Classification);
        return string.IsNullOrWhiteSpace(row.Profile) || row.Profile is "*" or "any" ? cls : cls + " · profile " + row.Profile;
    }

    /// <summary>Changed rows only (the Host marks <c>changed</c>; a row whose text differs is also treated as changed
    /// so an older Host without the flag still previews correctly).</summary>
    public static IReadOnlyList<ImpactLine> ChangedRoutes(ConfigImpactDto? impact)
    {
        if (impact is null) return [];
        var lines = new List<ImpactLine>();
        foreach (var r in impact.Classifications)
        {
            bool changed = r.Changed || !string.Equals(r.Before, r.After, StringComparison.Ordinal);
            if (!changed) continue;
            lines.Add(new ImpactLine(Scope(r), Route(r.Before), Route(r.After), IsRefused(r.After), IsRefused(r.Before)));
        }
        return lines;
    }

    public static bool IsRefused(string? route) => route is null || route.StartsWith("refused", StringComparison.OrdinalIgnoreCase);

    public static string Route(string? route) => string.IsNullOrWhiteSpace(route) ? "Not reported" : route;

    /// <summary>One-line headline: "2 routes change · 1 newly refused · 1 pricing gap" or "No routing change".</summary>
    public static string Headline(ConfigImpactDto? impact)
    {
        if (impact is null) return "Impact not computed (the candidate has structural errors).";
        var parts = new List<string>();
        int changed = ChangedRoutes(impact).Count;
        if (changed > 0) parts.Add(Count(changed, "route changes", "routes change"));
        if (impact.NewlyRefused.Count > 0) parts.Add(N(impact.NewlyRefused.Count) + " newly refused");
        if (impact.UnavailableDestinations.Count > 0) parts.Add(Count(impact.UnavailableDestinations.Count, "unavailable destination", "unavailable destinations"));
        if (impact.PricingGaps.Count > 0) parts.Add(Count(impact.PricingGaps.Count, "pricing gap", "pricing gaps"));
        if (impact.ApplicationsAffected.Count > 0) parts.Add(Count(impact.ApplicationsAffected.Count, "application affected", "applications affected"));
        return parts.Count == 0 ? "No routing change for any classification or profile." : string.Join(" · ", parts);
    }

    /// <summary>True when the preview contains something an operator must acknowledge before saving (requests that
    /// would now be refused, or destinations that cannot run).</summary>
    public static bool NeedsAttention(ConfigImpactDto? impact)
        => impact is not null && (impact.NewlyRefused.Count > 0 || impact.UnavailableDestinations.Count > 0 || impact.PricingGaps.Count > 0);

    private static string Count(int n, string one, string many) => N(n) + " " + (n == 1 ? one : many);
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
