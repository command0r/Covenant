using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;

namespace Covenant.Host;

/// <summary>Wire shape of one trace event (docs/ui-api-contract.md v2 "Trace"). No content, no credentials, no
/// provider free text: stage/state/bounded reason code/target identity only.</summary>
public sealed class TraceEventView
{
    [JsonPropertyName("seq")] public required long Seq { get; init; }
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; init; }
    [JsonPropertyName("stage")] public required string Stage { get; init; }
    [JsonPropertyName("attempt_id")] public string? AttemptId { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("ts_utc")] public required DateTimeOffset TsUtc { get; init; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; init; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    /// <summary>Provider attempt events only: the request's own max_tokens (null when the caller sent none).</summary>
    [JsonPropertyName("requested_max_output_tokens")] public int? RequestedMaxOutputTokens { get; init; }
    /// <summary>Provider attempt events only: the bound actually sent to the provider for this attempt.</summary>
    [JsonPropertyName("effective_max_output_tokens")] public int? EffectiveMaxOutputTokens { get; init; }
}

/// <summary>GET /admin/playground/submissions/{client_request_id} (review round 2, B7).</summary>
public sealed class PlaygroundSubmissionView
{
    [JsonPropertyName("client_request_id")] public required string ClientRequestId { get; init; }
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    /// <summary>running | completed.</summary>
    [JsonPropertyName("state")] public required string State { get; init; }
}

public sealed class TraceSnapshotView
{
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; init; }
    [JsonPropertyName("complete")] public required bool Complete { get; init; }
    /// <summary>True when more than the per-request bound of events was emitted (later ones were dropped).</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("events")] public required List<TraceEventView> Events { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TraceEventView))]
[JsonSerializable(typeof(TraceSnapshotView))]
[JsonSerializable(typeof(PlaygroundSubmissionView))]
public partial class TraceJsonContext : JsonSerializerContext;

/// <summary>Per-request event buffer. Created by the Host BEFORE the pipeline starts, so the earliest stage events are
/// retained for a subscriber that connects after submission. Sequence numbers are monotonic from 1 in emission order;
/// clients de-duplicate by seq. Thread-safe (the pipeline appends while SSE readers wait).</summary>
public sealed class TraceBuffer
{
    private readonly object _gate = new();
    private readonly List<TraceEventView> _events = [];
    private readonly int _maxEvents;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TraceBuffer(string requestId, int? configVersion, TimeSpan ttl, DateTimeOffset createdUtc, int maxEvents)
    {
        RequestId = requestId;
        ConfigVersion = configVersion;
        Ttl = ttl;
        CreatedUtc = createdUtc;
        _maxEvents = maxEvents;
    }

    public string RequestId { get; }
    public int? ConfigVersion { get; }
    public TimeSpan Ttl { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset? CompletedUtc { get; private set; }
    public bool Truncated { get; private set; }

    public bool IsComplete
    {
        get { lock (_gate) return CompletedUtc is not null; }
    }

    public void Append(PipelineTraceEvent e)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (CompletedUtc is not null) return;
            if (_events.Count >= _maxEvents) { Truncated = true; return; }
            _events.Add(new TraceEventView
            {
                Seq = _events.Count + 1,
                RequestId = RequestId,
                ConfigVersion = ConfigVersion,
                Stage = e.Stage,
                AttemptId = e.AttemptId,
                State = e.State,
                TsUtc = e.TsUtc,
                DurationMs = e.DurationMs is { } d ? Math.Round(d, 3) : null,
                ReasonCode = e.ReasonCode,
                Target = e.Target,
                RequestedMaxOutputTokens = e.RequestedMaxOutputTokens,
                EffectiveMaxOutputTokens = e.EffectiveMaxOutputTokens,
            });
            signal = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    public void Complete(DateTimeOffset utc)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (CompletedUtc is not null) return;
            CompletedUtc = utc;
            signal = _changed;
        }
        signal.TrySetResult();
    }

    /// <summary>Events with seq &gt; <paramref name="afterSeq"/>, whether the run is complete, and a task that completes on
    /// the next change (taken atomically with the read, so no event can slip between read and wait).</summary>
    public (List<TraceEventView> Events, bool Complete, Task Changed) Since(long afterSeq)
    {
        lock (_gate)
        {
            var list = _events.Where(e => e.Seq > afterSeq).ToList();
            return (list, CompletedUtc is not null, _changed.Task);
        }
    }

    public TraceSnapshotView Snapshot()
    {
        lock (_gate)
            return new TraceSnapshotView
            {
                RequestId = RequestId, ConfigVersion = ConfigVersion, Complete = CompletedUtc is not null,
                Truncated = Truncated, Events = _events.ToList(),
            };
    }

    internal bool IsExpired(DateTimeOffset now) => CompletedUtc is { } done && now - done > Ttl;
}

