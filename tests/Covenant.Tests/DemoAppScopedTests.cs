using System.Text.Json;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using UiModels = Covenant.Ui.Models;
using UiLogic = Covenant.Ui.Logic;

namespace Covenant.Tests;

/// <summary>Examples scoped to the selected calling application (never switching owner), workload control shape, the
/// Search-location pre-check (mirroring the Host), and explicit-only grants in workload setup — against the shape of the
/// real five-application demo configuration (v7).</summary>
public sealed class DemoAppScopedTests
{
    private const string Claude = "claude-sonnet-5-5";

    private static UiModels.PolicyDto P(string id, string cls, string? profile, string conn, string model) => new()
    {
        Id = id, Name = id, Enabled = true, Classifications = [cls], Profiles = profile is null ? [] : [profile], Strategy = "fixed",
        Targets = [new() { ConnectionId = conn, ModelId = model }],
    };

    private static UiModels.ApplicationDto A(string id, string team, params string[] grants) => new()
    {
        Id = id, Team = team, Enabled = true, ConsoleTesting = true, GrantedProfiles = grants.ToList(), DefaultProfile = grants[0], MaxOutputTokens = 2048,
    };

    /// <summary>The v7 demo shape: five applications, workload-scoped policies, Claude for public research and writing.</summary>
    private static UiModels.CovenantConfigDto V7() => new()
    {
        Version = 7, Mode = "hybrid",
        Connections =
        [
            new() { Id = "ollama", Kind = "Ollama", Boundary = "Local", Enabled = true, Endpoint = "http://127.0.0.1:11434/v1",
                    Models = [new() { ModelId = UiLogic.DemoExamples.CoderModel, Price = new() { InputPer1M = 0m, OutputPer1M = 0m } },
                              new() { ModelId = UiLogic.DemoExamples.ResearchModel, Price = new() { InputPer1M = 0m, OutputPer1M = 0m } }] },
            new() { Id = "anthropic", Kind = "Anthropic", Boundary = "Cloud", Enabled = true, SecretRef = "Anthropic:ApiKey",
                    Models = [new() { ModelId = Claude, Price = new() { InputPer1M = 3m, OutputPer1M = 15m } }] },
        ],
        Profiles =
        [
            new() { Id = "public-demo", Name = "Public demo", Baseline = "Public", WebSearchAllowed = true },
            new() { Id = "public-research", Name = "Public research", Baseline = "Public", WebSearchAllowed = true },
            new() { Id = "public-writing", Name = "Public writing", Baseline = "Public" },
            new() { Id = "internal-coding", Name = "Internal coding", Baseline = "Internal" },
            new() { Id = "internal-research", Name = "Internal research", Baseline = "Internal" },
        ],
        Applications =
        [
            A("budget-demo", "budget-demo", "public-writing"),
            A("communications-assistant", "communications", "public-writing"),
            A("demo-app", "platform", "public-demo"),
            A("engineering-assistant", "engineering", "internal-coding"),
            A("research-assistant", "research", "internal-research", "public-research"),
        ],
        Policies =
        [
            P("public-demo", "Public", "public-demo", "ollama", UiLogic.DemoExamples.CoderModel),
            P("public-research", "Public", "public-research", "anthropic", Claude),
            P("public-writing", "Public", "public-writing", "anthropic", Claude),
            P("internal-coding", "Internal", "internal-coding", "ollama", UiLogic.DemoExamples.CoderModel),
            P("internal-research", "Internal", "internal-research", "ollama", UiLogic.DemoExamples.ResearchModel),
            P("internal", "Internal", null, "ollama", UiLogic.DemoExamples.CoderModel),
            P("sensitive", "Pii", null, "ollama", UiLogic.DemoExamples.CoderModel),
        ],
        Budgets =
        [
            new() { Id = "global", Scope = "global", CapUsd = 25m, Enforcement = "hard" },
            new() { Id = "budget-demo", Scope = "application:budget-demo", CapUsd = 0.0001m, Enforcement = "hard" },
        ],
        Settings = new() { CacheTtlSeconds = 300 },
    };

    private static List<UiModels.ApplicationSummaryDto> Apps(UiModels.CovenantConfigDto c) => c.Applications.Select(a => new UiModels.ApplicationSummaryDto
    {
        Id = a.Id, Team = a.Team, Enabled = true, ConsoleTesting = true, GrantedProfiles = a.GrantedProfiles.ToList(), DefaultProfile = a.DefaultProfile,
    }).ToList();

