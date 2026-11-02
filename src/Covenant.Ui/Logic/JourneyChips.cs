using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>One semantic chip of the request journey. Tone: ok | refused | sensitive | local | external | neutral. Every chip
/// carries text (and the component adds an icon) — colour is never the only signal.</summary>
public sealed record JourneyChip(string Label, string Tone, string? Icon = null, string? Title = null);

/// <summary>The request journey in four separate facts (demo journey §1): data classification (Public / Internal / Sensitive)
/// and WHY (detected vs workload label), the workload, the actual destination (Local / External) and what happened to the
/// data (sanitized, retrieved externally, cached). Plus the outcome and the cost split. Pure, from the audit record.</summary>
public static class JourneyChips
{
    /// <summary>"Public" | "Internal" | "Sensitive · PII" | "Sensitive · PHI".</summary>
    public static string DataLabel(string? classification) => PolicyText.IsSensitive(classification)
        ? "Sensitive · " + PolicyText.ClassificationLabel(classification)
        : PolicyText.ClassificationLabel(classification);

    public static IReadOnlyList<JourneyChip> For(AuditEntryView r, IReadOnlyList<ConnectionDto>? connections, IReadOnlyList<WorkloadProfileDto>? profiles)
    {
        var chips = new List<JourneyChip>();
        bool served = string.Equals(r.Effect, "Allow", StringComparison.Ordinal);
        // ADR-0018: when inspection could not complete, the recorded class is only the restrictive default — never shown as
        // a detection.
        bool notInspected = r.ClassificationEvaluated == false;
        bool sensitive = !notInspected && PolicyText.IsSensitive(r.Classification);
        if (notInspected && !served && r.StoppedAtStage == "classify")
        {
            chips.Add(new("Refused · inspection incomplete", "refused", "cross", r.Reason));
            chips.Add(new("Classification not evaluated — nothing was sent", "neutral", null, "Inspection could not complete within its bounds, so the request was refused before any provider or tool."));
            return chips;
        }

        // Outcome — red only when sensitive data was blocked. A governance block and a provider outage are different facts.
        if (served)
        {
            var servedDest = ModelFacts.Destination(r, connections);
            bool detected = r.Signal is { Length: > 0 } || !string.Equals(r.Classification, r.BaselineClassification, StringComparison.OrdinalIgnoreCase);
            chips.Add(sensitive && detected && servedDest.Boundary == "Local" && !r.CacheHit
                ? new("Served locally · sensitive data detected", "local", "check", "Detection raised the classification; the policy kept the request inside the perimeter.")
                : new("Served", "ok", "check"));
        }
        else if (r.DenialKind == "UpstreamFailure")
            chips.Add(new("Provider unavailable", "refused", "cross", r.Reason));
        else if (r.DenialKind == "Cancelled")
            chips.Add(new("Cancelled", "refused", "cross", r.Reason));
        else if (sensitive && r.StoppedAtStage is "policy" or "redact")
            chips.Add(new("Blocked · sensitive data cannot go external", "sensitive", "cross", r.Reason));
        else
        {
            var where = r.StoppedAtStage is { Length: > 0 } st ? " at " + TraceModel.Label(st) : "";
            chips.Add(new("Refused" + where, sensitive || r.Redaction is { Outcome: "refused" } ? "sensitive" : "refused", "cross", r.Reason));
        }

        // Data classification and why: detection is a content fact; the baseline is a workload label.
        bool escalated = !string.Equals(r.Classification, r.BaselineClassification, StringComparison.OrdinalIgnoreCase);
        var why = r.Signal is { Length: > 0 } sig ? "detected: " + sig
            : escalated || r.BaselineClassification is null ? null : "workload label";
        chips.Add(new(DataLabel(r.Classification) + (why is null ? "" : " (" + why + ")"), sensitive ? "sensitive" : "neutral",
            sensitive ? "warn" : null, "Data classification: Public permits external processing, it does not require it."));

        // Workload (the declared task).
        if (r.ProfileId is { Length: > 0 } pid)
        {
            var p = profiles?.FirstOrDefault(x => string.Equals(x.Id, pid, StringComparison.Ordinal));
            chips.Add(new("Workload · " + (p?.Name ?? pid), "neutral", null, "The declared kind of work; it selects the policy."));
        }

        // Actual destination.
        var dest = ModelFacts.Destination(r, connections);
        if (r.CacheHit) chips.Add(new("Cache hit · no provider call", "ok", "history", dest.Text));
        else if (dest.Boundary is "Local") chips.Add(new("Local · " + dest.Text, "local", "local", "Ran inside the perimeter."));
        else if (dest.Boundary is "Cloud") chips.Add(new("External · " + dest.Text, "external", "cloud", "Sent to an approved external provider."));
        else if (dest.Text != ModelFacts.NotRecorded) chips.Add(new(dest.Text, "neutral"));

        // What happened to the data.
        if (r.Redaction is { } red)
        {
            int n = red.Counts.Sum(c => c.Count);
            chips.Add(red.Outcome == "applied"
                ? new("Sanitized payload · " + n + (n == 1 ? " identifier" : " identifiers") + " replaced", "external", "shield",
                    "Only the Covenant-built structured payload left; classification stays " + PolicyText.ClassificationLabel(red.OriginalClassification) + ". Not anonymisation.")
                : new("Sanitization refused", "sensitive", "shield", red.ReasonCode));
        }
        if (r.ToolCalls is { Count: > 0 } tools && tools.Any(t => t.CostBasis != "no_call"))
            chips.Add(new("External retrieval · " + string.Join(", ", tools.Select(t => t.Provider).Distinct()), "external", "globe",
                "Web search is a separate external call with its own charge, independent of where the model ran."));

        // Cost split (inference vs search), unknown never shown as $0.
        decimal? inference = r.Attempts is { } atts ? (atts.Any(a => a.CostUsd is null) ? null : atts.Sum(a => a.CostUsd ?? 0m)) : null;
        decimal search = r.ToolCalls?.Sum(t => t.CostUsd ?? 0m) ?? 0m;
        if (r.Attempts is { Count: > 0 } || search > 0m)
            chips.Add(new("Inference " + (inference is { } i ? Money.Usd(i) : "unknown") + (search > 0m ? " · search " + Money.Usd(search) : ""), "neutral", null,
                "Charged after execution: reported usage × configured price (estimates when usage was not reported). Local is not free: infrastructure is excluded."));
        return chips;
    }
}
