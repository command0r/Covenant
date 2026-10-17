using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>The "Create workload profile" form (review round 3). Nothing here is applied: <see cref="ProfileEditing.Add"/>
/// puts the profile into the configuration DRAFT; the Host validates it on Preview impact and activates it on Save &amp; apply.</summary>
public sealed class NewProfileForm
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Trusted baseline, wire spelling: Public | Internal | Pii | Phi.</summary>
    public string Baseline { get; set; } = "Internal";
    public string Description { get; set; } = "";
}

/// <summary>The "Add application" form. An application is the CALLING SYSTEM: it owns a team (who pays), its budgets apply,
/// and it is granted the workload profiles it may use. The console runs requests on its behalf (ADR-0012). Creating it
/// here adds it to the configuration DRAFT only; a /v1 client additionally needs a startup key (Auth:Keys) bound to it.</summary>
public sealed class NewApplicationForm
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Team { get; set; } = "";
    public string Description { get; set; } = "";
    public bool ConsoleTesting { get; set; } = true;
    public List<string> GrantedProfiles { get; set; } = [];
    public string? DefaultProfile { get; set; }
}

/// <summary>One profile row of the editor: who is granted it, whose default it is, which policies are scoped to it.</summary>
public sealed record ProfileUsage(string ProfileId, IReadOnlyList<string> GrantedTo, IReadOnlyList<string> DefaultOf, IReadOnlyList<string> ScopedPolicies);

/// <summary>Draft-only edits of workload profiles and application grants (review round 3). Mirrors the Host's
/// rules (src/Covenant.Governance/ConfigValidation.cs — profiles/applications) so the draft the console builds passes
/// them; the Host's validation stays authoritative. The baseline is a SERVER-SIDE trusted classification: detection can
/// only escalate it, and the console never offers a client-side classification control. Applications are edited, never
/// created or deleted here: their keys are startup secrets (Auth:Keys). Pure.</summary>
public static class ProfileEditing
{
    /// <summary>The Host's implicit profile (PolicyEngineV2.LegacyInternalProfile, baseline Internal): always exists,
    /// used when an application has no default profile. Not declared in <c>profiles</c>; never created or removed here.</summary>
    public const string LegacyInternal = "legacy-internal";

    /// <summary>Baselines in display order (contract spellings).</summary>
    public static IReadOnlyList<string> Baselines => PolicyEditing.AllClassifications;

    public const string BaselineExplained =
        "The baseline is a trusted classification held in server configuration. The detector can only escalate it (e.g. a US SSN pattern raises Public to PII), never lower it.";

    public const string PlaygroundExplained =
        "The Playground can only run with a profile granted to the chosen application; there is no client-side classification control.";

    public const string ApplicationsExplained =
        "An application is the calling system (for example a support portal or a coding assistant). It belongs to a team — that team and its budgets pay for its requests — and it is granted the workload profiles it may use. The console runs requests on its behalf; nothing here bypasses its grants.";

    public const string ApplicationKeysExplained =
        "Adding an application here lets the console run as it. A real client calling /v1 also needs an application key, which is a startup secret (Auth:Keys with Application set to this id) — keys are never created or shown in the console.";

    public const int MaxDisplayNameChars = 100;
    public const int MaxDescriptionChars = 300;

    /// <summary>"Support portal" — or the id when no display name is configured.</summary>
    public static string ApplicationName(string id, string? displayName) => string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();

    public static IReadOnlyList<string> Problems(NewApplicationForm f, CovenantConfigDto cfg)
    {
        var p = new List<string>();
        var id = f.Id.Trim();
        if (!ConnectionRules.IsValidId(id)) p.Add("Application id must be 1–64 characters of letters, digits, '.', '_' or '-'.");
        else if (FindApplication(cfg, id) is not null) p.Add("An application with id '" + id + "' already exists.");
        if (string.IsNullOrWhiteSpace(f.Team)) p.Add("Enter the owning team (it pays for the application's requests).");
        else if (!ConnectionRules.IsValidId(f.Team.Trim())) p.Add("Team must be 1–64 characters of letters, digits, '.', '_' or '-'.");
        if (f.DisplayName.Trim().Length > MaxDisplayNameChars) p.Add("Display name must be at most 100 characters.");
        if (f.Description.Trim().Length > MaxDescriptionChars) p.Add("Description must be at most 300 characters.");
        foreach (var g in f.GrantedProfiles.Where(g => !Exists(cfg, g))) p.Add("Profile '" + g + "' does not exist.");
        if (f.DefaultProfile is { Length: > 0 } d && !f.GrantedProfiles.Contains(d, StringComparer.Ordinal))
            p.Add("The default profile must also be granted.");
        return p;
    }

