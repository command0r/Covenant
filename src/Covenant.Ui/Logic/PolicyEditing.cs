using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>The "Create policy" form (spec §5 Policies). Nothing here is applied: <see cref="PolicyEditing.Add"/> puts the
/// built policy into the configuration DRAFT; the Host validates it on Preview impact and activates it on Save &amp; apply.</summary>
public sealed class NewPolicyForm
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Wire spellings: Public | Internal | Pii | Phi.</summary>
    public List<string> Classifications { get; set; } = [];
    /// <summary>Optional workload-profile applicability (empty = every profile).</summary>
    public List<string> Profiles { get; set; } = [];
    /// <summary>Ordered "connection/model" keys: primary first, then approved fallbacks.</summary>
    public List<string> Targets { get; set; } = [];
    /// <summary>fixed | length-heuristic</summary>
    public string Strategy { get; set; } = "fixed";
}

/// <summary>Result of planning a split: the two draft policies, or why it cannot be done.</summary>
public sealed record PolicySplitPlan(string? Problem, PolicyDto? PublicPolicy, PolicyDto? RestrictedPolicy, IReadOnlyList<string> Notes);

/// <summary>Draft-only policy edits (create, remove with impact, applicability, split). PII/PHI never get a cloud target:
/// every helper refuses to produce one, and the Host rejects one anyway (<c>sensitive_cloud_target</c>). Pure.</summary>
public static class PolicyEditing
{
    /// <summary>Contract spellings of DataClassification, in display order.</summary>
    public static readonly IReadOnlyList<string> AllClassifications = ["Public", "Internal", "Pii", "Phi"];

    public static bool IsValidId(string? id) => ConnectionRules.IsValidId(id);

    /// <summary>A free policy id derived from <paramref name="seed"/>.</summary>
    public static string UniqueId(string seed, CovenantConfigDto cfg)
        => ConnectionRules.UniqueId(seed, cfg.Policies.Select(p => p.Id));

    /// <summary>Targets that may be chosen for a policy covering <paramref name="classifications"/>: enabled connections'
    /// permitted models; cloud models carry a disabled reason for PII/PHI or in Local-only mode.</summary>
    public static IReadOnlyList<TargetOption> TargetOptions(IEnumerable<string> classifications, CovenantConfigDto cfg)
        => PolicyRoutes.Options(new PolicyDto { Classifications = classifications.ToList() }, cfg);

    /// <summary>Every reason the form cannot be added to the draft (empty = ready).</summary>
    public static IReadOnlyList<string> Problems(NewPolicyForm f, CovenantConfigDto cfg)
    {
        var p = new List<string>();
        if (!IsValidId(f.Id)) p.Add("Policy id must be 1–64 characters of letters, digits, '.', '_' or '-'.");
        else if (cfg.Policies.Any(x => string.Equals(x.Id, f.Id, StringComparison.Ordinal))) p.Add("A policy with id '" + f.Id + "' already exists.");
        if (string.IsNullOrWhiteSpace(f.Name)) p.Add("Enter a policy name.");
        if (f.Classifications.Count == 0) p.Add("Choose at least one classification.");
        foreach (var unknown in f.Classifications.Where(c => !AllClassifications.Contains(c)))
            p.Add("Unknown classification '" + unknown + "'.");
        foreach (var missing in f.Profiles.Where(id => !cfg.Profiles.Any(x => string.Equals(x.Id, id, StringComparison.Ordinal))))
            p.Add("Workload profile '" + missing + "' does not exist.");
        if (f.Strategy is not ("fixed" or "length-heuristic")) p.Add("Choose a routing strategy.");
        if (f.Targets.Count == 0) p.Add("Choose at least one approved target (the primary).");
        var options = TargetOptions(f.Classifications, cfg);
        foreach (var t in f.Targets)
        {
            var o = options.FirstOrDefault(x => x.Value == t);
            if (o is null) p.Add(t + " is not an enabled, permitted model.");
            else if (o.DisabledReason is { } why) p.Add(t + ": " + why);
        }
        if (f.Targets.Distinct(StringComparer.Ordinal).Count() != f.Targets.Count) p.Add("A target is listed twice.");
        return p;
    }

    public static PolicyDto Build(NewPolicyForm f) => new()
    {
        Id = f.Id.Trim(),
        Name = f.Name.Trim(),
        Enabled = true,
        Classifications = AllClassifications.Where(c => f.Classifications.Contains(c)).ToList(),
        Profiles = f.Profiles.ToList(),
        Targets = f.Targets.Select(ToTarget).ToList(),
        Strategy = f.Strategy,
        ThresholdTokens = f.Strategy == "length-heuristic" ? 400 : null,
    };

    /// <summary>Adds the form's policy to <paramref name="draft"/>. Returns the problems (draft untouched) or an empty list.</summary>
    public static IReadOnlyList<string> Add(CovenantConfigDto draft, NewPolicyForm f)
    {
        var problems = Problems(f, draft);
        if (problems.Count == 0) draft.Policies.Add(Build(f));
        return problems;
    }

