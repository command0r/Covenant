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

    /// <summary>The connection's boundary ("Local"/"Cloud") from the configuration, or null when unknown. Pre-v2
    /// "local" adapter ids count as Local.</summary>
    public static string? BoundaryOf(string? connectionId, IReadOnlyList<ConnectionDto>? connections)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return null;
        if (connections is not null)
            foreach (var c in connections)
                if (string.Equals(c.Id, connectionId, StringComparison.Ordinal))
                    return ComposerRules.BoundaryLabel(c.Boundary);
        return string.Equals(connectionId, "local", StringComparison.OrdinalIgnoreCase) ? "Local" : null;
    }

    /// <summary>Actual destination: the serving attempt (or the cache's original model), else the last attempted
    /// target. Uses Attempts[] when recorded, then ServedByModel / AttemptedModel. Never the selected-only route.</summary>
    public static DestinationView Destination(AuditEntryView r, IReadOnlyList<ConnectionDto>? connections = null)
    {
        if (r.CacheHit)
        {
            var origin = string.IsNullOrWhiteSpace(r.ServedByModel) ? "" : " (originally " + r.ServedByModel + ")";
            return new DestinationView("Cache — no provider call" + origin, BoundaryOf(ConnectionPart(r.ServedByModel), connections), true);
        }
        if (r.Attempts is { Count: > 0 } attempts)
        {
            var servedAttempt = attempts.LastOrDefault(AttemptFacts.IsSuccess);
            var a = servedAttempt ?? attempts[^1];
            var target = TraceModel.Target(a) ?? NotRecorded;
            return new DestinationView(servedAttempt is null ? target + " — not served" : target,
                BoundaryOf(a.ConnectionId, connections), servedAttempt is not null);
        }
        if (!string.IsNullOrWhiteSpace(r.ServedByModel))
            return new DestinationView(r.ServedByModel, BoundaryOf(ConnectionPart(r.ServedByModel), connections), true);
        if (!string.IsNullOrWhiteSpace(r.AttemptedModel))
            return new DestinationView(r.AttemptedModel + " — not served", BoundaryOf(ConnectionPart(r.AttemptedModel), connections), false);
        return new DestinationView(IsV2(r) ? NoProviderCall : NotRecorded, null, false);
    }

    /// <summary>"conn/model" → "conn"; "local:tag" (pre-v2) → "local"; otherwise null.</summary>
    public static string? ConnectionPart(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        int slash = model.IndexOf('/');
        if (slash > 0) return model[..slash];
        if (model.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) return "local";
        return null;
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

/// <summary>Where a request was actually served (or last attempted), with its boundary. <see cref="Boundary"/> is
/// "Local", "Cloud" or null when the connection's boundary is not known (shown as text, never guessed).</summary>
public sealed record DestinationView(string Text, string? Boundary, bool Served);

/// <summary>One recorded provider attempt, ready to display. <see cref="EffectiveMaxOutputTokens"/> = the output bound
/// sent for this attempt; <see cref="Truncated"/> = it finished with "length" (review round 2).</summary>
public sealed record AttemptLine(int Number, string Target, string Outcome, string? ErrorClass, string Duration, string Cost, string? Boundary)
{
    public int? EffectiveMaxOutputTokens { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>Attempt-level evidence (audit v3 <c>Attempts[]</c>, ADR-0013): fallback is shown as what it was —
/// a second, approved attempt after a transient failure — never as a silent retry.</summary>
public static class AttemptFacts
{
    /// <summary>Contract vocabulary: Outcome ∈ served | failed | cancelled.</summary>
    public static bool IsSuccess(AuditAttemptView a) => string.Equals(a.Outcome, "served", StringComparison.Ordinal);

    public static IReadOnlyList<AttemptLine> Lines(AuditEntryView r, IReadOnlyList<ConnectionDto>? connections = null)
    {
        if (r.Attempts is not { Count: > 0 } attempts) return [];
        var lines = new List<AttemptLine>(attempts.Count);
        int n = 0;
        foreach (var a in attempts)
        {
            n++;
            lines.Add(new AttemptLine(
                n,
                TraceModel.Target(a) ?? ModelFacts.NotRecorded,
                string.IsNullOrWhiteSpace(a.Outcome) ? ModelFacts.NotRecorded : a.Outcome,
                string.IsNullOrWhiteSpace(a.ErrorClass) ? null : a.ErrorClass,
                FormatMs(a.DurationMs),
                a.CostUsd is { } c ? CostText.BasisCost(a.CostBasis, c) : CostText.NotReported,
                ModelFacts.BoundaryOf(a.ConnectionId, connections))
            {
                EffectiveMaxOutputTokens = a.EffectiveMaxOutputTokens,
                Truncated = a.Truncated == true,
            });
        }
        return lines;
    }

    /// <summary>"1.20 s · ≈ $0.0004 estimated · bound 64 · truncated (length)".</summary>
    public static string Detail(AttemptLine a)
        => a.Duration + " · " + a.Cost
           + (a.EffectiveMaxOutputTokens is { } b ? " · bound " + b.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
           + (a.Truncated ? " · truncated (length)" : "");

    /// <summary>"Attempt 1 (openai/gpt-x) failed: timeout; attempt 2 served." — only when more than one attempt
    /// was recorded and the last one served. Null otherwise.</summary>
    public static string? FallbackSentence(AuditEntryView r)
    {
        if (r.Attempts is not { Count: > 1 } attempts || !IsSuccess(attempts[^1])) return null;
        var parts = new List<string>();
        for (int i = 0; i < attempts.Count - 1; i++)
        {
            var a = attempts[i];
            var why = string.IsNullOrWhiteSpace(a.ErrorClass) ? (string.IsNullOrWhiteSpace(a.Outcome) ? "not served" : a.Outcome) : a.ErrorClass;
            parts.Add("attempt " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + " (" + (TraceModel.Target(a) ?? "target not recorded") + ") failed: " + why);
        }
        var s = string.Join("; ", parts) + "; attempt " + attempts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " (" + (TraceModel.Target(attempts[^1]) ?? "target not recorded") + ") served.";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>"2 provider attempts recorded." or null when the record lists none.</summary>
    public static string? CountSentence(AuditEntryView r)
        => r.Attempts is { Count: > 0 } a
            ? a.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (a.Count == 1 ? " provider attempt recorded." : " provider attempts recorded.")
            : null;

    private static string FormatMs(double ms)
        => ms < 1000
            ? ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms"
            : (ms / 1000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s";
}
