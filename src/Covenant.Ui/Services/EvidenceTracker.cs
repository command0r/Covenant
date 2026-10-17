using System.Net;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

public enum EvidenceState { Idle, Pending, Found, Unavailable, Error }

/// <summary>Lookup state for one request's audit record. Never claims the audit write failed: a bounded
/// lookup that finds nothing reports "not in the current audit segment yet".</summary>
public sealed record EvidenceStatus(EvidenceState State, AuditEntryView? Record = null, string? Message = null, int Attempts = 0)
{
    public static readonly EvidenceStatus Idle = new(EvidenceState.Idle);
}

/// <summary>Polls GET /admin/requests/{id} (admin token, via AdminApi) after a governed request. The audit sink
/// is asynchronous, so 404 means "pending" for a bounded time (≈15 s), then "unavailable — retry".</summary>
public sealed class EvidenceTracker(AdminApi api, AppState state)
{
    public const string UnavailableMessage = "Not in the current audit segment yet — retry.";

    /// <summary>Waits before each attempt, in seconds (sum ≈ 14 s).</summary>
    public static readonly double[] RetryDelaysSeconds = [0.5, 1, 1.5, 2, 3, 3, 3];

    public async Task<EvidenceStatus> TrackAsync(string id, Action<EvidenceStatus>? progress, CancellationToken ct)
    {
        int attempts = 0;
        foreach (var delay in RetryDelaysSeconds)
        {
            progress?.Invoke(new EvidenceStatus(EvidenceState.Pending, Attempts: attempts));
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
            catch (OperationCanceledException) { return new EvidenceStatus(EvidenceState.Idle, Attempts: attempts); }

            attempts++;
            var r = await LookupOnceAsync(id, ct);
            if (r.State != EvidenceState.Pending) return r with { Attempts = attempts };
        }
        return new EvidenceStatus(EvidenceState.Unavailable, Message: UnavailableMessage, Attempts: attempts);
    }

    /// <summary>One lookup. 404 → Pending (caller decides whether to keep waiting).</summary>
    public async Task<EvidenceStatus> LookupOnceAsync(string id, CancellationToken ct)
    {
        if (!state.Connected)
            return new EvidenceStatus(EvidenceState.Error, Message: "Connect the console (admin token) to look up evidence.");
        try
        {
            var record = await api.GetRequestAsync(id, ct);
            return new EvidenceStatus(EvidenceState.Found, record);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return new EvidenceStatus(EvidenceState.Pending);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            return new EvidenceStatus(EvidenceState.Error, Message: "Admin token rejected — reconnect to look up evidence.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EvidenceStatus(EvidenceState.Idle);
        }
        catch (Exception ex)
        {
            return new EvidenceStatus(EvidenceState.Error, Message: "Evidence lookup failed: " + ex.Message);
        }
    }
}
