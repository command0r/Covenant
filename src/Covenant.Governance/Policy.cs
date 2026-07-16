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

/// <summary>
/// Selects the first permitted route for the request's classification. If the caller requested a
/// specific model, it must be among the permitted routes for that classification, otherwise deny.
/// Fail-closed throughout.
/// </summary>
public sealed class PolicyEngine(PolicyConfig config) : IPolicyEngine
{
    public PolicyOutcome Evaluate(InferenceContext ctx)
    {
        if (!config.AllowedRoutes.TryGetValue(ctx.Classification, out var routes) || routes.Count == 0)
            return PolicyOutcome.Deny($"no permitted route for classification '{ctx.Classification}'");

        var requested = ctx.Request.RequestedModel;
        if (string.IsNullOrWhiteSpace(requested))
            return PolicyOutcome.Allow(routes[0]); // default to the first (typically cheapest) permitted route

        var match = routes.FirstOrDefault(r => string.Equals(r.ModelId, requested, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? PolicyOutcome.Allow(match)
            : PolicyOutcome.Deny($"model '{requested}' not permitted for classification '{ctx.Classification}'");
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
