using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>A named workload the demo uses. Routing is by this profile (+ classification), never by prompt keywords.</summary>
public sealed record WorkloadPreset(string ProfileId, string Name, string Baseline, string Purpose);

/// <summary>What "route this workload to this model" would change in the DRAFT — or why it is refused (draft untouched).</summary>
public sealed record WorkloadRoutePlan(string? Problem, IReadOnlyList<string> Changes)
{
    public bool Ok => Problem is null;
}

/// <summary>Explicit, ADDITIVE demo setup (docs/CONSOLE-DEMO-CLARITY-TASKS.md §3/§4/§8): routes one named workload profile
/// to one configured model by adding (1) the profile if missing, (2) a policy scoped to exactly that profile and its
/// baseline, and (3) a grant of that profile to the console-testable applications. Existing policies, profiles,
/// connections and prices are never modified or removed; the operator previews the impact and saves a new version.
/// Refused: a cloud model for a non-Public workload (no unreviewed cloud fallback for Internal/sensitive work), an
/// unpriced cloud model (no invented pricing), a disabled connection, or a workload already routed by its own policy.</summary>
public static class WorkloadRouteSetup
{
    public static readonly IReadOnlyList<WorkloadPreset> Presets =
    [
        new("public-writing", "Public writing", "Public", "Public content drafted with an approved model."),
        new("public-research", "Public research", "Public", "Public research; may use governed web search where allowed."),
        new("internal-coding", "Internal coding", "Internal", "Internal code assistance on a local model."),
        new("internal-research", "Internal research", "Internal", "Internal analysis on a stronger local model."),
    ];

    /// <summary>The presets a model may serve: a cloud model only Public workloads.</summary>
    public static IReadOnlyList<WorkloadPreset> PresetsFor(ConnectionDto connection)
        => ComposerRules.BoundaryLabel(connection.Boundary) == "Cloud" ? Presets.Where(p => p.Baseline == "Public").ToList() : Presets;

    public static WorkloadRoutePlan Plan(CovenantConfigDto cfg, WorkloadPreset preset, string connectionId, string modelId)
        => Build(cfg, preset, connectionId, modelId, apply: false);

    /// <summary>Applies the plan to <paramref name="draft"/> when it is allowed; returns the same plan either way.</summary>
    public static WorkloadRoutePlan ApplyTo(CovenantConfigDto draft, WorkloadPreset preset, string connectionId, string modelId)
        => Build(draft, preset, connectionId, modelId, apply: true);

    private static WorkloadRoutePlan Build(CovenantConfigDto cfg, WorkloadPreset preset, string connectionId, string modelId, bool apply)
    {
        var conn = cfg.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn is null) return Refuse("Connection '" + connectionId + "' is not configured.");
        if (!conn.Enabled) return Refuse("Connection '" + connectionId + "' is disabled — enable it first.");
        var model = conn.Models.FirstOrDefault(m => m.ModelId == modelId);
        if (model is null) return Refuse("Model '" + modelId + "' is not permitted on '" + connectionId + "' — add it to the connection first.");
        bool cloud = ComposerRules.BoundaryLabel(conn.Boundary) == "Cloud";
        if (cloud && preset.Baseline != "Public")
            return Refuse(preset.Name + " is " + preset.Baseline + " work: it never routes to a cloud model.");
        if (cloud && PolicyRoutes.IsLocalOnly(cfg.Mode))
            return Refuse("The configuration is Local-only: cloud models cannot run. Switch to Hybrid on the Policies page first.");
        if (cloud && (model.Price?.InputPer1M is null || model.Price?.OutputPer1M is null))
            return Refuse("Model '" + modelId + "' has no input/output price. Enter the provider's current prices on the connection first — an unknown price is never $0.");

        var profile = cfg.Profiles.FirstOrDefault(p => p.Id == preset.ProfileId);
        if (profile is not null && !string.Equals(profile.Baseline, preset.Baseline, StringComparison.Ordinal))
            return Refuse("Profile '" + preset.ProfileId + "' exists with baseline " + profile.Baseline + ", not " + preset.Baseline + ". Use another profile or edit it under Policies.");
        var existing = cfg.Policies.FirstOrDefault(p => p.Enabled && p.Profiles.Contains(preset.ProfileId, StringComparer.Ordinal)
                                                         && p.Classifications.Contains(preset.Baseline, StringComparer.OrdinalIgnoreCase));
        if (existing is not null)
            return Refuse("'" + preset.Name + "' is already routed by policy '" + existing.Id + "'. Edit that policy under Policies instead of adding a second rule (two rules would be ambiguous and refused).");

        var changes = new List<string>();
        if (profile is null) changes.Add("Add workload profile '" + preset.ProfileId + "' (" + preset.Name + ", baseline " + preset.Baseline + ").");
        var policyId = PolicyEditing.UniqueId(preset.ProfileId, cfg);
        changes.Add("Add policy '" + policyId + "': " + preset.Baseline + " requests of profile '" + preset.ProfileId + "' → " + connectionId + "/" + modelId
                    + (cloud ? " (cloud, priced)" : " (local)") + ". No fallback; other profiles and PII/PHI keep their existing rules.");
        var grantTo = cfg.Applications.Where(a => a.Enabled && a.ConsoleTesting && !a.GrantedProfiles.Contains(preset.ProfileId, StringComparer.Ordinal)).ToList();
        foreach (var a in grantTo) changes.Add("Grant '" + preset.ProfileId + "' to application '" + a.Id + "' (its other grants and default are unchanged).");
        if (preset.ProfileId == "public-research")
            changes.Add("Web search stays as configured — allow it on this profile under Policies → Applications & workload profiles if wanted.");

        if (apply)
        {
            if (profile is null)
                cfg.Profiles.Add(new WorkloadProfileDto { Id = preset.ProfileId, Name = preset.Name, Baseline = preset.Baseline, Description = preset.Purpose });
            cfg.Policies.Add(new PolicyDto
            {
                Id = policyId, Name = preset.Name + " → " + modelId, Enabled = true,
                Classifications = [preset.Baseline], Profiles = [preset.ProfileId], Strategy = "fixed",
                Retry = new RetrySettingsDto { MaxAttempts = 1, DeadlineMs = 120_000, On = [] },
                Targets = [new PolicyTargetDto { ConnectionId = connectionId, ModelId = modelId }],
                Explanation = preset.Purpose + " Routed by workload profile, not by the prompt's content.",
            });
            foreach (var a in grantTo) a.GrantedProfiles.Add(preset.ProfileId);
        }
        return new WorkloadRoutePlan(null, changes);
    }

    private static WorkloadRoutePlan Refuse(string why) => new(why, []);
}
