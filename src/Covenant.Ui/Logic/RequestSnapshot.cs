using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>How the request names its model: let policy choose (normal), pick an eligible target from the
/// configuration, or type a model id as test input. Overrides never grant permission — policy still decides.</summary>
public enum RouteMode { Policy, Catalog, Explicit }

/// <summary>The shared composer's editable form (Playground and the Overview composer bind to the same instance in
/// PlaygroundState). Mutable on purpose; a run never reads it after submission — it reads the
/// <see cref="RequestSnapshot"/> frozen from it.</summary>
public sealed class PlaygroundForm
{
    public const int DefaultMaxTokens = 256;
    public const string DefaultWorkflow = "playground";
    public const string DefaultUseCase = "console-test";

    /// <summary>Preset letter the form was last filled from (A–D), or null for a custom request.</summary>
    public char? Scenario { get; set; }
    /// <summary>"Run as" application (GET /admin/applications id). The console never holds its credential.</summary>
    public string? ApplicationId { get; set; }
    /// <summary>Workload profile; only profiles granted to the application are selectable. Null = the server uses
    /// the application's default profile.</summary>
    public string? ProfileId { get; set; }
    public string Prompt { get; set; } = "";
    public RouteMode Mode { get; set; } = RouteMode.Policy;
    public string? CatalogModel { get; set; }
    public string? ExplicitModel { get; set; }
    public bool Streaming { get; set; } = true;
    public int? MaxTokens { get; set; } = DefaultMaxTokens;
    public string Workflow { get; set; } = DefaultWorkflow;
    public string UseCase { get; set; } = DefaultUseCase;
    /// <summary>Scenario D: append a fresh nonce on every run so the request can never be a cache hit.</summary>
    public bool FreshNonce { get; set; }

    /// <summary>The model the request will ask for, or null to let policy choose.</summary>
    public string? RequestedModel => Mode switch
    {
        RouteMode.Catalog => Blank(CatalogModel),
        RouteMode.Explicit => Blank(ExplicitModel),
        _ => null,
    };

    /// <summary>Form-only problems (prompt, override, output cap). <see cref="ComposerRules.Problem"/> adds the
    /// application/profile checks in front of these.</summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Prompt)) return "Enter a prompt.";
            if (Mode == RouteMode.Catalog && Blank(CatalogModel) is null) return "Choose an eligible model, or let policy choose.";
            if (Mode == RouteMode.Explicit && Blank(ExplicitModel) is null) return "Enter a model id, or let policy choose.";
            if (MaxTokens is <= 0) return "Max output tokens must be a positive number (or empty for the application's bound).";
            return null;
        }
    }

    /// <summary>Clears the request inputs. Keeps nothing that came from the server.</summary>
    public void Clear()
    {
        Scenario = null;
        ApplicationId = null;
        ProfileId = null;
        Prompt = "";
        Mode = RouteMode.Policy;
        CatalogModel = null;
        ExplicitModel = null;
        Streaming = true;
        MaxTokens = DefaultMaxTokens;
        Workflow = DefaultWorkflow;
        UseCase = DefaultUseCase;
        FreshNonce = false;
    }

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>One submitted request, frozen at Run. Immutable: editing the form (or a preset) afterwards cannot
/// relabel the result. <see cref="Prompt"/> is exactly what was sent (including any scenario-D nonce);
/// <see cref="ClientRequestId"/> is unique per Run so the Host refuses a duplicate submission (409) instead of
/// running it twice.</summary>
public sealed record RequestSnapshot(
    int Sequence,
    string ScenarioLabel,
    char? Scenario,
    string? ApplicationId,
    string? ProfileId,
    string Prompt,
    string BasePrompt,
    string? RequestedModel,
    RouteMode Mode,
    bool Streaming,
    int? MaxTokens,
    string? Workflow,
    string? UseCase,
    bool FreshNonce,
    string ClientRequestId,
    DateTimeOffset SubmittedUtc)
{
    public const string PolicyChooses = "Let policy choose";

    public string RoutingLabel => RequestedModel ?? PolicyChooses;

    public string Title => "Request #" + Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + ScenarioLabel;

    /// <summary>A fresh client request id (GUID, "N" format).</summary>
    public static string NewClientRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>Freezes the form. <paramref name="nonce"/> is appended when the form asks for a fresh nonce
    /// (default <see cref="ScenarioPresets.NewNonce"/>); <paramref name="clientRequestId"/> defaults to a new GUID.
    /// Both are injectable for tests.</summary>
    public static RequestSnapshot Freeze(int sequence, PlaygroundForm form, DateTimeOffset nowUtc, string? nonce = null, string? clientRequestId = null)
    {
        var basePrompt = form.Prompt;
        var prompt = form.FreshNonce ? basePrompt + (nonce ?? ScenarioPresets.NewNonce()) : basePrompt;
        var preset = ScenarioPresets.Find(form.Scenario);
        var matches = preset is not null && preset.StillMatches(form);
        return new RequestSnapshot(
            Sequence: sequence,
            ScenarioLabel: matches ? "Scenario " + preset!.Letter + " · " + preset.Title : "Custom",
            Scenario: matches ? preset!.Letter : null,
            ApplicationId: PlaygroundForm.Blank(form.ApplicationId),
            ProfileId: PlaygroundForm.Blank(form.ProfileId),
            Prompt: prompt,
            BasePrompt: basePrompt,
            RequestedModel: form.RequestedModel,
            Mode: form.Mode,
            Streaming: form.Streaming,
            MaxTokens: form.MaxTokens,
            Workflow: PlaygroundForm.Blank(form.Workflow),
            UseCase: PlaygroundForm.Blank(form.UseCase),
            FreshNonce: form.FreshNonce,
            ClientRequestId: clientRequestId ?? NewClientRequestId(),
            SubmittedUtc: nowUtc);
    }

    /// <summary>True while the form still describes this request; false → "Previous request — the form has
    /// changed since".</summary>
    public bool MatchesForm(PlaygroundForm form)
        => string.Equals(BasePrompt, form.Prompt, StringComparison.Ordinal)
           && string.Equals(ApplicationId, PlaygroundForm.Blank(form.ApplicationId), StringComparison.Ordinal)
           && string.Equals(ProfileId, PlaygroundForm.Blank(form.ProfileId), StringComparison.Ordinal)
           && string.Equals(RequestedModel, form.RequestedModel, StringComparison.Ordinal)
           && Streaming == form.Streaming
           && MaxTokens == form.MaxTokens
           && string.Equals(Workflow, PlaygroundForm.Blank(form.Workflow), StringComparison.Ordinal)
           && string.Equals(UseCase, PlaygroundForm.Blank(form.UseCase), StringComparison.Ordinal)
           && FreshNonce == form.FreshNonce;
}

