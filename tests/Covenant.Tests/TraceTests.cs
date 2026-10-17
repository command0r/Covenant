using System.Net;
using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0013 real request trace: emitted by the pipeline itself (server UTC time, Stopwatch durations, bounded
/// reason codes), buffered BEFORE the pipeline starts, served as snapshot and SSE (replay-then-live, Last-Event-ID),
/// skipped stages explicit, compact summary persisted in the audit record.</summary>
public sealed class TraceTests : IDisposable
{
    private static readonly string[] Stages = ["auth", "shape", "classify", "policy", "cache", "rate_limit", "pause", "budget", "provider", "attribution"];

    private readonly HybridSeedFactory _factory = new();
    private readonly HttpClient _client;

    public TraceTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static List<(long Id, string? Event, JsonElement Data)> ParseSse(string body)
    {
        var list = new List<(long, string?, JsonElement)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            long id = 0;
            string? evt = null;
            string? data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal)) id = long.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture);
                else if (line.StartsWith("event: ", StringComparison.Ordinal)) evt = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            }
            if (data is null) continue;
            list.Add((id, evt, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return list;
    }

    private async Task<string> RunConsoleAsync(string json)
    {
        using var resp = await Playground.PostAsync(_client, json);
        return CorrelationTestKit.HeaderId(resp);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, string? lastEventId = null)
    {
        using var req = CorrelationTestKit.Admin(HttpMethod.Get, path);
        if (lastEventId is not null) req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        using var resp = await _client.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Subscribing_after_completion_replays_every_event_then_ends()
    {
        var id = await RunConsoleAsync(Playground.Body("demo-app", "Summarize this public announcement.", profile: "public-demo"));
        var (status, body) = await GetAsync($"/admin/requests/{id}/trace/stream");
        Assert.Equal(HttpStatusCode.OK, status);
        var events = ParseSse(body);
        Assert.Equal("end", events[^1].Event);
        var data = events.Where(e => e.Event is null).ToList();
        Assert.Equal(Enumerable.Range(1, data.Count).Select(i => (long)i), data.Select(e => e.Id));   // monotonic seq from 1
        Assert.All(data, e => Assert.Equal(id, e.Data.GetProperty("request_id").GetString()));
        Assert.All(data, e => Assert.Equal(1, e.Data.GetProperty("config_version").GetInt32()));

        foreach (var stage in Stages)
        {
            Assert.Contains(data, e => e.Data.GetProperty("stage").GetString() == stage && e.Data.GetProperty("state").GetString() == "running");
            var terminal = data.Last(e => e.Data.GetProperty("stage").GetString() == stage && e.Data.GetProperty("state").GetString() != "running");
            Assert.Equal("passed", terminal.Data.GetProperty("state").GetString());
            Assert.True(terminal.Data.GetProperty("duration_ms").GetDouble() >= 0);
        }
        var attempt = data.First(e => e.Data.TryGetProperty("attempt_id", out _));
        Assert.Equal("att_1", attempt.Data.GetProperty("attempt_id").GetString());
        Assert.Equal($"openai/{V2.CloudModel}", attempt.Data.GetProperty("target").GetString());
        Assert.DoesNotContain("public announcement", body, StringComparison.Ordinal);   // never content
        Assert.DoesNotContain("cloud reply", body, StringComparison.Ordinal);

        // Snapshot: same events, complete.
        var (snapStatus, snap) = await GetAsync($"/admin/requests/{id}/trace");
        Assert.Equal(HttpStatusCode.OK, snapStatus);
        using var doc = JsonDocument.Parse(snap);
        Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(data.Count, doc.RootElement.GetProperty("events").GetArrayLength());

        // Durable compact summary in the audit record.
        var audit = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.NotNull(audit.Trace);
        Assert.All(Stages.Where(s => s != "provider"), s => Assert.Contains(audit.Trace!, t => t.Stage == s && t.State == "passed"));
        Assert.Contains(audit.Trace!, t => t.Stage == "provider" && t.AttemptId == "att_1" && t.State == "passed");
    }

    [Fact]
    public async Task Last_Event_ID_resumes_after_the_given_sequence()
    {
        var id = await RunConsoleAsync(Playground.Body("demo-app", "an internal question"));
        var (_, all) = await GetAsync($"/admin/requests/{id}/trace/stream");
        int total = ParseSse(all).Count(e => e.Event is null);
        var (_, resumed) = await GetAsync($"/admin/requests/{id}/trace/stream", lastEventId: "5");
        var events = ParseSse(resumed).Where(e => e.Event is null).ToList();
        Assert.Equal(total - 5, events.Count);
        Assert.Equal(6, events[0].Id);
        Assert.Equal("end", ParseSse(resumed)[^1].Event);
    }

    [Fact]
    public async Task Refusal_marks_every_stage_not_entered_as_skipped_not_reached()
    {
        var id = await RunConsoleAsync(Playground.Body("demo-app", "hello", profile: "not-granted"));
        var (_, snap) = await GetAsync($"/admin/requests/{id}/trace");
        using var doc = JsonDocument.Parse(snap);
        var events = doc.RootElement.GetProperty("events").EnumerateArray().ToList();
        var auth = events.Last(e => e.GetProperty("stage").GetString() == "auth");
        Assert.Equal("refused", auth.GetProperty("state").GetString());
        Assert.Equal("governance_denied", auth.GetProperty("reason_code").GetString());
        foreach (var stage in Stages.Skip(1))
        {
            var ev = Assert.Single(events, e => e.GetProperty("stage").GetString() == stage);
            Assert.Equal("skipped", ev.GetProperty("state").GetString());
            Assert.Equal("not_reached", ev.GetProperty("reason_code").GetString());
        }
        var entry = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Contains(entry.Trace!, t => t.Stage == "provider" && t.State == "skipped" && t.ReasonCode == "not_reached");
        Assert.Empty(entry.Attempts!);
    }

    [Fact]
    public async Task A_subscriber_joining_mid_request_gets_the_early_events_then_live_ones_until_the_end()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var client = factory.CreateClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Cloud.Gate = gate;
        factory.Cloud.Then(StubChatClient.Mode.StreamGated);

        // Streaming console request: headers (with the request id) commit on the first delta; the provider then waits.
        using var req = CorrelationTestKit.Admin(HttpMethod.Post, "/admin/playground/requests");
        req.Content = new StringContent(Playground.Body("demo-app", "Summarize this public announcement.", profile: "public-demo", stream: true),
            System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);

        using var streamReq = CorrelationTestKit.Admin(HttpMethod.Get, $"/admin/requests/{id}/trace/stream");
        using var stream = await client.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

        using var snapReq = CorrelationTestKit.Admin(HttpMethod.Get, $"/admin/requests/{id}/trace");
        using var snapResp = await client.SendAsync(snapReq);
        using (var snap = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync()))
        {
            Assert.False(snap.RootElement.GetProperty("complete").GetBoolean());   // still in flight
            var first = snap.RootElement.GetProperty("events")[0];
            Assert.Equal(1, first.GetProperty("seq").GetInt64());
            Assert.Equal("auth", first.GetProperty("stage").GetString());          // the very first event was retained
            Assert.Equal("running", first.GetProperty("state").GetString());
        }

        gate.SetResult();
        var body = await stream.Content.ReadAsStringAsync();       // completes when the server writes event: end
        await resp.Content.ReadAsStringAsync();
        var events = ParseSse(body);
        Assert.Equal("end", events[^1].Event);
        var data = events.Where(e => e.Event is null).ToList();
        Assert.Equal(1, data[0].Id);
        Assert.Equal(Enumerable.Range(1, data.Count).Select(i => (long)i), data.Select(e => e.Id));
        Assert.Contains(data, e => e.Data.GetProperty("stage").GetString() == "attribution" && e.Data.GetProperty("state").GetString() == "passed");
    }

    [Fact]
    public async Task Unknown_trace_is_404_and_malformed_id_is_400()
    {
        var (status, _) = await GetAsync("/admin/requests/req_00000000000000000000000000000000/trace");
        Assert.Equal(HttpStatusCode.NotFound, status);
        var (streamStatus, _) = await GetAsync("/admin/requests/req_00000000000000000000000000000000/trace/stream");
        Assert.Equal(HttpStatusCode.NotFound, streamStatus);
        var (bad, _) = await GetAsync("/admin/requests/bad%20id/trace");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        using var noToken = await _client.GetAsync("/admin/requests/req_00000000000000000000000000000000/trace");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }
}

