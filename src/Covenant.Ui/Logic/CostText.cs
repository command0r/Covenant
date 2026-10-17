using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Cost and token wording driven by the recorded CostBasis (ADR-0009). Unknown is never rendered as
/// "$0": only no_call and zero_priced may show a zero, and both say why. "mixed" (review round 2, B2) = the
/// request total combines charges of different bases (e.g. a failed attempt's estimate plus a reported fallback):
/// labelled "Mixed (includes estimates)", never as a plain reported figure.</summary>
public static class CostText
{
    public const string NotReported = "Not reported";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Significant-digit USD (Logic/Money): a non-zero charge is never shown as $0.00.</summary>
    public static string Usd(decimal value) => Money.Usd(value);

    public const string MixedLabel = "Mixed (includes estimates)";

    public static string Cost(AuditEntryView r) => r.CostBasis switch
    {
        "reported" => Usd(r.Usage.CostUsd) + " (estimated provider charge)",
        "mixed" => "≈ " + Usd(r.Usage.CostUsd) + " · " + MixedLabel,
        "zero_priced" => "$0.00 configured provider charge · infrastructure excluded",
        "estimated_from_text" or "estimated_failed_call" => "≈ " + Usd(r.Usage.CostUsd) + " estimated from text length",
        "unknown_failed_call" or "unpriced" => NotReported,
        "no_call" => "$0.00 — no provider call",
        null => Usd(r.Usage.CostUsd) + " (legacy record)",
        _ => NotReported,
    };

    /// <summary>Cost text for one provider attempt, by its recorded basis (same rules as <see cref="Cost"/>).</summary>
    public static string BasisCost(string? basis, decimal cost) => basis switch
    {
        "reported" => Usd(cost) + " reported usage",
        "zero_priced" => "$0.00 configured",
        "estimated_from_text" or "estimated_failed_call" => "≈ " + Usd(cost) + " estimated",
        "mixed" => "≈ " + Usd(cost) + " " + MixedLabel,
        "no_call" => "$0.00 — no provider call",
        _ => NotReported,
    };

    /// <summary>Short form for table cells (same rules, fewer words).</summary>
    public static string CostShort(AuditEntryView r) => r.CostBasis switch
    {
        "reported" => Usd(r.Usage.CostUsd),
        "mixed" => "≈ " + Usd(r.Usage.CostUsd) + " mixed",
        "zero_priced" => "$0.00 configured",
        "estimated_from_text" or "estimated_failed_call" => "≈ " + Usd(r.Usage.CostUsd),
        "unknown_failed_call" or "unpriced" => NotReported,
        "no_call" => "$0.00 no call",
        null => Usd(r.Usage.CostUsd) + " (legacy)",
        _ => NotReported,
    };

    /// <summary>Token counts only when the provider reported them. The backend stores 0 with
    /// UsageReported=false when unknown — that zero is not real and is never shown as a count.</summary>
    public static string Tokens(AuditEntryView r)
    {
        if (!string.IsNullOrWhiteSpace(r.AttemptedModel))
            return r.UsageReported == true ? Counts(r.Usage) : NotReported;
        if (ModelFacts.IsV2(r))
            return r.CacheHit ? "None — served from cache" : "None — no provider call";
        return Counts(r.Usage) + " (legacy record)";
    }

    private static string Counts(UsageView u)
        => u.InputTokens.ToString("N0", Inv) + " in · " + u.OutputTokens.ToString("N0", Inv) + " out · "
           + u.TotalTokens.ToString("N0", Inv) + " total";

    /// <summary>Human description of the basis for the detail drawer.</summary>
    public static string BasisDescription(string? basis) => basis switch
    {
        "reported" => "Provider-reported tokens × configured prices",
        "zero_priced" => "Model configured with a $0 provider price (in-perimeter compute; infrastructure not included)",
        "estimated_from_text" => "Priced model, usage not reported: estimated from text length (chars ÷ 4) and charged to the budget",
        "estimated_failed_call" => "A provider call started but failed: estimated from text length and charged to the budget",
        "unknown_failed_call" => "A provider call started but its cost could not be estimated",
        "unpriced" => "Model has no configured price — cost unknown, not zero",
        "mixed" => "Total of attempts with different cost bases (e.g. a failed attempt's estimate plus a reported fallback) — includes estimates, so it is not a reported figure",
        "no_call" => "No provider call was made",
        null => "Not recorded (legacy record)",
        _ => "Unrecognised basis: " + basis,
    };
}
