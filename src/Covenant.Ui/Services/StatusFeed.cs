using System.Text.Json;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>Keeps AppState.LastStatus current while connected. Primary: the SSE stream at /admin/status/stream
/// (read through HttpClient so the admin token travels in a header — EventSource cannot). On failure it
/// reconnects with backoff and marks the state stale ("Connection lost — retrying"); after two streaming
/// failures it falls back to polling GET /admin/status every 3 s. A second loop ticks AppState once a second
/// so freshness flips to Stale on time even when nothing arrives.</summary>
public sealed class StatusFeed(AdminApi api, AppState state) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int StreamFailuresBeforePolling = 2;

    private CancellationTokenSource? _cts;

    public bool Running => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
        _ = TickAsync(_cts.Token);
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
        state.SetFeedMode(FeedMode.None);
    }

    /// <summary>One immediate GET /admin/status; used after a mutation so the UI re-syncs from the server
    /// instead of assuming the mutation succeeded.</summary>
    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        var status = await api.GetStatusAsync(ct);
        state.ApplyStatus(status);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
                state.Tick(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { /* stopped */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int streamFailures = 0;
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (streamFailures < StreamFailuresBeforePolling)
                {
                    state.SetFeedMode(FeedMode.Stream);
                    await ReadStreamAsync(ct);
                    // A clean end of stream (server closed) is treated like a failure for backoff purposes.
                    streamFailures++;
                }
                else
                {
                    state.SetFeedMode(FeedMode.Polling);
                    await PollAsync(ct);
                    return; // PollAsync only returns on cancellation
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                return; // AdminApi already raised Unauthorized; ConsoleSession disconnects and stops us.
            }
            catch (Exception)
            {
                streamFailures++;
            }

            if (ct.IsCancellationRequested) return;
            attempt++;
            state.MarkFeedFailure("Connection lost — retrying");
            var delay = TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt, 3)));
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReadStreamAsync(CancellationToken ct)
    {
        using var response = await api.OpenStatusStreamAsync(ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return; // end of stream
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var json = line.AsSpan(5).Trim();
            if (json.IsEmpty) continue;
            StatusReport? status;
            try
            {
                status = JsonSerializer.Deserialize(json, UiJsonContext.Default.StatusReport);
            }
            catch (JsonException)
            {
                continue; // a malformed frame is skipped; the next one re-syncs
            }
            if (status is not null) state.ApplyStatus(status);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await api.GetStatusAsync(ct);
                state.ApplyStatus(status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized) { return; }
            catch (Exception)
            {
                state.MarkFeedFailure("Connection lost — retrying");
            }
            if (!await timer.WaitForNextTickAsync(ct)) return;
        }
    }

    public void Dispose() => Stop();
}