/// <summary>An application as the "Run as" select shows it: disabled ones stay visible with the reason.</summary>
public sealed record ApplicationChoice(ApplicationSummaryDto App, bool Selectable, string? Reason)
{
    public string Label => App.Id + (string.IsNullOrWhiteSpace(App.Team) ? "" : " · team " + App.Team) + (Reason is null ? "" : " — " + Reason);
}

/// <summary>A model-override option. <see cref="Boundary"/> is "Local", "Cloud" or null (not reported).</summary>
public sealed record OverrideOption(string Value, string Label, string? Boundary);

/// <summary>Composer validation and selection rules (pure). The server enforces the same rules (ADR-0012); these
/// exist so the console never offers an action the server is guaranteed to refuse, and says why.</summary>
public static class ComposerRules
{
    public static string? WhyNotSelectable(ApplicationSummaryDto app)
    {
        if (!app.Enabled) return "disabled";
        if (!app.ConsoleTesting) return "console testing not allowed";
        return null;
    }

    public static IReadOnlyList<ApplicationChoice> Applications(IReadOnlyList<ApplicationSummaryDto>? apps)
    {
        if (apps is null) return [];
        return apps
            .OrderBy(a => WhyNotSelectable(a) is null ? 0 : 1)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new ApplicationChoice(a, WhyNotSelectable(a) is null, WhyNotSelectable(a)))
            .ToList();
    }

    public static ApplicationSummaryDto? Find(IReadOnlyList<ApplicationSummaryDto>? apps, string? id)
    {
        if (apps is null || string.IsNullOrWhiteSpace(id)) return null;
        foreach (var a in apps)
            if (string.Equals(a.Id, id, StringComparison.Ordinal)) return a;
        return null;
    }

    /// <summary>Profiles the application may use — the ONLY ones the profile select offers.</summary>
    public static IReadOnlyList<string> GrantedProfiles(ApplicationSummaryDto? app)
        => app is null ? [] : app.GrantedProfiles.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();

    public static bool IsGranted(ApplicationSummaryDto? app, string? profileId)
        => app is not null && !string.IsNullOrWhiteSpace(profileId) && GrantedProfiles(app).Contains(profileId, StringComparer.Ordinal);

    /// <summary>The application's default profile when it is granted, else its first granted profile, else null
    /// (the server then applies its own default).</summary>
    public static string? DefaultProfile(ApplicationSummaryDto app)
    {
        if (IsGranted(app, app.DefaultProfile)) return app.DefaultProfile;
        var granted = GrantedProfiles(app);
        return granted.Count > 0 ? granted[0] : null;
    }

    /// <summary>Selects an application. Non-selectable or unknown ids are refused (returns false, form unchanged).
    /// The profile resets to the app's default unless the current one is granted to the new app as well.</summary>
    public static bool SelectApplication(PlaygroundForm form, IReadOnlyList<ApplicationSummaryDto>? apps, string? id)
    {
        var app = Find(apps, id);
        if (app is null || WhyNotSelectable(app) is not null) return false;
        form.ApplicationId = app.Id;
        if (!IsGranted(app, form.ProfileId)) form.ProfileId = DefaultProfile(app);
        return true;
    }

    /// <summary>Selects a profile; only a profile granted to the selected application is accepted.</summary>
    public static bool SelectProfile(PlaygroundForm form, IReadOnlyList<ApplicationSummaryDto>? apps, string? profileId)
    {
        var app = Find(apps, form.ApplicationId);
        if (!IsGranted(app, profileId)) return false;
        form.ProfileId = profileId;
        return true;
    }

    /// <summary>Why Run is not possible right now, or null. Application/profile problems come first.</summary>
    public static string? Problem(PlaygroundForm form, IReadOnlyList<ApplicationSummaryDto>? apps)
    {
        if (apps is null) return "Applications are not loaded yet.";
        if (!apps.Any(a => WhyNotSelectable(a) is null)) return "No application is enabled for console testing.";
        if (string.IsNullOrWhiteSpace(form.ApplicationId)) return "Choose an application to run as.";
        var app = Find(apps, form.ApplicationId);
        if (app is null) return "The selected application is no longer configured — choose another.";
        if (WhyNotSelectable(app) is { } why) return app.Id + " cannot run console requests: " + why + ".";
        if (!string.IsNullOrWhiteSpace(form.ProfileId) && !IsGranted(app, form.ProfileId))
            return "Profile " + form.ProfileId + " is not granted to " + app.Id + ".";
        return form.Problem;
    }

    public static WorkloadProfileDto? Profile(IReadOnlyList<WorkloadProfileDto>? profiles, string? id)
    {
        if (profiles is null || string.IsNullOrWhiteSpace(id)) return null;
        foreach (var p in profiles)
            if (string.Equals(p.Id, id, StringComparison.Ordinal)) return p;
        return null;
    }

    /// <summary>"Public tickets · baseline Public" — or just the id when the configuration is not readable.</summary>
    public static string ProfileLabel(IReadOnlyList<WorkloadProfileDto>? profiles, string id)
    {
        var p = Profile(profiles, id);
        if (p is null) return id;
        var name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name;
        return string.IsNullOrWhiteSpace(p.Baseline) ? name : name + " · baseline " + p.Baseline;
    }

    /// <summary>Targets an override may name for this profile: enabled, unpaused policies covering the profile's
    /// baseline (and naming the profile, or no profile), unpaused targets on enabled connections, cloud targets
    /// excluded in local-only mode. Detection may still escalate the classification at run time, so this is the
    /// best case — policy decides. Without a readable configuration, falls back to the legacy catalog.</summary>
    public static IReadOnlyList<OverrideOption> EligibleOverrides(CovenantConfigDto? config, string? profileId, CatalogResponse? legacyCatalog)
    {
        var result = new List<OverrideOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (config is not null)
        {
            var baseline = Profile(config.Profiles, profileId)?.Baseline;
            bool localOnly = ShellStatus.Mode(config.Mode).Tone == "local";
            foreach (var policy in config.Policies)
            {
                if (!policy.Enabled || policy.Paused) continue;
                if (policy.Profiles.Count > 0 && (profileId is null || !policy.Profiles.Contains(profileId, StringComparer.Ordinal))) continue;
                if (baseline is not null && !policy.Classifications.Contains(baseline, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var t in policy.Targets)
                {
                    if (t.Paused) continue;
                    var conn = config.Connections.FirstOrDefault(c => string.Equals(c.Id, t.ConnectionId, StringComparison.Ordinal));
                    if (conn is null || !conn.Enabled) continue;
                    var boundary = BoundaryLabel(conn.Boundary);
                    if (localOnly && boundary == "Cloud") continue;
                    var value = t.ConnectionId + "/" + t.ModelId;
                    if (!seen.Add(value)) continue;
                    var model = conn.Models.FirstOrDefault(m => string.Equals(m.ModelId, t.ModelId, StringComparison.Ordinal));
                    var name = string.IsNullOrWhiteSpace(model?.DisplayName) ? t.ModelId : model!.DisplayName!;
                    var connName = string.IsNullOrWhiteSpace(conn.DisplayName) ? conn.Id : conn.DisplayName;
                    result.Add(new OverrideOption(value, name + " · " + connName, boundary));
                }
            }
            return result;
        }
        if (legacyCatalog is not null)
            foreach (var m in legacyCatalog.Models)
                if (m.Classifications.Count > 0 && seen.Add(m.ModelId))
                    result.Add(new OverrideOption(m.ModelId, m.ModelId + " · " + m.Adapter,
                        string.Equals(m.Adapter, "local", StringComparison.OrdinalIgnoreCase) ? "Local" : "Cloud"));
        return result;
    }

    /// <summary>Normalizes a configured boundary to "Local" / "Cloud" / null.</summary>
    public static string? BoundaryLabel(string? boundary) => (boundary ?? "").Trim().ToLowerInvariant() switch
    {
        "local" => "Local",
        "cloud" => "Cloud",
        _ => null,
    };
}
