using Xunit;
using UiLogic = Covenant.Ui.Logic;
using UiModels = Covenant.Ui.Models;

namespace Covenant.Tests;

/// <summary>ADR-0019 in the console: the demo application's recommended pair (Internal → local, Public → rejected) uses the
/// SAME neutral prompt and is visible without "More examples"; switching the workload keeps the prompt and never leaves a
/// stale example claim; previews, schemes and chips say "rejected at Classify" from the server's facts. The UI never
/// fakes the outcome — no model pin, no example-name special case: the decision is the Host's (tested in
/// SensitiveDataActionTests).</summary>
public sealed class SensitiveDataActionUiTests
{
    private const string LocalModel = "qwen3:8b";

    /// <summary>The demo seed's shape (hybrid): public-demo rejects detected sensitive data, internal-default routes it.</summary>
    private static UiModels.CovenantConfigDto Seed(string publicAction = "reject") => new()
    {
        Version = 3, Mode = "hybrid",
        Connections =
        [
            new() { Id = "ollama", Kind = "Ollama", Boundary = "Local", Enabled = true, Models = [new() { ModelId = LocalModel, Price = new() { InputPer1M = 0m, OutputPer1M = 0m } }] },
            new() { Id = "openai", Kind = "OpenAi", Boundary = "Cloud", Enabled = true, Models = [new() { ModelId = "gpt-4o-mini", Price = new() { InputPer1M = 0.15m, OutputPer1M = 0.6m } }] },
        ],
        Profiles =
        [
            new() { Id = "public-demo", Name = "Public demo", Baseline = "Public", SensitiveDataAction = publicAction },
            new() { Id = "internal-default", Name = "Internal (default)", Baseline = "Internal" },   // null = route
        ],
        Applications =
        [
            new() { Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-demo", "internal-default"], DefaultProfile = "internal-default" },
        ],
        Policies =
        [
            new() { Id = "public", Name = "public", Enabled = true, Classifications = ["Public"], Strategy = "fixed",
                    Targets = [new() { ConnectionId = "openai", ModelId = "gpt-4o-mini" }, new() { ConnectionId = "ollama", ModelId = LocalModel }] },
            new() { Id = "internal", Name = "internal", Enabled = true, Classifications = ["Internal"], Strategy = "fixed", Targets = [new() { ConnectionId = "ollama", ModelId = LocalModel }] },
            new() { Id = "sensitive", Name = "sensitive", Enabled = true, Classifications = ["Pii", "Phi"], Strategy = "fixed", Targets = [new() { ConnectionId = "ollama", ModelId = LocalModel }] },
        ],
        Budgets = [new() { Id = "global", Scope = "global", CapUsd = 25m, Enforcement = "hard" }],
        Settings = new() { CacheTtlSeconds = 300 },
    };

    private static List<UiModels.ApplicationSummaryDto> Apps(UiModels.CovenantConfigDto c) => c.Applications.Select(a => new UiModels.ApplicationSummaryDto
    {
        Id = a.Id, Team = a.Team, Enabled = true, ConsoleTesting = true, GrantedProfiles = a.GrantedProfiles.ToList(), DefaultProfile = a.DefaultProfile,
    }).ToList();

    private static List<UiLogic.ExampleResolution> Relevant(UiModels.CovenantConfigDto cfg)
        => UiLogic.DemoExamples.All.Select(e => UiLogic.DemoExamples.ResolveFor(e, "demo-app", cfg, Apps(cfg), null, null)).Where(r => r.Relevant).ToList();

    private static UiLogic.ExampleResolution Get(List<UiLogic.ExampleResolution> rs, string id) => rs.Single(r => r.Example.Id == id);

    [Fact]
    public void The_demo_app_gets_the_internal_local_and_public_rejected_pair_with_one_neutral_prompt()
    {
        var rs = Relevant(Seed());
        var local = Get(rs, "sensitive-local");
        var rejected = Get(rs, "sensitive-public-rejected");
        Assert.True(local.Ready);
        Assert.True(rejected.Ready);
        Assert.Equal("internal-default", local.ProfileId);                      // resolves INTERNAL
        Assert.Equal("public-demo", rejected.ProfileId);
        Assert.Null(local.ModelPin);                                           // nothing faked by a pin
        Assert.Null(rejected.ModelPin);
        Assert.StartsWith("ollama/" + LocalModel, local.ConfiguredRoute, StringComparison.Ordinal);
        Assert.Contains("rejects detected sensitive data", rejected.ConfiguredRoute, StringComparison.Ordinal);

        Assert.Equal(local.Example.Prompt, rejected.Example.Prompt);           // the SAME request
        Assert.Contains(UiLogic.DemoExamples.SyntheticSsn, local.Example.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("public", local.Example.Prompt, StringComparison.OrdinalIgnoreCase);    // neutral: the workload declares
        Assert.DoesNotContain("internal", local.Example.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket", local.Example.Prompt, StringComparison.OrdinalIgnoreCase);

        // The explicit-external-pin example describes a POLICY refusal; a rejecting Public workload refuses earlier.
        Assert.DoesNotContain(rs, r => r.Example.Id == "sensitive-refusal");

        // Visible without "More examples": recommended, together, first.
        var groups = UiLogic.ExampleRecommendations.Split(rs);
        Assert.Equal(["sensitive-local", "sensitive-public-rejected"], groups.Recommended.Take(2).Select(r => r.Example.Id).ToArray());
        Assert.True(groups.Recommended.Count <= UiLogic.ExampleRecommendations.Max);
        Assert.DoesNotContain(groups.More, r => r.Example.Id.StartsWith("sensitive-", StringComparison.Ordinal) && r.Example.Id != "sensitive-refusal");
    }

    [Fact]
    public void An_internal_only_application_gets_no_sensitive_pair_noise()
    {
        var cfg = Seed();
        cfg.Applications.Add(new() { Id = "engineering-assistant", Team = "engineering", Enabled = true, ConsoleTesting = true,
                                     GrantedProfiles = ["internal-default"], DefaultProfile = "internal-default" });
        var ids = UiLogic.DemoExamples.All
            .Select(e => UiLogic.DemoExamples.ResolveFor(e, "engineering-assistant", cfg, Apps(cfg), null, null))
            .Where(r => r.Relevant).Select(r => r.Example.Id).ToList();
        Assert.DoesNotContain("sensitive-local", ids);
        Assert.DoesNotContain("sensitive-public-rejected", ids);
    }

    [Fact]
    public void Without_a_rejecting_public_workload_the_pair_is_not_offered()
    {
        var rs = Relevant(Seed(publicAction: "route"));
        Assert.DoesNotContain(rs, r => r.Example.Id == "sensitive-public-rejected");
        Assert.DoesNotContain(rs, r => r.Example.Id == "sensitive-local");      // never half a pair: its text promises the rejection
    }

    [Fact]
    public void Flipping_the_workload_after_fill_keeps_the_prompt_and_relabels_only_an_exact_twin()
    {
        var cfg = Seed();
        var rs = Relevant(cfg);
        var form = new UiLogic.PlaygroundForm();
        Assert.True(UiLogic.DemoExamples.ApplyTo(Get(rs, "sensitive-local"), form));
        var prompt = form.Prompt;
        Assert.Equal("Example · " + Get(rs, "sensitive-local").Title, UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch).ScenarioLabel);

        // Internal → Public: same prompt; the form is now exactly the Public example → labelled as THAT example (true claim).
        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "public-demo"));
        UiLogic.DemoExamples.AfterWorkloadChange(form, rs);
        Assert.Equal(prompt, form.Prompt);
        Assert.Equal("public-demo", form.ProfileId);
        Assert.Equal("sensitive-public-rejected", form.ExampleId);
        Assert.Equal(UiLogic.RouteMode.Policy, form.Mode);
        Assert.Equal("Example · " + Get(rs, "sensitive-public-rejected").Title, UiLogic.RequestSnapshot.Freeze(2, form, DateTimeOffset.UnixEpoch).ScenarioLabel);

        // And back.
        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "internal-default"));
        UiLogic.DemoExamples.AfterWorkloadChange(form, rs);
        Assert.Equal("sensitive-local", form.ExampleId);

        // Edited prompt, then flip: no example claim survives; the edited prompt is kept.
        form.Prompt += " Also list the next step.";
        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "public-demo"));
        UiLogic.DemoExamples.AfterWorkloadChange(form, rs);
        Assert.Null(form.ExampleId);
        Assert.EndsWith("Also list the next step.", form.Prompt, StringComparison.Ordinal);
        Assert.Equal("Custom", UiLogic.RequestSnapshot.Freeze(3, form, DateTimeOffset.UnixEpoch).ScenarioLabel);
    }

    [Fact]
    public void The_example_selector_follows_a_twin_relabel_instead_of_showing_the_placeholder()
    {
        // Browser bug: after Internal → Public the form held the Public twin (expected outcome relabelled) while the select
        // showed the placeholder and "Fill again"/"Run example" stayed. The picker now derives its value from this rule.
        var cfg = Seed();
        var rs = Relevant(cfg);
        var form = new UiLogic.PlaygroundForm();
        Assert.True(UiLogic.DemoExamples.ApplyTo(Get(rs, "sensitive-local"), form));
        string? picked = "sensitive-local";                                         // "Use example" sets the pick
        Assert.Equal("sensitive-local", UiLogic.ExampleSelection.Shown(picked, form.ExampleId));

        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "public-demo"));
        UiLogic.DemoExamples.AfterWorkloadChange(form, rs);
        picked = UiLogic.ExampleSelection.AfterSwitch(picked, form.ExampleId);
        Assert.Equal("sensitive-public-rejected", picked);                          // select, buttons and summary agree
        Assert.Equal(form.ExampleId, UiLogic.ExampleSelection.Shown(picked, form.ExampleId));

        // A switch that clears the example clears the selection too (placeholder, no "Fill again" for a stale example).
        form.Prompt += " edited";
        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "internal-default"));
        UiLogic.DemoExamples.AfterWorkloadChange(form, rs);
        picked = UiLogic.ExampleSelection.AfterSwitch(picked, form.ExampleId);
        Assert.Null(picked);
        Assert.Null(UiLogic.ExampleSelection.Shown(picked, form.ExampleId));

        // An operator's pick that is not applied yet is kept as long as nothing switches.
        Assert.Equal("cache-repeat", UiLogic.ExampleSelection.Shown("cache-repeat", null));
        Assert.Equal("cache-repeat", UiLogic.ExampleSelection.AfterSwitch("cache-repeat", "cache-repeat"));
    }

    [Fact]
    public void A_rejecting_workload_never_shows_the_stricter_local_rule_copy()
    {
        // Browser bug: the Public Reject workload still said "detection raises it and the stricter (local) rule applies"
        // next to the rejection explanation. One note, consistent with the gateway.
        var reject = Seed();
        var note = UiLogic.WorkloadDestination.For(reject, reject.Profiles[0]).Note;
        Assert.Equal(UiLogic.WorkloadDestination.RejectNote, note);
        Assert.DoesNotContain("stricter", note, StringComparison.Ordinal);

        var route = Seed(publicAction: "route");
        Assert.Equal(UiLogic.WorkloadDestination.StricterRuleNote, UiLogic.WorkloadDestination.For(route, route.Profiles[0]).Note);

        // Public routed LOCALLY + Reject: the routing fact stays, the rejection is added, the stricter-rule copy never appears.
        var local = Seed();
        local.Policies[0].Targets = [new() { ConnectionId = "ollama", ModelId = LocalModel }];
        var localNote = UiLogic.WorkloadDestination.For(local, local.Profiles[0]).Note;
        Assert.Contains("cloud-eligible, not cloud", localNote, StringComparison.Ordinal);
        Assert.EndsWith(UiLogic.WorkloadDestination.RejectNote, localNote, StringComparison.Ordinal);
        Assert.DoesNotContain("stricter", localNote, StringComparison.Ordinal);

        // The Internal (route) workload carries neither sentence.
        Assert.Null(UiLogic.WorkloadDestination.For(reject, reject.Profiles[1]).Note);
    }

    [Fact]
    public void Flipping_a_non_twin_example_clears_the_label_and_keeps_the_prompt()
    {
        var cfg = Seed();
        var rs = Relevant(cfg);
        var form = new UiLogic.PlaygroundForm();
        Assert.True(UiLogic.DemoExamples.ApplyTo(Get(rs, "cache-repeat"), form));
        Assert.Equal("public-demo", form.ProfileId);
        var prompt = form.Prompt;
        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "internal-default"));
        UiLogic.DemoExamples.AfterWorkloadChange(form, rs);
        Assert.Null(form.ExampleId);
        Assert.Equal(prompt, form.Prompt);
        Assert.Equal("Custom", UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch).ScenarioLabel);
    }

    [Fact]
    public void The_route_mirror_and_the_workload_editor_follow_the_rule()
    {
        var cfg = Seed();
        Assert.Contains("rejects detected sensitive data", UiLogic.DemoExamples.Route(cfg, "Pii", "public-demo").Refusal, StringComparison.Ordinal);
        Assert.Contains("rejects detected sensitive data", UiLogic.DemoExamples.Route(cfg, "Phi", "public-demo").Refusal, StringComparison.Ordinal);
        Assert.Equal("ollama", UiLogic.DemoExamples.Route(cfg, "Pii", "internal-default").Primary!.ConnectionId);
        Assert.Equal("openai", UiLogic.DemoExamples.Route(cfg, "Public", "public-demo").Primary!.ConnectionId);   // innocent Public unchanged

        var pub = cfg.Profiles[0];
        var internalProfile = cfg.Profiles[1];
        Assert.True(UiLogic.ProfileEditing.RejectsSensitive(pub));
        Assert.False(UiLogic.ProfileEditing.RejectsSensitive(internalProfile));      // null = route
        Assert.False(UiLogic.ProfileEditing.RejectsSensitive(null));
        Assert.NotNull(UiLogic.ProfileEditing.SetBaseline(pub, "Pii"));            // must switch to route first
        Assert.Equal("Public", pub.Baseline);

        var pii = new UiModels.WorkloadProfileDto { Id = "records", Baseline = "Pii" };
        Assert.NotNull(UiLogic.ProfileEditing.SetSensitiveAction(pii, "reject"));
        Assert.Null(pii.SensitiveDataAction);
        Assert.Null(UiLogic.ProfileEditing.SetSensitiveAction(internalProfile, "reject"));
        Assert.Equal("reject", internalProfile.SensitiveDataAction);
        Assert.Null(UiLogic.ProfileEditing.SetSensitiveAction(internalProfile, "route"));
        Assert.Equal("route", internalProfile.SensitiveDataAction);
    }

    [Fact]
    public void The_expected_route_shows_the_server_rejection_and_no_search_badge()
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = UiLogic.DemoExamples.SensitivePrompt, WebSources = UiLogic.WebSourcesMode.Required };
        var result = new UiModels.SimulateResponseDto
        {
            DryRun = true, ConfigVersion = 3, ApplicationId = "demo-app", Profile = "public-demo", Classification = "Pii", Baseline = "Public",
            DetectedSignal = "US SSN pattern", Decision = "refuse", RefusedAtStage = "classify", SuggestedProfiles = ["internal-default"],
            Reason = "sensitive data detected (PII: US SSN pattern): workload 'public-demo' rejects detected sensitive data — refused before routing, cache, web search or any model; nothing was sent. To process it on an approved route, choose workload 'internal-default'.",
        };
        var v = UiLogic.RouteSchemes.Expected(new UiLogic.RoutePreview(UiLogic.RoutePreview.KeyOf(form), result, null, false), form, 3, Apps(Seed()), Seed().Profiles);
        Assert.Equal("ready", v.State);
        Assert.Equal("Rejected — nothing would be sent", v.Destination.Line);
        Assert.Equal("blocked", v.Destination.Tone);
        Assert.Contains("rejected at Classify", v.Covenant.Sub, StringComparison.Ordinal);
        Assert.Contains("detected: US SSN pattern", v.Covenant.Line, StringComparison.Ordinal);
        Assert.Empty(v.Badges);                                                   // no search would run
    }

    [Fact]
    public void The_actual_route_and_chips_say_rejected_at_classify_and_keep_incomplete_distinct()
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = UiLogic.DemoExamples.SensitivePrompt };
        var snapshot = UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch);
        UiModels.AuditEntryView Rec(bool evaluated) => new()
        {
            Effect = "Deny", StoppedAtStage = "classify", DenialKind = "Governance", ClassificationEvaluated = evaluated,
            Classification = evaluated ? "Pii" : "Phi", BaselineClassification = "Public", Signal = evaluated ? "US SSN pattern" : "inspection incomplete",
            ApplicationId = "demo-app", ProfileId = "public-demo", Attempts = [], Reason = "x", Tags = new() { Team = "platform" },
        };

        var rejected = Rec(evaluated: true);
        var scheme = UiLogic.RouteSchemes.Actual(snapshot, rejected, false, Apps(Seed()), null, Seed().Profiles);
        Assert.Equal("Blocked — model not contacted", scheme.Destination.Line);
        Assert.Contains("rejected at Classify", scheme.Destination.Sub, StringComparison.Ordinal);
        Assert.Equal("Rejected · this workload does not accept sensitive data", UiLogic.JourneyChips.For(rejected, null, Seed().Profiles)[0].Label);

        var incomplete = Rec(evaluated: false);
        Assert.DoesNotContain("rejected at Classify", UiLogic.RouteSchemes.Actual(snapshot, incomplete, false, Apps(Seed()), null, Seed().Profiles).Destination.Sub ?? "", StringComparison.Ordinal);
        Assert.Equal("Refused · inspection incomplete", UiLogic.JourneyChips.For(incomplete, null, Seed().Profiles)[0].Label);
    }
}
