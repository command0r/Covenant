using System.Text.RegularExpressions;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Classification plus WHAT triggered it (a rule name, never content) — the signal goes into evidence.</summary>
public sealed record ClassificationResult(DataClassification Classification, string? Signal);

/// <summary>Derives a data classification from request content. Implementations must fail closed (escalate when unsure).</summary>
public interface IDataClassifier
{
    ClassificationResult Classify(InferenceRequest request);
}

/// <summary>First-slice pattern classifier, deliberately conservative — NOT a real DLP; it makes the
/// classification → policy seam real and testable. Swap behind <see cref="IDataClassifier"/> later.</summary>
public sealed partial class RegexDataClassifier : IDataClassifier
{
    public ClassificationResult Classify(InferenceRequest request)
    {
        var text = string.Join('\n', request.Messages.Select(m => m.Content));
        if (PhiPattern().IsMatch(text)) return new(DataClassification.Phi, "PHI keyword (MRN / diagnosis / patient id)");
        if (PiiPattern().IsMatch(text)) return new(DataClassification.Pii, "US SSN pattern");
        return new(DataClassification.Internal, null);
    }

    // US SSN — illustrative only.
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex PiiPattern();

    // Naive PHI markers — illustrative only.
    [GeneratedRegex(@"\b(MRN|diagnosis|patient\s+id)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PhiPattern();
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
