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

    public static string Cost(AuditEntryView r) => ToolOnly(r) ? "≈ " + Usd(r.Usage.CostUsd) + " web-search charge (no provider call)" : Split(r) ?? r.CostBasis switch
    {
        "estimated_unreported" => "≈ " + Usd(r.Usage.CostUsd) + " (includes an estimated web-search charge)",
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
        "estimated_unreported" => "≈ " + Usd(cost) + " estimated",
        _ => NotReported,
    };

    /// <summary>ADR-0015: cost text for one tool call (web search), by its recorded basis. A search charge is
    /// credits × the configured price, or the reservation when usage was not reported — never "estimated from text".</summary>
    public static string ToolCost(AuditToolCallView t) => t.CostBasis switch
    {
        "reported" => Usd(t.CostUsd ?? 0m) + (t.Credits is { } c ? " (" + c.ToString(Inv) + (c == 1 ? " credit" : " credits") + " × configured price)" : " reported usage"),
        "estimated_failed_call" => "≈ " + Usd(t.CostUsd ?? 0m) + " reserved charge (the search was sent and failed; it may be billed)",
        "estimated_unreported" => "≈ " + Usd(t.CostUsd ?? 0m) + " reserved charge (no usage reported)",
        "no_call" => "$0.00 — nothing was sent",
        _ => NotReported,
    };

    /// <summary>When a request has BOTH a tool charge (web search) and inference, the total split into its parts with each
    /// part's own basis — never one "provider charge" label for a total that is mostly (or only) the search.</summary>
    public static string? Split(AuditEntryView r)
    {
        if (r.ToolCalls is not { Count: > 0 } tools || ToolOnly(r)) return null;
        decimal tool = tools.Sum(t => t.CostUsd ?? 0m);
        decimal inference = Math.Max(0m, r.Usage.CostUsd - tool);
        var attempts = r.Attempts ?? new List<AuditAttemptView>();
        string inferenceBasis =
            attempts.Count == 0 ? "no provider call"
            : attempts.All(a => a.CostBasis == "zero_priced") ? "configured $0 local model, infrastructure excluded"
            : attempts.Any(a => a.CostBasis is "estimated_from_text" or "estimated_failed_call") ? "includes estimates"
            : attempts.Any(a => a.CostBasis is "unpriced" or "unknown_failed_call") ? "partly unknown"
            : "reported usage × configured price";
        string toolBasis = tools.All(t => t.CostBasis == "reported") ? "credits × configured price"
            : tools.All(t => t.CostBasis == "no_call") ? "not sent"
            : "reserved charge";
        return Usd(r.Usage.CostUsd) + " total · web search " + Usd(tool) + " (" + toolBasis + ") · inference "
               + Usd(inference) + " (" + inferenceBasis + ")";
    }

    /// <summary>True when the request's only charge is a tool call (e.g. a failed search before any provider call).</summary>
    public static bool ToolOnly(AuditEntryView r)
        => r.ToolCalls is { Count: > 0 } && (r.Attempts is null || r.Attempts.Count == 0) && r.CostBasis is not "no_call";

    /// <summary>Short form for table cells (same rules, fewer words).</summary>
    public static string CostShort(AuditEntryView r) => ToolOnly(r) ? "≈ " + Usd(r.Usage.CostUsd) + " search" : Split(r) is not null ? Usd(r.Usage.CostUsd) + " incl. search" : r.CostBasis switch
    {
        "estimated_unreported" => "≈ " + Usd(r.Usage.CostUsd),
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
        "estimated_unreported" => "A web search was charged at its reservation because the provider reported no usage",
        null => "Not recorded (legacy record)",
        _ => "Unrecognised basis: " + basis,
    };
}
