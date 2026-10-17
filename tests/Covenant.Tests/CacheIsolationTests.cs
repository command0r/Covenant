using System.Net;
using Covenant.Core;
using Covenant.Governance;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0013 exact cache with a bound configuration: key = application, team, profile, FINAL classification,
/// configuration version, approved candidates, enforced output bound and content. Hits run after the current
/// auth/shape/classify/policy checks, skip pause/budget/provider (a hit is served during a global pause — documented),
/// cost $0 with basis no_call, and are audited with CacheHit and the originally served model. Failures are never stored.</summary>
public sealed class CacheIsolationTests
{
    private readonly StubChatClient _mini = new("mini reply");
    private readonly StubChatClient _local = new("local reply");
    private readonly ResponseCache _cache = new();

    private static CovenantConfig Config(int version = 1)
        => V2Rig.FallbackConfig(new RetrySettings { MaxAttempts = 1, DeadlineMs = 60_000 }) with
        {
            Version = version,
            Settings = new OperationalSettings { CacheTtlSeconds = 300 },
            Applications =
            [
                new ApplicationConfig { Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-demo", "internal-default"], DefaultProfile = "internal-default" },
                new ApplicationConfig { Id = "batch-app", Team = "data", Enabled = true, ConsoleTesting = false, GrantedProfiles = ["public-demo", "internal-default"], DefaultProfile = "internal-default" },
            ],
        };

    private V2Rig Rig(CovenantConfig? cfg = null) => V2Rig.Build(cfg ?? Config(), new Dictionary<string, MEAI.IChatClient>
    {
        [$"openai:{V2.CloudModel}"] = _mini,
        [$"ollama:{V2.LocalTag}"] = _local,
    }, cache: _cache);

    [Fact]
    public async Task Exact_repeat_is_served_from_cache_with_no_provider_call_no_charge_and_explicit_provenance()
    {
        var rig = Rig();
        var (_, first) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        var spent = rig.Ledger.Spent("global", BudgetPeriod.Monthly);
        var (ctx, hit) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");

        Assert.Equal(1, _mini.Calls);
        Assert.True(hit.CacheHit);
        Assert.Equal("no_call", hit.CostBasis);
        Assert.Equal(0m, hit.Usage.CostUsd);
        Assert.Equal(first.ServedByModel, hit.ServedByModel);         // the original served model
        Assert.Empty(hit.Attempts!);
        Assert.Null(hit.SettledUsd);
        Assert.Equal(spent, rig.Ledger.Spent("global", BudgetPeriod.Monthly));
        // Trace: cache passed (cache_hit); every later stage skipped with reason cache_hit.
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "cache" && t.State == TraceStates.Passed && t.ReasonCode == TraceReasons.CacheHit);
        foreach (var stage in new[] { "rate_limit", "pause", "budget", "provider", "attribution" })
            Assert.Contains(ctx.TraceEvents, t => t.Stage == stage && t.State == TraceStates.Skipped && t.ReasonCode == TraceReasons.CacheHit);
        Assert.Contains(hit.Trace!, s => s.Stage == "provider" && s.State == TraceStates.Skipped);
    }

    [Fact]
    public async Task Entries_are_isolated_across_applications_teams_and_profiles()
    {
        var rig = Rig();
        await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");                       // demo-app / platform / public-demo
        var (_, otherApp) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", key: "batch-key");   // batch-app / data
        Assert.False(otherApp.CacheHit);
        Assert.Equal(2, _mini.Calls);

        // Same application, another profile ⇒ another classification baseline and key (Internal → local).
        var (_, otherProfile) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "internal-default");
        Assert.False(otherProfile.CacheHit);
        Assert.Equal(1, _local.Calls);
    }

    [Fact]
    public async Task A_new_configuration_version_never_reuses_an_entry()
    {
        var rig = Rig();
        await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        rig.Snapshot = new RuntimeSnapshot(Config(version: 2));       // any activation changes the key space
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(e.CacheHit);
        Assert.Equal(2, _mini.Calls);
        Assert.Equal(2, e.ConfigVersion);
    }

    [Fact]
    public async Task A_different_enforced_output_bound_is_a_different_entry()
    {
        var rig = Rig();
        await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", maxTokens: 400);
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", maxTokens: 5);
        Assert.False(e.CacheHit);
        Assert.Equal(2, _mini.Calls);
    }

    [Fact]
    public async Task Cache_hit_during_a_global_pause_is_served_and_a_miss_is_refused()
    {
        var rig = Rig();
        await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        rig.Kill.Trip("pause for the demo");

        var (hitCtx, hit) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(hitCtx.IsDenied, hitCtx.DenialReason);           // documented semantics: pause stops provider calls only
        Assert.True(hit.CacheHit);

        var (_, miss) = await rig.RunAsync(V2Rig.PublicPrompt + " (new)", profile: "public-demo");
        Assert.Equal("pause", miss.StoppedAtStage);
        Assert.Equal(1, _mini.Calls);
    }

    [Fact]
    public async Task Current_policy_is_enforced_before_a_hit()
    {
        var rig = Rig();
        await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        // Same version number, but the public policy is now paused: the policy stage (before the cache) refuses.
        rig.Snapshot = new RuntimeSnapshot(Config() with
        {
            Policies = Config().Policies.Select(p => p.Id == "public" ? p with { Paused = true } : p).ToList(),
        });
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal("policy", e.StoppedAtStage);
        Assert.False(e.CacheHit);
    }

    [Fact]
    public async Task Failures_and_partial_streams_are_never_stored()
    {
        _mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable))
             .Then(StubChatClient.Mode.StreamThenThrow)
             .Then(StubChatClient.Mode.Serve);
        var rig = Rig();
        var (_, failed) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal(PolicyEffect.Deny, failed.Effect);
        var (_, partial) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);
        Assert.Equal(PolicyEffect.Deny, partial.Effect);
        var (_, served) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(served.CacheHit);                                // nothing was cached from the failures
        Assert.Equal(3, _mini.Calls);
    }
}
