using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Selected vs attempted vs served model and the classification line, from recorded facts only.
/// A route chosen before a pause refusal or a cache hit is NOT a model that was attempted.</summary>
public sealed record ModelFactsView(string Selected, string Attempted, string Served, string Classification);

public static class ModelFacts
{
    public const string NotRecorded = "Not recorded";
    public const string NoProviderCall = "None — no provider call";
    public const string FromCache = "Served from cache (no provider call)";

    /// <summary>v2 records carry the selected/attempted fields; on a legacy record their absence means
    /// "Not recorded", never "none".</summary>
    public static bool IsV2(AuditEntryView r) => r.SchemaVersion is >= 2;

    public static ModelFactsView From(AuditEntryView r) => new(
        Selected(r), Attempted(r), Served(r), Classification(r));

    public static string Selected(AuditEntryView r)
    {
        if (!string.IsNullOrWhiteSpace(r.SelectedModel)) return r.SelectedModel;
        return IsV2(r) ? "None — no route selected" : NotRecorded;
    }

    public static string Attempted(AuditEntryView r)
    {
        if (!string.IsNullOrWhiteSpace(r.AttemptedModel)) return r.AttemptedModel;
        return IsV2(r) ? NoProviderCall : NotRecorded;
    }

    public static string Served(AuditEntryView r)
    {
        if (r.CacheHit)
            return string.IsNullOrWhiteSpace(r.ServedByModel) ? FromCache : FromCache + " · " + r.ServedByModel;
        if (!string.IsNullOrWhiteSpace(r.ServedByModel)) return r.ServedByModel;
        return string.Equals(r.Effect, "Deny", StringComparison.OrdinalIgnoreCase) ? "None — not served" : NotRecorded;
    }

    /// <summary>The restrictive default classification (PHI) is an enforcement default, not a detection: when a
    /// stage before Classify stopped the request it is shown as "Not evaluated".</summary>
    public static string Classification(AuditEntryView r)
    {
        if (r.ClassificationEvaluated is null) return NotRecorded;
        if (r.ClassificationEvaluated == false)
            return "Not evaluated — stopped at " + PipelineStages.Label(r.StoppedAtStage);
        var cls = string.IsNullOrWhiteSpace(r.Classification) ? NotRecorded : r.Classification;
        return string.IsNullOrWhiteSpace(r.Signal) ? cls + " · no rule matched" : cls + " · " + r.Signal;
    }

    /// <summary>Compact "selected → served" for table cells.</summary>
    public static string SelectedToServed(AuditEntryView r)
    {
        var sel = r.SelectedModel;
        var served = r.CacheHit ? "cache" : r.ServedByModel;
        if (string.IsNullOrWhiteSpace(sel) && string.IsNullOrWhiteSpace(served))
            return IsV2(r) ? "—" : (r.ServedByModel ?? NotRecorded);
        if (string.IsNullOrWhiteSpace(served)) return (sel ?? "") + " → not served";
        if (string.IsNullOrWhiteSpace(sel)) return IsV2(r) ? "— → " + served : served;
        return string.Equals(sel, served, StringComparison.Ordinal) ? sel : sel + " → " + served;
    }
}
