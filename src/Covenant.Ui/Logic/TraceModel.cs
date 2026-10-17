using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

// Pure reduction of real trace events (ADR-0013) into what the TraceBar shows. Nothing here invents a stage, a
// state or a duration: a stage the server never reported is "pending" while the trace is still open and
// "not reported" once it is complete; durations come only from the events' duration_ms.

public enum TraceStepState { Pending, Running, Passed, Refused, Failed, Cancelled, Skipped, NotReported }

/// <summary>One provider attempt (fallback is a second attempt, never a hidden retry). The output bounds come from
/// the attempt's trace events (review round 2, B12): requested = the request's own max_tokens, effective = the bound
/// actually sent to the provider for this attempt. Null = not reported.</summary>
public sealed record TraceAttemptView(
    int Number,
    string? AttemptId,
    TraceStepState State,
    string? Target,
    double? DurationMs,
    string? ReasonCode)
{
    public int? RequestedMaxOutputTokens { get; init; }
    public int? EffectiveMaxOutputTokens { get; init; }
}

/// <summary>One pipeline stage as reported. <see cref="Attempts"/> is non-empty only for the provider stage.</summary>
public sealed record TraceStepView(
    string Stage,
    string Label,
    TraceStepState State,
    double? DurationMs,
    string? ReasonCode,
    string? Target,
    DateTimeOffset? FirstTsUtc,
    IReadOnlyList<TraceAttemptView> Attempts);

public sealed record TraceView(IReadOnlyList<TraceStepView> Steps, bool Complete, long LastSeq, int? ConfigVersion, string Source)
{
    public static readonly TraceView Empty = new(TraceModel.EmptySteps(false), false, 0, null, "none");

    public bool HasEvents => LastSeq > 0 || Steps.Any(s => s.State != TraceStepState.Pending && s.State != TraceStepState.NotReported);
}

public static class TraceModel
{
    /// <summary>Canonical stage order (ADR-0013). Unknown stages from a newer Host are appended in seq order.</summary>
    public static readonly IReadOnlyList<string> Stages =
        ["auth", "shape", "classify", "policy", "cache", "rate_limit", "pause", "budget", "provider", "attribution"];

    public static string Label(string stage) => stage switch
    {
        "auth" => "Auth",
        "shape" => "Shape",
        "classify" => "Classify",
        "policy" => "Policy",
        "cache" => "Cache",
        "rate_limit" => "Rate limit",
        "pause" => "Pause",
        "budget" => "Budget",
        "provider" => "Provider",
        "attribution" => "Attribution",
        _ => stage,
    };

    public static TraceStepState ParseState(string? state) => state switch
    {
        "pending" => TraceStepState.Pending,
        "running" => TraceStepState.Running,
        "passed" => TraceStepState.Passed,
        "refused" => TraceStepState.Refused,
        "failed" => TraceStepState.Failed,
        "cancelled" => TraceStepState.Cancelled,
        "skipped" => TraceStepState.Skipped,
        _ => TraceStepState.NotReported,
    };

    public static string StateText(TraceStepState s) => s switch
    {
        TraceStepState.Pending => "Pending",
        TraceStepState.Running => "Running",
        TraceStepState.Passed => "Passed",
        TraceStepState.Refused => "Refused",
        TraceStepState.Failed => "Failed",
        TraceStepState.Cancelled => "Cancelled",
        TraceStepState.Skipped => "Skipped",
        _ => "Not reported",
    };

    internal static IReadOnlyList<TraceStepView> EmptySteps(bool complete)
        => Stages.Select(s => new TraceStepView(s, Label(s), complete ? TraceStepState.NotReported : TraceStepState.Pending,
            null, null, null, null, [])).ToList();

