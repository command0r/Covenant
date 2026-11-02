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
        await next(ctx, ct);
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