/// <summary>TraceHub bounds and retention (no Host).</summary>
public sealed class TraceHubTests
{
    private static PipelineTraceEvent Ev(string stage, string state) => new(stage, state, DateTimeOffset.UtcNow);

    [Fact]
    public void Expired_traces_are_410_not_404_and_unknown_ids_are_404()
    {
        var clock = new SettableClock(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
        var hub = new TraceHub(clock);
        var b = hub.Begin("req_a", 1, retentionMinutes: 10);
        b.Append(Ev("auth", "running"));
        b.Complete(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(TraceHub.Lookup.Found, hub.TryGet("req_a", out _));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TraceHub.Lookup.Expired, hub.TryGet("req_a", out _));
        Assert.Equal(TraceHub.Lookup.Unknown, hub.TryGet("req_never", out _));
    }

    [Fact]
    public void Events_and_requests_are_bounded()
    {
        var hub = new TraceHub(maxRequests: 3, maxEventsPerRequest: 4);
        var b = hub.Begin("req_0", 1, 10);
        for (int i = 0; i < 10; i++) b.Append(Ev("auth", "running"));
        var snap = b.Snapshot();
        Assert.Equal(4, snap.Events.Count);
        Assert.True(snap.Truncated);
        b.Complete(DateTimeOffset.UtcNow);

        for (int i = 1; i <= 3; i++) hub.Begin($"req_{i}", 1, 10);   // a 4th buffer evicts the oldest COMPLETED one
        Assert.Equal(TraceHub.Lookup.Expired, hub.TryGet("req_0", out _));
        Assert.Equal(TraceHub.Lookup.Found, hub.TryGet("req_3", out _));
    }

    [Fact]
    public void A_completed_buffer_accepts_no_more_events_and_wakes_waiters()
    {
        var hub = new TraceHub();
        var b = hub.Begin("req_x", 2, 10);
        var (_, complete, changed) = b.Since(0);
        Assert.False(complete);
        b.Append(Ev("auth", "running"));
        Assert.True(changed.IsCompleted);
        var (_, _, next) = b.Since(1);
        b.Complete(DateTimeOffset.UtcNow);
        Assert.True(next.IsCompleted);
        b.Append(Ev("shape", "running"));
        Assert.Single(b.Snapshot().Events);
        Assert.Equal(2, b.Snapshot().ConfigVersion);
    }
}