/// <summary>In-memory trace registry (ADR-0013): ≤ <c>maxRequests</c> buffers (oldest completed evicted first), ≤ 64 events
/// each, retention = the request's configuration <c>trace_retention_minutes</c> after completion. Expired ids are
/// remembered (bounded) so the API can answer 410 Gone rather than 404 for them. Never persisted — the audit record holds
/// the compact durable summary.</summary>
public sealed class TraceHub(TimeProvider? clock = null, int maxRequests = 2000, int maxEventsPerRequest = InferenceContext.MaxTraceEvents)
{
    private const int MaxRememberedExpired = 4000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, TraceBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredOrder = new();

    public TimeProvider Clock => _clock;

    public TraceBuffer Begin(string requestId, int? configVersion, int retentionMinutes)
    {
        var ttl = TimeSpan.FromMinutes(Math.Clamp(retentionMinutes, 1, 1440));
        var buffer = new TraceBuffer(requestId, configVersion, ttl, _clock.GetUtcNow(), maxEventsPerRequest);
        lock (_gate)
        {
            Sweep();
            if (_buffers.Remove(requestId)) _order.Remove(requestId);
            _buffers[requestId] = buffer;
            _order.AddLast(requestId);
            while (_buffers.Count > maxRequests) EvictOne();
        }
        return buffer;
    }

    public enum Lookup { Found, Unknown, Expired }

    private const int MaxSubmissions = 4000;
    private readonly Dictionary<string, TraceBuffer> _submissions = new(StringComparer.Ordinal);
    private readonly Queue<string> _submissionOrder = new();

    /// <summary>Console inference (review round 2, B7): maps the browser's client_request_id to the server request id
    /// BEFORE the pipeline starts, so the console can subscribe to the live trace while the provider is still waiting.
    /// Kept for the trace TTL (until the buffer expires after completion); bounded (oldest dropped first).</summary>
    public void RegisterSubmission(string clientRequestId, TraceBuffer buffer)
    {
        lock (_gate)
        {
            if (!_submissions.ContainsKey(clientRequestId)) _submissionOrder.Enqueue(clientRequestId);
            _submissions[clientRequestId] = buffer;
            while (_submissions.Count > MaxSubmissions && _submissionOrder.Count > 0) _submissions.Remove(_submissionOrder.Dequeue());
        }
    }

    /// <summary>The request a client_request_id was registered for, and whether it is still running. False when unknown
    /// or past the trace retention.</summary>
    public bool TryGetSubmission(string clientRequestId, out string requestId, out bool complete)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_submissions.TryGetValue(clientRequestId, out var buffer) && !buffer.IsExpired(now))
            {
                requestId = buffer.RequestId;
                complete = buffer.IsComplete;
                return true;
            }
            _submissions.Remove(clientRequestId);
            requestId = "";
            complete = false;
            return false;
        }
    }

    public Lookup TryGet(string requestId, out TraceBuffer? buffer)
    {
        lock (_gate)
        {
            Sweep();
            if (_buffers.TryGetValue(requestId, out buffer)) return Lookup.Found;
            return _expired.Contains(requestId) ? Lookup.Expired : Lookup.Unknown;
        }
    }

    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        var expired = _buffers.Values.Where(b => b.IsExpired(now)).Select(b => b.RequestId).ToList();
        foreach (var id in expired) Forget(id);
    }

    private void EvictOne()
    {
        // Oldest COMPLETED first; only when every buffer is still running is the oldest running one dropped.
        string? victim = null;
        foreach (var id in _order)
            if (_buffers[id].IsComplete) { victim = id; break; }
        victim ??= _order.First?.Value;
        if (victim is not null) Forget(victim);
    }

    private void Forget(string id)
    {
        _buffers.Remove(id);
        _order.Remove(id);
        if (_expired.Add(id))
        {
            _expiredOrder.Enqueue(id);
            while (_expiredOrder.Count > MaxRememberedExpired) _expired.Remove(_expiredOrder.Dequeue());
        }
    }
}

