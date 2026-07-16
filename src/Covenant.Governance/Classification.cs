using System.Text.RegularExpressions;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Derives a data classification from request content. Implementations must fail closed (escalate when unsure).</summary>
public interface IDataClassifier
{
    DataClassification Classify(InferenceRequest request);
}

/// <summary>
/// First-slice classifier: pattern-based detection of obvious PII/PHI, defaulting to Internal.
/// Deliberately conservative. This is NOT a substitute for a real DLP classifier — it exists to make
/// the classification → policy seam real and testable. Swap behind <see cref="IDataClassifier"/> later.
/// </summary>
public sealed partial class RegexDataClassifier : IDataClassifier
{
    public DataClassification Classify(InferenceRequest request)
    {
        var text = string.Join('\n', request.Messages.Select(m => m.Content));
        if (PhiPattern().IsMatch(text)) return DataClassification.Phi;
        if (PiiPattern().IsMatch(text)) return DataClassification.Pii;
        return DataClassification.Internal;
    }

    // US SSN — illustrative only.
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex PiiPattern();

    // Naive PHI markers — illustrative only.
    [GeneratedRegex(@"\b(MRN|diagnosis|patient\s+id)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PhiPattern();
}

/// <summary>Pipeline stage: classifies the request and records the result on the context.</summary>
public sealed class ClassifyStage(IDataClassifier classifier) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        ctx.Classification = classifier.Classify(ctx.Request);
        await next(ctx, ct);
    }
}
