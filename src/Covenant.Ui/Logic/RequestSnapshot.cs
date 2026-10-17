namespace Covenant.Ui.Logic;

public enum RouteMode { Policy, Catalog, Explicit }

/// <summary>The Playground's editable form. Mutable on purpose (inputs bind to it); a run never reads it after
/// submission — it reads the <see cref="RequestSnapshot"/> frozen from it.</summary>
public sealed class PlaygroundForm
{
    public const int DefaultMaxTokens = 256;

    /// <summary>Preset letter the form was last filled from (A–D), or null for a custom request.</summary>
    public char? Scenario { get; set; }
    public string Prompt { get; set; } = "";
    public RouteMode Mode { get; set; } = RouteMode.Policy;
    public string? CatalogModel { get; set; }
    public string? ExplicitModel { get; set; }
    public bool Streaming { get; set; }
    public int? MaxTokens { get; set; } = DefaultMaxTokens;
    public string Workflow { get; set; } = "playground";
    public string UseCase { get; set; } = "console-test";
    /// <summary>Scenario D: append a fresh nonce on every run so the request can never be a cache hit.</summary>
    public bool FreshNonce { get; set; }

    /// <summary>The model the request will ask for, or null to let policy choose.</summary>
    public string? RequestedModel => Mode switch
    {
        RouteMode.Catalog => Blank(CatalogModel),
        RouteMode.Explicit => Blank(ExplicitModel),
        _ => null,
    };

    /// <summary>Why Run is not possible right now, or null.</summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Prompt)) return "Enter a prompt.";
            if (Mode == RouteMode.Catalog && Blank(CatalogModel) is null) return "Choose a model from the catalog, or let policy choose.";
            if (Mode == RouteMode.Explicit && Blank(ExplicitModel) is null) return "Enter an explicit model id, or let policy choose.";
            if (MaxTokens is <= 0) return "Max output tokens must be a positive number (or empty for the provider default).";
            return null;
        }
    }

    public void Clear()
    {
        Scenario = null;
        Prompt = "";
        Mode = RouteMode.Policy;
        CatalogModel = null;
        ExplicitModel = null;
        Streaming = false;
        MaxTokens = DefaultMaxTokens;
        Workflow = "playground";
        UseCase = "console-test";
        FreshNonce = false;
    }

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>One submitted request, frozen at Run. Immutable: editing the form (or a preset) afterwards cannot
/// relabel the result. <see cref="Prompt"/> is exactly what was sent (including any scenario-D nonce).</summary>
public sealed record RequestSnapshot(
    int Sequence,
    string ScenarioLabel,
    char? Scenario,
    string Prompt,
    string BasePrompt,
    string? RequestedModel,
    RouteMode Mode,
    bool Streaming,
    int? MaxTokens,
    string? Workflow,
    string? UseCase,
    bool FreshNonce,
    DateTimeOffset SubmittedUtc)
{
    public const string PolicyChooses = "Let policy choose";

    public string RoutingLabel => RequestedModel ?? PolicyChooses;

    public string Title => "Request #" + Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + ScenarioLabel;

    /// <summary>Freezes the form. <paramref name="nonce"/> is appended when the form asks for a fresh nonce
    /// (pass <see cref="ScenarioPresets.NewNonce"/>; injectable for tests).</summary>
    public static RequestSnapshot Freeze(int sequence, PlaygroundForm form, DateTimeOffset nowUtc, string? nonce = null)
    {
        var basePrompt = form.Prompt;
        var prompt = form.FreshNonce ? basePrompt + (nonce ?? ScenarioPresets.NewNonce()) : basePrompt;
        var preset = ScenarioPresets.Find(form.Scenario);
        var label = preset is not null && preset.StillMatches(form) ? "Scenario " + preset.Letter + " · " + preset.Title : "Custom";
        return new RequestSnapshot(
            Sequence: sequence,
            ScenarioLabel: label,
            Scenario: preset is not null && preset.StillMatches(form) ? preset.Letter : null,
            Prompt: prompt,
            BasePrompt: basePrompt,
            RequestedModel: form.RequestedModel,
            Mode: form.Mode,
            Streaming: form.Streaming,
            MaxTokens: form.MaxTokens,
            Workflow: PlaygroundForm.Blank(form.Workflow),
            UseCase: PlaygroundForm.Blank(form.UseCase),
            FreshNonce: form.FreshNonce,
            SubmittedUtc: nowUtc);
    }

    /// <summary>True while the form still describes this request; false → "Previous request — the form has
    /// changed since".</summary>
    public bool MatchesForm(PlaygroundForm form)
        => string.Equals(BasePrompt, form.Prompt, StringComparison.Ordinal)
           && string.Equals(RequestedModel, form.RequestedModel, StringComparison.Ordinal)
           && Streaming == form.Streaming
           && MaxTokens == form.MaxTokens
           && string.Equals(Workflow, PlaygroundForm.Blank(form.Workflow), StringComparison.Ordinal)
           && string.Equals(UseCase, PlaygroundForm.Blank(form.UseCase), StringComparison.Ordinal)
           && FreshNonce == form.FreshNonce;
}
