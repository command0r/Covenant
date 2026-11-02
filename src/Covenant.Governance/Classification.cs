using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Classification plus WHAT triggered it (a rule name, never content) — the signal goes into evidence.
/// <paramref name="Incomplete"/> (ADR-0018): inspection could not complete (size, time/work bound, malformed input). The
/// request must then be REFUSED — its classification is unknown, and no content may reach any provider or tool.</summary>
public sealed record ClassificationResult(DataClassification Classification, string? Signal, bool Incomplete = false);

/// <summary>Derives a data classification from request content. Implementations must fail closed (escalate when unsure).</summary>
public interface IDataClassifier
{
    ClassificationResult Classify(InferenceRequest request);
}

/// <summary>Compatibility name for the classifier the Host composes. Since ADR-0018 it IS the local, bounded, normalized,
/// fail-closed inspector (<see cref="LocalSensitiveDataInspector"/>) — the first-slice hyphen-SSN / PHI-keyword rules are
/// gone. Not comprehensive DLP: see <see cref="InspectionSignals.Coverage"/>.</summary>
public sealed class RegexDataClassifier : IDataClassifier
{
    private readonly LocalSensitiveDataInspector _inspector = new();

    public ClassificationResult Classify(InferenceRequest request) => _inspector.Classify(request);
}

/// <summary>Pipeline stage: classifies the request and records the result on the context.
/// With a bound configuration (ADR-0012): final = max(trusted baseline, detected) — the detector can only
/// ESCALATE. Its no-match result (Internal with no signal) means "no escalation", never "public", so Public is
/// reachable only through a server-configured Public-baseline profile with nothing detected. Without a
/// configuration the legacy behaviour (classification = detector result) is unchanged.</summary>
public sealed class ClassifyStage(IDataClassifier classifier) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var result = classifier.Classify(ctx.Request);
        if (result.Incomplete)
        {
            // ADR-0018: unknown is not "PHI, send it to the local model" — nothing is sent anywhere. The classification stays
            // the restrictive default and is reported as NOT evaluated; the signal names the reason, never content.
            ctx.ClassificationSignal = result.Signal;
            ctx.ClassificationEvaluated = false;
            ctx.Deny("sensitive-data inspection could not complete (input too large or too complex) — refused; nothing was sent",
                DenialKind.Governance, code: TraceReasons.InspectionIncomplete);
            return;
        }
        if (ctx.Configuration is null)
        {
            ctx.Classification = result.Classification;
        }
        else
        {
            var baseline = ctx.BaselineClassification ?? DataClassification.Internal;
            ctx.BaselineClassification = baseline;
            ctx.Classification = ClassificationRules.Combine(baseline, result);
        }
        ctx.ClassificationSignal = result.Signal;
        ctx.ClassificationEvaluated = true;
        // ADR-0019: a workload that rejects detected sensitive data stops here — before policy, sanitization, cache, rate
        // limit, budget, web search and provider. The record keeps the escalated class and the signal label (never content).
        if (ctx.Configuration is { } cfg && SensitiveDataRules.Rejects(cfg, ctx.ProfileId, ctx.Classification))
        {
            var app = ctx.ApplicationId is { } aid ? cfg.FindApplication(aid) : null;
            ctx.Deny(SensitiveDataRules.Explain(cfg, app, ctx.ProfileId!, ctx.Classification, result.Signal),
                DenialKind.Governance, code: TraceReasons.SensitiveDataRejected);
            return;
        }
        await next(ctx, ct);
    }
}

/// <summary>ADR-0019: the per-workload action on detected PII/PHI. One rule for the classify stage, the simulator and the
/// policy engine (defence in depth), so preview, impact and execution cannot diverge.</summary>
public static class SensitiveDataRules
{
    /// <summary>True when <paramref name="final"/> is PII/PHI and the workload rejects detected sensitive data. Only a
    /// Public/Internal-baseline profile can reject (validation enforces it; re-checked here), so under such a profile a
    /// PII/PHI classification can only come from detection.</summary>
    public static bool Rejects(IRequestConfiguration cfg, string? profileId, DataClassification final)
        => final >= DataClassification.Pii
           && profileId is not null
           && cfg.FindProfile(profileId) is { SensitiveDataAction: SensitiveDataAction.Reject } p
           && p.Baseline < DataClassification.Pii;

    /// <summary>Granted workloads of <paramref name="app"/> under which the policy engine WOULD route <paramref name="final"/>
    /// (action Route, an eligible target). Computed by the engine — never a hard-coded name. At most 3, config order.</summary>
    public static IReadOnlyList<string> Suggestions(IRequestConfiguration cfg, ApplicationConfig? app, string? currentProfile, DataClassification final)
    {
        var list = new List<string>();
        if (app is null) return list;
        foreach (var g in app.GrantedProfiles)
        {
            if (string.Equals(g, currentProfile, StringComparison.Ordinal) || list.Contains(g, StringComparer.Ordinal)) continue;
            if (cfg.FindProfile(g) is not { SensitiveDataAction: SensitiveDataAction.Route } p || p.Baseline >= DataClassification.Pii) continue;
            if (PolicyEngineV2.Evaluate(cfg, final, g, null, []).Effect != PolicyEffect.Allow) continue;
            list.Add(g);
            if (list.Count == 3) break;
        }
        return list;
    }

    /// <summary>The bounded refusal reason (no content): what was detected, which workload rejected it, that nothing was
    /// sent, and — when one exists — which granted workload would accept it.</summary>
    public static string Explain(IRequestConfiguration cfg, ApplicationConfig? app, string profileId, DataClassification final, string? signal)
    {
        var what = final == DataClassification.Phi ? "PHI" : "PII";
        var reason = $"sensitive data detected ({what}{(signal is { Length: > 0 } s ? ": " + Evidence.Bounded(s) : "")}): "
                     + $"workload '{Evidence.Bounded(profileId)}' rejects detected sensitive data — refused before routing, cache, web search or any model; nothing was sent";
        var suggestions = Suggestions(cfg, app, profileId, final);
        return suggestions.Count == 0
            ? reason + ". No other workload granted to this application accepts it."
            : reason + $". To process it on an approved route, choose workload '{string.Join("' or '", suggestions)}'.";
    }
}

/// <summary>ADR-0012: final = max(trusted baseline, detected); a no-match detector result (Internal with no
/// signal) is "no escalation". Shared by the classify stage and the policy simulator.</summary>
public static class ClassificationRules
{
    public static DataClassification Combine(DataClassification baseline, ClassificationResult detected)
    {
        bool noEscalation = detected.Signal is null && detected.Classification == DataClassification.Internal;
        return noEscalation || detected.Classification <= baseline ? baseline : detected.Classification;
    }
}
