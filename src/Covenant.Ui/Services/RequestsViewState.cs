using Covenant.Ui.Logic;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>Requests browser state that survives navigation: the loaded pages of ONE audit segment (current or an
/// archived one), the cursor for the next older page, the filters and the selected request. Cleared on disconnect.
/// Holds audit records only (no response text). Loading is bounded (<see cref="MaxLoaded"/>).</summary>
public sealed class RequestsViewState : IDisposable
{
    public const int PageSize = 200;
    /// <summary>Upper bound of records held in the browser; beyond it the operator opens a request by id or narrows the segment.</summary>
    public const int MaxLoaded = 2000;

    private readonly AppState _state;

    public RequestsViewState(AppState state)
    {
        _state = state;
        _state.Disconnected += Clear;
    }

    /// <summary>Cursor paging of the selected segment (Logic/RequestPaging): requested segment, the Host's segment
    /// descriptor, next_cursor and the 410 cursor_expired state.</summary>
    public RequestPaging Paging { get; } = new();
    /// <summary>Segment id requested (null = current segment).</summary>
    public string? SegmentId => Paging.RequestedSegmentId;
    /// <summary>The Host's descriptor of the loaded pages (never inferred from a row).</summary>
    public SegmentInfo? Segment => Paging.Segment;
    public List<AuditEntryView> Items { get; } = [];
    public string? NextCursor => Paging.NextCursor;
    public bool Loaded => Paging.Loaded;
    public DateTimeOffset? LoadedUtc { get; set; }

    public List<AuditSegmentDto>? Segments { get; set; }
    public string? SegmentsError { get; set; }

    public RequestFilterCriteria Criteria { get; set; } = RequestFilterCriteria.None;

    /// <summary>The request open in the detail drawer (by correlation id), with its exact record when fetched.</summary>
    public string? SelectedId { get; set; }
    public AuditEntryView? SelectedRecord { get; set; }

    /// <summary>Starts over for a (possibly different) segment.</summary>
    public void ResetPages(string? segmentId)
    {
        Paging.Reset(segmentId);
        Items.Clear();
        LoadedUtc = null;
    }

    /// <summary>Appends a page, de-duplicating by id (defensive: stable cursors never overlap).</summary>
    public int AddPage(IEnumerable<AuditEntryView> page)
    {
        var seen = new HashSet<string>(Items.Select(i => i.Id), StringComparer.Ordinal);
        int added = 0;
        foreach (var r in page)
        {
            if (Items.Count >= MaxLoaded) break;
            if (seen.Add(r.Id)) { Items.Add(r); added++; }
        }
        return added;
    }

    private void Clear()
    {
        ResetPages(null);
        Segments = null;
        SegmentsError = null;
        Criteria = RequestFilterCriteria.None;
        SelectedId = null;
        SelectedRecord = null;
    }

    public void Dispose() => _state.Disconnected -= Clear;
}
