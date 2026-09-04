using Covenant.Core;
using Covenant.Governance;
using Xunit;

namespace Covenant.Tests;

/// <summary>Rate stage guarantees: caps deny with 429 semantics before money is spent, windows reset, team caps isolate (and cannot starve the appliance), and 0 = unlimited.</summary>
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

    private static InferencePipeline Pipeline(RateLimitConfig config, CountingSink sink, TimeProvider? clock = null)
        => new(
        [
            new AuditStage(sink),
            new RateLimitStage(new RateCounter(clock), new RateCounter(clock), config),
        ]);

    private static InferenceContext Ctx(string team = "platform") =>
        new(new InferenceRequest("tester", [new ChatMessage(ChatRole.User, "hi")], null,
            new AttributionTags(team, "w", "u")));

    [Fact]
    public async Task Requests_over_the_team_cap_are_denied_as_rate_limited_and_audited()
    {
        var sink = new CountingSink();
        var pipeline = Pipeline(new RateLimitConfig { PerTeamPerMinute = 2 }, sink);

        var outcomes = new List<bool>();
        for (int i = 0; i < 4; i++)
        {
            var ctx = Ctx();
            await pipeline.ExecuteAsync(ctx, default);
            outcomes.Add(ctx.IsDenied);
        }

        Assert.Equal([false, false, true, true], outcomes);              // cap 2 → exactly 2 admitted
        Assert.Equal(4, sink.Entries.Count);                             // refusals audited like everything
        Assert.Contains("rate limit exceeded for team 'platform'", sink.Entries[2].Reason);
    }

    [Fact]
    public async Task Denial_kind_is_rate_limited()
    {
        var pipeline = Pipeline(new RateLimitConfig { PerTeamPerMinute = 1 }, new CountingSink());

        await pipeline.ExecuteAsync(Ctx(), default);
        var second = Ctx();
        await pipeline.ExecuteAsync(second, default);

        Assert.Equal(DenialKind.RateLimited, second.DenialKind);
    }

    [Fact]
    public async Task Window_resets_after_a_minute()
    {
        var clock = new ManualClock();
        var pipeline = Pipeline(new RateLimitConfig { PerTeamPerMinute = 1 }, new CountingSink(), clock);

        await pipeline.ExecuteAsync(Ctx(), default);
        clock.Now += TimeSpan.FromSeconds(61);
        var next = Ctx();
        await pipeline.ExecuteAsync(next, default);

        Assert.False(next.IsDenied);
    }

    [Fact]
    public async Task Global_cap_bounds_admitted_load_across_teams()
    {
        var pipeline = Pipeline(new RateLimitConfig { PerTeamPerMinute = 5, GlobalPerMinute = 3 }, new CountingSink());

        var a = Ctx("payments"); await pipeline.ExecuteAsync(a, default);
        var b = Ctx("risk");     await pipeline.ExecuteAsync(b, default);
        var c = Ctx("platform"); await pipeline.ExecuteAsync(c, default);
        var d = Ctx("payments"); await pipeline.ExecuteAsync(d, default);

        Assert.False(a.IsDenied);
        Assert.False(b.IsDenied);
        Assert.False(c.IsDenied);
        Assert.True(d.IsDenied);
        Assert.Contains("appliance cap", d.DenialReason!);
    }

    [Fact]
    public async Task Team_over_its_own_cap_does_not_consume_the_appliance_cap()
    {
        // Regression (verifier finding): a runaway team's REJECTED flood must not starve other teams.
        var pipeline = Pipeline(new RateLimitConfig { PerTeamPerMinute = 1, GlobalPerMinute = 3 }, new CountingSink());

        await pipeline.ExecuteAsync(Ctx("runaway"), default);            // admitted (1/3 global)
        for (int i = 0; i < 5; i++)
            await pipeline.ExecuteAsync(Ctx("runaway"), default);        // team-denied; must not count globally

        var b = Ctx("risk");     await pipeline.ExecuteAsync(b, default);
        var c = Ctx("platform"); await pipeline.ExecuteAsync(c, default);

        Assert.False(b.IsDenied);                                        // global still has room: 2/3, 3/3
        Assert.False(c.IsDenied);
    }

    [Fact]
    public async Task Team_named_global_does_not_collide_with_the_appliance_counter()
    {
        // Regression (verifier finding): separate counters — a team's name can never alias the appliance key.
        var pipeline = Pipeline(new RateLimitConfig { PerTeamPerMinute = 2, GlobalPerMinute = 10 }, new CountingSink());

        var first = Ctx("global");  await pipeline.ExecuteAsync(first, default);
        var second = Ctx("Global"); await pipeline.ExecuteAsync(second, default);
        var third = Ctx("global");  await pipeline.ExecuteAsync(third, default);

        Assert.False(first.IsDenied);
        Assert.False(second.IsDenied);
        Assert.True(third.IsDenied);                                     // denied by its TEAM cap…
        Assert.Contains("for team 'global'", third.DenialReason!);       // …with the team reason, not "appliance cap"
    }

    [Fact]
    public async Task Zero_config_means_unlimited()
    {
        var pipeline = Pipeline(new RateLimitConfig(), new CountingSink());

        for (int i = 0; i < 50; i++)
        {
            var ctx = Ctx();
            await pipeline.ExecuteAsync(ctx, default);
            Assert.False(ctx.IsDenied);
        }
    }

    [Fact]
    public void Counter_increments_are_atomic_under_concurrency()
    {
        var counter = new RateCounter();
        var returns = new int[4_000];

        Parallel.For(0, 4_000, i => returns[i] = counter.Increment("k"));

        Assert.Equal(4_000, returns.Distinct().Count());                 // every increment observed exactly once
        Assert.Equal(4_000, returns.Max());
    }
}
