using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;
using UiModels = Covenant.Ui.Models;
using UiLogic = Covenant.Ui.Logic;

namespace Covenant.Tests;

/// <summary>Console demo clarity (docs/CONSOLE-DEMO-CLARITY-TASKS.md): examples resolved from the CURRENT configuration
/// (never silently downgraded, never tripped by another application's exhausted budget), one actionable search-readiness
/// cause, application identity, honest header environment, and evidence of the chosen policy and cache outcome.</summary>
public sealed class DemoClarityTests
{
    // ---------------------------------------------------------------- fixtures (console DTO shapes)

    private static UiModels.ConnectionDto Ollama() => new()
    {
        Id = "ollama", Kind = "Ollama", Boundary = "Local", Enabled = true, Endpoint = "http://127.0.0.1:11434/v1",
        Models =
        [
            new() { ModelId = UiLogic.DemoExamples.CoderModel, Price = new() { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } },
            new() { ModelId = UiLogic.DemoExamples.ResearchModel, MaxOutputTokens = 4096, Price = new() { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } },
        ],
    };

    private static UiModels.ConnectionDto Anthropic(bool enabled = true) => new()
    {
        Id = "anthropic", Kind = "Anthropic", Boundary = "Cloud", Enabled = enabled, SecretRef = "Anthropic:ApiKey",
        Models = [new() { ModelId = "claude-test", Price = new() { InputPer1M = 1m, OutputPer1M = 5m, Basis = "provider-list" } }],
    };

    private static UiModels.PolicyDto P(string id, string[] classes, string[] profiles, params (string C, string M)[] targets) => new()
    {
        Id = id, Name = id, Enabled = true, Classifications = classes.ToList(), Profiles = profiles.ToList(), Strategy = "fixed",
        Targets = targets.Select(t => new UiModels.PolicyTargetDto { ConnectionId = t.C, ModelId = t.M }).ToList(),
    };

    private static UiModels.CovenantConfigDto Demo(int cacheTtl = 0, bool anthropic = true) => new()
    {
        Version = 3, Mode = "hybrid",
        Connections = [Ollama(), Anthropic(anthropic)],
        Profiles =
        [
            new() { Id = "public-research", Name = "Public research", Baseline = "Public", WebSearchAllowed = true },
            new() { Id = "public-writing", Name = "Public writing", Baseline = "Public" },
            new() { Id = "internal-coding", Name = "Internal coding", Baseline = "Internal" },
            new() { Id = "internal-research", Name = "Internal research", Baseline = "Internal" },
        ],
        Applications =
        [
            new() { Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true, MaxOutputTokens = 2048,
                    GrantedProfiles = ["public-research", "public-writing", "internal-coding", "internal-research"], DefaultProfile = "public-writing" },
            new() { Id = "aaa-budget-demo", Team = "budget-demo", Enabled = true, ConsoleTesting = true,
                    GrantedProfiles = ["public-research", "public-writing"], DefaultProfile = "public-writing" },
        ],
        Policies =
        [
            P("public", ["Public"], [], ("anthropic", "claude-test"), ("ollama", UiLogic.DemoExamples.CoderModel)),
            P("internal-coding", ["Internal"], ["internal-coding"], ("ollama", UiLogic.DemoExamples.CoderModel)),
            P("internal-research", ["Internal"], ["internal-research"], ("ollama", UiLogic.DemoExamples.ResearchModel)),
            P("internal", ["Internal"], [], ("ollama", UiLogic.DemoExamples.CoderModel)),
            P("sensitive", ["Pii", "Phi"], [], ("ollama", UiLogic.DemoExamples.CoderModel)),
        ],
        Budgets = [new() { Id = "global", Scope = "global", CapUsd = 100m, Enforcement = "hard" }],
        Settings = new() { CacheTtlSeconds = cacheTtl },
    };

    private static List<UiModels.ApplicationSummaryDto> Apps(UiModels.CovenantConfigDto cfg) => cfg.Applications.Select(a => new UiModels.ApplicationSummaryDto
    {
        Id = a.Id, Team = a.Team, Enabled = a.Enabled, ConsoleTesting = a.ConsoleTesting, GrantedProfiles = a.GrantedProfiles.ToList(),
        DefaultProfile = a.DefaultProfile, DisplayName = a.DisplayName,
        Budgets = cfg.Budgets.Where(b => b.Scope == "global" || b.Scope == "team:" + a.Team || b.Scope == "application:" + a.Id).Select(b => b.Id).ToList(),
    }).ToList();

