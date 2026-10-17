namespace Covenant.Core;

/// <summary>Why a request was refused. Callers see different HTTP statuses (401 vs 403 vs 502);
/// the audit trail records all of them as denials.</summary>
public enum DenialKind { None, Governance, UpstreamFailure, Unauthenticated, RateLimited, Unsupported, Cancelled }

/// <summary>Mutable pipeline state; exactly one terminal outcome: Response (served) or DenialReason
/// (refused). Default Classification is the most restrictive — fail-closed until classify runs.</summary>
public sealed class InferenceContext(InferenceRequest request)
{
    public InferenceRequest Request { get; } = request;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Resolved caller identity. Starts as the request's self-declared values; the auth
    /// stage overwrites it on a valid key. Downstream stages read this, never the raw request.</summary>
    public CallerIdentity Identity { get; set; } = new(request.Principal, request.Attribution);

    /// <summary>Written by the classify stage. Defaults to the most restrictive class (fail-closed).</summary>
    public DataClassification Classification { get; set; } = DataClassification.Phi;

    /// <summary>What the classifier matched (e.g. "US SSN pattern") — metadata for evidence, never content.</summary>
    public string? ClassificationSignal { get; set; }

    /// <summary>Written by the policy stage.</summary>
    public PolicyOutcome? Policy { get; set; }

    /// <summary>Written by the provider-call stage on success.</summary>
    public InferenceResponse? Response { get; set; }

    /// <summary>Installed by the Host for streamed requests (ADR-0002): the provider stage forwards
    /// deltas here; never invoked for a request denied pre-flight. Null = buffered mode.</summary>
    public Func<ChatDelta, CancellationToken, ValueTask>? DeltaSink { get; set; }

    /// <summary>Written by the attribution stage.</summary>
    public AttributionRecord? Attribution { get; set; }

    /// <summary>Set when a stage refuses the request. Mutually exclusive with a successful Response.</summary>
    public string? DenialReason { get; private set; }

    public DenialKind DenialKind { get; private set; } = DenialKind.None;

    /// <summary>Set by the cache stage on a hit: served without any provider call, cost $0.</summary>
    public bool ServedFromCache { get; set; }

    public bool IsDenied => DenialReason is not null;

    // ---- Execution facts for evidence (ADR-0009). Observed, never inferred from reason strings. ----

    /// <summary>Stage currently executing; set by the pipeline as each stage is entered.</summary>
    public string? CurrentStage { get; set; }

    /// <summary>Stage that refused the request (null when it completed).</summary>
    public string? StoppedAtStage { get; private set; }

    /// <summary>True once the classify stage ran. When false, Classification is the restrictive
    /// enforcement default and must be presented as "not evaluated", never as a detection.</summary>
    public bool ClassificationEvaluated { get; set; }

    /// <summary>Set immediately before a provider call is started (not merely selected by policy).</summary>
    public string? AttemptedModel { get; set; }

    /// <summary>Characters of assistant output already emitted before a stream failed or was cancelled.</summary>
    public int PartialOutputChars { get; set; }

    /// <summary>How the request's cost was determined (ADR-0009): reported | zero_priced |
    /// estimated_from_text | estimated_failed_call | unknown_failed_call | unpriced | no_call.</summary>
    public string? CostBasis { get; set; }

    /// <summary>Charge recorded for a provider call that was attempted but produced no response
    /// (failure/cancellation): estimated, never a silent zero.</summary>
    public Usage? FailedCallUsage { get; set; }

    /// <summary>Refuse the request. First reason wins; later calls are ignored. The stopping stage is
    /// the one executing, unless the caller names a finer one (e.g. "pause" inside the budget stage).</summary>
    public void Deny(string reason, DenialKind kind = DenialKind.Governance, string? stage = null)
    {
        if (DenialReason is not null) return;
        DenialReason = reason;
        DenialKind = kind;
        StoppedAtStage = stage ?? CurrentStage;
    }
}
