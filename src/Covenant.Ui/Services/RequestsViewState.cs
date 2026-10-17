using Covenant.Ui.Logic;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>Requests browser state that survives navigation: the loaded snapshot of the current audit segment,
/// the filters and the selected request. Cleared on disconnect. Holds audit records only (no response text).</summary>
public sealed class RequestsViewState : IDisposable
{
    public const int SnapshotLimit = 500;

    private readonly AppState _state;

    public RequestsViewState(AppState state)
    {
        _state = state;
        _state.Disconnected += Clear;
    }

    public RequestsResponse? Snapshot { get; set; }
    public DateTimeOffset? LoadedUtc { get; set; }
    public RequestFilterCriteria Criteria { get; set; } = RequestFilterCriteria.None;

    /// <summary>The request open in the detail drawer (by correlation id), with its exact record when fetched.</summary>
    public string? SelectedId { get; set; }
    public AuditEntryView? SelectedRecord { get; set; }

    private void Clear()
    {
        Snapshot = null;
        LoadedUtc = null;
        Criteria = RequestFilterCriteria.None;
        SelectedId = null;
        SelectedRecord = null;
    }

    public void Dispose() => _state.Disconnected -= Clear;
}
