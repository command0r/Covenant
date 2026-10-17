using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Policy engine v2 (ADR-0013), pure: evaluates the request's bound configuration version.
/// Precedence: a policy naming the request's profile beats one naming only classifications; two matching
/// policies at the same specificity are refused (validation rejects that configuration; runtime refuses
/// rather than choosing an arbitrary winner). No match ⇒ refusal. Paused policy ⇒ refusal. Each target is
/// then checked for eligibility (route paused, connection known + enabled, model permitted on the connection,
/// mode boundary, sensitive-cloud rule, priced under a hard budget); the eligible ones in order are the
/// candidates [primary, approved fallback…]. Availability (is a client registered/healthy) is NOT
/// eligibility — the provider stage owns that and fails closed.</summary>
public static class PolicyEngineV2
{
    public const string LegacyInternalProfile = "legacy-internal";

    /// <summary>Policies applicable to (classification, profile) at the highest specificity tier.</summary>
    public static IReadOnlyList<PolicyConfigV2> Match(CovenantConfig config, DataClassification classification, string? profileId)
    {
        var matching = config.Policies
            .Where(p => p.Enabled && p.Classifications.Contains(classification)
                        && (p.Profiles.Count == 0 || (profileId is not null && p.Profiles.Contains(profileId, StringComparer.Ordinal))))
            .ToList();
        var specific = matching.Where(p => p.Profiles.Count > 0).ToList();
        return specific.Count > 0 ? specific : matching;
    }

    /// <summary>Why this target is not eligible for this request; null = eligible.</summary>
    public static string? Ineligibility(IRequestConfiguration cfg, PolicyConfigV2 policy, PolicyTarget target, DataClassification classification)
    {
        if (target.Paused) return "route paused";
        var connection = cfg.FindConnection(target.ConnectionId);
        if (connection is null)
        {
            // Legacy-cloud compatibility (migration-v2.md): the imported PII/PHI rule names the unconfigured
            // local placeholder exactly as before v2; it stays a candidate so the provider stage refuses it
            // fail-closed ("no adapter registered …"), preserving the pre-v2 evidence. Never in other modes.
            return cfg.Config.Mode == ProviderMode.LegacyCloud ? null : $"unknown connection '{Evidence.Bounded(target.ConnectionId)}'";
        }
        if (!connection.Enabled) return $"connection '{connection.Id}' disabled";
        var model = cfg.FindModel(connection.Id, target.ModelId);
        if (model is null) return $"model not permitted on connection '{connection.Id}'";
        if (connection.Boundary == Boundary.Cloud)
        {
            if (cfg.Config.Mode == ProviderMode.LocalOnly) return "cloud destination excluded in local-only mode";
            if (classification >= DataClassification.Pii && !policy.AllowSensitiveCloud)
                return $"{classification} is never routed to a cloud destination";
            if (model.Price is null && cfg.Config.Budgets.Any(b => b.Enforcement == BudgetEnforcement.Hard))
                return "unpriced paid destination under a hard budget";
        }
        return null;
    }

    public static PolicyOutcome Evaluate(IRequestConfiguration cfg, InferenceContext ctx)
        => Evaluate(cfg, ctx.Classification, ctx.ProfileId, ctx.Request.RequestedModel, ctx.Request.Messages);

    public static PolicyOutcome Evaluate(
        IRequestConfiguration cfg, DataClassification classification, string? profileId, string? requestedModel,
        IReadOnlyList<ChatMessage> messages)
    {
        var matches = Match(cfg.Config, classification, profileId);
        if (matches.Count == 0)
            return PolicyOutcome.Deny($"no permitted route for classification '{classification}'");
        if (matches.Count > 1)
            return PolicyOutcome.Deny(
                $"ambiguous policy overlap for classification '{classification}' (policies {string.Join(", ", matches.Select(m => Evidence.Bounded(m.Id)))}): refused, no arbitrary winner");

        var policy = matches[0];
        if (policy.Paused)
            return PolicyOutcome.Deny($"policy '{Evidence.Bounded(policy.Id)}' is paused") with { PolicyId = policy.Id };

        var eligible = new List<(PolicyTarget Target, RouteTarget Route)>();
        var excluded = new List<ExcludedTarget>();
        foreach (var t in policy.Targets)
        {
            var route = new RouteTarget(t.ConnectionId, t.ModelId);
            if (Ineligibility(cfg, policy, t, classification) is { } why) excluded.Add(new ExcludedTarget(route, why));
            else eligible.Add((t, route));
        }

        // Model override: must name a target of the matching policy AND be eligible; it pins (no fallback).
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            bool Named(RouteTarget r) => string.Equals(r.ModelId, requestedModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"{r.AdapterKey}/{r.ModelId}", requestedModel, StringComparison.OrdinalIgnoreCase);
            var pinned = eligible.FirstOrDefault(e => Named(e.Route)).Route;
            if (pinned is not null)
                return Allowed(cfg, policy, classification, pinned, [pinned], excluded, "allowed");
            var refusedNamed = excluded.FirstOrDefault(x => Named(x.Target));
            var reason = refusedNamed is not null
                ? $"model '{Evidence.Bounded(requestedModel)}' not permitted for classification '{classification}' ({refusedNamed.Reason})"
                : $"model '{Evidence.Bounded(requestedModel)}' not permitted for classification '{classification}'";
            return PolicyOutcome.Deny(reason) with { PolicyId = policy.Id, Excluded = excluded };
        }

