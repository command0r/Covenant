using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Console rules for the ADR-0016 sanitized structured-record route (pure). The Host decides and re-validates at
/// execution; these rules only make the demo honest: on a dedicated sanitized workload the operator SEES the exact payload
/// that would leave (placeholders only) and confirms it before Run. A preview that would refuse may still be run — the Host
/// refuses again, nothing leaves, and the refusal is audited.</summary>
public static class SanitizedRoute
{
    public static readonly IReadOnlyList<(string Value, string Label)> IdentifierTypes =
        [("email", "E-mail address"), ("phone", "Phone number"), ("us_ssn", "US SSN (required — the detector flags it)")];

    /// <summary>Task ids of covenant.record.v1 (the Host's GET /admin/redaction/schema is authoritative for wording).</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Tasks =
        [("triage_case", "Triage the case"), ("reply_outline", "Outline a reply"), ("compliance_checklist", "Closing checklist")];

    /// <summary>The profile is named by an enabled policy carrying a redaction contract.</summary>
    public static bool IsWorkload(CovenantConfigDto? cfg, string? profileId)
        => cfg is not null && profileId is { Length: > 0 }
           && cfg.Policies.Any(p => p.Enabled && p.Redaction is not null && p.Profiles.Contains(profileId, StringComparer.Ordinal));

    /// <summary>Why Run is blocked on a sanitized workload; null = may run.</summary>
    public static string? RunProblem(bool isWorkload, RoutePreview? preview, PlaygroundForm form, string? confirmedKey)
    {
        if (!isWorkload) return null;
        if (preview is null || preview.Loading || !preview.IsCurrent(form))
            return "Sanitized workload: preview the route first — you will see the exact payload before anything leaves.";
        if (preview.Result?.Redaction is { Outcome: "applied" } && !string.Equals(confirmedKey, preview.Key, StringComparison.Ordinal))
            return "Review the sanitized payload and confirm it before sending it to an external model.";
        return null;
    }

    /// <summary>Human wording for the Host's bounded refusal codes (the Host's reason text follows it).</summary>
    public static string RefusalTitle(string? code) => code switch
    {
        "redaction_unsupported_shape" => "Not a structured record",
        "redaction_unknown_field" => "Field not supported (free text is never accepted)",
        "redaction_invalid_value" => "A field value is not allowed",
        "redaction_not_allowed" => "Task or identifier not allowed by this policy",
        "redaction_incomplete" => "Sensitive data would remain",
        "redaction_not_covered" => "Only PII is covered (never PHI)",
        _ => "Sanitized route refused",
    };

    public const string Limits =
        "Restricted structured-data demonstration — not anonymisation. Only the supported fields are accepted; identifiers are "
        + "replaced by placeholders; the classification stays PII; web search is not available on this route.";
}