    /// <summary>Adds the application to <paramref name="draft"/> (enabled). Returns the problems (draft untouched) or empty.</summary>
    public static IReadOnlyList<string> AddApplication(CovenantConfigDto draft, NewApplicationForm f)
    {
        var problems = Problems(f, draft);
        if (problems.Count > 0) return problems;
        draft.Applications.Add(new ApplicationDto
        {
            Id = f.Id.Trim(),
            Team = f.Team.Trim(),
            Enabled = true,
            ConsoleTesting = f.ConsoleTesting,
            MaxOutputTokens = 1024, // Match the Host default; serializing an unset nullable DTO otherwise yields an invalid zero bound.
            GrantedProfiles = f.GrantedProfiles.Distinct(StringComparer.Ordinal).ToList(),
            DefaultProfile = string.IsNullOrWhiteSpace(f.DefaultProfile) ? null : f.DefaultProfile,
            DisplayName = string.IsNullOrWhiteSpace(f.DisplayName) ? null : f.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim(),
        });
        return problems;
    }

    public static string? SetDisplayName(ApplicationDto app, string? name)
    {
        var v = (name ?? "").Trim();
        if (v.Length > MaxDisplayNameChars) return "Display name must be at most 100 characters.";
        app.DisplayName = v.Length == 0 ? null : v;
        return null;
    }

    public static string? SetApplicationDescription(ApplicationDto app, string? description)
    {
        var v = (description ?? "").Trim();
        if (v.Length > MaxDescriptionChars) return "Description must be at most 300 characters.";
        app.Description = v.Length == 0 ? null : v;
        return null;
    }

    public static bool IsDeclared(CovenantConfigDto cfg, string? id)
        => id is not null && cfg.Profiles.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Declared, or the Host's implicit legacy-internal profile.</summary>
    public static bool Exists(CovenantConfigDto cfg, string? id)
        => string.Equals(id, LegacyInternal, StringComparison.Ordinal) || IsDeclared(cfg, id);

    public static WorkloadProfileDto? Find(CovenantConfigDto cfg, string? id)
        => id is null ? null : cfg.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public static ApplicationDto? FindApplication(CovenantConfigDto cfg, string? id)
        => id is null ? null : cfg.Applications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    public static bool IsBaseline(string? b) => b is not null && Baselines.Contains(b, StringComparer.Ordinal);

    /// <summary>A free profile id derived from <paramref name="seed"/>.</summary>
    public static string UniqueId(string seed, CovenantConfigDto cfg)
        => ConnectionRules.UniqueId(seed, cfg.Profiles.Select(p => p.Id).Append(LegacyInternal));

    /// <summary>"Public tickets (baseline Public)"; the implicit profile is labelled as such.</summary>
    public static string Label(CovenantConfigDto cfg, string id)
    {
        if (Find(cfg, id) is { } p)
            return (string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name) + " (baseline " + PolicyText.ClassificationLabel(p.Baseline) + ")";
        return string.Equals(id, LegacyInternal, StringComparison.Ordinal)
            ? LegacyInternal + " (built-in, baseline Internal)"
            : id + " (not configured)";
    }

    public static ProfileUsage Usage(CovenantConfigDto cfg, string profileId) => new(
        profileId,
        cfg.Applications.Where(a => a.GrantedProfiles.Contains(profileId, StringComparer.Ordinal)).Select(a => a.Id).ToList(),
        cfg.Applications.Where(a => string.Equals(a.DefaultProfile, profileId, StringComparison.Ordinal)).Select(a => a.Id).ToList(),
        cfg.Policies.Where(p => p.Profiles.Contains(profileId, StringComparer.Ordinal)).Select(p => p.Id).ToList());

    // ---- profiles ----

    /// <summary>Every reason the create form cannot be added to the draft (empty = ready).</summary>
    public static IReadOnlyList<string> Problems(NewProfileForm f, CovenantConfigDto cfg)
    {
        var p = new List<string>();
        var id = f.Id.Trim();
        if (!ConnectionRules.IsValidId(id)) p.Add("Profile id must be 1–64 characters of letters, digits, '.', '_' or '-'.");
        else if (string.Equals(id, LegacyInternal, StringComparison.Ordinal)) p.Add("'" + LegacyInternal + "' is the built-in profile; choose another id.");
        else if (IsDeclared(cfg, id)) p.Add("A profile with id '" + id + "' already exists.");
        if (string.IsNullOrWhiteSpace(f.Name)) p.Add("Enter a profile name.");
        if (!IsBaseline(f.Baseline)) p.Add("Choose a baseline: Public, Internal, PII or PHI.");
        return p;
    }

