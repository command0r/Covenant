namespace Covenant.Ui.Logic;

/// <summary>A demo preset. It only fills the form — the governed pipeline decides the outcome. There is
/// deliberately no "expected outcome" simulation here.</summary>
public sealed record ScenarioPreset(
    char Letter,
    string Title,
    string Hint,
    string Prompt,
    RouteMode Mode,
    string? ExplicitModel,
    bool FreshNonce,
    string Note)
{
    /// <summary>True while the form still holds this preset's request (labels the snapshot "Scenario X").</summary>
    public bool StillMatches(PlaygroundForm form)
        => form.Scenario == Letter
           && string.Equals(form.Prompt, Prompt, StringComparison.Ordinal)
           && form.Mode == Mode
           && (Mode != RouteMode.Explicit || string.Equals(PlaygroundForm.Blank(form.ExplicitModel), ExplicitModel, StringComparison.Ordinal))
           && form.FreshNonce == FreshNonce;
}

public static class ScenarioPresets
{
    /// <summary>Short synthetic internal question. Contains nothing the classifier treats as PII/PHI.</summary>
    public const string NormalPrompt =
        "Synthetic test: in two sentences, explain what an expense-approval workflow is for a new employee.";

    public const string UnapprovedModel = "gpt-4-unapproved-test";

    /// <summary>Synthetic sensitive fixture. 078-05-1120 is the published sample SSN (never issued to a person);
    /// it matches the PII rule \b\d{3}-\d{2}-\d{4}\b in RegexDataClassifier, so policy routes it to the local
    /// model or refuses it when no local route is usable.</summary>
    public const string SensitivePrompt =
        "Synthetic test record: patient John Example, SSN 078-05-1120 (the published sample number), summarize the visit";

    public static readonly IReadOnlyList<ScenarioPreset> All =
    [
        new('A', "Permitted request", "Policy chooses the route",
            NormalPrompt, RouteMode.Policy, null, false,
            "A normal internal question; policy picks the route."),
        new('B', "Unapproved model", "Requests a model policy does not permit",
            NormalPrompt, RouteMode.Explicit, UnapprovedModel, false,
            "Test input: selecting a model does not grant permission."),
        new('C', "Sensitive data", "Synthetic PII fixture → local route",
            SensitivePrompt, RouteMode.Policy, null, false,
            "Synthetic record with the published sample SSN; must stay on the local route or be refused."),
        new('D', "Pause and resume", "Fresh nonce every run — never cached",
            NormalPrompt, RouteMode.Policy, null, true,
            "A fresh nonce is appended on every run, so it is never a cache hit. Pause, run, resume, run again."),
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

    /// <summary>Populates the form's inputs from the preset. Streaming, max tokens, labels and the key are
    /// left as the operator set them. Never touches policy, pause state or credentials.</summary>
    public static void ApplyTo(ScenarioPreset preset, PlaygroundForm form)
    {
        form.Scenario = preset.Letter;
        form.Prompt = preset.Prompt;
        form.Mode = preset.Mode;
        form.ExplicitModel = preset.Mode == RouteMode.Explicit ? preset.ExplicitModel : form.ExplicitModel;
        form.FreshNonce = preset.FreshNonce;
    }

    /// <summary>A fresh per-run nonce for scenario D.</summary>
    public static string NewNonce() => $" [run {Guid.NewGuid():N}]";
}
