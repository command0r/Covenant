using System.Net;
using System.Text.Json;
using Covenant.Ui.Logic;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>Where a run's trace currently comes from (shown next to the TraceBar). <see cref="Locating"/> = the run was
/// submitted and the console is asking the Host for its request id (GET /admin/playground/submissions/{id}).</summary>
public enum TraceFeedState { Idle, Locating, Connecting, Live, Reconnecting, Snapshot, Complete, Expired, Unavailable }

/// <summary>Follows one request's real trace (ADR-0013): GET /admin/requests/{id}/trace/stream over the admin
/// HttpClient (the token travels in a header; EventSource cannot do that). Replay-then-live; on a dropped
/// connection it reconnects with Last-Event-ID = the highest applied seq, de-duplicating by seq. 404/410 →
/// one snapshot attempt (GET …/trace); a 404 there means the trace expired (bounded retention) — the console
/// then falls back to the compact trace on the audit record. Bounded: at most <see cref="MaxReconnects"/>
/// stream reconnects and <see cref="MaxSnapshotPolls"/> snapshot polls.</summary>
public sealed class TraceClient(AdminApi api)
{
    public const int MaxReconnects = 3;
    public const int MaxSnapshotPolls = 10;
    public static readonly TimeSpan SnapshotPollInterval = TimeSpan.FromSeconds(1);

    public async Task FollowAsync(string requestId, TraceAccumulator acc, Action<TraceFeedState, string?> report, CancellationToken ct)
    {
        int failures = 0;
        report(TraceFeedState.Connecting, null);
        while (!ct.IsCancellationRequested && !acc.Complete)
        {
            try
            {
                using var response = await api.OpenTraceStreamAsync(requestId, acc.LastSeq, ct);
                report(TraceFeedState.Live, null);
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                var parser = new SseParser();
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (parser.Feed(line) is not { } ev) continue;
                    if (ev.Event == "end")
                    {
                        acc.MarkComplete();
                        report(TraceFeedState.Complete, null);
                        return;
                    }
                    if (ev.Data.Length == 0) continue;
                    TraceEventDto? e;
                    try { e = JsonSerializer.Deserialize(ev.Data, UiJsonContext.Default.TraceEventDto); }
                    catch (JsonException) { continue; }
                    if (e is null) continue;
                    if (e.Seq == 0 && long.TryParse(ev.Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var idSeq))
                        e.Seq = idSeq;
                    if (acc.Add(e)) report(TraceFeedState.Live, null);
                }
                // Stream closed without "end": reconnect and resume.
                failures++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                await SnapshotAsync(requestId, acc, report, expiredOnNotFound: true, ct);
                return;
            }
            catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                return; // ConsoleSession disconnects.
            }
            catch (Exception)
            {
                failures++;
            }

            if (failures > MaxReconnects)
            {
                await SnapshotAsync(requestId, acc, report, expiredOnNotFound: true, ct);
                return;
            }
            report(TraceFeedState.Reconnecting, null);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, 1 << (failures - 1))), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (acc.Complete) report(TraceFeedState.Complete, null);
    }

    /// <summary>Snapshot fallback; polls (bounded) while the server says the trace is not complete.</summary>
    private async Task SnapshotAsync(string requestId, TraceAccumulator acc, Action<TraceFeedState, string?> report, bool expiredOnNotFound, CancellationToken ct)
    {
        for (int i = 0; i < MaxSnapshotPolls && !ct.IsCancellationRequested; i++)
        {
            try
            {
                var snap = await api.GetTraceAsync(requestId, ct);
                acc.ApplySnapshot(snap);
                if (acc.Complete)
                {
                    report(TraceFeedState.Complete, null);
                    return;
                }
                report(TraceFeedState.Snapshot, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                if (expiredOnNotFound) acc.MarkExpired();
                report(TraceFeedState.Expired, "Trace expired or not available on this Host");
                return;
            }
            catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                return;
            }
            catch (Exception ex)
            {
                report(TraceFeedState.Unavailable, "Trace lookup failed: " + ex.Message);
                return;
            }
            try { await Task.Delay(SnapshotPollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
        if (!acc.Complete) report(TraceFeedState.Unavailable, "Trace did not complete within the console's wait");
    }
}