    private static readonly List<UiModels.BudgetStateDto> Budgets =
    [
        new() { Id = "global", Scope = "global", CapUsd = 25m, RemainingUsd = 24m, Enforcement = "hard" },
        new() { Id = "budget-demo", Scope = "application:budget-demo", CapUsd = 0.0001m, RemainingUsd = 0.0001m, Enforcement = "hard" },
    ];

    private static UiModels.WebSearchStatusDto Search(bool ready = true) => new()
    {
        KeyConfigured = ready, EgressApproved = true, ModeAllowsSearch = true, Enabled = true, PriceConfigured = true,
        ProfilesAllowed = ["public-demo", "public-research"], Ready = ready, ConfigVersion = 7,
    };

    private static List<UiLogic.ExampleResolution> For(string app, UiModels.WebSearchStatusDto? search = null)
    {
        var cfg = V7();
        return UiLogic.DemoExamples.All
            .Select(e => UiLogic.DemoExamples.ResolveFor(e, app, cfg, Apps(cfg), Budgets, search ?? Search()))
            .Where(r => r.Relevant).ToList();
    }

    private static string[] Ids(IEnumerable<UiLogic.ExampleResolution> rs) => rs.Select(r => r.Example.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Each_application_lists_only_examples_its_workloads_and_routes_support()
    {
        Assert.Equal(["internal-coding"], Ids(For("engineering-assistant")));                       // no research-model or public examples
        // ADR-0019: the sensitive pair ("sensitive-local" / "sensitive-public-rejected") is offered only to an application whose
        // workloads show BOTH halves (Internal routes sensitive data locally AND Public rejects it) — none in v7; see
        // SensitiveDataActionUiTests for demo-app. Engineering (Internal only) gets no sensitive-data noise.
        Assert.Equal(["cache-repeat", "internal-research", "public-research", "sensitive-refusal"], Ids(For("research-assistant")));
        Assert.Equal(["cache-repeat", "public-writing", "sensitive-refusal"], Ids(For("communications-assistant")));
        Assert.Equal(["budget-refusal", "cache-repeat", "public-writing", "sensitive-refusal"], Ids(For("budget-demo")));
        // demo-app's Public workload searches the web and answers on the LOCAL coder: the generic web-research example and the
        // local-inference + governed-search example both describe it; nothing external-model-only is offered.
        Assert.Equal(["cache-repeat", "public-local-research", "public-research"], Ids(For("demo-app")));   // Public only here; no external to block
        Assert.DoesNotContain("public-local-research", Ids(For("research-assistant")));   // its Public research answers on Claude
        Assert.DoesNotContain("budget-refusal", Ids(For("research-assistant")));
    }

    [Fact]
    public void A_mixed_writing_application_gets_writing_examples_not_coding_ones_on_a_shared_coder_route()
    {
        // communications-assistant: Internal writing (routed to the same local coder model) + Public writing (Claude).
        var cfg = V7();
        cfg.Profiles.Add(new() { Id = "internal-writing", Name = "Internal writing", Baseline = "Internal" });
        cfg.Policies.Add(P("internal-writing", "Internal", "internal-writing", "ollama", UiLogic.DemoExamples.CoderModel));
        cfg.Applications.Single(a => a.Id == "communications-assistant").GrantedProfiles = ["internal-writing", "public-writing"];
        var ids = UiLogic.DemoExamples.All
            .Select(e => UiLogic.DemoExamples.ResolveFor(e, "communications-assistant", cfg, Apps(cfg), Budgets, Search()))
            .Where(r => r.Relevant).Select(r => r.Example.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(["cache-repeat", "internal-writing", "public-writing", "sensitive-refusal"], ids);   // generic examples kept
        Assert.DoesNotContain("internal-coding", ids);                                                 // same model ≠ coding work
        var writing = UiLogic.DemoExamples.ResolveFor(UiLogic.DemoExamples.Find("internal-writing")!, "communications-assistant", cfg, Apps(cfg), Budgets, Search());
        Assert.True(writing.Ready);
        Assert.Equal("internal-writing", writing.ProfileId);
        Assert.Equal(["internal-coding"], Ids(For("engineering-assistant")));                         // a declared coding workload still gets it
    }

    private static (string[] Recommended, string[] More) Groups(UiModels.CovenantConfigDto cfg, string app)
    {
        var relevant = UiLogic.DemoExamples.All
            .Select(e => UiLogic.DemoExamples.ResolveFor(e, app, cfg, Apps(cfg), Budgets, Search()))
            .Where(r => r.Relevant).ToList();
        var g = UiLogic.ExampleRecommendations.Split(relevant);
        return (g.Recommended.Select(r => r.Example.Id).ToArray(), g.More.Select(r => r.Example.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Each_application_recommends_at_most_three_task_examples_and_keeps_diagnostics_under_more()
    {
        var cfg = V7();
        Assert.Equal(["internal-coding"], Groups(cfg, "engineering-assistant").Recommended);
        Assert.Equal(["internal-research"], Groups(cfg, "research-assistant").Recommended);          // cloud research is optional
        Assert.Equal(["cache-repeat", "public-research", "sensitive-refusal"], Groups(cfg, "research-assistant").More);
        Assert.Equal(["public-writing"], Groups(cfg, "communications-assistant").Recommended);
        Assert.Equal(["cache-repeat", "sensitive-refusal"], Groups(cfg, "communications-assistant").More);
        Assert.Equal(["public-writing", "budget-refusal"], Groups(cfg, "budget-demo").Recommended);  // dedicated budget app only
        Assert.Equal(["public-local-research"], Groups(cfg, "demo-app").Recommended);
        foreach (var app in new[] { "engineering-assistant", "research-assistant", "communications-assistant", "budget-demo", "demo-app" })
            Assert.True(Groups(cfg, app).Recommended.Length is >= 1 and <= UiLogic.ExampleRecommendations.Max);
    }

    [Fact]
    public void The_engineering_comparison_is_titled_from_the_configured_external_model_and_explains_both_steps()
    {
        var cfg = V7();
        cfg.Connections.Single(c => c.Id == "anthropic").Models.Add(new() { ModelId = "claude-opus-5-5", Price = new() { InputPer1M = 4m, OutputPer1M = 20m } });
        cfg.Profiles.Add(new() { Id = "public-coding", Name = "Public coding", Baseline = "Public" });
        var route = P("public-coding", "Public", "public-coding", "ollama", UiLogic.DemoExamples.CoderModel);
        route.Targets.Add(new() { ConnectionId = "anthropic", ModelId = "claude-opus-5-5" });
        cfg.Policies.Add(route);
        cfg.Applications.Single(a => a.Id == "engineering-assistant").GrantedProfiles = ["internal-coding", "public-coding"];

        var (recommended, _) = Groups(cfg, "engineering-assistant");
        Assert.Equal(["public-coding-escalation", "internal-coding"], recommended);
        var r = UiLogic.DemoExamples.ResolveFor(UiLogic.DemoExamples.Find("public-coding-escalation")!, "engineering-assistant", cfg, Apps(cfg), Budgets, Search());
        Assert.Equal("Compare local coding with Claude Opus 5.5", r.Title);                  // derived from the route, not hard-coded
        Assert.Contains("Step 1", r.Steps, StringComparison.Ordinal);
        Assert.Contains("second, paid request", r.Steps, StringComparison.Ordinal);
        Assert.Contains("your judgement", r.Steps, StringComparison.Ordinal);               // no automatic quality claim

        // Without an approved external alternative, nothing is claimed: no comparison example, no derived title.
        route.Targets.RemoveAt(1);
        Assert.False(UiLogic.DemoExamples.ResolveFor(UiLogic.DemoExamples.Find("public-coding-escalation")!, "engineering-assistant", cfg, Apps(cfg), Budgets, Search()).Relevant);
        Assert.Equal("Claude Opus 5.5", UiLogic.ModelLabels.Readable("claude-opus-5-5"));
        Assert.Equal("qwen2.5-coder:7b", UiLogic.ModelLabels.Readable("qwen2.5-coder:7b"));
    }

    // ---------------------------------------------------------------- sensitive examples, example pins, modified marker

    private static UiLogic.ExampleResolution ResolveFor(string example, string app)
    {
        var cfg = V7();
        return UiLogic.DemoExamples.ResolveFor(UiLogic.DemoExamples.Find(example)!, app, cfg, Apps(cfg), Budgets, Search());
    }

    [Fact]
    public void Without_a_rejecting_public_workload_only_the_explicit_external_pin_example_is_offered()
    {
        // research-assistant: Internal research + Public research (Claude, route action). The pin example is offered;
        // the Internal/Public pair is not (no Public workload rejects sensitive data here — ADR-0019).
        var local = ResolveFor("sensitive-local", "research-assistant");
        var blocked = ResolveFor("sensitive-refusal", "research-assistant");
        Assert.Equal(local.Example.Prompt, blocked.Example.Prompt);                      // same synthetic input
        Assert.False(local.Relevant);
        Assert.False(local.Ready);
        Assert.Equal("anthropic/" + Claude, blocked.ModelPin);                           // explicit external attempt
        Assert.Contains("Blocked by policy", blocked.Example.Expected, StringComparison.Ordinal);
        Assert.Contains("not a provider outage", blocked.Example.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_workload_clears_an_example_pin_but_keeps_an_explicit_user_pin()
    {
        var cfg = V7();
        var form = new UiLogic.PlaygroundForm();
        var blocked = ResolveFor("sensitive-refusal", "communications-assistant");
        Assert.True(UiLogic.DemoExamples.ApplyTo(blocked, form));
        Assert.True(form.PinnedByExample);
        Assert.Equal("anthropic/" + Claude, form.RequestedModel);

        // Workload switch (PlaygroundState.SelectProfile → ResetExamplePin): back to "Let policy choose".
        Assert.True(UiLogic.ComposerRules.SelectProfile(form, Apps(cfg), "public-writing"));
        Assert.True(UiLogic.DemoExamples.ResetExamplePin(form));
        Assert.Equal(UiLogic.RouteMode.Policy, form.Mode);
        Assert.Null(form.RequestedModel);
        Assert.False(form.PinnedByExample);

        // A pin the operator chose is NOT silently removed (the composer shows a warning and a "Let policy choose" button).
        form.Mode = UiLogic.RouteMode.Catalog;
        form.CatalogModel = "anthropic/" + Claude;
        form.PinnedByExample = false;
        Assert.False(UiLogic.DemoExamples.ResetExamplePin(form));
        Assert.Equal("anthropic/" + Claude, form.RequestedModel);
    }

    [Fact]
    public void Changes_are_shown_before_use_and_an_edited_form_loses_its_expected_outcome()
    {
        var blocked = ResolveFor("sensitive-refusal", "communications-assistant");
        var form = new UiLogic.PlaygroundForm { ApplicationId = "communications-assistant", ProfileId = "public-writing", Prompt = "draft" };
        var changes = UiLogic.DemoExamples.ChangesFor(blocked, form);
        Assert.Contains(changes, c => c.StartsWith("Model: explicitly pinned to anthropic/", StringComparison.Ordinal));
        Assert.Contains("Prompt: replaced", changes);

        Assert.True(UiLogic.DemoExamples.ApplyTo(blocked, form));
        Assert.Empty(UiLogic.DemoExamples.ChangesFor(blocked, form));                   // nothing left to change
        Assert.True(UiLogic.DemoExamples.StillMatches(blocked.Example, form));
        form.Mode = UiLogic.RouteMode.Policy;                                            // the operator edited the routing
        Assert.False(UiLogic.DemoExamples.StillMatches(blocked.Example, form));          // → "Modified", no stale expectation
        Assert.Equal("Custom", UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch).ScenarioLabel);
    }

    [Fact]
    public void Both_research_examples_ask_one_targeted_official_source_question()
    {
        foreach (var id in new[] { "public-research", "public-local-research" })
        {
            var ex = UiLogic.DemoExamples.Find(id)!;
            Assert.StartsWith("site:dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core .NET 10 support end date.", ex.Prompt, StringComparison.Ordinal);
            Assert.Contains("only the retrieved sources", ex.Prompt, StringComparison.Ordinal);
            Assert.Equal(UiLogic.WebSourcesMode.Required, ex.WebSources);
        }
    }

    [Fact]
    public void Applying_an_example_keeps_the_selected_application_and_owner()
    {
        foreach (var app in new[] { "research-assistant", "communications-assistant", "engineering-assistant", "demo-app", "budget-demo" })
            foreach (var r in For(app).Where(r => r.Ready))
            {
                Assert.Equal(app, r.ApplicationId);
                var form = new UiLogic.PlaygroundForm { ApplicationId = app };
                Assert.True(UiLogic.DemoExamples.ApplyTo(r, form));
                Assert.Equal(app, form.ApplicationId);
            }
    }

    [Fact]
    public void Public_research_resolves_the_configured_cloud_destination_without_needing_a_local_model()
    {
        var r = For("research-assistant").Single(x => x.Example.Id == "public-research");
        Assert.True(r.Ready);
        Assert.Equal("public-research", r.ProfileId);
        Assert.StartsWith("anthropic/" + Claude, r.ConfiguredRoute, StringComparison.Ordinal);
        Assert.Contains("Team research", r.Payer, StringComparison.Ordinal);

        var internalResearch = For("research-assistant").Single(x => x.Example.Id == "internal-research");
        Assert.StartsWith("ollama/" + UiLogic.DemoExamples.ResearchModel, internalResearch.ConfiguredRoute, StringComparison.Ordinal);   // stays local
    }

    [Fact]
    public void A_relevant_example_that_cannot_run_now_stays_listed_with_its_cause()
    {
        var r = For("research-assistant", Search(ready: false)).Single(x => x.Example.Id == "public-research");
        Assert.False(r.Ready);
        Assert.True(r.Relevant);
        Assert.Contains(r.Missing, m => m.Href == "settings#ws-h");
    }

    [Fact]
    public void Ordinary_examples_on_the_budget_demo_app_say_its_budget_is_exhausted()
    {
        var writing = For("budget-demo").Single(x => x.Example.Id == "public-writing");
        Assert.False(writing.Ready);
        Assert.Contains(writing.Missing, m => m.Text.Contains("budget budget-demo is exhausted", StringComparison.Ordinal));
        Assert.True(For("budget-demo").Single(x => x.Example.Id == "budget-refusal").Ready);
    }

    // ---------------------------------------------------------------- workload control

    [Theory]
    [InlineData(0, UiLogic.WorkloadChoiceKind.None)]
    [InlineData(1, UiLogic.WorkloadChoiceKind.Single)]
    [InlineData(2, UiLogic.WorkloadChoiceKind.Segmented)]
    [InlineData(3, UiLogic.WorkloadChoiceKind.Select)]
    [InlineData(5, UiLogic.WorkloadChoiceKind.Select)]
    public void Workload_control_shape_follows_the_number_of_granted_workloads(int count, UiLogic.WorkloadChoiceKind kind)
        => Assert.Equal(kind, UiLogic.ComposerRules.WorkloadChoice(count));

    [Fact]
    public void Workload_icons_come_from_configuration()
    {
        var cfg = V7();
        string Icon(string id) => UiLogic.ComposerRules.WorkloadIcon(cfg.Profiles.Single(p => p.Id == id));
        Assert.Equal("globe", Icon("public-research"));
        Assert.Equal("pen", Icon("public-writing"));
        Assert.Equal("code", Icon("internal-coding"));
        Assert.Equal("shield", Icon("internal-research"));
    }

    [Theory]
    [InlineData(0, 2, "ArrowRight", 1)]
    [InlineData(1, 2, "ArrowRight", 0)]
    [InlineData(0, 2, "ArrowLeft", 1)]
    [InlineData(1, 2, "Home", 0)]
    [InlineData(0, 2, "End", 1)]
    [InlineData(0, 2, "Tab", 0)]
    public void Arrow_keys_move_within_the_radio_group(int current, int count, string key, int next)
        => Assert.Equal(next, UiLogic.ComposerRules.NextIndex(current, count, key));

    // ---------------------------------------------------------------- search location

    [Theory]
    [InlineData("What's the weather today?", null, true)]
    [InlineData("What's the weather today?", "Toronto", false)]
    [InlineData("What's the weather in Toronto today?", null, false)]
    [InlineData("Explain photosynthesis.", null, false)]
    public void Weather_without_a_place_is_caught_before_sending(string prompt, string? location, bool blocked)
    {
        Assert.Equal(blocked, UiLogic.SearchIntent.MissingLocation(UiLogic.WebSourcesMode.Auto, prompt, location) is not null);
        Assert.Null(UiLogic.SearchIntent.MissingLocation(UiLogic.WebSourcesMode.Off, prompt, location));   // Off never searches
        var form = new UiLogic.PlaygroundForm { ApplicationId = "x", Prompt = prompt, Location = location ?? "", WebSources = UiLogic.WebSourcesMode.Required };
        Assert.Equal(blocked, form.Problem is not null);
    }

    [Fact]
    public void Location_effect_is_explicit_when_web_sources_are_off()
    {
        Assert.Contains("Ignored", UiLogic.SearchIntent.LocationEffect(UiLogic.WebSourcesMode.Off, "weather?", "Toronto"), StringComparison.Ordinal);
        Assert.Contains("given to the model", UiLogic.SearchIntent.LocationEffect(UiLogic.WebSourcesMode.Required, "weather?", "Toronto"), StringComparison.Ordinal);
        Assert.Contains("prompt names Ottawa", UiLogic.SearchIntent.LocationEffect(UiLogic.WebSourcesMode.Auto, "weather in Ottawa", null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("What's the weather in Toronto today?")]
    [InlineData("Give me the forecast for New York City this weekend")]
    [InlineData("temperature at São Paulo right now")]
    [InlineData("What's the weather for Today?")]
    [InlineData("weather in my area")]
    [InlineData("Explain photosynthesis")]
    public void Console_weather_and_place_rules_match_the_host(string text)
    {
        Assert.Equal(WebSearchPlanner.IsWeather(text), UiLogic.SearchIntent.IsWeather(text));
        Assert.Equal(WebSearchPlanner.ExtractPlace(text), UiLogic.SearchIntent.ExtractPlace(text));
    }

    // ---------------------------------------------------------------- explicit grants only

    [Fact]
    public void Workload_setup_grants_nothing_unless_an_owner_is_chosen()
    {
        var cfg = V7();
        cfg.Profiles.RemoveAll(p => p.Id == "public-research");
        cfg.Policies.RemoveAll(p => p.Id == "public-research");
        foreach (var a in cfg.Applications) a.GrantedProfiles.Remove("public-research");
        if (cfg.Applications.Single(a => a.Id == "research-assistant").DefaultProfile == "public-research")
            cfg.Applications.Single(a => a.Id == "research-assistant").DefaultProfile = "internal-research";
        var preset = UiLogic.WorkloadRouteSetup.Presets.Single(p => p.ProfileId == "public-research");

        var none = UiLogic.WorkloadRouteSetup.Plan(cfg, preset, "anthropic", Claude);
        Assert.True(none.Ok, none.Problem);
        Assert.DoesNotContain(none.Changes, c => c.StartsWith("Grant", StringComparison.Ordinal));
        Assert.Contains(none.Changes, c => c.Contains("No application is granted", StringComparison.Ordinal));

        var draft = UiLogic.ConfigCopy.Deep(cfg);
        var chosen = UiLogic.WorkloadRouteSetup.ApplyTo(draft, preset, "anthropic", Claude, ["research-assistant"]);
        Assert.True(chosen.Ok, chosen.Problem);
        Assert.Contains("public-research", draft.Applications.Single(a => a.Id == "research-assistant").GrantedProfiles);
        Assert.All(draft.Applications.Where(a => a.Id != "research-assistant"), a => Assert.DoesNotContain("public-research", a.GrantedProfiles));
        Assert.Equal(["internal-research", "public-research"], draft.Applications.Single(a => a.Id == "research-assistant").GrantedProfiles.ToArray());
    }

    // ---------------------------------------------------------------- query intent (May / years)

    [Theory]
    [InlineData("What was the weather in Toronto on May 3?")]
    [InlineData("Toronto weather 3 May")]
    [InlineData("Toronto rainfall in May 2025")]
    [InlineData("Toronto weather in 2024")]
    public void Historical_may_and_year_questions_keep_their_own_period(string question)
        => Assert.Equal(WebSearchPlanner.BuildQuery(question, null), WebSearchPlanner.BuildSearchQuery(question, null, DateTimeOffset.Parse("2026-10-17T01:30:00Z")));

    [Fact]
    public void May_as_a_verb_does_not_block_todays_date()
        => Assert.EndsWith("October 17 2026", WebSearchPlanner.BuildSearchQuery("Toronto weather: may it rain now?", null, DateTimeOffset.Parse("2026-10-17T01:30:00Z")), StringComparison.Ordinal);

    // ---------------------------------------------------------------- final usability corrections

    [Fact]
    public void A_new_request_defaults_to_1024_output_tokens_and_keeps_explicit_edits()
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "communications-assistant", Prompt = "x" };
        Assert.Equal(1024, form.MaxTokens);
        form.MaxTokens = 2048;
        Assert.Equal(2048, UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch).MaxTokens);   // what was typed is what is sent

        // Applying an example that sets no bound keeps the operator's value; the research example sets its own.
        var writing = For("communications-assistant").Single(r => r.Example.Id == "public-writing");
        Assert.True(UiLogic.DemoExamples.ApplyTo(writing, form));
        Assert.Equal(2048, form.MaxTokens);
        form.Clear();
        Assert.Equal(1024, form.MaxTokens);
    }

    [Fact]
    public void A_truncated_answer_carries_a_prominent_warning_with_the_enforced_bound()
    {
        var r = new UiModels.AuditEntryView
        {
            SchemaVersion = 2, EffectiveMaxOutputTokens = 256,
            Attempts = [new() { AttemptId = "att_1", Outcome = "served", Truncated = true, EffectiveMaxOutputTokens = 256 }],
        };
        var warning = UiLogic.RecordFacts.TruncationWarning(r);
        Assert.NotNull(warning);
        Assert.Contains("256 tokens enforced", warning, StringComparison.Ordinal);
        Assert.Contains("\"length\"", warning, StringComparison.Ordinal);
        r.Attempts[0].Truncated = false;
        Assert.Null(UiLogic.RecordFacts.TruncationWarning(r));
    }

    [Fact]
    public void The_writing_example_is_offered_only_for_a_writing_workload()
    {
        Assert.DoesNotContain("public-writing", Ids(For("research-assistant")));
        Assert.Contains("public-writing", Ids(For("communications-assistant")));

        // Even a non-search Public research workload on the same cloud model is not "writing".
        var cfg = V7();
        cfg.Profiles.Single(p => p.Id == "public-research").WebSearchAllowed = false;
        var writing = UiLogic.DemoExamples.ResolveFor(UiLogic.DemoExamples.Find("public-writing")!, "research-assistant", cfg, Apps(cfg), Budgets, Search());
        Assert.False(writing.Relevant);
    }

    [Fact]
    public void Two_workloads_with_different_baselines_use_short_labels_and_keep_full_names()
    {
        var cfg = V7();
        UiModels.WorkloadProfileDto? P(string id) => cfg.Profiles.Single(p => p.Id == id);
        var labels = UiLogic.ComposerRules.SegmentLabels([P("internal-research"), P("public-research")], ["internal-research", "public-research"]);
        Assert.Equal(["Internal", "Public"], labels.Select(l => l.Text).ToArray());
        Assert.Equal(["Internal research", "Public research"], labels.Select(l => l.FullName).ToArray());
        Assert.All(labels, l => Assert.True(l.Short));

        var same = UiLogic.ComposerRules.SegmentLabels([P("public-research"), P("public-writing")], ["public-research", "public-writing"]);
        Assert.Equal(["Public research", "Public writing"], same.Select(l => l.Text).ToArray());   // full names, wrapped — not a fake binary
        Assert.All(same, l => Assert.False(l.Short));
    }

    [Fact]
    public void Search_off_shows_the_precise_reason_without_alarm()
    {
        // November brief: a precise reason is visible even with web sources Off (neutral styling until search is requested).
        var cfg = V7();
        var app = Apps(cfg).Single(a => a.Id == "research-assistant");
        var r = UiLogic.WebSourcesText.Readiness(Search(), null, cfg, app, "internal-research");
        Assert.False(r.Possible);
        Assert.True(r.WorkloadCause);
        Assert.Equal("Internal workloads cannot use web search — it is only for Public workloads, independent of the inference route.", r.QuietLine);
        Assert.Null(r.Href);                                                                          // correct behaviour, nothing to configure
        Assert.Equal("public-research", r.SwitchToProfile);

        var server = UiLogic.WebSourcesText.Readiness(Search(ready: false), null, cfg, app, "public-research");
        Assert.False(server.WorkloadCause);
        Assert.Equal("The server has no Tavily API key.", server.QuietLine);
        Assert.Equal("settings#ws-h", server.Href);
        Assert.Null(UiLogic.WebSourcesText.Readiness(Search(), null, cfg, app, "public-research").QuietLine);
    }
}
