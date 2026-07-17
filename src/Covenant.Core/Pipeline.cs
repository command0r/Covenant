using System.Diagnostics;

namespace Covenant.Core;

/// <summary>Continuation delegate that invokes the next stage in the chain.</summary>
public delegate Task PipelineDelegate(InferenceContext context, CancellationToken cancellationToken);

/// <summary>
/// One ordered stage of the inference pipeline. A stage may inspect or mutate the context, call
/// <paramref name="next"/> to continue, or short-circuit by returning without calling it (e.g. on a denial).
/// </summary>
public interface IPipelineStage
{
    Task InvokeAsync(InferenceContext context, PipelineDelegate next, CancellationToken cancellationToken);
}

/// <summary>
/// Diagnostics source for the pipeline (ADR-0003). BCL-only — Core takes no OpenTelemetry dependency;
/// the Host decides whether anything listens. With no listener, StartActivity returns null and the
/// instrumentation is effectively free (src/CLAUDE.md hot-path rule). Spans carry governance METADATA
/// only — never prompt or response content.
/// </summary>
public static class CovenantDiagnostics
{
    public const string SourceName = "Covenant.Pipeline";
    public static readonly ActivitySource Source = new(SourceName);
}

/// <summary>
/// Composes stages into a single execution chain, in registration order. The chain is built once at
/// construction (hot-path friendly). Once a context is denied, no further stage runs — fail-closed.
/// Each stage runs inside its own activity span (nested, middleware-onion shape); the request span is
/// enriched with the governance outcome on completion.
/// </summary>
public sealed class InferencePipeline
{
    private readonly PipelineDelegate _entry;

    public InferencePipeline(IReadOnlyList<IPipelineStage> stages)
    {
        PipelineDelegate next = static (_, _) => Task.CompletedTask;
        for (int i = stages.Count - 1; i >= 0; i--)
        {
            var stage = stages[i];
            var spanName = "covenant.stage." + stage.GetType().Name;
            var localNext = next;
            next = (ctx, ct) => ctx.IsDenied ? Task.CompletedTask : RunStageAsync(stage, spanName, ctx, localNext, ct);
        }
        _entry = next;
    }

    public async Task ExecuteAsync(InferenceContext context, CancellationToken cancellationToken)
    {
        using var activity = CovenantDiagnostics.Source.StartActivity("covenant.request");
        try
        {
            await _entry(context, cancellationToken);
        }
        finally
        {
            if (activity is not null) Enrich(activity, context);
        }
    }

    private static async Task RunStageAsync(
        IPipelineStage stage, string spanName, InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        using var activity = CovenantDiagnostics.Source.StartActivity(spanName);
        await stage.InvokeAsync(ctx, next, ct);
    }

    /// <summary>Governance metadata only (ADR-0003): classification, outcome, route, usage, attribution
    /// tags. Deliberately no message content, ever. Spans are diagnostics; the audit chain is evidence.</summary>
    private static void Enrich(Activity activity, InferenceContext ctx)
    {
        activity.SetTag("covenant.classification", ctx.Classification.ToString());
        activity.SetTag("covenant.effect", ctx.IsDenied ? "deny" : "allow");
        activity.SetTag("covenant.stream", ctx.Request.Stream);
        activity.SetTag("covenant.team", ctx.Request.Attribution.Team);
        activity.SetTag("covenant.workflow", ctx.Request.Attribution.Workflow);
        activity.SetTag("covenant.use_case", ctx.Request.Attribution.UseCase);

        if (ctx.Policy?.Route is { } route)
        {
            activity.SetTag("covenant.adapter", route.AdapterKey);
            activity.SetTag("covenant.model", route.ModelId);
        }

        if (ctx.IsDenied)
        {
            activity.SetTag("covenant.denial_kind", ctx.DenialKind.ToString());
            activity.SetTag("covenant.denial_reason", ctx.DenialReason);
            activity.SetStatus(ActivityStatusCode.Error, ctx.DenialReason);
        }

        if (ctx.Response is { } resp)
        {
            activity.SetTag("covenant.tokens.input", resp.Usage.InputTokens);
            activity.SetTag("covenant.tokens.output", resp.Usage.OutputTokens);
            activity.SetTag("covenant.cost_usd", (double)resp.Usage.CostUsd);
        }
    }
}
