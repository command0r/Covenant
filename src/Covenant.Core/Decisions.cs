namespace Covenant.Core;

public enum PolicyEffect { Deny, Allow }

/// <summary>A concrete routing target chosen by policy. AdapterKey resolves to an IChatClient in the adapter layer.</summary>
public sealed record RouteTarget(string AdapterKey, string ModelId);

/// <summary>Outcome of policy evaluation. On Allow, Route is non-null; on Deny, Reason explains why.</summary>
public sealed record PolicyOutcome(PolicyEffect Effect, string Reason, RouteTarget? Route)
{
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
    string? PromptPreview = null);
