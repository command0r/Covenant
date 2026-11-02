using System.Globalization;
using System.Text.RegularExpressions;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>The examples the composer recommends for the selected application (at most <see cref="ExampleRecommendations.Max"/>)
/// and the rest, shown only behind an explicit "More examples" disclosure. Both lists keep each example's own readiness.</summary>
public sealed record ExampleGroups(IReadOnlyList<ExampleResolution> Recommended, IReadOnlyList<ExampleResolution> More);

/// <summary>Usability review: an application sees the 2–3 examples that demonstrate ITS declared work; generic diagnostics
/// (cache, sensitive refusal, cloud research) stay available under "More examples". A budget-refusal example is recommended
/// only where it is relevant at all — an application with its own deliberate tiny budget. Grants and readiness are untouched:
/// this only orders what <see cref="DemoExamples.ResolveFor"/> already found relevant. Pure.</summary>
public static partial class ExampleRecommendations
{
    public const int Max = 3;

    /// <summary>Task-specific examples, in recommendation order. Anything else is secondary.</summary>
    public static readonly IReadOnlyList<string> Primary =
    [
        "public-coding-escalation", "internal-coding",
        "public-writing", "internal-writing",
        "public-local-research", "internal-research",
        "sanitized-record",
        "budget-refusal",
    ];

    /// <summary>ADR-0019 demonstration pair: the same sensitive prompt served locally on an Internal workload and rejected on a
    /// Public one. Recommended together — never one without the other — when the application's configuration shows BOTH (an
    /// Internal workload approved for sensitive data and a Public workload that rejects it).</summary>
    public static readonly IReadOnlyList<string> SensitivePair = ["sensitive-local", "sensitive-public-rejected"];

    public static ExampleGroups Split(IReadOnlyList<ExampleResolution> relevant)
    {
        var pair = SensitivePair.Select(id => relevant.FirstOrDefault(r => r.Example.Id == id)).ToList();
        bool showPair = pair.All(r => r is not null);
        var recommended = (showPair ? pair.Select(r => r!) : Enumerable.Empty<ExampleResolution>())
            .Concat(relevant
                .Where(r => Primary.Contains(r.Example.Id))
                .OrderBy(r => IndexOf(r.Example.Id))
                .Take(Max - (showPair ? SensitivePair.Count : 0)))
            .ToList();
        if (recommended.Count == 0) recommended = relevant.Take(2).ToList();   // no task-specific fit: the first generic ones
        var more = relevant.Where(r => !recommended.Contains(r)).ToList();
        return new ExampleGroups(recommended, more);
    }

    private static int IndexOf(string id)
    {
        for (int i = 0; i < Primary.Count; i++) if (Primary[i] == id) return i;
        return int.MaxValue;
    }
}

/// <summary>The example selector's value (pure; ExamplePicker). The operator's pick wins while it still describes the form; the
/// form's own example label (set by "Use example", or by a twin relabel after a workload switch — DemoExamples.AfterWorkloadChange)
/// is shown otherwise. The select, the "Fill again"/"Use example" button and the summary therefore always name the SAME example
/// (browser review: a relabelled twin showed the placeholder while its buttons stayed).</summary>
public static class ExampleSelection
{
    /// <summary>Value the selector shows (null = the placeholder).</summary>
    public static string? Shown(string? picked, string? formExampleId) => picked ?? formExampleId;

    /// <summary>After an application or workload switch: a pick that no longer is the form's example follows the form's
    /// (possibly relabelled) example, or clears when the form has none.</summary>
    public static string? AfterSwitch(string? picked, string? formExampleId)
        => picked is not null && !string.Equals(picked, formExampleId, StringComparison.Ordinal) ? formExampleId : picked;
}

/// <summary>Human labels for configured models — never an availability claim: the label comes from the APPLIED configuration
/// (the model's display name when configured), else a readable form of the exact id.</summary>
public static partial class ModelLabels
{
    public static string Friendly(CovenantConfigDto? cfg, string? connectionId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "the approved model";
        var model = cfg?.Connections.FirstOrDefault(c => string.Equals(c.Id, connectionId, StringComparison.Ordinal))
            ?.Models.FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(model?.DisplayName)) return model!.DisplayName!.Trim();
        return Readable(modelId);
    }

    /// <summary>"connection/model" form.</summary>
    public static string Friendly(CovenantConfigDto? cfg, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "the approved model";
        int slash = target.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? Readable(target) : Friendly(cfg, target[..slash], target[(slash + 1)..]);
    }

    /// <summary>"claude-opus-5-5" → "Claude Opus 5.5"; any other id is returned unchanged (exact ids are honest).</summary>
    public static string Readable(string modelId)
    {
        var m = ClaudeId().Match(modelId);
        if (!m.Success) return modelId;
        var family = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups[1].Value);
        return "Claude " + family + " " + m.Groups[2].Value + (m.Groups[3].Success ? "." + m.Groups[3].Value : "");
    }

    [GeneratedRegex(@"^claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeId();
}