    private static List<UiModels.BudgetStateDto> Budgets(bool budgetDemoExhausted) =>
    [
        new() { Id = "global", Scope = "global", CapUsd = 100m, RemainingUsd = 99m, Enforcement = "hard" },
        new() { Id = "budget-demo", Scope = "application:aaa-budget-demo", CapUsd = 0.0001m, RemainingUsd = budgetDemoExhausted ? 0m : 0.0001m, Enforcement = "hard" },
    ];

    private static UiModels.WebSearchStatusDto SearchReady() => new()
    {
        KeyConfigured = true, EgressApproved = true, ModeAllowsSearch = true, Enabled = true, PriceConfigured = true,
        PricePerCreditUsd = 0.008m, ProfilesAllowed = ["public-research"], Ready = true,
    };

    private static UiLogic.ExampleResolution Resolve(string id, UiModels.CovenantConfigDto cfg, bool exhausted = true, UiModels.WebSearchStatusDto? search = null)
        => UiLogic.DemoExamples.Resolve(UiLogic.DemoExamples.Find(id)!, cfg, Apps(cfg), Budgets(exhausted), search ?? SearchReady());

    // ---------------------------------------------------------------- examples

    [Fact]
    public void Each_example_resolves_to_the_workload_and_route_it_claims()
    {
        var cfg = Demo(cacheTtl: 60);
        var research = Resolve("public-research", cfg);
        Assert.True(research.Ready);
        Assert.Equal("public-research", research.ProfileId);

        var writing = Resolve("public-writing", cfg);
        Assert.True(writing.Ready);
        Assert.Equal("public-writing", writing.ProfileId);
        Assert.StartsWith("anthropic/claude-test", writing.ConfiguredRoute, StringComparison.Ordinal);

        var coding = Resolve("internal-coding", cfg);
        Assert.Equal("internal-coding", coding.ProfileId);
        Assert.StartsWith("ollama/" + UiLogic.DemoExamples.CoderModel, coding.ConfiguredRoute, StringComparison.Ordinal);
        Assert.Contains("policy internal-coding", coding.ConfiguredRoute, StringComparison.Ordinal);   // scoped rule, not the broad one

        var deep = Resolve("internal-research", cfg);
        Assert.Equal("internal-research", deep.ProfileId);
        Assert.StartsWith("ollama/" + UiLogic.DemoExamples.ResearchModel, deep.ConfiguredRoute, StringComparison.Ordinal);

        var refusal = Resolve("sensitive-refusal", cfg);
        Assert.True(refusal.Ready);
        Assert.Equal("anthropic/claude-test", refusal.ModelPin);
        Assert.Contains("refused", refusal.ConfiguredRoute, StringComparison.Ordinal);

        Assert.True(Resolve("cache-repeat", cfg).Ready);
    }

    [Fact]
    public void An_exhausted_low_budget_demo_application_never_captures_ordinary_examples()
    {
        // "aaa-budget-demo" sorts first and is granted the Public profiles; its own budget is exhausted.
        var cfg = Demo(cacheTtl: 60);
        foreach (var ex in UiLogic.DemoExamples.All.Where(e => e.Need != UiLogic.ExampleNeed.BudgetRefusal))
        {
            var r = UiLogic.DemoExamples.Resolve(ex, cfg, Apps(cfg), Budgets(budgetDemoExhausted: true), SearchReady());
            if (r.Ready) Assert.True(r.ApplicationId == "demo-app", $"{ex.Id} picked {r.ApplicationId}");
        }
        var budget = Resolve("budget-refusal", cfg);
        Assert.True(budget.Ready);
        Assert.Equal("aaa-budget-demo", budget.ApplicationId);
    }

