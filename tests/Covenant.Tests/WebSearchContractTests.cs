using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
// Aliased, not imported (see ContractRoundTripTests): Core/Host and the console both have config shapes.
using UiModels = Covenant.Ui.Models;
using UiLogic = Covenant.Ui.Logic;

namespace Covenant.Tests;

/// <summary>ADR-0015 wire contracts Host ⇄ console, field by field, plus the console's pure web-sources rules (state,
/// wording, URL safety). A field the console drops or misspells fails here — in particular a console save must never
/// silently drop <c>web_search</c> or a profile's <c>web_search_allowed</c>.</summary>
public sealed class WebSearchContractTests
{
    [Fact]
    public void Demo_presets_do_not_reuse_a_previous_web_search_request()
    {
        var form = new UiLogic.PlaygroundForm { WebSources = UiLogic.WebSourcesMode.Required, Location = "Toronto" };
        var preset = UiLogic.ScenarioPresets.All[1];
        UiLogic.ScenarioPresets.ApplyTo(preset, form);
        Assert.Equal(UiLogic.WebSourcesMode.Off, form.WebSources);
        Assert.Empty(form.Location);
        Assert.True(preset.StillMatches(form));
        form.WebSources = UiLogic.WebSourcesMode.Required;
        Assert.False(preset.StillMatches(form));
    }

