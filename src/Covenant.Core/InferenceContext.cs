namespace Covenant.Core;

/// <summary>Why a request was refused: a governance decision, or an upstream failure handled fail-closed.
/// Callers see different HTTP statuses (403 vs 502); the audit trail records both as denials.</summary>
public enum DenialKind { None, Governance, UpstreamFailure }

/// <summary>
/// Mutable state carried through the pipeline. Stages read the request and earlier decisions and write
/// their own. Exactly one terminal outcome is produced: a Response (served) or a DenialReason (refused).
/// Default Classification is the most restrictive value — fail-closed until the classify stage runs.
/// </summary>
public sealed class InferenceContext(InferenceRequest request)
{
    public InferenceRequest Request { get; } = request;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Written by the classify stage. Defaults to the most restrictive class (fail-closed).</summary>
    public DataClassification Classification { get; set; } = DataClassification.Phi;

    /// <summary>Written by the policy stage.</summary>
    public PolicyOutcome? Policy { get; set; }

    /// <summary>Written by the provider-call stage on success.</summary>
    public InferenceResponse? Response { get; set; }

    /// <summary>Written by the attribution stage.</summary>
    public AttributionRecord? Attribution { get; set; }

    /// <summary>Set when a stage refuses the request. Mutually exclusive with a successful Response.</summary>
    public string? DenialReason { get; private set; }

    public DenialKind DenialKind { get; private set; } = DenialKind.None;

    public bool IsDenied => DenialReason is not null;

    /// <summary>Refuse the request. First reason wins; later calls are ignored.</summary>
    public void Deny(string reason, DenialKind kind = DenialKind.Governance)
    {
        if (DenialReason is not null) return;
        DenialReason = reason;
        DenialKind = kind;
    }
}