    public static WorkloadProfileDto Build(NewProfileForm f) => new()
    {
        Id = f.Id.Trim(),
        Name = f.Name.Trim(),
        Baseline = f.Baseline,
        Description = string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim(),
    };

    /// <summary>Adds the form's profile to <paramref name="draft"/>. Returns the problems (draft untouched) or an empty list.
    /// A new profile is granted to nobody: grant it to an application below.</summary>
    public static IReadOnlyList<string> Add(CovenantConfigDto draft, NewProfileForm f)
    {
        var problems = Problems(f, draft);
        if (problems.Count == 0) draft.Profiles.Add(Build(f));
        return problems;
    }

    /// <summary>Renames a profile. Refused (returns why, profile untouched) for a blank name.</summary>
    public static string? SetName(WorkloadProfileDto profile, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A profile needs a name.";
        profile.Name = name.Trim();
        return null;
    }

    public static void SetDescription(WorkloadProfileDto profile, string? description)
        => profile.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>Changes the trusted baseline. Refused for anything but Public | Internal | Pii | Phi.</summary>
    public static string? SetBaseline(WorkloadProfileDto profile, string? baseline)
    {
        if (!IsBaseline(baseline)) return "Choose a baseline: Public, Internal, PII or PHI.";
        if (profile.WebSearchAllowed && !string.Equals(baseline, "Public", StringComparison.Ordinal))
            return "Turn off web search for this profile first — only a Public-baseline profile may search (ADR-0015).";
        profile.Baseline = baseline;
        return null;
    }

    /// <summary>ADR-0015: web search permission. Only a Public-baseline profile may carry it (the Host validates too);
    /// turning it off is always allowed.</summary>
    public static string? SetWebSearch(WorkloadProfileDto profile, bool allowed)
    {
        if (allowed && !string.Equals(profile.Baseline, "Public", StringComparison.Ordinal))
            return "Only a Public-baseline profile may use web search — Internal, PII and PHI requests are never searched.";
        profile.WebSearchAllowed = allowed;
        return null;
    }

    public const string WebSearchExplained =
        "Web search sends a bounded query to Tavily for Public requests only, and only when web search is enabled in Settings. The search is charged to your budgets.";

    /// <summary>Why the profile cannot be removed from the draft (empty = removable). Removal is refused while it is an
    /// application's default (choose another default first), and while a policy is scoped to it ALONE — dropping the
    /// only profile from a policy would widen it to every profile, so re-scope or remove that policy first.</summary>
    public static IReadOnlyList<string> RemovalBlockers(CovenantConfigDto cfg, string profileId)
    {
        var b = new List<string>();
        if (string.Equals(profileId, LegacyInternal, StringComparison.Ordinal))
        {
            b.Add("The built-in profile cannot be removed.");
            return b;
        }
        if (!IsDeclared(cfg, profileId))
        {
            b.Add("Profile '" + profileId + "' is not in the draft.");
            return b;
        }
        foreach (var app in cfg.Applications.Where(a => string.Equals(a.DefaultProfile, profileId, StringComparison.Ordinal)))
            b.Add("It is the default profile of application '" + app.Id + "': choose another default for it first (requests without a profile would otherwise have no trusted baseline).");
        foreach (var pol in cfg.Policies.Where(p => p.Profiles.Count == 1 && string.Equals(p.Profiles[0], profileId, StringComparison.Ordinal)))
            b.Add("Policy '" + pol.Id + "' applies only to this profile: removing the profile would widen that policy to every profile. Re-scope or remove the policy first.");
        return b;
    }

    /// <summary>What removing the profile changes in the draft (shown before the Host's impact preview).</summary>
    public static IReadOnlyList<string> RemovalImpact(CovenantConfigDto cfg, string profileId)
    {
        var lines = new List<string>();
        var u = Usage(cfg, profileId);
        foreach (var app in u.GrantedTo)
            lines.Add("Application '" + app + "' loses the grant: it can no longer request this profile (the console Playground no longer offers it).");
        foreach (var pol in cfg.Policies.Where(p => p.Profiles.Count > 1 && p.Profiles.Contains(profileId, StringComparer.Ordinal)))
            lines.Add("Policy '" + pol.Id + "' stops listing this profile; it still applies to " + string.Join(", ", pol.Profiles.Where(x => x != profileId)) + ".");
        if (lines.Count == 0) lines.Add("No application is granted this profile and no policy names it: removing it changes no route.");
        return lines;
    }