    private static readonly DateTimeOffset Retrieved = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static CovenantConfig WithSearch() => V2.Hybrid(tweak: c => c with
    {
        WebSearch = new WebSearchSettings
        {
            Enabled = true, MaxResults = 2, TimeoutSeconds = 7, PricePerCreditUsd = 0.008m,
            PriceBasis = "Tavily pay-as-you-go", PriceEffectiveDate = new DateOnly(2026, 10, 1),
        },
        Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { WebSearchAllowed = true } : p).ToList(),
    });

    [Fact]
    public void Console_save_round_trips_web_search_settings_and_profile_permission_unchanged()
    {
        var host = WithSearch();
        var getBody = JsonSerializer.Serialize(new ConfigGetResponse { Version = host.Version, Etag = "\"v1\"", Config = host, Secrets = new(), AllowList = [WebSearchEgress.Authority] }, ConfigJsonContext.Default.ConfigGetResponse);

        var ui = JsonSerializer.Deserialize(getBody, UiModels.UiJsonContext.Default.ConfigEnvelopeDto)!;
        var ws = ui.Config!.WebSearch!;
        Assert.True(ws.Enabled);
        Assert.Equal(2, ws.MaxResults);
        Assert.Equal(7, ws.TimeoutSeconds);
        Assert.Equal(0.008m, ws.PricePerCreditUsd);
        Assert.Equal("Tavily pay-as-you-go", ws.PriceBasis);
        Assert.Equal("2026-10-01", ws.PriceEffectiveDate);
        Assert.True(ui.Config.Profiles.Single(p => p.Id == "public-demo").WebSearchAllowed);
        Assert.False(ui.Config.Profiles.Single(p => p.Id == "internal-default").WebSearchAllowed);

        // The console's deep copy (draft) and PUT body keep both.
        var draft = UiLogic.ConfigCopy.Deep(ui.Config);
        var put = JsonSerializer.Serialize(new UiModels.ConfigPutRequestDto { Config = draft, ChangeSummary = "round trip" }, UiModels.UiJsonContext.Default.ConfigPutRequestDto);
        var back = JsonSerializer.Deserialize(put, ConfigJsonContext.Default.ConfigPutRequest)!.Config!;
        Assert.Equal(host.WebSearch, back.WebSearch);
        Assert.True(back.Profiles.Single(p => p.Id == "public-demo").WebSearchAllowed);
        var env = V2.Env(true, WebSearchEgress.Authority);
        Assert.True(ConfigValidator.Validate(back, new ValidationEnvironment
        {
            EgressAllowList = env.EgressAllowList, SecretAvailable = env.SecretAvailable, LocalEndpoints = env.LocalEndpoints, SearchKeyAvailable = true,
        }).IsValid);
    }

    [Fact]
    public void A_configuration_without_web_search_stays_without_it_after_a_console_save()
    {
        var host = V2.Hybrid();
        var ui = JsonSerializer.Deserialize(JsonSerializer.Serialize(host, ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;
        Assert.Null(ui.WebSearch);
        var json = JsonSerializer.Serialize(ui, UiModels.UiJsonContext.Default.CovenantConfigDto);
        Assert.DoesNotContain("web_search\"", json, StringComparison.Ordinal);    // nulls omitted; no implicit enablement
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CovenantConfig)!;
        Assert.Null(back.WebSearch);
    }

    [Fact]
    public void Change_summary_names_a_web_search_change()
    {
        var before = JsonSerializer.Deserialize(JsonSerializer.Serialize(WithSearch(), ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;
        var after = UiLogic.ConfigCopy.Deep(before);
        after.WebSearch!.Enabled = false;
        Assert.Contains("web search settings changed", UiLogic.ChangeSummary.Lines(before, after));
        after.WebSearch = null;
        Assert.Contains("web search settings changed", UiLogic.ChangeSummary.Lines(before, after));
    }

    [Theory]
    [InlineData(UiLogic.WebSourcesMode.Off, null, null)]
    [InlineData(UiLogic.WebSourcesMode.Auto, "auto", "Toronto")]
    [InlineData(UiLogic.WebSourcesMode.Required, "required", "Toronto")]
    public void Playground_body_carries_web_sources_and_location_the_host_accepts(UiLogic.WebSourcesMode mode, string? wire, string? location)
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "demo-app", Prompt = "What's the weather today?", WebSources = mode, Location = "  Toronto " };
        var snapshot = UiLogic.RequestSnapshot.Freeze(1, form, Retrieved);
        Assert.Equal(location, snapshot.Location);                   // trimmed; never sent when Off
        var body = JsonSerializer.Serialize(new UiModels.PlaygroundRequestDto
        {
            ApplicationId = "demo-app", Messages = [new UiModels.ChatMessageDto { Role = "user", Content = snapshot.Prompt }],
            ClientRequestId = snapshot.ClientRequestId,
            WebSearch = UiLogic.WebSourcesText.Wire(snapshot.WebSources),
            Location = snapshot.WebSources == UiLogic.WebSourcesMode.Off ? null : snapshot.Location,
        }, UiModels.UiJsonContext.Default.PlaygroundRequestDto);
        if (wire is null) Assert.DoesNotContain("web_search", body, StringComparison.Ordinal);
        var host = JsonSerializer.Deserialize(body, CovenantJsonContext.Default.PlaygroundRequest)!;
        Assert.Null(PlaygroundApi.Validate(host));
        Assert.Equal(wire, host.WebSearch);
        Assert.Equal(location, host.Location);
        Assert.Equal(mode switch { UiLogic.WebSourcesMode.Auto => WebSearchMode.Auto, UiLogic.WebSourcesMode.Required => WebSearchMode.Required, _ => WebSearchMode.Off },
            PlaygroundApi.ParseWebSearch(host.WebSearch));
    }

    [Fact]
    public void Snapshot_is_immutable_and_detects_a_changed_web_sources_form()
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "demo-app", Prompt = "weather today", WebSources = UiLogic.WebSourcesMode.Auto, Location = "Toronto" };
        var snapshot = UiLogic.RequestSnapshot.Freeze(1, form, Retrieved);
        Assert.True(snapshot.MatchesForm(form));
        form.Location = "Ottawa";
        Assert.False(snapshot.MatchesForm(form));
        Assert.Equal("Toronto", snapshot.Location);
        form.Location = "Toronto";
        form.WebSources = UiLogic.WebSourcesMode.Required;
        Assert.False(snapshot.MatchesForm(form));
        Assert.Equal(UiLogic.WebSourcesMode.Auto, snapshot.WebSources);

        form.Clear();
        Assert.Equal(UiLogic.WebSourcesMode.Off, form.WebSources);
        Assert.Equal("", form.Location);
    }

    [Fact]
    public void Oversized_location_blocks_run_only_when_web_sources_are_on()
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "demo-app", Prompt = "weather", Location = new string('x', 101) };
        Assert.Null(form.Problem);                                        // Off: the field is not sent
        form.WebSources = UiLogic.WebSourcesMode.Auto;
        Assert.NotNull(form.Problem);
        Assert.Contains("100", form.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_and_sources_views_reach_the_console_without_secrets()
    {
        var cfg = new RuntimeSnapshot(WithSearch());
        var stub = new StubSearchClient();
        var status = WebSearchApi.Status(cfg, stub, keyConfigured: true, egressApproved: false);
        var json = JsonSerializer.Serialize(status, WebSearchJsonContext.Default.WebSearchStatusView);
        var ui = JsonSerializer.Deserialize(json, UiModels.UiJsonContext.Default.WebSearchStatusDto)!;
        Assert.True(ui.KeyConfigured);
        Assert.False(ui.EgressApproved);
        Assert.True(ui.ModeAllowsSearch);
        Assert.True(ui.Enabled);
        Assert.True(ui.PriceConfigured);
        Assert.Equal(0.008m, ui.PricePerCreditUsd);
        Assert.Equal("public-demo", Assert.Single(ui.ProfilesAllowed));
        Assert.False(ui.Ready);                                         // egress missing
        Assert.Contains(ui.Setup, s => s.Contains(WebSearchEgress.Authority, StringComparison.Ordinal));
        Assert.Contains(UiLogic.WebSourcesText.Checklist(ui), c => !c.Ok && c.Label.Contains("allow-list", StringComparison.Ordinal));
        Assert.DoesNotContain("tvly-", json, StringComparison.Ordinal);

        var state = new WebSearchState { Mode = WebSearchMode.Required, Triggered = true, Cue = "weather", Outcome = TraceStates.Passed, DurationMs = 41.5, RetrievedUtc = Retrieved };
        state.Sources = [new WebSource("Toronto weather", "https://weather.example.org/toronto", "Sunny — snippet text", Retrieved)];
        var view = WebSearchApi.View("req_1", state);
        var viewJson = JsonSerializer.Serialize(view, WebSearchJsonContext.Default.RequestSearchView);
        Assert.DoesNotContain("Sunny", viewJson, StringComparison.Ordinal);   // snippets never leave the server for display
        var uiView = JsonSerializer.Deserialize(viewJson, UiModels.UiJsonContext.Default.RequestSearchDto)!;
        Assert.Equal("req_1", uiView.RequestId);
        Assert.Equal("required", uiView.Mode);
        Assert.Equal("passed", uiView.Outcome);
        Assert.Equal(Retrieved, uiView.RetrievedUtc);
        var src = Assert.Single(uiView.Sources);
        Assert.Equal("https://weather.example.org/toronto", src.Url);
        Assert.Equal("Toronto weather", src.Title);
        Assert.Equal("Searched: 1 source retrieved at 2026-10-17 12:00 UTC (cue: weather).",
            UiLogic.WebSourcesText.Status(UiLogic.WebSourcesMode.Required, uiView, 200));
    }

    [Fact]
    public void Audit_web_search_and_tool_calls_reach_the_console_with_tool_wording()
    {
        var entry = new AuditEntry("req_1", Retrieved, "console:demo-app", new AttributionTags("platform", "console", "playground"),
            DataClassification.Public, PolicyEffect.Deny, "web search timed out", null, new Usage(0, 0, 0.008m),
            CostBasis: "estimated_failed_call", ConfigVersion: 3, Attempts: [], SettledUsd: 0.008m,
            WebSearch: new AuditWebSearch("required", true, "weather", "failed", "search_timeout", null, null, 10_000),
            ToolCalls: [new AuditToolCall("tool_1", "web_search", "tavily", "failed", "search_timeout", 10_000, 0.008m, "estimated_failed_call", 0.008m, null)]);
        var ui = JsonSerializer.Deserialize(JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEntry), UiModels.UiJsonContext.Default.AuditEntryView)!;
        Assert.Equal("search_timeout", ui.WebSearch!.ReasonCode);
        Assert.Equal("weather", ui.WebSearch.Cue);
        var tool = Assert.Single(ui.ToolCalls!);
        Assert.Equal("web_search", tool.Tool);
        Assert.Equal(0.008m, tool.CostUsd);
        Assert.True(UiLogic.CostText.ToolOnly(ui));
        Assert.Contains("web-search charge", UiLogic.CostText.Cost(ui), StringComparison.Ordinal);
        Assert.DoesNotContain("text length", UiLogic.CostText.Cost(ui), StringComparison.Ordinal);
        Assert.Contains("reserved charge", UiLogic.CostText.ToolCost(tool), StringComparison.Ordinal);

        // Backward compatible: a pre-ADR-0015 record has neither field.
        var legacy = JsonSerializer.Deserialize(JsonSerializer.Serialize(entry with { WebSearch = null, ToolCalls = null }, AuditJsonContext.Default.AuditEntry),
            UiModels.UiJsonContext.Default.AuditEntryView)!;
        Assert.Null(legacy.WebSearch);
        Assert.Null(legacy.ToolCalls);
        Assert.False(UiLogic.CostText.ToolOnly(legacy));
    }

    [Theory]
    [InlineData("https://example.org/a?b=1", true)]
    [InlineData("HTTP://Example.org/", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/relative", false)]
    [InlineData("https://user:pw@example.org/", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData(null, false)]
    public void Console_renders_only_safe_source_links(string? url, bool safe)
        => Assert.Equal(safe, UiLogic.WebSourcesText.SafeHref(url) is not null);

    [Theory]
    [InlineData("location_required", "location")]
    [InlineData("search_sensitive", "Public")]
    [InlineData("search_not_permitted", "Nothing was sent")]
    [InlineData("search_timeout", "timed out")]
    [InlineData("search_empty", "no usable sources")]
    [InlineData("search_not_triggered", "no search was made")]
    public void Every_search_reason_code_has_plain_words(string code, string fragment)
        => Assert.Contains(fragment, UiLogic.WebSourcesText.Reason(code), StringComparison.Ordinal);

    [Fact]
    public void Status_line_comes_from_what_the_host_recorded_not_from_the_form()
    {
        Assert.Equal("Web sources: off.", UiLogic.WebSourcesText.Status(UiLogic.WebSourcesMode.Off, null, null));
        Assert.Contains("checking", UiLogic.WebSourcesText.Status(UiLogic.WebSourcesMode.Auto, null, null), StringComparison.Ordinal);
        Assert.Contains("no search record", UiLogic.WebSourcesText.Status(UiLogic.WebSourcesMode.Auto, null, 404), StringComparison.Ordinal);
        var skipped = new UiModels.RequestSearchDto { Outcome = "skipped", ReasonCode = "search_not_triggered" };
        Assert.Contains("no search was made", UiLogic.WebSourcesText.Status(UiLogic.WebSourcesMode.Auto, skipped, 200), StringComparison.Ordinal);
        var refused = new UiModels.RequestSearchDto { Outcome = "refused", ReasonCode = "location_required" };
        Assert.Contains("needs a location", UiLogic.WebSourcesText.Status(UiLogic.WebSourcesMode.Auto, refused, 200), StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_web_search_toggle_is_public_only_and_blocks_a_baseline_change()
    {
        var pub = new UiModels.WorkloadProfileDto { Id = "p", Baseline = "Public" };
        Assert.Null(UiLogic.ProfileEditing.SetWebSearch(pub, true));
        Assert.True(pub.WebSearchAllowed);
        Assert.NotNull(UiLogic.ProfileEditing.SetBaseline(pub, "Internal"));   // turn search off first
        Assert.Equal("Public", pub.Baseline);
        Assert.Null(UiLogic.ProfileEditing.SetWebSearch(pub, false));
        Assert.Null(UiLogic.ProfileEditing.SetBaseline(pub, "Internal"));

        var internalProfile = new UiModels.WorkloadProfileDto { Id = "i", Baseline = "Internal" };
        Assert.NotNull(UiLogic.ProfileEditing.SetWebSearch(internalProfile, true));
        Assert.False(internalProfile.WebSearchAllowed);
    }

    [Fact]
    public void Console_trace_lists_the_search_stage_between_budget_and_provider()
    {
        var stages = UiLogic.TraceModel.Stages.ToList();
        Assert.Equal(stages.IndexOf("budget") + 1, stages.IndexOf("search"));
        Assert.Equal(stages.IndexOf("search") + 1, stages.IndexOf("provider"));
        Assert.Equal("Web search", UiLogic.TraceModel.Label("search"));
        var view = UiLogic.TraceModel.Reduce(
        [
            new UiModels.TraceEventDto { Seq = 1, Stage = "search", State = "running" },
            new UiModels.TraceEventDto { Seq = 2, Stage = "search", State = "refused", ReasonCode = "location_required", DurationMs = 0.2 },
        ], complete: true);
        var step = view.Steps.Single(s => s.Stage == "search");
        Assert.Equal(UiLogic.TraceStepState.Refused, step.State);
        Assert.Equal("location_required", step.ReasonCode);
    }
}