    /// <summary>Reduces events (any order, possibly duplicated) into one step per stage. Duplicates are dropped by
    /// <c>seq</c> (first copy wins — a replay never overrides what was already applied); events are then applied in
    /// <c>seq</c> order, so a late-delivered earlier event cannot overwrite a later state. Provider events are
    /// grouped into attempts by <c>attempt_id</c> in order of first appearance.</summary>
    public static TraceView Reduce(IEnumerable<TraceEventDto> events, bool complete, int? configVersion = null, string source = "live")
    {
        var unique = new Dictionary<long, TraceEventDto>();
        foreach (var e in events)
            if (e is not null && !string.IsNullOrWhiteSpace(e.Stage))
                unique.TryAdd(e.Seq, e);
        var ordered = unique.Values.OrderBy(e => e.Seq).ToList();

        var byStage = new Dictionary<string, List<TraceEventDto>>(StringComparer.Ordinal);
        var extraOrder = new List<string>();
        foreach (var e in ordered)
        {
            var stage = e.Stage.Trim().ToLowerInvariant();
            if (!byStage.TryGetValue(stage, out var list))
            {
                byStage[stage] = list = [];
                if (!Stages.Contains(stage)) extraOrder.Add(stage);
            }
            list.Add(e);
            configVersion ??= e.ConfigVersion;
        }

        var steps = new List<TraceStepView>(Stages.Count + extraOrder.Count);
        foreach (var stage in Stages.Concat(extraOrder))
        {
            if (!byStage.TryGetValue(stage, out var evs))
            {
                steps.Add(new TraceStepView(stage, Label(stage), complete ? TraceStepState.NotReported : TraceStepState.Pending,
                    null, null, null, null, []));
                continue;
            }
            steps.Add(stage == "provider" ? ReduceProvider(evs) : ReduceStage(stage, evs));
        }

        long lastSeq = ordered.Count > 0 ? ordered[^1].Seq : 0;
        return new TraceView(steps, complete, lastSeq, configVersion, source);
    }

    private static TraceStepView ReduceStage(string stage, List<TraceEventDto> evs)
    {
        var last = evs[^1];
        double? duration = null;
        string? reason = null, target = null;
        foreach (var e in evs)
        {
            if (e.DurationMs is { } d) duration = d;
            if (!string.IsNullOrWhiteSpace(e.ReasonCode)) reason = e.ReasonCode;
            if (!string.IsNullOrWhiteSpace(e.Target)) target = e.Target;
        }
        return new TraceStepView(stage, Label(stage), ParseState(last.State), duration, reason, target, evs[0].TsUtc, []);
    }

    private static TraceStepView ReduceProvider(List<TraceEventDto> evs)
    {
        // Group by attempt id in order of first appearance. Events WITHOUT an attempt id describe the stage itself
        // (e.g. "skipped: not_reached", "pending") — they are an attempt only when nothing else is reported and the
        // stage actually ran (a provider event that is neither pending nor skipped).
        var ids = new List<string>();
        var byId = new Dictionary<string, List<TraceEventDto>>(StringComparer.Ordinal);
        var stageLevel = new List<TraceEventDto>();
        foreach (var e in evs)
        {
            if (string.IsNullOrWhiteSpace(e.AttemptId)) { stageLevel.Add(e); continue; }
            if (!byId.TryGetValue(e.AttemptId, out var list))
            {
                byId[e.AttemptId] = list = [];
                ids.Add(e.AttemptId);
            }
            list.Add(e);
        }

        var attempts = new List<TraceAttemptView>();
        foreach (var id in ids)
        {
            var r = ReduceStage("provider", byId[id]);
            int? requested = null, effective = null;
            foreach (var e in byId[id])
            {
                if (e.RequestedMaxOutputTokens is { } rq) requested = rq;
                if (e.EffectiveMaxOutputTokens is { } ef) effective = ef;
            }
            attempts.Add(new TraceAttemptView(attempts.Count + 1, id, r.State, r.Target, r.DurationMs, r.ReasonCode)
            {
                RequestedMaxOutputTokens = requested,
                EffectiveMaxOutputTokens = effective,
            });
        }
        if (attempts.Count == 0 && stageLevel.Count > 0)
        {
            var r = ReduceStage("provider", stageLevel);
            if (r.State is not (TraceStepState.Pending or TraceStepState.Skipped or TraceStepState.NotReported))
                attempts.Add(new TraceAttemptView(1, null, r.State, r.Target, r.DurationMs, r.ReasonCode));
        }

        TraceStepState state;
        if (attempts.Any(a => a.State == TraceStepState.Running)) state = TraceStepState.Running;
        else if (attempts.Count > 0) state = attempts[^1].State;
        else state = ParseState(stageLevel[^1].State);

        double? total = null;
        foreach (var a in attempts)
            if (a.DurationMs is { } d) total = (total ?? 0) + d;
        if (attempts.Count == 0)
            foreach (var e in stageLevel)
                if (e.DurationMs is { } d) total = d;

        var lastAttempt = attempts.Count > 0 ? attempts[^1] : null;
        string? reason = lastAttempt?.ReasonCode;
        if (reason is null)
            foreach (var e in stageLevel)
                if (!string.IsNullOrWhiteSpace(e.ReasonCode)) reason = e.ReasonCode;
        return new TraceStepView("provider", Label("provider"), state, total, reason, lastAttempt?.Target, evs[0].TsUtc, attempts);
    }

