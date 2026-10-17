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

    // ---- Configuration binding & identity (ADR-0011/0012). Additive; null/defaults when no snapshot. ----

    /// <summary>The configuration version this request is bound to. Set once by the Host at entry (acquired
    /// snapshot) and never replaced mid-request. Null ⇒ stages use their constructor-injected legacy config.</summary>
    public IRequestConfiguration? Configuration { get; set; }

    /// <summary>Version of <see cref="Configuration"/> (evidence: audit ConfigVersion).</summary>
    public int? ConfigVersion { get; set; }

    /// <summary>"v1" (application key on /v1) or "console" (admin-authenticated console inference).</summary>
    public string EntryPoint { get; private set; } = "v1";

    /// <summary>Who operated the console (honest: "shared-admin-token" — one shared credential cannot
    /// attribute an individual). Null on /v1.</summary>
    public string? OperatorRef { get; private set; }

    /// <summary>Console entry point only (ADR-0012): the application identity the Host resolved server-side after
    /// admin authentication. Never part of the wire-derived <see cref="InferenceRequest"/>; null on /v1, where a key
    /// is always required.</summary>
    public PreAuthenticatedIdentity? PreAuthenticated { get; private set; }

    /// <summary>Server-side only: called by the Host's console endpoint after it authenticated the admin session
    /// and resolved the application. /v1 ingress never calls it. Once per context.</summary>
    public void BindConsoleIdentity(PreAuthenticatedIdentity identity, string operatorRef)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (PreAuthenticated is not null) throw new InvalidOperationException("console identity is already bound");
        PreAuthenticated = identity;
        EntryPoint = "console";
        OperatorRef = operatorRef;
    }

    /// <summary>Resolved application (written by the auth stage when the configuration knows it).</summary>
    public string? ApplicationId { get; set; }

    /// <summary>Resolved workload profile (requested-and-granted, application default, or legacy-internal).</summary>
    public string? ProfileId { get; set; }

    /// <summary>Trusted baseline classification from the profile. Detection may escalate above it, never below.</summary>
    public DataClassification? BaselineClassification { get; set; }

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
