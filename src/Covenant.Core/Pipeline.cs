using System.Diagnostics;

namespace Covenant.Core;

/// <summary>Continuation delegate that invokes the next stage in the chain.</summary>
public delegate Task PipelineDelegate(InferenceContext context, CancellationToken cancellationToken);

/// <summary>One ordered pipeline stage: mutate the context, call next to continue, or short-circuit
/// by returning without calling it (e.g. on a denial).</summary>
public interface IPipelineStage
{
    Task InvokeAsync(InferenceContext context, PipelineDelegate next, CancellationToken cancellationToken);
}

/// <summary>Pipeline diagnostics source (ADR-0003). BCL-only — no OTel dependency in Core; with no
/// listener the instrumentation is free. Spans carry governance METADATA only, never content.</summary>
public static class CovenantDiagnostics
{
    public const string SourceName = "Covenant.Pipeline";
    public static readonly ActivitySource Source = new(SourceName);
}

/// <summary>Composes stages into one chain, built once at construction (hot-path friendly). Once a
/// context is denied, no further stage runs — fail-closed. Each stage gets a nested activity span.</summary>
public sealed class InferencePipeline
{
    private readonly PipelineDelegate _entry;
    private readonly IReadOnlyList<string> _tracedStages;

    /// <summary>The audit stage is the evidence writer itself, not a governance step: it is never traced.</summary>
    private const string AuditStageName = "audit";

    public InferencePipeline(IReadOnlyList<IPipelineStage> stages)
    {
        var traced = new List<string>();
        foreach (var s in stages)
        {
            var n = StageName(s);
            if (n != AuditStageName && !traced.Contains(n)) traced.Add(n);
        }
        _tracedStages = traced;

        PipelineDelegate next = static (_, _) => Task.CompletedTask;
        for (int i = stages.Count - 1; i >= 0; i--)
        {
            var stage = stages[i];
            var spanName = "covenant.stage." + stage.GetType().Name;
            var stageName = StageName(stage);
            bool isTraced = stageName != AuditStageName;
            var localNext = next;
            next = (ctx, ct) => ctx.IsDenied ? Task.CompletedTask : RunStageAsync(stage, spanName, stageName, isTraced, ctx, localNext, ct);
        }
        _entry = next;
    }

    /// <summary>Traced stage names in execution order (excludes the audit stage).</summary>
    public IReadOnlyList<string> TracedStages => _tracedStages;

    public async Task ExecuteAsync(InferenceContext context, CancellationToken cancellationToken)
    {
        using var activity = CovenantDiagnostics.Source.StartActivity("covenant.request");
        if (context.StageOrder.Count == 0) context.StageOrder = _tracedStages;
        try
        {
            await _entry(context, cancellationToken);
        }
        finally
        {
            context.CompleteTrace();   // idempotent: the audit stage normally completed it before writing evidence
            if (activity is not null) Enrich(activity, context);
        }
    }

    /// <summary>Runs one stage and emits its real trace (ADR-0013): "running" on entry; "passed" with the Stopwatch-measured
    /// duration at the moment the stage hands over to the next one (or returns without denying — e.g. a cache hit); on a
    /// short-circuit denial "refused" / "failed" (upstream) / "cancelled" with a bounded reason code; "cancelled" or
    /// "failed" when an exception escapes the stage before it passed.</summary>
    private static async Task RunStageAsync(
        IPipelineStage stage, string spanName, string stageName, bool traced, InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        using var activity = CovenantDiagnostics.Source.StartActivity(spanName);
        ctx.CurrentStage = stageName;
        if (!traced)
        {
            await stage.InvokeAsync(ctx, next, ct);
            return;
        }

        ctx.MarkStageEntered(stageName);
        ctx.Trace(stageName, TraceStates.Running);
        var watch = Stopwatch.StartNew();
        bool handedOver = false;
        PipelineDelegate onward = (c, t) =>
        {
            if (!handedOver)
            {
                handedOver = true;
                // A stage that had nothing to do (e.g. web search Off / not triggered) reports "skipped" with its reason,
                // never a misleading "passed".
                var skip = c.TakeStageSkip();
                c.Trace(stageName, skip is null ? TraceStates.Passed : TraceStates.Skipped, watch.Elapsed.TotalMilliseconds, skip);
            }
            return next(c, t);
        };

        try
        {
            await stage.InvokeAsync(ctx, onward, ct);
        }
        catch (OperationCanceledException)
        {
            if (!handedOver) ctx.Trace(stageName, TraceStates.Cancelled, watch.Elapsed.TotalMilliseconds, TraceReasons.ClientCancelled);
            throw;
        }
        catch (Exception)
        {
            if (!handedOver) ctx.Trace(stageName, TraceStates.Failed, watch.Elapsed.TotalMilliseconds, TraceReasons.Error);
            throw;
        }

        if (handedOver) return;
        if (ctx.IsDenied)
        {
            var state = ctx.DenialKind switch
            {
                DenialKind.Cancelled => TraceStates.Cancelled,
                DenialKind.UpstreamFailure => TraceStates.Failed,
                _ => TraceStates.Refused,
            };
            ctx.Trace(stageName, state, watch.Elapsed.TotalMilliseconds, ctx.DenialCode ?? TraceReasons.ForDenial(ctx.DenialKind));
        }
        else
        {
            var skip = ctx.TakeStageSkip();
            ctx.Trace(stageName, skip is null ? TraceStates.Passed : TraceStates.Skipped, watch.Elapsed.TotalMilliseconds,
                skip ?? (ctx.ServedFromCache && stageName == "cache" ? TraceReasons.CacheHit : null));
        }
    }

    /// <summary>Stable evidence names for stages (audit field StoppedAtStage); unknown stage types
    /// fall back to their lower-cased type name without the "Stage" suffix.</summary>
    public static string StageName(IPipelineStage stage)
    {
        var name = stage.GetType().Name;
        if (name.EndsWith("Stage", StringComparison.Ordinal)) name = name[..^5];
        return name switch
        {
            "Classify" => "classify",
            "RateLimit" => "rate_limit",
            "ProviderCall" => "provider",
            "WebSearch" => "search",
            _ => name.ToLowerInvariant(),
        };
    }

    /// <summary>Governance metadata only (ADR-0003): classification, outcome, route, usage, attribution
    /// tags. Deliberately no message content, ever. Spans are diagnostics; the audit chain is evidence.</summary>
    private static void Enrich(Activity activity, InferenceContext ctx)
    {
        activity.SetTag("covenant.classification", ctx.Classification.ToString());
        activity.SetTag("covenant.effect", ctx.IsDenied ? "deny" : "allow");
        activity.SetTag("covenant.stream", ctx.Request.Stream);
        activity.SetTag("covenant.team", ctx.Identity.Tags.Team);
        activity.SetTag("covenant.workflow", ctx.Identity.Tags.Workflow);
        activity.SetTag("covenant.use_case", ctx.Identity.Tags.UseCase);

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