        if (eligible.Count == 0)
            return PolicyOutcome.Deny(
                $"no eligible destination for classification '{classification}' under policy '{Evidence.Bounded(policy.Id)}'"
                + (excluded.Count > 0 ? ": " + string.Join("; ", excluded.Select(x => $"{x.Target.AdapterKey}/{x.Target.ModelId}: {x.Reason}")) : ""))
                with { PolicyId = policy.Id, Excluded = excluded };

        var routes = eligible.Select(e => e.Route).ToList();
        if (routes.Count == 1)
            return Allowed(cfg, policy, classification, routes[0], routes, excluded, "allowed");

        if (policy.Strategy == RoutingStrategy.LengthHeuristic)
        {
            // Length-based heuristic (named honestly — not quality-aware): chars/4 above the threshold takes the
            // LAST eligible target; remaining eligible targets stay as ordered fallback.
            long approx = messages.Sum(m => (long)m.Content.Length) / 4;
            var chosen = approx > policy.ThresholdTokens ? routes[^1] : routes[0];
            var ordered = new List<RouteTarget> { chosen };
            ordered.AddRange(routes.Where(r => !ReferenceEquals(r, chosen)));
            return Allowed(cfg, policy, classification, chosen, ordered, excluded,
                $"allowed (length-based heuristic: complexity-routed ~{approx} tokens, threshold {policy.ThresholdTokens} → {chosen.ModelId})");
        }

        return Allowed(cfg, policy, classification, routes[0], routes, excluded,
            $"allowed (fixed primary {routes[0].AdapterKey}/{routes[0].ModelId}; fallback: {string.Join(", ", routes.Skip(1).Select(r => $"{r.AdapterKey}/{r.ModelId}"))})");
    }

    private static PolicyOutcome Allowed(
        IRequestConfiguration cfg, PolicyConfigV2 policy, DataClassification classification, RouteTarget primary,
        IReadOnlyList<RouteTarget> candidates, IReadOnlyList<ExcludedTarget> excluded, string reason)
        => PolicyOutcome.Allow(primary, reason) with
        {
            PolicyId = policy.Id,
            Candidates = candidates,
            Excluded = excluded,
            Explanation = Explain(cfg, policy, classification, candidates),
        };

    public static string Explain(IRequestConfiguration cfg, PolicyConfigV2 policy, DataClassification classification, IReadOnlyList<RouteTarget> candidates)
    {
        var head = $"{classification} → {candidates[0].AdapterKey}/{candidates[0].ModelId}";
        if (candidates.Count > 1)
            head += $" (fallback: {string.Join(", ", candidates.Skip(1).Select(r => $"{r.AdapterKey}/{r.ModelId}"))})";
        return $"{head} · policy {policy.Id} v{cfg.Version}";
    }

    /// <summary>One-line effective route for (classification, profile) — used by impact preview and the
    /// simulator: "openai/gpt-4o-mini (fallback: ollama/qwen3:8b)" or "refused: &lt;reason&gt;".</summary>
    public static string Summary(IRequestConfiguration cfg, DataClassification classification, string? profileId)
    {
        var outcome = Evaluate(cfg, classification, profileId, null, []);
        if (outcome.Effect == PolicyEffect.Deny) return "refused: " + outcome.Reason;
        var c = outcome.Candidates;
        var s = $"{c[0].AdapterKey}/{c[0].ModelId}";
        if (c.Count > 1) s += $" (fallback: {string.Join(", ", c.Skip(1).Select(r => $"{r.AdapterKey}/{r.ModelId}"))})";
        return s;
    }
}