    [Fact]
    public void Missing_prerequisites_are_named_with_a_link_and_never_downgraded()
    {
        var cfg = Demo(cacheTtl: 0, anthropic: false);
        var cache = Resolve("cache-repeat", cfg);
        Assert.False(cache.Ready);
        Assert.Contains(cache.Missing, m => m.Text.Contains("cache is disabled", StringComparison.Ordinal) && m.Href == "settings#op-h");

        var writing = Resolve("public-writing", cfg);
        Assert.False(writing.Ready);                                          // never falls back to a local "writing" route
        Assert.Contains(writing.Missing, m => m.Href == "connections");

        var notReady = UiLogic.DemoExamples.Resolve(UiLogic.DemoExamples.Find("public-research")!, cfg, Apps(cfg), Budgets(true),
            new UiModels.WebSearchStatusDto { KeyConfigured = false, Setup = ["Set Tavily:ApiKey"] });
        Assert.False(notReady.Ready);
        Assert.Contains(notReady.Missing, m => m.Href == "settings#ws-h");

        var budget = Resolve("budget-refusal", cfg, exhausted: false);
        Assert.False(budget.Ready);
        Assert.Contains(budget.Missing, m => m.Href == "budgets");
    }

    [Fact]
    public void Research_example_states_the_enforced_output_bound_honestly()
    {
        var cfg = Demo();
        var r = Resolve("internal-research", cfg);
        Assert.Contains("2048 requested", r.OutputNote, StringComparison.Ordinal);
        Assert.Contains("→ 2048 enforced", r.OutputNote, StringComparison.Ordinal);

        cfg.Applications[0].MaxOutputTokens = 1024;
        var capped = Resolve("internal-research", cfg);
        Assert.Contains("→ 1024 enforced", capped.OutputNote, StringComparison.Ordinal);
        Assert.Contains("cut short", capped.OutputNote, StringComparison.Ordinal);
        Assert.DoesNotContain("Slower", UiLogic.DemoExamples.Find("internal-research")!.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_an_example_fills_the_form_without_running_and_labels_the_snapshot()
    {
        var cfg = Demo();
        var r = Resolve("sensitive-refusal", cfg);
        var form = new UiLogic.PlaygroundForm();
        Assert.True(UiLogic.DemoExamples.ApplyTo(r, form));
        Assert.Equal("demo-app", form.ApplicationId);
        Assert.Equal(UiLogic.RouteMode.Catalog, form.Mode);
        Assert.Equal("anthropic/claude-test", form.RequestedModel);
        Assert.Contains("000-12-3456", form.Prompt, StringComparison.Ordinal);
        Assert.Equal("Example · " + r.Example.Title, UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch).ScenarioLabel);
        form.Prompt += " edited";
        Assert.Equal("Custom", UiLogic.RequestSnapshot.Freeze(2, form, DateTimeOffset.UnixEpoch).ScenarioLabel);

        var notReady = UiLogic.DemoExamples.Resolve(UiLogic.DemoExamples.Find("cache-repeat")!, Demo(cacheTtl: 0), Apps(Demo()), null, null);
        Assert.False(UiLogic.DemoExamples.ApplyTo(notReady, new UiLogic.PlaygroundForm()));
    }

    [Fact]
    public void The_synthetic_marker_is_explicitly_invalid_and_still_escalates_to_pii()
    {
        var prompt = UiLogic.DemoExamples.Find("sensitive-refusal")!.Prompt;
        Assert.Contains("000-12-3456", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("078-05-1120", prompt, StringComparison.Ordinal);
        var result = new RegexDataClassifier().Classify(new InferenceRequest("t", [new ChatMessage(ChatRole.User, prompt)], null, AttributionTags.Unattributed));
        Assert.Equal(DataClassification.Pii, result.Classification);
    }

    [Fact]
    public void Ui_route_mirror_prefers_the_profile_scoped_rule_and_reports_ambiguity()
    {
        var cfg = Demo();
        Assert.Equal("internal-coding", UiLogic.DemoExamples.Route(cfg, "Internal", "internal-coding").Policy!.Id);
        Assert.Equal("internal", UiLogic.DemoExamples.Route(cfg, "Internal", "some-other").Policy!.Id);
        cfg.Policies.Add(P("dup", ["Internal"], ["internal-coding"], ("ollama", UiLogic.DemoExamples.CoderModel)));
        Assert.Contains("ambiguous", UiLogic.DemoExamples.Route(cfg, "Internal", "internal-coding").Refusal, StringComparison.Ordinal);
        Assert.Contains("never routed to cloud", UiLogic.DemoExamples.Ineligible(cfg, new() { ConnectionId = "anthropic", ModelId = "claude-test" }, "Pii"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- search readiness

    [Theory]
    [InlineData("key", "Tavily API key", "settings#ws-h")]
    [InlineData("egress", "egress allow-list", "settings#ws-h")]
    [InlineData("local", "Local-only", "policies")]
    [InlineData("disabled", "disabled", "settings#ws-h")]
    [InlineData("price", "price", "settings#ws-h")]
    public void Search_readiness_gives_one_specific_cause_with_its_fix(string gap, string fragment, string href)
    {
        var s = SearchReady();
        s.Ready = false;
        switch (gap)
        {
            case "key": s.KeyConfigured = false; s.Enabled = false; break;   // several gaps: the most fundamental one only
            case "egress": s.EgressApproved = false; break;
            case "local": s.ModeAllowsSearch = false; break;
            case "disabled": s.Enabled = false; break;
            case "price": s.PriceConfigured = false; break;
        }
        var cfg = Demo();
        var r = UiLogic.WebSourcesText.Readiness(s, null, cfg, Apps(cfg)[0], "public-research");
        Assert.False(r.Possible);
        Assert.True(r.Known);
        Assert.Contains(fragment, r.Cause, StringComparison.Ordinal);
        Assert.Equal(href, r.Href);
    }

    [Fact]
    public void A_denied_profile_offers_an_explicit_switch_never_an_automatic_one()
    {
        var cfg = Demo();
        var app = Apps(cfg)[0];
        var r = UiLogic.WebSourcesText.Readiness(SearchReady(), null, cfg, app, "public-writing");
        Assert.False(r.Possible);
        Assert.Equal("public-research", r.SwitchToProfile);

        var ok = UiLogic.WebSourcesText.Readiness(SearchReady(), null, cfg, app, "public-research");
        Assert.True(ok.Possible);

        var unknown = UiLogic.WebSourcesText.Readiness(null, "status endpoint missing", cfg, app, "public-research");
        Assert.True(unknown.Possible);                                       // runtime stays authoritative
        Assert.False(unknown.Known);
    }

    [Theory]
    [InlineData("location_required", UiLogic.SearchProblemKind.MissingLocation)]
    [InlineData("search_not_permitted", UiLogic.SearchProblemKind.Setup)]
    [InlineData("search_sensitive", UiLogic.SearchProblemKind.PolicyRefusal)]
    [InlineData("search_timeout", UiLogic.SearchProblemKind.ProviderFailure)]
    [InlineData("search_empty", UiLogic.SearchProblemKind.ProviderFailure)]
    [InlineData(null, UiLogic.SearchProblemKind.None)]
    public void Search_outcomes_are_classified_by_kind(string? code, UiLogic.SearchProblemKind kind)
        => Assert.Equal(kind, UiLogic.WebSourcesText.Kind(code));

    // ---------------------------------------------------------------- applications

    [Fact]
    public void A_new_application_is_validated_and_round_trips_with_display_name_through_the_host()
    {
        // Start from a real Host configuration as the console reads it.
        var cfg = JsonSerializer.Deserialize(JsonSerializer.Serialize(V2.Hybrid(), ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;
        var bad = new UiLogic.NewApplicationForm { Id = "demo-app", Team = "" };
        Assert.Equal(2, UiLogic.ProfileEditing.Problems(bad, cfg).Count);   // duplicate id + missing team

        var form = new UiLogic.NewApplicationForm
        {
            Id = "support-portal", DisplayName = "Support portal", Team = "support", Description = "Answers customer questions",
            GrantedProfiles = ["public-demo"], DefaultProfile = "public-demo",
        };
        Assert.Empty(UiLogic.ProfileEditing.AddApplication(cfg, form));
        var added = cfg.Applications.Single(a => a.Id == "support-portal");
        Assert.True(added.Enabled);
        Assert.Equal("Support portal", UiLogic.ProfileEditing.ApplicationName(added.Id, added.DisplayName));

        var json = JsonSerializer.Serialize(cfg, UiModels.UiJsonContext.Default.CovenantConfigDto);
        var host = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CovenantConfig)!;
        var app = host.Applications.Single(a => a.Id == "support-portal");
        Assert.Equal("Support portal", app.DisplayName);
        Assert.True(ConfigValidator.Validate(host, V2.Env()).IsValid);          // the console's new application passes Host validation
        Assert.Equal("Answers customer questions", app.Description);
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(host, ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;
        Assert.Equal("Support portal", back.Applications.Single(a => a.Id == "support-portal").DisplayName);

        // A configuration written before display names existed still loads (null = show the id).
        var legacy = JsonSerializer.Deserialize(JsonSerializer.Serialize(V2.Hybrid(), ConfigJsonContext.Default.CovenantConfig), ConfigJsonContext.Default.CovenantConfig)!;
        Assert.All(legacy.Applications, a => Assert.Null(a.DisplayName));
        Assert.True(ConfigValidator.Validate(legacy, V2.Env()).IsValid);
        var tooLong = V2.Hybrid(tweak: c => c with { Applications = c.Applications.Select(a => a with { DisplayName = new string('x', 101) }).ToList() });
        Assert.Contains(ConfigValidator.Validate(tooLong, V2.Env()).Errors, e => e.Path.EndsWith(".display_name", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_selectable_application_is_identified_as_the_only_choice()
    {
        var cfg = Demo();
        var apps = Apps(cfg);
        apps[1].ConsoleTesting = false;
        Assert.Equal("demo-app", UiLogic.ComposerRules.OnlySelectable(apps)!.Id);
        apps[1].ConsoleTesting = true;
        Assert.Null(UiLogic.ComposerRules.OnlySelectable(apps));
        Assert.Equal("Team platform pays · budgets global", UiLogic.ComposerRules.PayerText(apps[0]));
    }

    [Theory]
    [InlineData("demo-local-only", "Demo instance")]
    [InlineData("demo-hybrid", "Demo instance")]
    [InlineData("local-demo-5210", "local-demo-5210")]
    [InlineData(null, "Environment not reported")]
    public void Header_environment_never_poses_as_the_routing_mode(string? env, string label)
    {
        var v = UiLogic.ShellStatus.Environment(env, 3, "1.2.3", null);
        Assert.Equal(label, v.Label);
        Assert.Contains("Active configuration: v3", v.Details, StringComparison.Ordinal);
        Assert.Equal("Hybrid · v3", UiLogic.ShellStatus.ModeWithVersion("hybrid", 3));
    }

    // ---------------------------------------------------------------- evidence: policy + cache outcome

    private static readonly IReadOnlyDictionary<string, MEAI.IChatClient> Clients = new Dictionary<string, MEAI.IChatClient>
    {
        [$"openai:{V2.CloudModel}"] = new StubChatClient("cloud"),
        [$"ollama:{V2.LocalTag}"] = new StubChatClient("local"),
    };

    [Fact]
    public async Task Evidence_names_the_policy_and_the_exact_cache_outcome()
    {
        var off = V2Rig.Build(V2.Hybrid(), Clients);
        var (ctx, e) = await off.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal("public", e.PolicyId);
        Assert.Equal(CacheOutcomes.Disabled, e.CacheOutcome);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "cache" && t.State == TraceStates.Skipped && t.ReasonCode == TraceReasons.CacheDisabled);

        var on = V2Rig.Build(V2.Hybrid(tweak: c => c with { Settings = c.Settings with { CacheTtlSeconds = 60 } }), Clients, cache: new ResponseCache());
        var (miss, me) = await on.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal(CacheOutcomes.Miss, me.CacheOutcome);
        Assert.Contains(miss.TraceEvents, t => t.Stage == "cache" && t.State == TraceStates.Passed && t.ReasonCode == TraceReasons.CacheMiss);
        var (_, he) = await on.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal(CacheOutcomes.Hit, he.CacheOutcome);
        Assert.True(he.CacheHit);

        // A refusal before the cache stage still records who asked; the cache was never reached.
        var (_, refused) = await off.RunAsync(V2Rig.PublicPrompt, profile: "no-such-profile");
        Assert.Equal(CacheOutcomes.NotReached, refused.CacheOutcome);
        Assert.Equal("demo-app", refused.ApplicationId);
    }
}
