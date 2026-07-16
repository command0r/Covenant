namespace Covenant.Core;

public enum PolicyEffect { Deny, Allow }

/// <summary>A concrete routing target chosen by policy. AdapterKey resolves to an IChatClient in the adapter layer.</summary>
public sealed record RouteTarget(string AdapterKey, string ModelId);

/// <summary>Outcome of policy evaluation. On Allow, Route is non-null; on Deny, Reason explains why.</summary>
public sealed record PolicyOutcome(PolicyEffect Effect, string Reason, RouteTarget? Route)
{
    public static PolicyOutcome Deny(string reason) => new(PolicyEffect.Deny, reason, null);
    public static PolicyOutcome Allow(RouteTarget route) => new(PolicyEffect.Allow, "allowed", route);
}

/// <summary>Cost attributed to a caller's tags for one inference.</summary>
public sealed record AttributionRecord(AttributionTags Tags, string Model, Usage Usage);

/// <summary>
/// The content of one audit entry. The tamper-evidence chain (previous/entry hash) is added by the
/// durable sink as it writes, so the chain is a property of the log, not of this stage's output.
/// Effect/Reason record the FINAL outcome of the request — a request the policy stage allowed but a
/// later stage refused (or that errored) is evidence of a denial, not of an allow.
/// </summary>
public sealed record AuditEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    string Principal,
    AttributionTags Tags,
    DataClassification Classification,
    PolicyEffect Effect,
    string Reason,
    string? ServedByModel,
    Usage Usage);