    /// <summary>Builds a view from the compact trace persisted on the audit record (audit v3 <c>Trace[]</c>, Core
    /// AuditTraceStep) when the live trace has expired. Provider steps carry their <c>AttemptId</c>; the matching
    /// <c>Attempts[]</c> entry supplies the target.</summary>
    public static TraceView? FromAuditSummary(IReadOnlyList<AuditTraceStepView>? summary, IReadOnlyList<AuditAttemptView>? attempts = null, int? configVersion = null)
    {
        if (summary is null || summary.Count == 0) return null;
        var events = new List<TraceEventDto>(summary.Count);
        long seq = 0;
        foreach (var step in summary)
        {
            if (string.IsNullOrWhiteSpace(step.Stage) || string.IsNullOrWhiteSpace(step.State)) continue;
            string? target = null;
            if (step.AttemptId is { Length: > 0 } aid && attempts?.FirstOrDefault(a => string.Equals(a.AttemptId, aid, StringComparison.Ordinal)) is { } attempt)
                target = Target(attempt);
            events.Add(new TraceEventDto
            {
                Seq = ++seq, Stage = step.Stage, State = step.State, DurationMs = step.DurationMs,
                AttemptId = step.AttemptId, ReasonCode = step.ReasonCode, Target = target,
            });
        }
        return events.Count == 0 ? null : Reduce(events, complete: true, configVersion, source: "audit summary");
    }

    /// <summary>"connection/model" for an attempt, or whichever half is recorded.</summary>
    public static string? Target(AuditAttemptView a)
    {
        var c = string.IsNullOrWhiteSpace(a.ConnectionId) ? null : a.ConnectionId;
        var m = string.IsNullOrWhiteSpace(a.ModelId) ? null : a.ModelId;
        if (c is null && m is null) return null;
        return c is null ? m : m is null ? c : c + "/" + m;
    }
}

/// <summary>Collects trace events for one request as they arrive (live stream, replay after reconnect, snapshot)
/// and remembers what is needed to resume: the highest seq seen (sent as Last-Event-ID) and whether the server
/// said the trace is complete or expired. Not thread-safe; the console is single-threaded (WASM).</summary>
public sealed class TraceAccumulator
{
    private readonly Dictionary<long, TraceEventDto> _events = new();

    public bool Complete { get; private set; }
    public bool Expired { get; private set; }
    public int? ConfigVersion { get; private set; }
    public long LastSeq { get; private set; }
    public int Count => _events.Count;

    /// <summary>Adds one event; false when that seq was already applied (duplicate from a replay).</summary>
    public bool Add(TraceEventDto e)
    {
        if (e is null || string.IsNullOrWhiteSpace(e.Stage)) return false;
        if (!_events.TryAdd(e.Seq, e)) return false;
        if (e.Seq > LastSeq) LastSeq = e.Seq;
        ConfigVersion ??= e.ConfigVersion;
        return true;
    }

    public int AddRange(IEnumerable<TraceEventDto> events)
    {
        int added = 0;
        foreach (var e in events)
            if (Add(e)) added++;
        return added;
    }

    public void ApplySnapshot(TraceSnapshotDto snapshot)
    {
        AddRange(snapshot.Events);
        ConfigVersion ??= snapshot.ConfigVersion;
        if (snapshot.Complete) Complete = true;
    }

    public void MarkComplete() => Complete = true;
    public void MarkExpired() => Expired = true;

    public TraceView View(string source = "live") => TraceModel.Reduce(_events.Values, Complete, ConfigVersion, source);
}

/// <summary>One parsed Server-Sent Event.</summary>
public sealed record SseEvent(string? Id, string Event, string Data);

/// <summary>Line-oriented SSE parser (WHATWG event-stream rules, the subset Covenant emits): "id:", "event:",
/// "data:" (multi-line joined with \n), ":" comments; a blank line dispatches. Pure, so it is unit-tested.</summary>
public sealed class SseParser
{
    private string? _id;
    private string? _event;
    private System.Text.StringBuilder? _data;

    /// <summary>Feeds one line (without its terminator). Returns an event when the line completes one.</summary>
    public SseEvent? Feed(string line)
    {
        if (line.Length == 0)
        {
            if (_data is null && _event is null) { _id = null; return null; }
            var ev = new SseEvent(_id, _event ?? "message", _data?.ToString() ?? "");
            _id = null; _event = null; _data = null;
            return ev;
        }
        if (line[0] == ':') return null;
        int colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "id": _id = value; break;
            case "event": _event = value; break;
            case "data":
                if (_data is null) _data = new System.Text.StringBuilder(value);
                else _data.Append('\n').Append(value);
                break;
        }
        return null;
    }
}
