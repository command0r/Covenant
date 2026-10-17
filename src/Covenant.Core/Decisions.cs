namespace Covenant.Core;

public enum PolicyEffect { Deny, Allow }

/// <summary>A concrete routing target chosen by policy. AdapterKey resolves to an IChatClient in the adapter layer.</summary>
public sealed record RouteTarget(string AdapterKey, string ModelId);

/// <summary>A policy target that was NOT eligible for this request, and why (ADR-0013 eligibility rules).</summary>
public sealed record ExcludedTarget(RouteTarget Target, string Reason);

/// <summary>Outcome of policy evaluation. On Allow, Route is non-null; on Deny, Reason explains why.
/// v2 (ADR-0013) adds — additively — the matched policy, the ordered eligible candidates (Route first, then
/// approved fallbacks), the excluded targets with reasons, and a human explanation. Legacy evaluation leaves
/// them at their defaults.</summary>
public sealed record PolicyOutcome(PolicyEffect Effect, string Reason, RouteTarget? Route)
{
    /// <summary>Stable id of the policy that decided (null for legacy evaluation or no match).</summary>
    public string? PolicyId { get; init; }

    /// <summary>Ordered eligible targets: [primary, fallback…]. Empty on Deny and for legacy evaluation.</summary>
    public IReadOnlyList<RouteTarget> Candidates { get; init; } = [];

    /// <summary>Targets the policy named that were ineligible, with the reason.</summary>
    public IReadOnlyList<ExcludedTarget> Excluded { get; init; } = [];

    /// <summary>e.g. "Public → openai/gpt-4o-mini (fallback: ollama/qwen3:8b) · policy public-demo v7".</summary>
    public string? Explanation { get; init; }

    public static PolicyOutcome Deny(string reason) => new(PolicyEffect.Deny, reason, null);
    public static PolicyOutcome Allow(RouteTarget route) => new(PolicyEffect.Allow, "allowed", route);
    public static PolicyOutcome Allow(RouteTarget route, string reason) => new(PolicyEffect.Allow, reason, route);
}

/// <summary>Cost attributed to a caller's tags for one inference.</summary>
public sealed record AttributionRecord(AttributionTags Tags, string Model, Usage Usage);

/// <summary>Content of one audit entry; the tamper-evidence chain is added by the sink as it writes.
/// Effect/Reason record the FINAL outcome — allowed-then-refused (or errored) is evidence of a denial.</summary>
public sealed record AuditEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    string Principal,
    AttributionTags Tags,
    DataClassification Classification,
    PolicyEffect Effect,
    string Reason,
    string? ServedByModel,
    Usage Usage,
    double DurationMs = 0,
    int PromptChars = 0,
    string? PromptSha256 = null,
    string? Signal = null,
    string? PromptPreview = null,
    bool CacheHit = false,
    // ---- v2 (ADR-0009): additive, nullable. Absent on legacy lines → "Not recorded", never a guess.
    // The chain hashes stored bytes, so legacy lines verify unchanged; new lines carry the new fields.
    int? SchemaVersion = null,
    string? CorrelationId = null,
    bool? ClassificationEvaluated = null,
    string? DenialKind = null,
    string? StoppedAtStage = null,
    string? SelectedModel = null,
    string? AttemptedModel = null,
    bool? Stream = null,
    bool? UsageReported = null,
    string? CostBasis = null,
    // ---- v3 (ADR-0011..0013): additive, nullable. EventType null/"request" = an inference record; any other
    // value ("config.activated", …) is a control-plane event, excluded from request counts, spend and FinOps.
    string? EventType = null,
    int? ConfigVersion = null,
    string? EntryPoint = null,
    string? OperatorRef = null,
    string? ApplicationId = null,
    string? ProfileId = null,
    DataClassification? BaselineClassification = null);

/// <summary>Not a property on AuditEntry: a computed property would be serialized into every audit line.</summary>
public static class AuditEntryKinds
{
    public const string Request = "request";
    public const string ConfigActivated = "config.activated";

    /// <summary>True for inference records (legacy lines have no EventType ⇒ request).</summary>
    public static bool IsRequest(this AuditEntry entry) => entry.EventType is null or Request;
}
