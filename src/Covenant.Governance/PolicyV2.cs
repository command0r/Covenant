using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Policy engine v2 (ADR-0013), pure: evaluates the request's bound configuration version.
/// Precedence: a policy naming the request's profile beats one naming only classifications; two matching
/// policies at the same specificity are refused (validation rejects that configuration; runtime refuses
/// rather than choosing an arbitrary winner). No match ⇒ refusal. Paused policy ⇒ refusal. Each target is
/// then checked for eligibility (route paused, connection known + enabled, model permitted on the connection,
/// mode boundary, PII/PHI never cloud, priced under a hard budget); the eligible ones in order are the
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

    /// <summary>Why this target is not eligible for this request; null = eligible. No mode bypasses these rules:
    /// an unknown or disabled connection is never eligible (legacy-cloud included), and PII/PHI never reach a
    /// cloud destination (no escape hatch; validation also refuses such a policy).</summary>
    public static string? Ineligibility(IRequestConfiguration cfg, PolicyTarget target, DataClassification classification)
    {
        if (target.Paused) return "route paused";
        var connection = cfg.FindConnection(target.ConnectionId);
        if (connection is null) return $"unknown connection '{Evidence.Bounded(target.ConnectionId)}'";
        if (!connection.Enabled)
            return $"connection '{connection.Id}' is disabled" + (connection.Endpoint is null ? " (not configured)" : "");
        var model = cfg.FindModel(connection.Id, target.ModelId);
        if (model is null) return $"model not permitted on connection '{connection.Id}'";
        if (connection.Boundary == Boundary.Cloud)
        {
            if (cfg.Config.Mode == ProviderMode.LocalOnly) return "cloud destination excluded in local-only mode";
            if (classification >= DataClassification.Pii)
                return $"{classification} is never routed to a cloud destination";
            if (model.Price is null && cfg.Config.Budgets.Any(b => b.Enforcement == BudgetEnforcement.Hard))
                return "unpriced paid destination under a hard budget";
        }
        return null;
    }

    /// <summary>Resolves a model override against routes (ADR-0013): "connection/model" names exactly that target;
    /// a bare model id resolves only when exactly one route carries it. Returns the matching distinct routes (0, 1 or
    /// several — several means ambiguous and MUST be refused, never "first wins").</summary>
    public static List<RouteTarget> ResolveOverride(IEnumerable<RouteTarget> routes, string requested)
    {
        bool IsComposite(RouteTarget r) => string.Equals($"{r.AdapterKey}/{r.ModelId}", requested, StringComparison.OrdinalIgnoreCase);
        bool IsBare(RouteTarget r) => string.Equals(r.ModelId, requested, StringComparison.OrdinalIgnoreCase);

        var matches = new List<RouteTarget>();
        foreach (var r in routes)
            if ((IsComposite(r) || IsBare(r)) && !matches.Contains(r)) matches.Add(r);
        if (matches.Count > 1)
        {
            // The composite form is exact: it wins when it singles out one route and no OTHER route carries the
            // same string as a bare model id (that pathological collision stays ambiguous — refused).
            var composite = matches.Where(IsComposite).ToList();
            if (composite.Count == 1 && matches.Where(IsBare).All(b => b == composite[0]))
                return composite;
        }
        return matches;
    }

    /// <summary>Evidence text for an ambiguous bare model id.</summary>
    public static string AmbiguousOverride(string requested, IEnumerable<RouteTarget> matches)
        => $"model id '{Evidence.Bounded(requested)}' is ambiguous across connections "
           + string.Join(", ", matches.Select(m => Evidence.Bounded(m.AdapterKey)).Distinct(StringComparer.Ordinal))
           + " — use connection/model";

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
            if (Ineligibility(cfg, t, classification) is { } why) excluded.Add(new ExcludedTarget(route, why));
            else eligible.Add((t, route));
        }

        // Model override: must resolve to exactly ONE eligible target of the matching policy; it pins (no fallback).
        // An ambiguous bare id (eligible on two connections) is refused — never "first wins".
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            var pinned = ResolveOverride(eligible.Select(e => e.Route), requestedModel);
            if (pinned.Count > 1)
                return PolicyOutcome.Deny(AmbiguousOverride(requestedModel, pinned)) with { PolicyId = policy.Id, Excluded = excluded };
            if (pinned.Count == 1)
                return Allowed(cfg, policy, classification, pinned[0], [pinned[0]], excluded, "allowed");
            var refusedNamed = ResolveOverride(excluded.Select(x => x.Target), requestedModel);
            var reason = refusedNamed.Count > 0
                ? $"model '{Evidence.Bounded(requestedModel)}' not permitted for classification '{classification}' ({excluded.First(x => x.Target == refusedNamed[0]).Reason})"
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
