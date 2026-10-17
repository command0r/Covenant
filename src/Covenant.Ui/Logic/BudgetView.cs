using System.Globalization;
using System.Text.Json;
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
        "warn-only" or "warnonly" => "Warn only — never blocks",
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

/// <summary>Lenient reader for GET /admin/budgets/{id}/requests ("contributing settled request ids and amounts"):
/// accepts a bare array or <c>{"items":[…]}</c> and the obvious alternative field names, so a small backend naming
/// difference degrades to "Not reported" values instead of a broken page. Unknown shapes return null.</summary>
public static class BudgetRequests
{
    public static List<BudgetRequestDto>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind == JsonValueKind.Object && TryGet(root, out array, "items", "requests") && array.ValueKind == JsonValueKind.Array) { }
            else return null;

            var list = new List<BudgetRequestDto>();
            foreach (var e in array.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "request_id", "id", "requestId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new BudgetRequestDto
                {
                    RequestId = id,
                    AttemptId = Str(e, "attempt_id", "attemptId"),
                    AmountUsd = Dec(e, "amount_usd", "settled_usd", "cost_usd", "amount"),
                    TimestampUtc = Time(e, "ts_utc", "settled_utc", "timestamp_utc", "utc"),
                    Scope = Str(e, "scope"),
                    Kind = Str(e, "kind", "op"),
                    Overrun = TryGet(e, out var ov, "overrun") && ov.ValueKind == JsonValueKind.True,
                });
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement o, out JsonElement value, params string[] names)
    {
        foreach (var n in names)
            if (o.TryGetProperty(n, out value)) return true;
        value = default;
        return false;
    }

    private static string? Str(JsonElement o, params string[] names)
        => TryGet(o, out var v, names) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement o, params string[] names)
        => TryGet(o, out var v, names) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTimeOffset? Time(JsonElement o, params string[] names)
        => TryGet(o, out var v, names) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var t) ? t : null;
}
