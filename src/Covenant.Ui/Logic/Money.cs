using System.Globalization;

namespace Covenant.Ui.Logic;

/// <summary>USD formatting with precision appropriate to tiny charges (spec §4.5, ADR-0014 "Precision"):
/// a non-zero amount is never shown as "$0.00". Rules (invariant culture, the bundle ships no globalization data):
/// <list type="bullet">
/// <item>null → "Not reported" (unknown is never a zero).</item>
/// <item>exactly zero → "$0.00".</item>
/// <item>|v| ≥ 1 → two decimals with thousands separators ("$1,234.56").</item>
/// <item>0.01 ≤ |v| &lt; 1 → up to four decimals, at least two ("$0.50", "$0.0123").</item>
/// <item>|v| &lt; 0.01 → three significant digits, trailing zeros trimmed ("$0.000184", "$0.003").</item>
/// <item>0 &lt; |v| &lt; 0.000001 → "&lt;$0.000001" (still visibly non-zero).</item>
/// </list>
/// Negative amounts carry a leading minus ("-$1.23").</summary>
public static class Money
{
    public const string NotReported = "Not reported";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const decimal Floor = 0.000001m;

    public static string Usd(decimal? value) => value is { } v ? Usd(v) : NotReported;

    public static string Usd(decimal value)
    {
        if (value == 0m) return "$0.00";
        var sign = value < 0 ? "-" : "";
        var abs = Math.Abs(value);
        if (abs >= 1m) return sign + "$" + Math.Round(abs, 2, MidpointRounding.AwayFromZero).ToString("N2", Inv);
        if (abs >= 0.01m) return sign + "$" + TrimTo(Math.Round(abs, 4, MidpointRounding.AwayFromZero), 2);
        if (abs < Floor) return sign + "<$0.000001";
        return sign + "$" + TrimTo(Significant(abs, 3), 2);
    }

    /// <summary>Caps and limits: always two decimals ("$250.00"), tiny caps still significant ("$0.005").</summary>
    public static string Cap(decimal value)
        => Math.Abs(value) >= 0.01m || value == 0m
            ? (value < 0 ? "-" : "") + "$" + Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero).ToString("N2", Inv)
            : Usd(value);

    /// <summary>A price per one million tokens ("$0.15 / 1M"); null = "Unpriced".</summary>
    public static string PerMillion(decimal? value) => value is { } v ? Usd(v) + " / 1M" : "Unpriced";

    /// <summary>"in $0.15 · out $0.60 per 1M tokens"; "Unpriced — cost unknown, not zero" when either side is missing.</summary>
    public static string Price(decimal? input, decimal? output)
    {
        if (input is null || output is null) return "Unpriced — cost unknown, not zero";
        if (input == 0m && output == 0m) return "$0 provider charge (infrastructure excluded)";
        return "in " + Usd(input.Value) + " · out " + Usd(output.Value) + " per 1M tokens";
    }

    /// <summary>Rounds to <paramref name="digits"/> significant digits (0 &lt; v &lt; 1).</summary>
    private static decimal Significant(decimal v, int digits)
    {
        int leadingZeros = 0;
        var probe = v;
        while (probe < 0.1m && leadingZeros < 26)
        {
            probe *= 10m;
            leadingZeros++;
        }
        int decimals = Math.Min(28, leadingZeros + digits);
        return Math.Round(v, decimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>Fixed-point text with trailing zeros removed but at least <paramref name="minDecimals"/> decimals.</summary>
    private static string TrimTo(decimal v, int minDecimals)
    {
        var text = v.ToString("0.############################", Inv);
        int dot = text.IndexOf('.');
        int have = dot < 0 ? 0 : text.Length - dot - 1;
        if (have >= minDecimals) return text;
        if (dot < 0) text += ".";
        return text + new string('0', minDecimals - have);
    }

    /// <summary>"62%" style share for meters; "n/a" without a positive cap.</summary>
    public static string Share(decimal part, decimal whole)
    {
        if (whole <= 0) return "n/a";
        var pct = part / whole * 100m;
        return pct.ToString(pct >= 10 ? "F0" : "F1", Inv) + "%";
    }
}
