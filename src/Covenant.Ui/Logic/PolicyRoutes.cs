using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Why a configured target is or is not usable, judged from the configuration only (the same structural
/// rules the Host's engine applies: paused, connection known + enabled, model permitted, mode boundary, PII/PHI never
/// cloud). Availability (health) is a separate fact; the policy simulator is the authority for a specific request.</summary>
public sealed record TargetState(PolicyTargetDto Target, string Key, string? Boundary, string? ExcludedReason)
{
    public bool Eligible => ExcludedReason is null;
}

/// <summary>An option in the "add target" list: only enabled connections' permitted models; cloud models are not
/// offered to PII/PHI policies or in local-only mode (the reason is shown instead).</summary>
public sealed record TargetOption(string ConnectionId, string ModelId, string Label, string? Boundary, string? DisabledReason)
{
    public string Value => ConnectionId + "/" + ModelId;
}

/// <summary>Wording for policies (spec §5 Policies): definite effective state, never "may use".</summary>
public static class PolicyText
{
    public const string SensitiveNeverCloud = "PII/PHI never route to cloud destinations.";

    public static string ClassificationLabel(string? c) => (c ?? "").Trim().ToLowerInvariant() switch
    {
        "public" => "Public",
        "internal" => "Internal",
        "pii" => "PII",
        "phi" => "PHI",
        "" => "Not reported",
        _ => c!.Trim(),
    };

    public static int ClassificationOrder(string? c) => (c ?? "").Trim().ToLowerInvariant() switch
    {
        "public" => 0, "internal" => 1, "pii" => 2, "phi" => 3, _ => 9,
    };

    public static bool IsSensitive(string? c) => (c ?? "").Trim().ToLowerInvariant() is "pii" or "phi";

    public static bool CoversSensitive(PolicyDto p) => p.Classifications.Any(IsSensitive);

    /// <summary>"Fixed + fallback" or "Length-based heuristic (threshold 400 tokens)".</summary>
    public static string StrategyName(PolicyDto p)
        => IsLengthHeuristic(p)
            ? "Length-based heuristic (threshold " + (p.ThresholdTokens ?? 400).ToString("N0", CultureInfo.InvariantCulture) + " tokens)"
            : "Fixed + fallback";

    public static bool IsLengthHeuristic(PolicyDto p) => string.Equals(p.Strategy, "length-heuristic", StringComparison.OrdinalIgnoreCase)
                                                         || string.Equals(p.Strategy, "LengthHeuristic", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Public", "PII + PHI", "Internal · profile internal-docs".</summary>
    public static string Applicability(PolicyDto p)
    {
        var cls = p.Classifications.Count == 0
            ? "No classification"
            : string.Join(" + ", p.Classifications.OrderBy(ClassificationOrder).Select(ClassificationLabel));
        return p.Profiles.Count == 0 ? cls : cls + " · profile " + string.Join(", ", p.Profiles);
    }

    /// <summary>"Active", "Paused — refuses its traffic", "Disabled — not evaluated".</summary>
    public static string StateText(PolicyDto p)
        => !p.Enabled ? "Disabled — not evaluated" : p.Paused ? "Paused — refuses its traffic" : "Active";

    public static string StateKind(PolicyDto p) => !p.Enabled ? "unknown" : p.Paused ? "paused" : "served";

    public const string PausedPolicyExplained =
        "A paused policy refuses the traffic it matches; it never falls through to a less specific policy.";
    public const string PausedRouteExplained =
        "A paused route is skipped; the next approved target in order serves instead. If every target is paused, the request is refused.";

    public static bool IsBoundary(string? boundary, string expected) => string.Equals(ComposerRules.BoundaryLabel(boundary), expected, StringComparison.Ordinal);
}

/// <summary>Effective routes and the editable target list.</summary>
public static class PolicyRoutes
{
    public static string Key(PolicyTargetDto t) => t.ConnectionId + "/" + t.ModelId;

    public static bool IsLocalOnly(string? mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return m is "localonly";
    }

    public static bool IsLegacy(string? mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return m is "legacycloud" or "cloud";
    }

    /// <summary>The configured target list with each target's structural eligibility.</summary>
    public static IReadOnlyList<TargetState> Targets(PolicyDto p, CovenantConfigDto cfg)
    {
        var list = new List<TargetState>(p.Targets.Count);
        foreach (var t in p.Targets)
        {
            var conn = cfg.Connections.FirstOrDefault(c => string.Equals(c.Id, t.ConnectionId, StringComparison.Ordinal));
            var boundary = conn is null ? null : ComposerRules.BoundaryLabel(conn.Boundary);
            string? why = null;
            if (t.Paused) why = "route paused — skipped";
            else if (conn is null) why = "connection '" + t.ConnectionId + "' is not configured";
            else if (!conn.Enabled) why = "connection '" + conn.Id + "' is disabled";
            else if (!conn.Models.Any(m => string.Equals(m.ModelId, t.ModelId, StringComparison.Ordinal))) why = "model not permitted on '" + conn.Id + "'";
            else if (boundary == "Cloud" && IsLocalOnly(cfg.Mode)) why = "cloud destination in Local-only mode";
            else if (boundary == "Cloud" && PolicyText.CoversSensitive(p)) why = "cloud destination for PII/PHI — never eligible";
            list.Add(new TargetState(t, Key(t), boundary, why));
        }
        return list;
    }

    /// <summary>The definite effective state line, e.g. "Public → openai/gpt-4o-mini · fallback: ollama/qwen2.5-coder:7b",
    /// "PII + PHI → refused (policy paused)", "Internal → ≤ 400 tokens: ollama/qwen · above: openai/gpt-x".</summary>
    public static string SummaryLine(PolicyDto p, CovenantConfigDto cfg)
    {
        var scope = PolicyText.Applicability(p);
        if (!p.Enabled) return scope + " → not evaluated (policy disabled)";
        if (p.Paused) return scope + " → refused (policy paused)";
        var eligible = Targets(p, cfg).Where(t => t.Eligible).Select(t => t.Key).ToList();
        if (eligible.Count == 0) return scope + " → refused (no eligible target)";
        if (PolicyText.IsLengthHeuristic(p) && eligible.Count > 1)
        {
            var n = (p.ThresholdTokens ?? 400).ToString("N0", CultureInfo.InvariantCulture);
            var rest = eligible.Skip(1).Take(eligible.Count - 2).ToList();
            var line = scope + " → ≤ " + n + " tokens: " + eligible[0] + " · above: " + eligible[^1];
            return rest.Count == 0 ? line : line + " · fallback: " + string.Join(", ", rest);
        }
        return eligible.Count == 1
            ? scope + " → " + eligible[0] + " · no fallback"
            : scope + " → " + eligible[0] + " · fallback: " + string.Join(", ", eligible.Skip(1));
    }

    /// <summary>Why matching requests are refused, for the detail panel (never on the summary line).</summary>
    public static IReadOnlyList<string> RefusalConditions(PolicyDto p, CovenantConfigDto cfg)
    {
        var list = new List<string>();
        if (!p.Enabled) list.Add("The policy is disabled: it is not evaluated, so its traffic falls to another matching policy or is refused when none matches.");
        if (p.Paused) list.Add("The policy is paused: every request it matches is refused.");
        foreach (var t in Targets(p, cfg).Where(t => !t.Eligible))
            list.Add(t.Key + ": " + t.ExcludedReason + ".");
        if (Targets(p, cfg).All(t => !t.Eligible)) list.Add("No eligible target remains: matching requests are refused (never redirected).");
        list.Add("A requested model outside the eligible targets is refused; an override pins one target (no fallback).");
        list.Add("Fallback happens only for the configured transient failures, before any content was streamed, within the attempt and deadline bounds.");
        if (PolicyText.CoversSensitive(p)) list.Add(PolicyText.SensitiveNeverCloud);
        return list;
    }

    /// <summary>Targets that may be added to <paramref name="p"/>: enabled connections' permitted models not already
    /// listed. Cloud models carry a disabled reason for PII/PHI policies and in Local-only mode.</summary>
    public static IReadOnlyList<TargetOption> Options(PolicyDto p, CovenantConfigDto cfg)
    {
        var listed = new HashSet<string>(p.Targets.Select(Key), StringComparer.Ordinal);
        var options = new List<TargetOption>();
        foreach (var c in cfg.Connections)
        {
            if (!c.Enabled) continue;
            var boundary = ComposerRules.BoundaryLabel(c.Boundary);
            foreach (var m in c.Models)
            {
                var key = c.Id + "/" + m.ModelId;
                if (listed.Contains(key)) continue;
                string? why = null;
                if (boundary == "Cloud" && PolicyText.CoversSensitive(p)) why = PolicyText.SensitiveNeverCloud;
                else if (boundary == "Cloud" && IsLocalOnly(cfg.Mode)) why = "Cloud destinations cannot execute in Local-only mode.";
                options.Add(new TargetOption(c.Id, m.ModelId, key + " · " + (boundary ?? "boundary not reported"), boundary, why));
            }
        }
        return options;
    }

    /// <summary>Moves the target at <paramref name="index"/> one place earlier (higher precedence). False at the top.</summary>
    public static bool MoveUp(List<PolicyTargetDto> targets, int index)
    {
        if (index <= 0 || index >= targets.Count) return false;
        (targets[index - 1], targets[index]) = (targets[index], targets[index - 1]);
        return true;
    }

    /// <summary>Moves the target at <paramref name="index"/> one place later. False at the bottom.</summary>
    public static bool MoveDown(List<PolicyTargetDto> targets, int index)
    {
        if (index < 0 || index >= targets.Count - 1) return false;
        (targets[index + 1], targets[index]) = (targets[index], targets[index + 1]);
        return true;
    }

    /// <summary>"Primary" for the first target, "Fallback 1", "Fallback 2" … after it.</summary>
    public static string RoleLabel(int index) => index == 0 ? "Primary" : "Fallback " + index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The "Move to Hybrid defaults" DRAFT action for imported legacy rules (spec §3 default demonstration
/// policy): Public → an enabled, priced cloud model with the local model as approved fallback; Internal, PII and
/// PHI → the local model only. Produces a candidate for the impact preview — it applies nothing.</summary>
public static class HybridDefaults
{
    public sealed record Plan(string? Problem, string? LocalTarget, string? CloudTarget);

    public static Plan Choose(CovenantConfigDto cfg)
    {
        var local = FirstModel(cfg, "Local", requirePrice: false);
        var cloud = FirstModel(cfg, "Cloud", requirePrice: true);
        if (local is null)
            return new Plan("Hybrid defaults need an enabled Local connection with at least one model (add one under Models & connections).", null, cloud);
        return new Plan(null, local, cloud);
    }

    /// <summary>Rewrites <paramref name="draft"/>: mode hybrid, policies replaced by the defaults. Returns the problem
    /// text (draft untouched) when no local model is configured.</summary>
    public static string? ApplyTo(CovenantConfigDto draft)
    {
        var plan = Choose(draft);
        if (plan.Problem is not null || plan.LocalTarget is null) return plan.Problem ?? "No local model.";
        var local = Split(plan.LocalTarget);
        var publicTargets = new List<PolicyTargetDto>();
        if (plan.CloudTarget is { } cloudKey)
        {
            var cloud = Split(cloudKey);
            publicTargets.Add(new PolicyTargetDto { ConnectionId = cloud.Conn, ModelId = cloud.Model });
        }
        publicTargets.Add(new PolicyTargetDto { ConnectionId = local.Conn, ModelId = local.Model });

        draft.Mode = "hybrid";
        draft.Policies =
        [
            new PolicyDto
            {
                Id = "public", Name = "Public: approved cloud model, local fallback", Enabled = true,
                Classifications = ["Public"], Targets = publicTargets, Strategy = "fixed",
                Explanation = "Public content may use the approved economical cloud model; the local model is the approved fallback.",
            },
            new PolicyDto
            {
                Id = "internal", Name = "Internal: local model", Enabled = true,
                Classifications = ["Internal"], Targets = [new PolicyTargetDto { ConnectionId = local.Conn, ModelId = local.Model }], Strategy = "fixed",
                Explanation = "Internal content stays on the approved local model.",
            },
            new PolicyDto
            {
                Id = "sensitive", Name = "PII/PHI: local only", Enabled = true,
                Classifications = ["Pii", "Phi"], Targets = [new PolicyTargetDto { ConnectionId = local.Conn, ModelId = local.Model }], Strategy = "fixed",
                Explanation = PolicyText.SensitiveNeverCloud,
            },
        ];
        return null;
    }

    private static string? FirstModel(CovenantConfigDto cfg, string boundary, bool requirePrice)
    {
        foreach (var c in cfg.Connections)
        {
            if (!c.Enabled || ComposerRules.BoundaryLabel(c.Boundary) != boundary) continue;
            foreach (var m in c.Models)
                if (!requirePrice || (m.Price is { InputPer1M: not null, OutputPer1M: not null }))
                    return c.Id + "/" + m.ModelId;
        }
        return null;
    }

    private static (string Conn, string Model) Split(string key)
    {
        int slash = key.IndexOf('/');
        return (key[..slash], key[(slash + 1)..]);
    }
}
