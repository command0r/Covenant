using Covenant.Core;

namespace Covenant.Governance;

/// <summary>
/// For each classification, the ordered list of routes a request may use. A missing or empty list
/// means "no permitted route" → deny. This is how PII/PHI is kept off public providers: simply do not
/// list a public route under those classifications.
/// </summary>
public sealed class PolicyConfig
{
    public required IReadOnlyDictionary<DataClassification, IReadOnlyList<RouteTarget>> AllowedRoutes { get; init; }
}

public interface IPolicyEngine
{
    PolicyOutcome Evaluate(InferenceContext context);
}

/// <summary>Route-selection knobs. Routes per classification are ORDERED cheapest → strongest;
/// prompts estimated above the threshold take the strongest permitted route, everything else takes
/// the cheapest. Token estimate is chars/4 — a deliberate, documented heuristic (a real tokenizer is
/// a swap behind this seam, not a redesign).</summary>
public sealed class RoutingOptions
{
    public long ComplexityTokenThreshold { get; init; } = 400;
}

/// <summary>
/// Policy + FinOps-aware route selection, fail-closed throughout:
///  - no permitted route for the classification → deny;
///  - caller-requested model must be within the permitted set, else deny;
///  - otherwise complexity-route among the permitted set (cheapest for simple prompts, strongest
///    for complex ones). One permitted route degenerates to the old first-route behavior.
/// </summary>
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
