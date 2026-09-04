namespace Covenant.Core;

/// <summary>Why a request was refused. Callers see different HTTP statuses (401 vs 403 vs 502);
/// the audit trail records all of them as denials.</summary>
public enum DenialKind { None, Governance, UpstreamFailure, Unauthenticated, RateLimited }

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

    /// <summary>Refuse the request. First reason wins; later calls are ignored.</summary>
    public void Deny(string reason, DenialKind kind = DenialKind.Governance)
    {
        if (DenialReason is not null) return;
        DenialReason = reason;
        DenialKind = kind;
    }
}
