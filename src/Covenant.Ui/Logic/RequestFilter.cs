using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

public enum TimeWindow { All, Last15Minutes, LastHour, Last24Hours }

/// <summary>Filter over the recent snapshot the console loaded (current audit segment, bounded). Immutable;
/// change with <c>with</c>. Null / empty members mean "any".</summary>
public sealed record RequestFilterCriteria
{
    public static readonly RequestFilterCriteria None = new();

    public DisplayOutcome? Outcome { get; init; }
    /// <summary>Matches the selected, attempted or served model (exact, case-insensitive).</summary>
    public string? Model { get; init; }
    public string? Team { get; init; }
    /// <summary>Audit v3 ApplicationId (exact, case-insensitive). Legacy records without it never match a set filter.</summary>
    public string? Application { get; init; }
    /// <summary>Substring of the request id (== correlation id), case-insensitive.</summary>
    public string? IdSearch { get; init; }
    public TimeWindow Window { get; init; } = TimeWindow.All;

    public bool IsActive => Outcome is not null || !string.IsNullOrWhiteSpace(Model) || !string.IsNullOrWhiteSpace(Team)
                            || !string.IsNullOrWhiteSpace(Application) || !string.IsNullOrWhiteSpace(IdSearch) || Window != TimeWindow.All;
}

public static class RequestFilter
{
    public static IReadOnlyList<AuditEntryView> Apply(IEnumerable<AuditEntryView> items, RequestFilterCriteria c, DateTimeOffset nowUtc)
    {
        var result = new List<AuditEntryView>();
        foreach (var r in items)
            if (Matches(r, c, nowUtc)) result.Add(r);
        return result;
    }

    public static bool Matches(AuditEntryView r, RequestFilterCriteria c, DateTimeOffset nowUtc)
    {
        if (c.Outcome is { } o && OutcomeModel.FromRecord(r).Outcome != o) return false;

        if (!string.IsNullOrWhiteSpace(c.Model))
        {
            var m = c.Model.Trim();
            if (!Same(r.SelectedModel, m) && !Same(r.AttemptedModel, m) && !Same(r.ServedByModel, m)) return false;
        }

        if (!string.IsNullOrWhiteSpace(c.Team) && !Same(r.Tags.Team, c.Team.Trim())) return false;

        if (!string.IsNullOrWhiteSpace(c.Application) && !Same(r.ApplicationId, c.Application.Trim())) return false;

        if (!string.IsNullOrWhiteSpace(c.IdSearch))
        {
            var q = c.IdSearch.Trim();
            if (r.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }

        var since = Since(c.Window, nowUtc);
        if (since is { } s && r.TimestampUtc < s) return false;

        return true;
    }

    public static DateTimeOffset? Since(TimeWindow w, DateTimeOffset nowUtc) => w switch
    {
        TimeWindow.Last15Minutes => nowUtc.AddMinutes(-15),
        TimeWindow.LastHour => nowUtc.AddHours(-1),
        TimeWindow.Last24Hours => nowUtc.AddHours(-24),
        _ => null,
    };

    public static string Label(TimeWindow w) => w switch
    {
        TimeWindow.Last15Minutes => "Last 15 minutes",
        TimeWindow.LastHour => "Last hour",
        TimeWindow.Last24Hours => "Last 24 hours",
        _ => "All loaded records",
    };

    /// <summary>Distinct non-empty model ids (selected/attempted/served) present in the snapshot, sorted.</summary>
    public static IReadOnlyList<string> Models(IEnumerable<AuditEntryView> items)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in items)
        {
            if (!string.IsNullOrWhiteSpace(r.SelectedModel)) set.Add(r.SelectedModel);
            if (!string.IsNullOrWhiteSpace(r.AttemptedModel)) set.Add(r.AttemptedModel);
            if (!string.IsNullOrWhiteSpace(r.ServedByModel)) set.Add(r.ServedByModel);
        }
        return [.. set];
    }

    public static IReadOnlyList<string> Teams(IEnumerable<AuditEntryView> items)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in items)
            if (!string.IsNullOrWhiteSpace(r.Tags.Team)) set.Add(r.Tags.Team);
        return [.. set];
    }

    public static IReadOnlyList<string> Applications(IEnumerable<AuditEntryView> items)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in items)
            if (!string.IsNullOrWhiteSpace(r.ApplicationId)) set.Add(r.ApplicationId);
        return [.. set];
    }

    public static IReadOnlyList<DisplayOutcome> Outcomes(IEnumerable<AuditEntryView> items)
    {
        var set = new SortedSet<DisplayOutcome>();
        foreach (var r in items) set.Add(OutcomeModel.FromRecord(r).Outcome);
        return [.. set];
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
