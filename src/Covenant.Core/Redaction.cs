namespace Covenant.Core;

// ADR-0016 constrained, opt-in sanitized-payload egress. Pure Core types: the redact stage (Governance) fills the state,
// the attempt gate re-verifies it, the cache keys on it and the audit stage records it. No original value is ever held
// here — only the transformed messages, counts and decisions.

/// <summary>Supported structured identifier types (ADR-0016). Anything else is not detected and not replaced.</summary>
public static class RedactionTypes
{
    public const string Email = "email";
    public const string Phone = "phone";
    public const string UsSsn = "us_ssn";

    public static readonly IReadOnlyList<string> Supported = [Email, Phone, UsSsn];

    /// <summary>Types the detector escalates to PII today: a sanitized-payload policy must replace all of them.</summary>
    public static readonly IReadOnlyList<string> RequiredForPii = [UsSsn];

    /// <summary>Transform identity recorded in evidence and in the cache partition.</summary>
    public const string Transform = "redact-v1";
}

/// <summary>Bounded reason codes of the redact stage and the per-attempt egress guard (ADR-0016).</summary>
public static class RedactionReasons
{
    public const string NotRequired = "redaction_not_required";
    public const string Applied = "redaction_applied";
    /// <summary>Not exactly one user message holding one covenant.record.v1 JSON object (prose, extra turns, bad JSON).</summary>
    public const string UnsupportedShape = "redaction_unsupported_shape";
    /// <summary>A field the schema does not define (no free-text fields exist).</summary>
    public const string UnknownField = "redaction_unknown_field";
    /// <summary>A field value of the wrong type, outside its enumeration/range, or a malformed identifier.</summary>
    public const string InvalidValue = "redaction_invalid_value";
    /// <summary>The task is not a schema task or not allowed by the policy; or an identifier type the policy does not allow.</summary>
    public const string NotAllowed = "redaction_not_allowed";
    /// <summary>The transformed payload still triggers a sensitive signal (unsupported type, PHI, …).</summary>
    public const string Incomplete = "redaction_incomplete";
    /// <summary>The request is not exactly PII (PHI is never covered).</summary>
    public const string NotCovered = "redaction_not_covered";
    /// <summary>A cloud attempt for a PII/PHI request without the verified sanitized payload.</summary>
    public const string SensitiveEgress = "sensitive_egress";
}

/// <summary>What the redact stage decided for one request. <see cref="Messages"/> is the transformed canonical message set
/// (null when refused): the ONLY messages any attempt of this request may receive.</summary>
public sealed class RedactionState
{
    public required string PolicyId { get; init; }
    public required DataClassification OriginalClassification { get; init; }
    public string Outcome { get; set; } = "refused";
    public string? ReasonCode { get; set; }
    public DataClassification? TransformedClassification { get; set; }
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<ChatMessage>? Messages { get; set; }

    public bool Applied => Outcome == "applied" && Messages is not null;

    public AuditRedaction ToAudit() => new(
        RedactionTypes.Transform,
        PolicyId,
        Outcome,
        ReasonCode,
        Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new AuditRedactionCount(kv.Key, kv.Value)).ToList(),
        OriginalClassification,
        TransformedClassification);
}
