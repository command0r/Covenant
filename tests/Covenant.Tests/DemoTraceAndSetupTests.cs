using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using UiModels = Covenant.Ui.Models;
using UiLogic = Covenant.Ui.Logic;

namespace Covenant.Tests;

/// <summary>Demo clarity §6–§8: honest durations and stage wording, cost ownership before/after execution, recorded-trace
/// replay that never invents data, and the additive workload-route setup.</summary>
public sealed class DemoTraceAndSetupTests
{
    // ---------------------------------------------------------------- §6 durations and wording

    [Theory]
    [InlineData(0.04, "<1 ms")]
    [InlineData(0.999, "<1 ms")]
    [InlineData(0.0, "0 ms")]
    [InlineData(1.4, "1 ms")]
    [InlineData(812.0, "812 ms")]
    [InlineData(1534.0, "1.53 s")]
    public void Durations_never_round_a_positive_value_to_zero(double ms, string text)
        => Assert.Equal(text, UiLogic.TraceModel.DurationText(ms));

    [Fact]
    public void Unknown_duration_stays_unknown()
    {
        Assert.Null(UiLogic.TraceModel.DurationText(null));
        Assert.Null(UiLogic.TraceModel.DurationText(-1));
        Assert.Null(UiLogic.TraceModel.DurationText(double.NaN));
    }

    private static UiLogic.TraceStepView Step(string stage, UiLogic.TraceStepState state, string? reason = null)
        => new(stage, UiLogic.TraceModel.Label(stage), state, 0.3, reason, null, null, []);

    [Fact]
    public void Stage_labels_describe_what_happens_before_and_after_the_call()
    {
        Assert.Equal("Identify caller", UiLogic.TraceModel.Label("auth"));
        Assert.Equal("Finalize cost", UiLogic.TraceModel.Label("attribution"));
        Assert.Contains("BEFORE", UiLogic.TraceModel.Explain("auth"), StringComparison.Ordinal);
        Assert.Contains("AFTER", UiLogic.TraceModel.Explain("attribution"), StringComparison.Ordinal);

        var skippedCost = Step("attribution", UiLogic.TraceStepState.Skipped, "not_reached");
        Assert.Equal("No usage", UiLogic.TraceModel.OutcomeText(skippedCost));
        Assert.DoesNotContain("Skipped", UiLogic.TraceModel.OutcomeText(skippedCost), StringComparison.Ordinal);
        Assert.Contains("still recorded with its application, team and workload", UiLogic.TraceModel.OutcomeNote(skippedCost), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UiLogic.TraceStepState.Passed, "cache_hit", "Hit")]
    [InlineData(UiLogic.TraceStepState.Passed, "cache_miss", "Miss")]
    [InlineData(UiLogic.TraceStepState.Skipped, "cache_disabled", "Off")]
    [InlineData(UiLogic.TraceStepState.Skipped, "web_search_bypass", "Bypassed")]
    public void Cache_chip_states_the_outcome_not_a_bare_passed(UiLogic.TraceStepState state, string reason, string text)
        => Assert.Equal(text, UiLogic.TraceModel.OutcomeText(Step("cache", state, reason)));

    // ---------------------------------------------------------------- §6 ownership

    private static UiModels.AuditEntryView Record(Action<UiModels.AuditEntryView>? tweak = null)
    {
        var r = new UiModels.AuditEntryView
        {
            Id = "req_1", SchemaVersion = 2, ApplicationId = "demo-app", ProfileId = "public-writing", PolicyId = "public-writing",
            ConfigVersion = 3, Effect = "Allow", CostBasis = "reported", Attempts = [new() { AttemptId = "att_1", Outcome = "served", CostUsd = 0.0004m, CostBasis = "reported" }],
            ReservedUsd = 0.002m, SettledUsd = 0.0004m, CacheOutcome = "miss",
        };
        r.Tags.Team = "platform";
        r.Usage.CostUsd = 0.0004m;
        tweak?.Invoke(r);
        return r;
    }

