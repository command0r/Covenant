using System.Diagnostics;
using Covenant.Core;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0003 guarantees: spans carry the governance outcome as metadata, never content. Raw ActivityListener — exactly the seam the Host wires the SDK to.</summary>
public sealed class TelemetryTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;

    public TelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == CovenantDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (_activities) _activities.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // Tests may run in parallel with other pipeline tests that also emit spans; a unique team tag
    // isolates this test's trace, and TraceId isolates its stage spans.
    private (Activity Root, List<Activity> Trace) FindTrace(string team)
    {
        lock (_activities)
        {
            var root = _activities.Single(a =>
                a.OperationName == "covenant.request" && (string?)a.GetTagItem("covenant.team") == team);
            var trace = _activities.Where(a => a.TraceId == root.TraceId).ToList();
            return (root, trace);
        }
    }

    [Fact]
    public async Task Allowed_request_span_carries_outcome_route_usage_and_cost()
    {
        var team = $"otel-allow-{Guid.NewGuid():n}";
        var ctx = await TelemetryPipeline.RunAsync("just a normal internal question", team, failClosed: false);

        var (root, trace) = FindTrace(team);

        Assert.Equal("allow", (string?)root.GetTagItem("covenant.effect"));
        Assert.Equal("Internal", (string?)root.GetTagItem("covenant.classification"));
        Assert.Equal("gpt-4o-mini", (string?)root.GetTagItem("covenant.model"));
        Assert.Equal(10L, root.GetTagItem("covenant.tokens.input"));
        Assert.True((double)root.GetTagItem("covenant.cost_usd")! > 0);
        Assert.Contains(trace, a => a.OperationName.StartsWith("covenant.stage."));   // per-stage spans exist
        Assert.False(ctx.IsDenied);
    }

    [Fact]
    public async Task Denied_request_span_records_deny_with_reason_and_error_status()
    {
        var team = $"otel-deny-{Guid.NewGuid():n}";
        await TelemetryPipeline.RunAsync("my SSN is 123-45-6789", team, failClosed: true);

        var (root, _) = FindTrace(team);

        Assert.Equal("deny", (string?)root.GetTagItem("covenant.effect"));
        Assert.Equal("Pii", (string?)root.GetTagItem("covenant.classification"));
        Assert.Equal("Governance", (string?)root.GetTagItem("covenant.denial_kind"));
        Assert.Equal(ActivityStatusCode.Error, root.Status);
    }

    [Fact]
    public async Task No_span_ever_contains_message_content()
    {
        var marker = $"canary-{Guid.NewGuid():n}";
        var team = $"otel-leak-{Guid.NewGuid():n}";
        await TelemetryPipeline.RunAsync($"my SSN is 123-45-6789 and the secret is {marker}", team, failClosed: true);

        var (_, trace) = FindTrace(team);

        foreach (var activity in trace)
            foreach (var tag in activity.TagObjects)
                Assert.DoesNotContain(marker, tag.Value?.ToString() ?? "");
    }
}