/// <summary>GET /admin/requests/{id}/trace (snapshot) and /trace/stream (SSE replay-then-live, Last-Event-ID resume,
/// <c>event: end</c> on completion; 404 unknown, 410 expired). Admin token required. AOT-safe (source-generated JSON).</summary>
public static class TraceApi
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    private static IResult Error(string error, string reason, int status, string? requestId = null)
        => Results.Json(new ErrorResponse { Error = error, Reason = reason, RequestId = requestId }, CovenantJsonContext.Default.ErrorResponse, statusCode: status);

    private static IResult? Missing(TraceHub.Lookup lookup, string id) => lookup switch
    {
        TraceHub.Lookup.Expired => Error("expired", "the transient trace for this request has expired; the audit record keeps the compact summary", StatusCodes.Status410Gone, id),
        TraceHub.Lookup.Unknown => Error("not_found", "no trace for this request id (unknown, or never traced by this process)", StatusCodes.Status404NotFound, id),
        _ => null,
    };

    public static void MapTrace(this WebApplication app, TraceHub hub, Func<HttpContext, bool> authorized)
    {
        app.MapGet("/admin/requests/{id}/trace", (HttpContext http, string id) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            if (!RequestCorrelation.IsWellFormed(id))
                return Error("invalid_id", "request id must be ≤64 chars of [A-Za-z0-9_-]", StatusCodes.Status400BadRequest);
            var lookup = hub.TryGet(id, out var buffer);
            if (Missing(lookup, id) is { } missing) return missing;
            return Results.Json(buffer!.Snapshot(), TraceJsonContext.Default.TraceSnapshotView);
        });

        app.MapGet("/admin/requests/{id}/trace/stream", async Task<IResult> (HttpContext http, string id, CancellationToken ct) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            if (!RequestCorrelation.IsWellFormed(id))
                return Error("invalid_id", "request id must be ≤64 chars of [A-Za-z0-9_-]", StatusCodes.Status400BadRequest);
            var lookup = hub.TryGet(id, out var buffer);
            if (Missing(lookup, id) is { } missing) return missing;

            long after = 0;
            if (http.Request.Headers["Last-Event-ID"].FirstOrDefault() is { Length: > 0 } last
                && long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                after = parsed;

            var resp = http.Response;
            resp.StatusCode = StatusCodes.Status200OK;
            resp.ContentType = "text/event-stream";
            resp.Headers.CacheControl = "no-store";
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (events, complete, changed) = buffer!.Since(after);
                    foreach (var e in events)
                    {
                        var json = JsonSerializer.Serialize(e, TraceJsonContext.Default.TraceEventView);
                        await resp.WriteAsync($"id: {e.Seq.ToString(CultureInfo.InvariantCulture)}\ndata: {json}\n\n", ct);
                        after = e.Seq;
                    }
                    if (complete)
                    {
                        await resp.WriteAsync("event: end\ndata: {}\n\n", ct);
                        await resp.Body.FlushAsync(ct);
                        break;
                    }
                    await resp.Body.FlushAsync(ct);
                    var finished = await Task.WhenAny(changed, Task.Delay(Heartbeat, ct));
                    if (finished != changed && !ct.IsCancellationRequested)
                        await resp.WriteAsync(": keep-alive\n\n", ct);
                }
            }
            catch (OperationCanceledException) { /* subscriber went away — normal */ }
            return Results.Empty;
        });
    }
}

/// <summary>Completes a request's trace buffer when the handler finishes (using-scope helper for multi-return handlers).</summary>
public sealed class TraceCompletion(TraceBuffer buffer, TimeProvider clock) : IDisposable
{
    public void Dispose() => buffer.Complete(clock.GetUtcNow());
}
