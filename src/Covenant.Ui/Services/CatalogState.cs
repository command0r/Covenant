namespace Covenant.Ui.Services;

/// <summary>Keeps the shared provider catalog (AppState.Catalog) fresh: loaded on connect by ConsoleSession,
/// refreshed every 30 s while connected and on demand after a governed test — so the header, Overview and
/// Models &amp; connections all show the same observed health. A later failure supersedes an earlier success
/// (the server keeps last_success_utc). Stops and forgets on disconnect.</summary>
public sealed class CatalogState : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly AppState _state;
    private readonly ConsoleSession _session;
    private CancellationTokenSource? _loop;

    public CatalogState(AppState state, ConsoleSession session)
    {
        _state = state;
        _session = session;
        _state.Changed += OnChanged;
        _state.Disconnected += StopLoop;
        OnChanged();
    }

    public bool Refreshing { get; private set; }

    public async Task RefreshAsync()
    {
        if (!_state.Connected || Refreshing) return;
        Refreshing = true;
        try { await _session.LoadCatalogAsync(); }
        finally { Refreshing = false; }
    }

    private void OnChanged()
    {
        if (_state.Connected && _loop is null)
        {
            _loop = new CancellationTokenSource();
            _ = LoopAsync(_loop.Token);
        }
        else if (!_state.Connected && _loop is not null)
        {
            StopLoop();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync();
        }
        catch (OperationCanceledException) { /* stopped */ }
    }

    private void StopLoop()
    {
        var cts = _loop;
        _loop = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    public void Dispose()
    {
        _state.Changed -= OnChanged;
        _state.Disconnected -= StopLoop;
        StopLoop();
    }
}
