using System.Globalization;

namespace Covenant.Ui.Services;

/// <summary>Invariant-culture formatting (the bundle ships no globalization data). Money is shown to four
/// decimals because per-request provider charges are fractions of a cent; totals use two.</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Usd(decimal value, int decimals = 4)
        => "$" + value.ToString("F" + decimals.ToString(Inv), Inv);

    public static string UsdTotal(decimal value) => Usd(value, value == decimal.Truncate(value) ? 2 : 4);

    public static string Int(long value) => value.ToString("N0", Inv);

    public static string Percent(decimal numerator, decimal denominator)
    {
        if (denominator <= 0) return "n/a";
        var pct = numerator / denominator * 100m;
        return pct.ToString(pct >= 10 ? "F0" : "F1", Inv) + "%";
    }

    public static int PercentClamped(decimal numerator, decimal denominator)
        => denominator <= 0 ? 0 : (int)Math.Clamp(numerator / denominator * 100m, 0m, 100m);

    public static string Utc(DateTimeOffset? t)
        => t is null ? "Not recorded" : t.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " UTC";

    public static string UtcShort(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm:ss", Inv);

    public static string Ms(double ms) => ms < 1000 ? ms.ToString("F0", Inv) + " ms" : (ms / 1000).ToString("F2", Inv) + " s";

    public static string Ago(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is null) return "never";
        var s = (int)Math.Max(0, (now - t.Value).TotalSeconds);
        if (s < 60) return s + " s ago";
        if (s < 3600) return (s / 60) + " min ago";
        return (s / 3600) + " h ago";
    }

    public static string Duration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return d.Seconds + " s";
        if (d.TotalHours < 1) return (int)d.TotalMinutes + " min";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h {d.Minutes} min";
        return $"{(int)d.TotalDays} d {d.Hours} h";
    }

    public static string OrNotRecorded(string? s) => string.IsNullOrWhiteSpace(s) ? "Not recorded" : s;
}
