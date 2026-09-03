using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Ordered permitted routes per classification. Missing or empty list → deny — this is how
/// PII/PHI stays off public providers: no public route listed under those classifications.</summary>
public sealed class PolicyConfig
{
    public required IReadOnlyDictionary<DataClassification, IReadOnlyList<RouteTarget>> AllowedRoutes { get; init; }
}

public interface IPolicyEngine
{
    PolicyOutcome Evaluate(InferenceContext context);
}

/// <summary>Route-selection knobs: routes are ORDERED cheapest → strongest; above-threshold prompts take
/// the strongest permitted route. Token estimate is chars/4 — deliberate heuristic behind a swap seam.</summary>
public sealed class RoutingOptions
{
    public long ComplexityTokenThreshold { get; init; } = 400;
}

/// <summary>Policy + FinOps route selection, fail-closed: no permitted route → deny; a requested model
/// must be within the permitted set, else deny; otherwise complexity-route (cheapest vs strongest).</summary>
public sealed class PolicyEngine(PolicyConfig config, RoutingOptions? routing = null) : IPolicyEngine
{
    private readonly RoutingOptions _routing = routing ?? new RoutingOptions();

    public PolicyOutcome Evaluate(InferenceContext ctx)
    {
        if (!config.AllowedRoutes.TryGetValue(ctx.Classification, out var routes) || routes.Count == 0)
            return PolicyOutcome.Deny($"no permitted route for classification '{ctx.Classification}'");

        var requested = ctx.Request.RequestedModel;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = routes.FirstOrDefault(r => string.Equals(r.ModelId, requested, StringComparison.OrdinalIgnoreCase));
            return match is not null
                ? PolicyOutcome.Allow(match)
                : PolicyOutcome.Deny($"model '{requested}' not permitted for classification '{ctx.Classification}'");
        }

        if (routes.Count == 1)
            return PolicyOutcome.Allow(routes[0]);

        long approxTokens = EstimateTokens(ctx.Request.Messages);
        var route = approxTokens > _routing.ComplexityTokenThreshold ? routes[^1] : routes[0];
        return PolicyOutcome.Allow(route,
            $"allowed (complexity-routed ~{approxTokens} tokens → {route.ModelId})");
    }

    private static long EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var m in messages) chars += m.Content.Length;
        return chars / 4;
    }
}

/// <summary>Pipeline stage: evaluates policy and short-circuits the pipeline on a denial.</summary>
public sealed class PolicyStage(IPolicyEngine engine) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var outcome = engine.Evaluate(ctx);
        ctx.Policy = outcome;

        if (outcome.Effect == PolicyEffect.Deny)
        {
            ctx.Deny(outcome.Reason); // do not call next: nothing that costs money runs after a deny
            return;
        }

        await next(ctx, ct);
    }
}