    /// <summary>Removes the profile, its grants and its entries in multi-profile policies from the draft. Returns the
    /// first blocker (draft untouched) or null.</summary>
    public static string? Remove(CovenantConfigDto draft, string profileId)
    {
        if (RemovalBlockers(draft, profileId) is { Count: > 0 } blockers) return blockers[0];
        draft.Profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        foreach (var app in draft.Applications) app.GrantedProfiles.RemoveAll(g => string.Equals(g, profileId, StringComparison.Ordinal));
        foreach (var pol in draft.Policies) pol.Profiles.RemoveAll(g => string.Equals(g, profileId, StringComparison.Ordinal));
        return null;
    }

    // ---- applications (existing only) ----

    /// <summary>Profiles an application's grant list can show: every declared profile, plus any id it is already
    /// granted that is not declared (the built-in legacy-internal, or a dangling id the Host will reject).</summary>
    public static IReadOnlyList<string> GrantOptions(CovenantConfigDto cfg, ApplicationDto app)
        => cfg.Profiles.Select(p => p.Id).Concat(app.GrantedProfiles).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Grants or revokes a profile. Revoking the application's default is refused (choose another default
    /// first); granting an unknown profile is refused.</summary>
    public static string? SetGrant(ApplicationDto app, string profileId, bool granted, CovenantConfigDto cfg)
    {
        bool has = app.GrantedProfiles.Contains(profileId, StringComparer.Ordinal);
        if (granted)
        {
            if (has) return null;
            if (!Exists(cfg, profileId)) return "Profile '" + profileId + "' does not exist.";
            app.GrantedProfiles.Add(profileId);
            return null;
        }
        if (!has) return null;
        if (string.Equals(app.DefaultProfile, profileId, StringComparison.Ordinal))
            return "'" + profileId + "' is the default profile of '" + app.Id + "': choose another default before revoking it.";
        app.GrantedProfiles.RemoveAll(g => string.Equals(g, profileId, StringComparison.Ordinal));
        return null;
    }

    /// <summary>Sets the default profile (null/empty = none: requests without a profile use the built-in
    /// legacy-internal, baseline Internal). The default must already be granted.</summary>
    public static string? SetDefault(ApplicationDto app, string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            app.DefaultProfile = null;
            return null;
        }
        if (!app.GrantedProfiles.Contains(profileId, StringComparer.Ordinal))
            return "Grant '" + profileId + "' to '" + app.Id + "' before making it the default.";
        app.DefaultProfile = profileId;
        return null;
    }

    public static string? SetTeam(ApplicationDto app, string? team)
    {
        if (string.IsNullOrWhiteSpace(team)) return "Team is required (attribution and team budgets use it).";
        app.Team = team.Trim();
        return null;
    }

    /// <summary>The enforced output bound (≥ 1; sent to the provider and used for the budget reservation).</summary>
    public static string? SetMaxOutputTokens(ApplicationDto app, int? value)
    {
        if (value is not { } v || v < 1) return "Max output tokens must be a whole number ≥ 1.";
        app.MaxOutputTokens = v;
        return null;
    }

    /// <summary>Everything the Host would reject about an application's profile settings (empty = consistent).</summary>
    public static IReadOnlyList<string> ApplicationProblems(ApplicationDto app, CovenantConfigDto cfg)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(app.Team)) p.Add("Team is required.");
        foreach (var g in app.GrantedProfiles.Where(g => !Exists(cfg, g)))
            p.Add("Granted profile '" + g + "' does not exist.");
        if (app.DefaultProfile is { } d)
        {
            if (!Exists(cfg, d)) p.Add("Default profile '" + d + "' does not exist.");
            else if (!app.GrantedProfiles.Contains(d, StringComparer.Ordinal)) p.Add("Default profile '" + d + "' is not granted.");
        }
        if (app.MaxOutputTokens is < 1) p.Add("Max output tokens must be ≥ 1.");
        return p;
    }

    /// <summary>What a request WITHOUT a profile resolves to for this application (Host ProfileResolution).</summary>
    public static string DefaultText(CovenantConfigDto cfg, ApplicationDto app)
        => app.DefaultProfile is { } d ? Label(cfg, d) : "None — requests without a profile use " + Label(cfg, LegacyInternal);
}
