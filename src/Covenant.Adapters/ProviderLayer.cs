using System.Text;
using Covenant.Core;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Adapters;

/// <summary>Resolves a route (adapter + model) to a Microsoft.Extensions.AI IChatClient.</summary>
public interface IChatClientRegistry
{
    bool TryResolve(string adapterKey, string modelId, out MEAI.IChatClient client);
}

/// <summary>Keys: exact "adapter:model" (how the Host registers — a route to an unregistered model
/// fails closed) or a bare "adapter" wildcard for test doubles. Exact match wins.</summary>
public sealed class ChatClientRegistry(IReadOnlyDictionary<string, MEAI.IChatClient> clients) : IChatClientRegistry
{
    public bool TryResolve(string adapterKey, string modelId, out MEAI.IChatClient client)
        => clients.TryGetValue($"{adapterKey}:{modelId}", out client!)
        || clients.TryGetValue(adapterKey, out client!);
}

/// <summary>Observed provider health (Task 06): written by real pipeline calls and admin probes. A later
/// failure always supersedes an earlier success for current state; last-success time is preserved.</summary>
public interface IProviderHealth
{
    void RecordSuccess(string adapterKey, string modelId);
    void RecordFailure(string adapterKey, string modelId, string error);
}

/// <summary>Classifies a provider exception into the bounded failure vocabulary of ADR-0013 (the policy's
/// <c>retry.on</c> set): <c>connect</c>, <c>timeout</c>, <c>429</c>, <c>5xx</c> (500/502/503/504) or <c>non_retryable</c>.
/// The Host supplies an SDK-aware implementation (it knows the OpenAI/Anthropic exception types); this layer only
/// knows BCL types. Caller cancellation never reaches a classifier — it is not a provider failure.</summary>
public interface IProviderFailureClassifier
{
    string Classify(Exception exception);
}

/// <summary>BCL-only classification: HttpRequestException with a status code (429 / 500,502,503,504 / other ⇒
/// non-retryable), without one ⇒ connect; SocketException ⇒ connect; TimeoutException / provider-side
/// OperationCanceledException ⇒ timeout; anything else ⇒ non-retryable. Inner exceptions are inspected.</summary>
public class BclProviderFailureClassifier : IProviderFailureClassifier
{
    public static readonly BclProviderFailureClassifier Instance = new();

    public static string FromStatus(int status) => status switch
    {
        429 => TraceReasons.RateLimited429,
        500 or 502 or 503 or 504 => TraceReasons.ServerError5xx,
        _ => TraceReasons.NonRetryable,
    };

    public virtual string Classify(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (ClassifyOne(e) is { } c) return c;
        }
        return TraceReasons.NonRetryable;
    }

    /// <summary>Null = "not recognised here, look at the inner exception".</summary>
    protected virtual string? ClassifyOne(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } status } => FromStatus((int)status),
        HttpRequestException => TraceReasons.Connect,
        System.Net.Sockets.SocketException => TraceReasons.Connect,
        TimeoutException => TraceReasons.Timeout,
        OperationCanceledException => TraceReasons.Timeout,   // provider-side (caller cancellation is handled before)
        _ => null,
    };
}