    /// <summary>What removing <paramref name="policy"/> changes, judged from the configuration: which classification ×
    /// profile combinations it serves that no other enabled policy covers (those become refused — fail-closed). The
    /// Host's impact preview (Preview impact) is the authority; this is the immediate warning.</summary>
    public static IReadOnlyList<string> RemovalImpact(PolicyDto policy, CovenantConfigDto cfg)
    {
        var lines = new List<string>();
        if (!policy.Enabled)
        {
            lines.Add("The policy is disabled: removing it changes no route.");
            return lines;
        }
        var others = cfg.Policies.Where(p => !ReferenceEquals(p, policy) && p.Enabled).ToList();
        foreach (var c in AllClassifications.Where(c => policy.Classifications.Contains(c)))
        {
            bool covered = others.Any(o => o.Classifications.Contains(c)
                                           && (o.Profiles.Count == 0 || (policy.Profiles.Count > 0 && policy.Profiles.All(o.Profiles.Contains))));
            var scope = PolicyText.ClassificationLabel(c) + (policy.Profiles.Count == 0 ? "" : " · profile " + string.Join(", ", policy.Profiles));
            lines.Add(covered
                ? scope + ": another enabled policy still matches."
                : scope + ": no other policy matches — these requests will be REFUSED (fail-closed).");
        }
        return lines;
    }

    /// <summary>Removes the policy with <paramref name="policyId"/> from the draft. False when not found.</summary>
    public static bool Remove(CovenantConfigDto draft, string policyId)
    {
        var p = draft.Policies.FirstOrDefault(x => string.Equals(x.Id, policyId, StringComparison.Ordinal));
        return p is not null && draft.Policies.Remove(p);
    }

    /// <summary>Changes which classifications a policy covers. Refused (returns why, policy untouched) when the set is empty,
    /// unknown, or would put PII/PHI behind a cloud target.</summary>
    public static string? SetClassifications(PolicyDto policy, IEnumerable<string> classifications, CovenantConfigDto cfg)
    {
        var wanted = classifications.ToHashSet(StringComparer.Ordinal);
        var next = AllClassifications.Where(wanted.Contains).ToList();
        if (next.Count == 0) return "A policy must cover at least one classification.";
        if (next.Any(PolicyText.IsSensitive) && CloudTargets(policy, cfg).FirstOrDefault() is { } cloud)
            return "Remove the cloud target " + cloud + " first: " + PolicyText.SensitiveNeverCloud;
        policy.Classifications = next;
        return null;
    }

    /// <summary>Changes the workload-profile applicability (empty = every profile). Refused for an unknown profile.</summary>
    public static string? SetProfiles(PolicyDto policy, IEnumerable<string> profiles, CovenantConfigDto cfg)
    {
        var next = profiles.Distinct(StringComparer.Ordinal).ToList();
        if (next.FirstOrDefault(pr => !cfg.Profiles.Any(x => string.Equals(x.Id, pr, StringComparison.Ordinal))) is { } missing)
            return "Workload profile '" + missing + "' does not exist.";
        policy.Profiles = next;
        return null;
    }

    /// <summary>Split is offered for a policy that covers Public AND at least one of Internal/PII/PHI.</summary>
    public static bool CanSplit(PolicyDto p)
        => p.Classifications.Contains("Public") && p.Classifications.Any(c => c is "Internal" or "Pii" or "Phi");

