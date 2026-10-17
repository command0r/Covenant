using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>A demo preset (spec §8: synthetic, bounded tasks with an independently checkable answer). It only
/// POPULATES the composer — Run is always a separate, explicit action, and the governed pipeline decides the
/// outcome. <see cref="Expectation"/> is the governance behaviour the scenario is designed to show under the
/// default demo policy; it is shown as an expectation, never as a result.</summary>
public sealed record ScenarioPreset(
    char Letter,
    string Title,
    string Hint,
    string Prompt,
    bool FreshNonce,
    string? PreferredBaseline,
    string Expectation,
    string ExpectedAnswer)
{
    /// <summary>True while the form still holds this preset's request (labels the snapshot "Scenario X").</summary>
    public bool StillMatches(PlaygroundForm form)
        => form.Scenario == Letter
           && string.Equals(form.Prompt, Prompt, StringComparison.Ordinal)
           && form.Mode == RouteMode.Policy
           && form.FreshNonce == FreshNonce
           && form.WebSources == WebSourcesMode.Off;
}

/// <summary>What applying a preset did. <see cref="Note"/> explains anything the operator still has to choose.</summary>
public sealed record PresetApplyResult(string? Note);

public static class ScenarioPresets
{
    /// <summary>Synthetic, public ticket text: nothing the detector treats as PII/PHI. Expected answer: low.</summary>
    public const string TicketPrompt =
        "Extract the priority from this ticket and answer only JSON {\"priority\":\"low|medium|high\"}: " +
        "'Printer on floor 3 is jammed again, not urgent.'";

    /// <summary>The same ticket with a synthetic sensitive marker. 078-05-1120 is the published sample SSN (never
    /// issued to a person); it matches RegexDataClassifier's US SSN rule, so detection escalates the Public
    /// baseline to Pii — the request must then stay local, or be refused when no local destination is eligible.</summary>
    public const string SensitiveTicketPrompt =
        "Extract the priority from this ticket and answer only JSON {\"priority\":\"low|medium|high\"}: " +
        "'Printer on floor 3 is jammed again, not urgent. Reporter SSN 078-05-1120 (synthetic test marker).'";

    public const string ExpectedLow = "{\"priority\":\"low\"}";

    public static readonly IReadOnlyList<ScenarioPreset> All =
    [
        new('A', "Public → cloud", "Public profile; economical approved cloud model",
            TicketPrompt, false, "Public",
            "Public profile: routed to the approved economical cloud model (or its approved fallback); cost attributed to the application's team.",
            ExpectedLow),
        new('B', "Sensitive → local", "Synthetic SSN marker escalates to PII",
            SensitiveTicketPrompt, false, "Public",
            "Detection escalates Public to PII: served by the approved local model; never a cloud model.",
            ExpectedLow),
        new('C', "Sensitive, no local route", "Same request; local destination unavailable",
            SensitiveTicketPrompt, false, "Public",
            "With no eligible local destination (local route paused, disabled or missing) the request is refused; no cloud call occurs.",
            ExpectedLow),
        new('D', "Pause demo", "Fresh nonce every run — never served from cache",
            TicketPrompt, true, null,
            "Pause new provider calls, run: refused at the pause stage. Resume, run again: served. A fresh nonce means it is never a cache hit.",
            ExpectedLow),
    ];

    public static ScenarioPreset? Find(char? letter)
    {
        if (letter is null) return null;
        var up = char.ToUpperInvariant(letter.Value);
        foreach (var p in All)
            if (p.Letter == up) return p;
        return null;
    }

    public static ScenarioPreset? Find(string? letter)
        => string.IsNullOrWhiteSpace(letter) || letter.Trim().Length != 1 ? null : Find(letter.Trim()[0]);

    /// <summary>Populates the form from the preset: prompt, nonce flag, "let policy choose", and — when the preset
    /// wants a baseline — a granted profile with that baseline. If no application is selected yet, the first
    /// console-testable application (preferring one with a matching profile) is selected. Never submits, never
    /// touches pause state, policy or credentials; streaming, output cap and labels stay as the operator set them.</summary>
    public static PresetApplyResult ApplyTo(ScenarioPreset preset, PlaygroundForm form,
        IReadOnlyList<ApplicationSummaryDto>? apps = null, IReadOnlyList<WorkloadProfileDto>? profiles = null)
    {
        form.Scenario = preset.Letter;
        form.Prompt = preset.Prompt;
        form.Mode = RouteMode.Policy;
        form.FreshNonce = preset.FreshNonce;
        form.WebSources = WebSourcesMode.Off;
        form.Location = "";

        if (apps is null) return new PresetApplyResult(null);

        var current = ComposerRules.Find(apps, form.ApplicationId);
        if (current is null || ComposerRules.WhyNotSelectable(current) is not null)
        {
            var selectable = apps.Where(a => ComposerRules.WhyNotSelectable(a) is null).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            var pick = preset.PreferredBaseline is { } want
                ? selectable.FirstOrDefault(a => MatchingProfile(a, want, profiles) is not null) ?? selectable.FirstOrDefault()
                : selectable.FirstOrDefault();
            if (pick is null) return new PresetApplyResult("No application is enabled for console testing.");
            ComposerRules.SelectApplication(form, apps, pick.Id);
            current = pick;
        }

        if (preset.PreferredBaseline is not { } baseline) return new PresetApplyResult(null);
        if (MatchingProfile(current, baseline, profiles) is { } profile)
        {
            form.ProfileId = profile;
            return new PresetApplyResult(null);
        }
        return new PresetApplyResult("No profile with a " + baseline + " baseline is granted to " + current.Id + " — choose a profile.");
    }

    /// <summary>First granted profile whose configured baseline equals <paramref name="baseline"/>; when the
    /// configuration is not readable, a granted profile whose id contains the baseline name.</summary>
    public static string? MatchingProfile(ApplicationSummaryDto app, string baseline, IReadOnlyList<WorkloadProfileDto>? profiles)
    {
        var granted = ComposerRules.GrantedProfiles(app);
        if (profiles is not null)
            foreach (var id in granted)
                if (string.Equals(ComposerRules.Profile(profiles, id)?.Baseline, baseline, StringComparison.OrdinalIgnoreCase))
                    return id;
        if (profiles is null || profiles.Count == 0)
            foreach (var id in granted)
                if (id.Contains(baseline, StringComparison.OrdinalIgnoreCase))
                    return id;
        return null;
    }

    /// <summary>Ready = the form still holds this preset and nothing blocks Run. Only then does Overview offer
    /// "Run preset".</summary>
    public static bool IsReady(ScenarioPreset preset, PlaygroundForm form, IReadOnlyList<ApplicationSummaryDto>? apps)
        => preset.StillMatches(form) && ComposerRules.Problem(form, apps) is null;

    /// <summary>A fresh per-run nonce for scenario D.</summary>
    public static string NewNonce() => $" [run {Guid.NewGuid():N}]";
}
