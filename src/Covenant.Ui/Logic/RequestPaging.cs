using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>What happened when a page or an error was applied to <see cref="RequestPaging"/>.</summary>
public enum PageResult
{
    /// <summary>The page belongs to the requested segment; its records may be shown.</summary>
    Accepted,
    /// <summary>The Host answered for another segment than the one requested (or a different one than the earlier
    /// pages): nothing is shown rather than mislabelled records.</summary>
    SegmentMismatch,
    /// <summary>410 cursor_expired: the segment was rotated (archived) or replaced since the cursor was issued. The
    /// list must be reloaded from the first page; the cursor is dropped, never redirected.</summary>
    CursorExpired,
    /// <summary>Any other failure (400 invalid_cursor / unknown_segment, 5xx, transport).</summary>
    Failed,
}

/// <summary>Stable, cursor-based paging of ONE audit segment (docs/ui-api-contract.md "Stable paging", review round 2
/// B8). The scope always comes from the page's segment descriptor (<c>segment.id</c>, <c>segment.label</c>,
/// <c>segment.is_current</c>) — never from the first row and never from what the console asked for. "Load older"
/// follows <c>next_cursor</c> exactly; a 410 <c>cursor_expired</c> stops paging and asks for a reload. Pure (no I/O).</summary>
public sealed class RequestPaging
{
    public const string CursorExpiredMessage = "The audit segment changed (archived) — reload the list.";

    /// <summary>Segment id the operator chose (null = the current segment).</summary>
    public string? RequestedSegmentId { get; private set; }
    /// <summary>Descriptor of the loaded pages, exactly as the Host sent it with the last accepted page.</summary>
    public SegmentInfo? Segment { get; private set; }
    public string? NextCursor { get; private set; }
    public bool Loaded { get; private set; }
    /// <summary>The cursor expired (410): Load older is disabled until the list is reloaded.</summary>
    public bool Expired { get; private set; }
    /// <summary>Operator-facing text of the last failure, or null.</summary>
    public string? Problem { get; private set; }

    public bool CanLoadOlder => Loaded && !Expired && NextCursor is not null;

    /// <summary>Starts over for a (possibly different) segment.</summary>
    public void Reset(string? segmentId)
    {
        RequestedSegmentId = string.IsNullOrWhiteSpace(segmentId) ? null : segmentId;
        Segment = null;
        NextCursor = null;
        Loaded = false;
        Expired = false;
        Problem = null;
    }

    /// <summary>Validates and applies one page. <paramref name="older"/> = this page was fetched with
    /// <see cref="NextCursor"/> (it must belong to the same segment as the pages already loaded).</summary>
    public PageResult ApplyPage(RequestsResponse page, bool older)
    {
        var seg = page.Segment;
        bool matches = RequestedSegmentId is null
            ? seg.IsCurrent
            : string.Equals(seg.Id, RequestedSegmentId, StringComparison.Ordinal) && !seg.IsCurrent;
        if (older && Segment is { } had && !string.Equals(had.Id, seg.Id, StringComparison.Ordinal)) matches = false;
        if (!matches)
        {
            Problem = "The Host answered for " + SegmentScope.Label(seg) + ", not the requested "
                      + (RequestedSegmentId is null ? "current segment" : "archived segment " + RequestedSegmentId)
                      + " — nothing is shown rather than mislabelled records.";
            NextCursor = null;
            return PageResult.SegmentMismatch;
        }
        Segment = seg;
        NextCursor = page.Items.Count == 0 ? null : page.NextCursor;
        Loaded = true;
        Expired = false;
        Problem = null;
        return PageResult.Accepted;
    }

    /// <summary>Applies a failed page request: HTTP <paramref name="status"/> and the Host's <c>error</c> code.</summary>
    public PageResult ApplyError(int? status, string? errorCode, string? message)
    {
        if (status == 410 && string.Equals(errorCode, "cursor_expired", StringComparison.Ordinal))
        {
            Expired = true;
            NextCursor = null;
            Problem = CursorExpiredMessage;
            return PageResult.CursorExpired;
        }
        Problem = status == 400 && string.Equals(errorCode, "invalid_cursor", StringComparison.Ordinal)
            ? "The Host rejected the paging cursor (invalid_cursor) — reload the list."
            : string.IsNullOrWhiteSpace(message) ? "Could not load requests." : message;
        return PageResult.Failed;
    }
}

/// <summary>Loaded-list maintenance for the Requests page (pure).</summary>
public static class RequestList
{
    /// <summary>Inserts the records of a freshly fetched NEWEST page that are not loaded yet at the top, in the page's
    /// newest-first order; already-loaded records (and their positions) are untouched; the list stays within
    /// <paramref name="max"/> (oldest dropped). Returns how many were added.</summary>
    /// <summary>True when a freshly fetched newest page does NOT reach the loaded records (no id in common) while the Host
    /// says older records exist beyond it: merging would leave an unreachable gap between the new page and the loaded
    /// ones (the old cursor continues below the OLD first page). The list must then restart from the new first page.</summary>
    public static bool NeedsReset(IReadOnlyCollection<AuditEntryView> loaded, IReadOnlyCollection<AuditEntryView> newestPage, string? pageNextCursor)
    {
        if (loaded.Count == 0 || newestPage.Count == 0 || pageNextCursor is null) return false;
        var ids = new HashSet<string>(loaded.Select(i => i.Id), StringComparer.Ordinal);
        return !newestPage.Any(r => ids.Contains(r.Id));
    }

    public static int MergeNewest(List<AuditEntryView> items, IEnumerable<AuditEntryView> newestFirst, int max)
    {
        var seen = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
        var fresh = newestFirst.Where(r => r is not null && seen.Add(r.Id)).ToList();
        items.InsertRange(0, fresh);
        if (items.Count > max) items.RemoveRange(max, items.Count - max);
        return fresh.Count;
    }
}
