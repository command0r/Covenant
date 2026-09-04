using Covenant.Core;
using Covenant.Governance;
using Xunit;

namespace Covenant.Tests;

/// <summary>Rate stage guarantees: caps deny with 429 semantics before money is spent, windows reset, team caps isolate, and 0 = unlimited (opt-in).</summary>
public class RateLimitTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static InferencePipeline Pipeline(RateCounter counter, RateLimitConfig config, CountingSink sink)
        => new(
        [
            new AuditStage(sink),
            new RateLimitStage(counter, config),
        ]);

    private static InferenceContext Ctx(string team = "platform") =>
        new(new InferenceRequest("tester", [new ChatMessage(ChatRole.User, "hi")], null,
            new AttributionTags(team, "w", "u")));

    [Fact]
    public async Task Requests_over_the_team_cap_are_denied_as_rate_limited_and_audited()
    {
        var sink = new CountingSink();
        var pipeline = Pipeline(new RateCounter(), new RateLimitConfig { PerTeamPerMinute = 2 }, sink);

        var outcomes = new List<bool>();
        for (int i = 0; i < 4; i++)
        {
            var ctx = Ctx();
            await pipeline.ExecuteAsync(ctx, default);
            outcomes.Add(ctx.IsDenied);
        }

        Assert.Equal([false, false, true, true], outcomes);              // cap 2 → third and fourth denied
        Assert.Equal(4, sink.Entries.Count);                             // refusals audited like everything
        Assert.Contains("rate limit exceeded for team 'platform'", sink.Entries[2].Reason);
    }

    [Fact]
    public async Task Denial_kind_is_rate_limited_for_http_429_mapping()
    {
        var pipeline = Pipeline(new RateCounter(), new RateLimitConfig { PerTeamPerMinute = 1 }, new CountingSink());

        await pipeline.ExecuteAsync(Ctx(), default);
        var second = Ctx();
        await pipeline.ExecuteAsync(second, default);

        Assert.Equal(DenialKind.RateLimited, second.DenialKind);
    }

    [Fact]
    public async Task Window_resets_after_a_minute()
    {
        var clock = new ManualClock();
        var pipeline = Pipeline(new RateCounter(clock), new RateLimitConfig { PerTeamPerMinute = 1 }, new CountingSink());

        await pipeline.ExecuteAsync(Ctx(), default);
        clock.Now += TimeSpan.FromSeconds(61);
        var next = Ctx();
        await pipeline.ExecuteAsync(next, default);

        Assert.False(next.IsDenied);                                     // fresh window, fresh budget
    }

    [Fact]
    public async Task Team_caps_are_isolated_but_the_global_cap_bounds_everyone()
    {
        var sink = new CountingSink();
        var pipeline = Pipeline(new RateCounter(),
            new RateLimitConfig { PerTeamPerMinute = 5, GlobalPerMinute = 3 }, sink);

        var a = Ctx("payments"); await pipeline.ExecuteAsync(a, default);
        var b = Ctx("risk");     await pipeline.ExecuteAsync(b, default);
        var c = Ctx("platform"); await pipeline.ExecuteAsync(c, default);
        var d = Ctx("payments"); await pipeline.ExecuteAsync(d, default);

        Assert.False(a.IsDenied);
        Assert.False(b.IsDenied);
        Assert.False(c.IsDenied);
        Assert.True(d.IsDenied);                                         // appliance-wide cap wins
        Assert.Contains("appliance cap", d.DenialReason!);
    }

    [Fact]
    public async Task Zero_config_means_unlimited()
    {
        var pipeline = Pipeline(new RateCounter(), new RateLimitConfig(), new CountingSink());

        for (int i = 0; i < 50; i++)
        {
            var ctx = Ctx();
            await pipeline.ExecuteAsync(ctx, default);
            Assert.False(ctx.IsDenied);
        }
    }
}