/// <summary>The one place provider concepts exist (ADR-0001): canonical ↔ Microsoft.Extensions.AI
/// mapping around the resolved IChatClient. No governance decisions here; no provider concept escapes.
/// <para>ADR-0013 bounded fallback: with a bound configuration the stage iterates the policy's ordered eligible
/// candidates (<see cref="PolicyOutcome.Candidates"/>). Before EACH attempt the request's <see cref="IAttemptGate"/>
/// (installed by the budget stage) re-checks the global pause and reserves budget; a refusal stops at stage
/// pause/budget. A failed attempt falls back to the next candidate only when its failure class is in the policy's
/// <c>retry.on</c>, within <c>maxAttempts</c> and <c>deadlineMs</c>, and NEVER after any content reached the client;
/// never on caller cancellation (rethrown) or non-retryable errors. Every started attempt is recorded
/// (<see cref="InferenceContext.Attempts"/>) and settled exactly once; health is recorded per connection:model.</para>
/// <para>Deadline semantics: no attempt starts after <c>deadlineMs</c>; an attempt still waiting for its response (or,
/// streaming, its first content) at the deadline is cancelled and classified <c>timeout</c>. Once streamed content is
/// flowing the deadline no longer applies (the connection timeout does) — a delivered answer is never cut off by the
/// fallback budget.</para></summary>
/// <param name="preferRequestClients">ADR-0011: resolve against the provider clients compiled into the request's
/// bound configuration snapshot (so a request keeps the clients of the version it started on). The Host sets it
/// only when its own snapshot-backed registry is the DI registration; a test-substituted registry always wins.</param>
public sealed class ProviderCallStage(
    IChatClientRegistry registry,
    IProviderHealth? health = null,
    bool preferRequestClients = false,
    IProviderFailureClassifier? classifier = null) : IPipelineStage
{
    private const string Stage = "provider";
    private readonly IProviderFailureClassifier _classifier = classifier ?? BclProviderFailureClassifier.Instance;

    private enum AttemptEnd { Served, Failed, ClientGone }

    private sealed record Plan(IReadOnlyList<RouteTarget> Candidates, int MaxAttempts, int? DeadlineMs, HashSet<string> RetryOn);

    private static HashSet<string> NoRetry() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy compositions (no configuration, or no policy v2 candidates) make exactly one attempt on Route.</summary>
    private static Plan PlanFor(InferenceContext ctx, RouteTarget route)
    {
        var outcome = ctx.Policy!;
        if (ctx.Configuration is not { } cfg || outcome.Candidates.Count == 0)
            return new Plan([route], 1, null, NoRetry());
        var retry = outcome.PolicyId is { } pid ? cfg.Config.Policies.FirstOrDefault(p => p.Id == pid)?.Retry : null;
        if (retry is null) return new Plan([outcome.Candidates[0]], 1, null, NoRetry());
        return new Plan(outcome.Candidates, Math.Clamp(retry.MaxAttempts, 1, 3), retry.DeadlineMs > 0 ? (int?)retry.DeadlineMs : null,
            new HashSet<string>(retry.On, StringComparer.OrdinalIgnoreCase));
    }

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var route = ctx.Policy?.Route;
        if (route is null) { ctx.Deny("no route resolved before provider call"); return; }

        var effective = preferRequestClients && ctx.Configuration?.GetResource<IChatClientRegistry>() is { } snapshotClients
            ? snapshotClients
            : registry;
        var plan = PlanFor(ctx, route);
        var messages = ctx.Request.Messages.Select(ToMeai).ToList();

        using var deadline = plan.DeadlineMs is not null ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (deadline is not null) deadline.CancelAfter(plan.DeadlineMs!.Value);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; ; i++)
        {
            var target = plan.Candidates[i];
            var attemptId = "att_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (ctx.AttemptGate is { } gate)
            {
                var admission = gate.Admit(ctx, target, attemptId);
                if (!admission.Admitted)
                {
                    ctx.Deny(admission.Reason ?? "attempt refused", DenialKind.Governance, admission.Stage, admission.Code);
                    return;
                }
            }

            if (!effective.TryResolve(target.AdapterKey, target.ModelId, out var client))
            {
                ctx.AttemptGate?.Release(ctx, attemptId);
                ctx.Deny($"no adapter registered for '{target.AdapterKey}:{target.ModelId}'", code: TraceReasons.NoClient); // fail-closed
                return;
            }

            var attempt = new AttemptRecord
            {
                AttemptId = attemptId,
                ConnectionId = target.AdapterKey,
                ModelId = target.ModelId,
                ReservedUsd = ctx.AttemptGate is null ? null : ctx.ActiveReservationUsd,
            };
            ctx.Attempts.Add(attempt);
            var (end, errorClass, message) = await AttemptAsync(ctx, client, target, attempt, messages, deadline, ct);

            if (end == AttemptEnd.Served)
            {
                await next(ctx, ct);
                SettleServed(ctx, attempt);
                return;
            }
            if (end == AttemptEnd.ClientGone)
            {
                // Writing to the CLIENT failed (it disconnected): an incomplete request, not a provider failure.
                ctx.Deny("client disconnected during streaming", DenialKind.Cancelled, code: TraceReasons.ClientDisconnected);
                return;
            }

            bool emitted = ctx.PartialOutputChars > 0;
            bool moreCandidates = i + 1 < plan.Candidates.Count && i + 1 < plan.MaxAttempts;
            bool withinDeadline = plan.DeadlineMs is not { } ms || (elapsed.ElapsedMilliseconds < ms && !(deadline?.IsCancellationRequested ?? false));
            bool retryable = errorClass is not null && plan.RetryOn.Contains(errorClass);
            if (emitted || !moreCandidates || !withinDeadline || !retryable)
            {
                // Terminal failure: content already emitted (never continued by a second provider), no approved
                // fallback left, deadline spent, or a failure class the policy does not retry.
                ctx.Deny(message, DenialKind.UpstreamFailure, code: errorClass);
                return;
            }
            ctx.PartialOutputChars = 0;   // nothing was emitted; the next attempt starts clean
        }
    }

    /// <summary>One provider attempt. Caller cancellation is rethrown after the attempt is recorded and settled.</summary>
    private async Task<(AttemptEnd End, string? ErrorClass, string Message)> AttemptAsync(
        InferenceContext ctx, MEAI.IChatClient client, RouteTarget target, AttemptRecord attempt,
        List<MEAI.ChatMessage> messages, CancellationTokenSource? deadline, CancellationToken ct)
    {
        var targetLabel = $"{target.AdapterKey}/{target.ModelId}";
        var callToken = deadline?.Token ?? ct;
        ctx.AttemptedModel = target.ModelId;   // a call is now actually started (not merely selected)
        ctx.Trace(Stage, TraceStates.Running, attemptId: attempt.AttemptId, target: targetLabel);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var options = new MEAI.ChatOptions { ModelId = target.ModelId, MaxOutputTokens = RequestBounds.OutputTokenBound(ctx, target) };

        try
        {
            // ADR-0002: streaming is an emission mode, not a different pipeline. The stream is consumed
            // HERE so attribution, budget, and audit run on the normal unwind with real usage.
            if (ctx.Request.Stream && ctx.DeltaSink is { } sink)
            {
                var text = new StringBuilder();
                MEAI.UsageDetails? usageDetails = null;
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, callToken))
                {
                    foreach (var uc in update.Contents.OfType<MEAI.UsageContent>())
                        usageDetails = uc.Details;

                    if (update.Text is { Length: > 0 } fragment)
                    {
                        if (text.Length == 0) deadline?.CancelAfter(Timeout.Infinite);   // content flowing: deadline no longer applies
                        text.Append(fragment);
                        ctx.PartialOutputChars = text.Length;
                        try
                        {
                            await sink(new ChatDelta(fragment), ct);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            End(ctx, attempt, watch, TraceStates.Cancelled, TraceReasons.ClientDisconnected, targetLabel);
                            SettleIncomplete(ctx, attempt);
                            return (AttemptEnd.ClientGone, TraceReasons.ClientDisconnected, "client disconnected during streaming");
                        }
                    }
                }

                health?.RecordSuccess(target.AdapterKey, target.ModelId);
                ctx.ServedTarget = target;
                ctx.Response = new InferenceResponse(
                    Message: new ChatMessage(ChatRole.Assistant, text.ToString()),
                    Usage: new Usage(usageDetails?.InputTokenCount ?? 0, usageDetails?.OutputTokenCount ?? 0, 0m),
                    ServedByModel: target.ModelId,
                    UsageReported: usageDetails?.InputTokenCount is not null && usageDetails.OutputTokenCount is not null);
            }
            else
            {
                var response = await client.GetResponseAsync(messages, options, callToken);
                health?.RecordSuccess(target.AdapterKey, target.ModelId);
                ctx.ServedTarget = target;
                ctx.Response = new InferenceResponse(
                    Message: new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty),
                    Usage: new Usage(
                        InputTokens: response.Usage?.InputTokenCount ?? 0,
                        OutputTokens: response.Usage?.OutputTokenCount ?? 0,
                        CostUsd: 0m), // cost is computed by the attribution stage
                    ServedByModel: target.ModelId,
                    UsageReported: response.Usage?.InputTokenCount is not null && response.Usage.OutputTokenCount is not null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — not a provider failure (a provider-side timeout is, below). Possibly billed: settled.
            End(ctx, attempt, watch, TraceStates.Cancelled, TraceReasons.ClientCancelled, targetLabel);
            SettleIncomplete(ctx, attempt);
            throw;
        }
        catch (Exception ex)
        {
            // Adapter contract: error mapping (src/CLAUDE.md). An upstream failure is a governed, audited outcome —
            // never an unhandled exception escaping the pipeline as a 500. The deadline firing is a timeout.
            var errorClass = deadline is { IsCancellationRequested: true } && !ct.IsCancellationRequested
                ? TraceReasons.Timeout
                : _classifier.Classify(ex);
            var verb = ctx.Request.Stream && ctx.DeltaSink is not null ? "stream failed" : "call failed";
            var message = $"provider '{target.AdapterKey}' {verb}: {Bound(ex.Message)}";
            health?.RecordFailure(target.AdapterKey, target.ModelId, Bound(ex.Message));
            End(ctx, attempt, watch, TraceStates.Failed, errorClass, targetLabel);
            SettleIncomplete(ctx, attempt);
            return (AttemptEnd.Failed, errorClass, message);
        }

        attempt.Outcome = "served";
        attempt.DurationMs = watch.Elapsed.TotalMilliseconds;
        ctx.Trace(Stage, TraceStates.Passed, attempt.DurationMs, attemptId: attempt.AttemptId, target: targetLabel);
        return (AttemptEnd.Served, null, "served");
    }

    private static void End(InferenceContext ctx, AttemptRecord attempt, System.Diagnostics.Stopwatch watch, string state, string errorClass, string target)
    {
        attempt.Outcome = state == TraceStates.Cancelled ? "cancelled" : "failed";
        attempt.ErrorClass = errorClass;
        attempt.DurationMs = watch.Elapsed.TotalMilliseconds;
        ctx.Trace(Stage, state, attempt.DurationMs, errorClass, attempt.AttemptId, target);
    }

    /// <summary>A started attempt that produced no complete response may still be billed (ADR-0009/0014): settled at the
    /// conservative reservation-bound estimate — never $0.</summary>
    private static void SettleIncomplete(InferenceContext ctx, AttemptRecord attempt)
    {
        if (ctx.AttemptGate is not { } gate) return;
        var charge = gate.Settle(ctx, attempt.AttemptId, null);
        attempt.CostUsd = charge.CostUsd;
        attempt.CostBasis = charge.CostBasis;
        attempt.Overrun = charge.Overrun;
        ctx.CostBasis = charge.CostBasis;
    }

    /// <summary>The served attempt is settled with the attributed (priced) cost after the attribution stage ran.</summary>
    private static void SettleServed(InferenceContext ctx, AttemptRecord attempt)
    {
        decimal? cost = ctx.Attribution?.Usage.CostUsd;
        attempt.CostBasis = ctx.CostBasis;
        if (ctx.AttemptGate is not { } gate)
        {
            attempt.CostUsd = cost;
            return;
        }
        var charge = gate.Settle(ctx, attempt.AttemptId, cost);
        attempt.CostUsd = charge.CostUsd;
        attempt.CostBasis ??= charge.CostBasis;
        attempt.Overrun = charge.Overrun;
    }

    /// <summary>Provider error text lands in evidence: bounded, single-line.</summary>
    private static string Bound(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "…";
    }

    private static MEAI.ChatMessage ToMeai(ChatMessage m) => new(ToMeaiRole(m.Role), m.Content);

    private static MEAI.ChatRole ToMeaiRole(ChatRole role) => role switch
    {
        ChatRole.System => MEAI.ChatRole.System,
        ChatRole.User => MEAI.ChatRole.User,
        ChatRole.Assistant => MEAI.ChatRole.Assistant,
        ChatRole.Tool => MEAI.ChatRole.Tool,
        _ => MEAI.ChatRole.User
    };
}