    [Fact]
    public void Ownership_is_assigned_before_and_finalized_after()
    {
        var r = Record();
        Assert.Equal("application demo-app · team platform · workload public-writing · policy public-writing · config v3", UiLogic.Ownership.Assigned(r));
        Assert.Contains("reserved", UiLogic.Ownership.Finalized(r), StringComparison.Ordinal);
        Assert.StartsWith("Miss", UiLogic.Ownership.Cache(r), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_is_attributed_but_not_charged()
    {
        var r = Record(x =>
        {
            x.Effect = "Deny"; x.StoppedAtStage = "budget"; x.Attempts = []; x.CostBasis = "no_call"; x.ReservedUsd = null; x.SettledUsd = null;
            x.Usage.CostUsd = 0m; x.CacheOutcome = "disabled";
        });
        Assert.Contains("team platform", UiLogic.Ownership.Assigned(r), StringComparison.Ordinal);
        var charged = UiLogic.Ownership.Finalized(r);
        Assert.Contains("No charge", charged, StringComparison.Ordinal);
        Assert.Contains("refused at Budget", charged, StringComparison.Ordinal);
        Assert.Contains("recorded with its application, team and workload", charged, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_search_charge_is_shown_as_the_tool_charge_it_is()
    {
        var r = Record(x =>
        {
            x.Effect = "Deny"; x.StoppedAtStage = "search"; x.Attempts = []; x.CostBasis = "estimated_failed_call"; x.Usage.CostUsd = 0.008m;
            x.ToolCalls = [new() { ToolCallId = "tool_1", Tool = "web_search", Provider = "tavily", Outcome = "failed", CostUsd = 0.008m, CostBasis = "estimated_failed_call" }];
            x.ReservedUsd = 0.012m; x.SettledUsd = 0.008m; x.CacheOutcome = "bypass_web_search";
        });
        var charged = UiLogic.Ownership.Finalized(r);
        Assert.Contains("web-search charge", charged, StringComparison.Ordinal);
        Assert.Contains("1 tool call (web_search)", charged, StringComparison.Ordinal);
        Assert.StartsWith("Bypassed", UiLogic.Ownership.Cache(r), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_hit_costs_nothing_and_says_why()
        => Assert.Contains("exact cache", UiLogic.Ownership.Finalized(Record(x => { x.CacheHit = true; x.CacheOutcome = "hit"; x.Attempts = []; })), StringComparison.Ordinal);

    // ---------------------------------------------------------------- §7 replay

    private static UiModels.TraceEventDto E(long seq, string stage, string state, double? ms = null, string? reason = null)
        => new() { Seq = seq, Stage = stage, State = state, DurationMs = ms, ReasonCode = reason };

    [Fact]
    public void Replay_reveals_the_recorded_events_and_ends_on_the_recorded_trace()
    {
        var live = new List<UiModels.TraceEventDto>
        {
            E(1, "auth", "running"), E(2, "auth", "passed", 0.2), E(3, "policy", "running"), E(4, "policy", "refused", 0.5, "governance_denied"),
        };
        var script = UiLogic.TraceReplay.Script(live.AsEnumerable().Reverse().Append(live[0]));   // any order, duplicates
        Assert.Equal(live.Select(e => e.Seq), script.Select(e => e.Seq));                         // live events unchanged

        var first = UiLogic.TraceReplay.Frame(script, 1, 3);
        Assert.Equal(UiLogic.TraceStepState.Running, first.Steps.Single(s => s.Stage == "auth").State);
        Assert.Equal(UiLogic.TraceStepState.Pending, first.Steps.Single(s => s.Stage == "policy").State);
        Assert.False(first.Complete);

        var last = UiLogic.TraceReplay.Frame(script, script.Count, 3);
        var recorded = UiLogic.TraceModel.Reduce(live, complete: true, 3);
        Assert.True(last.Complete);
        foreach (var stage in UiLogic.TraceModel.Stages)
        {
            var a = last.Steps.Single(s => s.Stage == stage);
            var b = recorded.Steps.Single(s => s.Stage == stage);
            Assert.Equal(b.State, a.State);
            Assert.Equal(b.DurationMs, a.DurationMs);                                             // recorded durations, never synthetic
            Assert.Equal(b.ReasonCode, a.ReasonCode);
        }
    }

    [Fact]
    public void Replay_of_an_audit_summary_adds_running_reveals_without_durations()
    {
        var summary = new List<UiModels.AuditTraceStepView>
        {
            new() { Stage = "auth", State = "passed", DurationMs = 0.3 },
            new() { Stage = "cache", State = "passed", DurationMs = 0.1, ReasonCode = "cache_hit" },
        };
        var script = UiLogic.TraceReplay.Script(UiLogic.TraceModel.EventsFromAuditSummary(summary));
        Assert.Equal(["running", "passed", "running", "passed"], script.Select(e => e.State).ToArray());
        Assert.All(script.Where(e => e.State == "running"), e => Assert.Null(e.DurationMs));
        var final = UiLogic.TraceReplay.Frame(script, script.Count, null);
        Assert.Equal(0.1, final.Steps.Single(s => s.Stage == "cache").DurationMs);
        Assert.Equal("cache_hit", final.Steps.Single(s => s.Stage == "cache").ReasonCode);
    }

    [Fact]
    public void Replay_pacing_is_bounded()
    {
        Assert.Equal(1300, UiLogic.TraceReplay.DelayMs(0.5));
        Assert.Equal(650, UiLogic.TraceReplay.DelayMs(1));
        Assert.Equal(UiLogic.TraceReplay.DelayMs(4), UiLogic.TraceReplay.DelayMs(100));
        Assert.Equal(UiLogic.TraceReplay.DelayMs(0.25), UiLogic.TraceReplay.DelayMs(0));
    }

    // ---------------------------------------------------------------- §8 workload route setup (additive)

    private static UiModels.CovenantConfigDto UiConfig(Func<CovenantConfig, CovenantConfig>? tweak = null)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(V2.Hybrid(tweak: tweak), ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;

    private static UiLogic.WorkloadPreset Preset(string id) => UiLogic.WorkloadRouteSetup.Presets.Single(p => p.ProfileId == id);

    [Fact]
    public void Routing_a_workload_is_additive_unambiguous_and_passes_host_validation()
    {
        var draft = UiConfig();
        var before = UiLogic.ConfigCopy.Deep(draft);
        var plan = UiLogic.WorkloadRouteSetup.ApplyTo(draft, Preset("internal-coding"), "ollama", V2.LocalTag);
        Assert.True(plan.Ok, plan.Problem);

        // Nothing that existed was modified or removed.
        foreach (var p in before.Policies)
            Assert.Equal(UiLogic.ConfigCopy.Canonical(p), UiLogic.ConfigCopy.Canonical(draft.Policies.Single(x => x.Id == p.Id)));
        Assert.All(before.Applications, a => Assert.All(a.GrantedProfiles, g => Assert.Contains(g, draft.Applications.Single(x => x.Id == a.Id).GrantedProfiles)));
        Assert.Equal(before.Connections.Count, draft.Connections.Count);

        var host = JsonSerializer.Deserialize(JsonSerializer.Serialize(draft, UiModels.UiJsonContext.Default.CovenantConfigDto), ConfigJsonContext.Default.CovenantConfig)!;
        Assert.True(ConfigValidator.Validate(host, V2.Env()).IsValid, string.Join("; ", ConfigValidator.Validate(host, V2.Env()).Errors.Select(e => e.Code + " " + e.Message)));
        var snap = new RuntimeSnapshot(host);
        var coding = PolicyEngineV2.Evaluate(snap, DataClassification.Internal, "internal-coding", null, []);
        Assert.Equal(PolicyEffect.Allow, coding.Effect);
        Assert.Equal("internal-coding", coding.PolicyId);
        Assert.Equal("internal", PolicyEngineV2.Evaluate(snap, DataClassification.Internal, "internal-default", null, []).PolicyId);   // others unchanged
        Assert.Equal("sensitive", PolicyEngineV2.Evaluate(snap, DataClassification.Pii, "internal-coding", null, []).PolicyId);       // PII keeps its local rule

        var again = UiLogic.WorkloadRouteSetup.Plan(draft, Preset("internal-coding"), "ollama", V2.LocalTag);
        Assert.False(again.Ok);                                                                  // never a second, ambiguous rule
        Assert.Contains("already routed by policy", again.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Cloud_routes_are_public_only_priced_only_and_hybrid_only()
    {
        var cfg = UiConfig();
        var internalOnCloud = UiLogic.WorkloadRouteSetup.Plan(cfg, Preset("internal-research"), "openai", V2.CloudModel);
        Assert.Contains("never routes to a cloud model", internalOnCloud.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLogic.WorkloadRouteSetup.PresetsFor(cfg.Connections.Single(c => c.Id == "openai")), p => p.Baseline != "Public");

        var unpriced = UiConfig(c => c with { Connections = [V2.Ollama(), V2.OpenAi(priced: false)] });
        Assert.Contains("never $0", UiLogic.WorkloadRouteSetup.Plan(unpriced, Preset("public-writing"), "openai", V2.CloudModel).Problem, StringComparison.Ordinal);

        cfg.Mode = "local-only";
        Assert.Contains("Local-only", UiLogic.WorkloadRouteSetup.Plan(cfg, Preset("public-writing"), "openai", V2.CloudModel).Problem, StringComparison.Ordinal);

        var ok = UiLogic.WorkloadRouteSetup.Plan(UiConfig(), Preset("public-writing"), "openai", V2.CloudModel);
        Assert.True(ok.Ok);
        Assert.Contains(ok.Changes, c => c.Contains("(cloud, priced)", StringComparison.Ordinal));
    }
}
