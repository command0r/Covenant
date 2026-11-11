using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>A Host dry-run route for the composer inputs captured in <see cref="Key"/>. Stale once the form changes
/// (application, profile, prompt or model) — shown as "Preview is for a previous form; preview again".</summary>
public sealed record RoutePreview(string Key, SimulateResponseDto? Result, string? Error, bool Loading)
{
    public static string KeyOf(PlaygroundForm f)
        => string.Join("\u001f", f.ApplicationId ?? "", f.ProfileId ?? "", f.RequestedModel ?? "", f.Prompt);

    /// <summary>The form holds enough for a Host dry run: a sender and a prompt. The workload switch uses this to decide
    /// whether the Expected route can follow it at once (dry run only — no model call, no record, no spend).</summary>
    public static bool CanPreview(PlaygroundForm f)
        => !string.IsNullOrWhiteSpace(f.ApplicationId) && !string.IsNullOrWhiteSpace(f.Prompt);

    public bool IsCurrent(PlaygroundForm f, int? activeVersion = null)
        => string.Equals(Key, KeyOf(f), StringComparison.Ordinal)
           && (activeVersion is null || Result?.ConfigVersion == activeVersion);

    /// <summary>"Policy public-writing → anthropic/claude… (approved fallback: …)", or "Refused: …".</summary>
    public static string Summary(SimulateResponseDto r)
    {
        if (!string.Equals(r.Decision, "route", StringComparison.Ordinal))
            return "Would be refused" + (r.PolicyId is { Length: > 0 } p ? " by policy " + p : "") + ": " + (r.Reason ?? "no reason reported");
        var head = (r.PolicyId is { Length: > 0 } pid ? "Policy " + pid + " → " : "→ ") + (r.Primary ?? "primary not reported");
        return r.Fallbacks.Count > 0 ? head + " (approved fallback: " + string.Join(", ", r.Fallbacks) + ")" : head;
    }

    /// <summary>"Profile public-demo sets Public; detection found US SSN pattern → Pii."</summary>
    public static string Classification(SimulateResponseDto r)
        => "Profile " + (r.Profile ?? "default") + " sets " + (r.Baseline ?? "?")
           + (r.DetectedSignal is { Length: > 0 } sig ? "; detection found " + sig + " → " + r.Classification : " → " + r.Classification) + ".";
}
