using System.Text;

namespace Covenant.Core;

// Execution facts of one request (ADR-0013/0014): canonical trace events, provider attempts, the attempt gate
// (pause re-check + budget reservation before EACH attempt) and the enforced token bounds. Pure Core types — the
// Governance budget gate and the Host trace hub implement/consume them; Core depends on nothing.

/// <summary>Canonical trace states (ADR-0013).</summary>
public static class TraceStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Passed = "passed";
    public const string Refused = "refused";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";

    public static bool IsTerminal(string state) => state is not (Pending or Running);
}

/// <summary>Bounded reason codes carried by trace events and attempt evidence. Never provider free text, never
/// content: a fixed vocabulary only.</summary>
public static class TraceReasons
{
    public const string NotReached = "not_reached";
    public const string CacheHit = "cache_hit";
    public const string Paused = "paused";
    public const string BudgetExceeded = "budget_exceeded";
    public const string ClientCancelled = "client_cancelled";
    public const string ClientDisconnected = "client_disconnected";
    public const string NoClient = "no_client";
    public const string Error = "error";

    // Provider failure classes (IProviderFailureClassifier, ADR-0013) — also the policy retry.on vocabulary.
    public const string Connect = "connect";
    public const string Timeout = "timeout";
    public const string RateLimited429 = "429";
    public const string ServerError5xx = "5xx";
    public const string NonRetryable = "non_retryable";

    /// <summary>Default code of a denial when the denying stage did not name a finer one.</summary>
    public static string ForDenial(DenialKind kind) => kind switch
    {
        DenialKind.Unauthenticated => "unauthenticated",
        DenialKind.RateLimited => "rate_limited",
        DenialKind.Unsupported => "unsupported",
        DenialKind.Cancelled => ClientCancelled,
        DenialKind.UpstreamFailure => "upstream_failure",
        _ => "governance_denied",
    };
}

/// <summary>One canonical stage/attempt event (ADR-0013) as emitted by the pipeline itself: server UTC timestamp,
/// Stopwatch-measured duration on terminal states, bounded reason code. Sequence numbers are assigned by the
/// consumer (the Host trace hub) in emission order.</summary>
public sealed record PipelineTraceEvent(
    string Stage,
    string State,
    DateTimeOffset TsUtc,
    double? DurationMs = null,
    string? ReasonCode = null,
    string? AttemptId = null,
    string? Target = null);

/// <summary>Mutable record of one provider attempt while the request runs; the audit stage freezes it into an
/// <see cref="AuditAttempt"/>. Created immediately before the provider call starts (Started = true).</summary>
public sealed class AttemptRecord
{
    public required string AttemptId { get; init; }
    public required string ConnectionId { get; init; }
    public required string ModelId { get; init; }
    /// <summary>running | served | failed | cancelled.</summary>
    public string Outcome { get; set; } = TraceStates.Running;
    public string? ErrorClass { get; set; }
    public double DurationMs { get; set; }
    public decimal? CostUsd { get; set; }
    public string? CostBasis { get; set; }
    public decimal? ReservedUsd { get; set; }
    public bool? Overrun { get; set; }

    public AuditAttempt ToAudit() => new(AttemptId, ConnectionId, ModelId, Outcome, ErrorClass, DurationMs, CostUsd, CostBasis, ReservedUsd, Overrun);
}

/// <summary>Result of asking the gate whether an attempt may start.</summary>
public sealed record AttemptAdmission(bool Admitted, decimal ReservedUsd = 0m, string? Reason = null, string? Stage = null, string? Code = null)
{
    public static AttemptAdmission Ok(decimal reservedUsd) => new(true, reservedUsd);
    public static AttemptAdmission Refused(string reason, string stage, string code) => new(false, 0m, reason, stage, code);
}

/// <summary>What one attempt was charged. Settled = false when this call was a duplicate (already settled — ignored).</summary>
public readonly record struct AttemptCharge(decimal CostUsd, string CostBasis, bool Overrun, bool Settled);

/// <summary>Per-request attempt gate (ADR-0013/0014), installed by the budget stage on configured requests. The provider
/// stage calls <see cref="Admit"/> before EVERY attempt (the first attempt was already reserved by the budget stage;
/// later attempts re-check the global pause and reserve), <see cref="Settle"/> exactly once per started attempt and
/// <see cref="Release"/> for an admitted attempt that never started. Implementations are idempotent per attempt id.</summary>
public interface IAttemptGate
{
    AttemptAdmission Admit(InferenceContext context, RouteTarget target, string attemptId);

    /// <summary>Settles a started attempt. <paramref name="costUsd"/> = the priced reported usage; null ⇒ the conservative
    /// reservation-bound estimate (failed, cancelled or unreported). A reported cost above the reservation is settled in
    /// full and flagged as an overrun.</summary>
    AttemptCharge Settle(InferenceContext context, string attemptId, decimal? costUsd);

    void Release(InferenceContext context, string attemptId);
}

/// <summary>Enforced token bounds (ADR-0014). The input bound is the UTF-8 byte count of every message's content plus
/// 16 per message (byte-level tokenizers never emit more tokens than bytes; chars/4 is an average and is never used for
/// enforcement). The output bound is min(request max_tokens, application MaxOutputTokens, model MaxOutputTokens); it is
/// SENT to the provider (ChatOptions.MaxOutputTokens) so the bound is real.</summary>
public static class RequestBounds
{
    public const int DefaultOutputTokens = 1024;
    public const int PerMessageOverheadTokens = 16;

    public static long InputTokenBound(IReadOnlyList<ChatMessage> messages)
    {
        long total = 0;
        foreach (var m in messages) total += Encoding.UTF8.GetByteCount(m.Content) + PerMessageOverheadTokens;
        return total;
    }

    /// <summary>Without a bound configuration (pure pipeline compositions) the request's own max_tokens is passed
    /// through unchanged (pre-v2 behaviour).</summary>
    public static int? OutputTokenBound(InferenceContext ctx, RouteTarget target)
    {
        if (ctx.Configuration is not { } cfg) return ctx.Request.MaxOutputTokens;
        int bound = ctx.ApplicationId is { } appId && cfg.FindApplication(appId) is { } app ? app.MaxOutputTokens : DefaultOutputTokens;
        if (ctx.Request.MaxOutputTokens is { } requested) bound = Math.Min(bound, requested);
        if (cfg.FindModel(target.AdapterKey, target.ModelId)?.MaxOutputTokens is { } modelMax) bound = Math.Min(bound, modelMax);
        return Math.Max(1, bound);
    }
}
