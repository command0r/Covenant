using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Shape stage (right after auth, so the audit entry names the resolved caller): a request whose
/// wire carried content governance cannot inspect — image/file blocks, tool definitions, tool results —
/// is denied fail-closed rather than silently stripped. Silently dropping an image would let unclassified
/// data reach a provider; silently dropping tools would lie to the client. Both are recorded as evidence.</summary>
public sealed class ShapeStage : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (ctx.Request.UnsupportedFeature is { } feature)
        {
            ctx.Deny($"unsupported request shape: {feature}", DenialKind.Unsupported);
            return;
        }
        if (ctx.Request.MaxOutputTokens is < 1)
        {
            ctx.Deny("max_tokens must be a positive integer", DenialKind.Unsupported);
            return;
        }
        await next(ctx, ct);
    }
}
