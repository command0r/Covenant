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

    public static ExampleGroups Split(IReadOnlyList<ExampleResolution> relevant)
    {
        var recommended = relevant
            .Where(r => Primary.Contains(r.Example.Id))
            .OrderBy(r => IndexOf(r.Example.Id))
            .Take(Max)
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