    /// <summary>Plans "Split this policy": Public → an approved cloud model with the local model as fallback;
    /// Internal/PII/PHI → local only. Cloud targets come from the policy itself, else the first enabled, priced cloud
    /// model; local targets from the policy, else the first enabled local model. The restricted policy NEVER receives a
    /// cloud target. In Local-only mode Public stays local (cloud cannot execute there).</summary>
    public static PolicySplitPlan PlanSplit(PolicyDto p, CovenantConfigDto cfg)
    {
        var notes = new List<string>();
        if (!CanSplit(p))
            return new PolicySplitPlan("Split needs a policy that covers Public and at least one of Internal, PII or PHI.", null, null, notes);

        var local = LocalTargets(p, cfg).ToList();
        if (local.Count == 0 && FirstModel(cfg, "Local", requirePrice: false) is { } anyLocal)
        {
            local.Add(anyLocal);
            notes.Add("The policy had no local target; " + anyLocal + " (first enabled local model) serves Internal/PII/PHI.");
        }
        if (local.Count == 0)
            return new PolicySplitPlan("Split needs an enabled Local connection with a permitted model (add one under Models & connections).", null, null, notes);

        var cloud = new List<string>();
        if (PolicyRoutes.IsLocalOnly(cfg.Mode))
            notes.Add("Local-only mode: cloud destinations cannot execute, so Public stays on the local model.");
        else
        {
            cloud.AddRange(CloudTargets(p, cfg));
            if (cloud.Count == 0 && FirstModel(cfg, "Cloud", requirePrice: true) is { } anyCloud)
            {
                cloud.Add(anyCloud);
                notes.Add("The policy had no cloud target; " + anyCloud + " (first enabled, priced cloud model) is proposed for Public.");
            }
            if (cloud.Count == 0) notes.Add("No enabled, priced cloud model is configured: Public stays on the local model.");
        }

        var restrictedClasses = AllClassifications.Where(c => c != "Public" && p.Classifications.Contains(c)).ToList();
        var publicPolicy = new PolicyDto
        {
            Id = UniqueIdExcluding(p.Id + "-public", cfg, p.Id),
            Name = "Public: " + (cloud.Count > 0 ? "approved cloud model, local fallback" : "local model"),
            Enabled = p.Enabled,
            Paused = p.Paused,
            Classifications = ["Public"],
            Profiles = p.Profiles.ToList(),
            Targets = cloud.Concat(local).Select(ToTarget).ToList(),
            Strategy = "fixed",
            Retry = CopyRetry(p.Retry),
            Explanation = "Split from policy '" + p.Id + "': Public content may use the approved cloud model; the local model is the approved fallback.",
        };
        var restricted = new PolicyDto
        {
            Id = UniqueIdExcluding(p.Id + "-restricted", cfg, p.Id, publicPolicy.Id),
            Name = string.Join("/", restrictedClasses.Select(PolicyText.ClassificationLabel)) + ": local only",
            Enabled = p.Enabled,
            Paused = p.Paused,
            Classifications = restrictedClasses,
            Profiles = p.Profiles.ToList(),
            Targets = local.Select(ToTarget).ToList(),   // never a cloud target
            Strategy = "fixed",
            Retry = CopyRetry(p.Retry),
            Explanation = "Split from policy '" + p.Id + "'. " + PolicyText.SensitiveNeverCloud,
        };
        return new PolicySplitPlan(null, publicPolicy, restricted, notes);
    }

    /// <summary>Replaces the policy with the two planned drafts (same position). Returns the problem (draft untouched) or null.</summary>
    public static string? ApplySplit(CovenantConfigDto draft, string policyId)
    {
        var p = draft.Policies.FirstOrDefault(x => string.Equals(x.Id, policyId, StringComparison.Ordinal));
        if (p is null) return "Policy '" + policyId + "' is not in the draft.";
        var plan = PlanSplit(p, draft);
        if (plan.Problem is not null || plan.PublicPolicy is null || plan.RestrictedPolicy is null) return plan.Problem ?? "Split could not be planned.";
        int i = draft.Policies.IndexOf(p);
        draft.Policies.RemoveAt(i);
        draft.Policies.Insert(i, plan.RestrictedPolicy);
        draft.Policies.Insert(i, plan.PublicPolicy);
        return null;
    }

    private static IEnumerable<string> LocalTargets(PolicyDto p, CovenantConfigDto cfg)
        => PolicyRoutes.Targets(p, cfg).Where(t => t.Boundary == "Local" && !t.Target.Paused && t.ExcludedReason is null).Select(t => t.Key);

    /// <summary>Enabled, permitted cloud targets of the policy (ignoring the PII/PHI exclusion so a split can move them to Public).</summary>
    private static IEnumerable<string> CloudTargets(PolicyDto p, CovenantConfigDto cfg)
    {
        foreach (var t in p.Targets)
        {
            if (t.Paused) continue;
            var conn = cfg.Connections.FirstOrDefault(c => string.Equals(c.Id, t.ConnectionId, StringComparison.Ordinal));
            if (conn is null || !conn.Enabled || ComposerRules.BoundaryLabel(conn.Boundary) != "Cloud") continue;
            if (!conn.Models.Any(m => string.Equals(m.ModelId, t.ModelId, StringComparison.Ordinal))) continue;
            yield return PolicyRoutes.Key(t);
        }
    }

    private static string? FirstModel(CovenantConfigDto cfg, string boundary, bool requirePrice)
    {
        foreach (var c in cfg.Connections)
        {
            if (!c.Enabled || ComposerRules.BoundaryLabel(c.Boundary) != boundary) continue;
            foreach (var m in c.Models)
                if (!requirePrice || m.Price is { InputPer1M: not null, OutputPer1M: not null })
                    return c.Id + "/" + m.ModelId;
        }
        return null;
    }

    private static string UniqueIdExcluding(string seed, CovenantConfigDto cfg, params string[] alsoTaken)
        => ConnectionRules.UniqueId(seed, cfg.Policies.Select(p => p.Id).Concat(alsoTaken));

    private static RetrySettingsDto? CopyRetry(RetrySettingsDto? r)
        => r is null ? null : new RetrySettingsDto { MaxAttempts = r.MaxAttempts, DeadlineMs = r.DeadlineMs, On = r.On?.ToList() };

    private static PolicyTargetDto ToTarget(string key)
    {
        int slash = key.IndexOf('/');
        return slash <= 0 ? new PolicyTargetDto { ConnectionId = key } : new PolicyTargetDto { ConnectionId = key[..slash], ModelId = key[(slash + 1)..] };
    }
}
