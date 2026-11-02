using System.Text.Json;
using Covenant.Host;
using Xunit;
using UiModels = Covenant.Ui.Models;
using UiLogic = Covenant.Ui.Logic;

namespace Covenant.Tests;

/// <summary>docs/CONSOLE-USABILITY-NOVEMBER.md: workload ≠ destination, precise search reasons, Requests refresh without gaps,
/// focused entity edits (isolation, dirty tracking, stale-version refusal), budget dialog rules, honest longest-stage
/// indicator, separate connection facts.</summary>
public sealed class NovemberUsabilityTests
{
    private static UiModels.CovenantConfigDto Ui(Func<Covenant.Core.CovenantConfig, Covenant.Core.CovenantConfig>? tweak = null)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(V2.Hybrid(tweak: tweak), ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;

    private static UiModels.WorkloadProfileDto Profile(UiModels.CovenantConfigDto c, string id) => c.Profiles.Single(p => p.Id == id);

    // ---------------------------------------------------------------- §1 workload → destination

    [Fact]
    public void Public_workload_shows_its_approved_cloud_destination_and_internal_stays_local()
    {
        var cfg = Ui();
        var pub = UiLogic.WorkloadDestination.For(cfg, Profile(cfg, "public-demo"));
        Assert.True(pub.Ok);
        Assert.Equal("Cloud", pub.Boundary);
        Assert.Equal("openai/" + V2.CloudModel, pub.Target);
        Assert.Equal("ollama/" + V2.LocalTag, Assert.Single(pub.Fallbacks));
        Assert.Equal("public", pub.PolicyId);

        var internalDest = UiLogic.WorkloadDestination.For(cfg, Profile(cfg, "internal-default"));
        Assert.Equal("Local", internalDest.Boundary);
    }

    [Fact]
    public void Public_in_local_only_mode_says_public_is_not_cloud_here()
    {
        var cfg = Ui();
        cfg.Mode = "local-only";
        var d = UiLogic.WorkloadDestination.For(cfg, Profile(cfg, "public-demo"));
        Assert.Equal("Local", d.Boundary);                                         // cloud target excluded, local fallback serves
        Assert.Contains("Local-only", d.Note, StringComparison.Ordinal);
        Assert.Equal("policies", d.Href);
    }

    [Fact]
    public void A_workload_without_a_route_names_the_problem_and_links_to_routes()
    {
        var cfg = Ui();
        cfg.Policies.RemoveAll(p => p.Classifications.Contains("Public"));
        var d = UiLogic.WorkloadDestination.For(cfg, Profile(cfg, "public-demo"));
        Assert.False(d.Ok);
        Assert.Contains("No approved destination", d.Problem, StringComparison.Ordinal);
        Assert.Equal("policies", d.Href);
    }

    [Fact]
    public void An_example_label_does_not_survive_a_workload_switch()
    {
        var cfg = Ui();
        var apps = cfg.Applications.Select(a => new UiModels.ApplicationSummaryDto
        {
            Id = a.Id, Team = a.Team, Enabled = a.Enabled, ConsoleTesting = a.ConsoleTesting, GrantedProfiles = a.GrantedProfiles.ToList(), DefaultProfile = a.DefaultProfile,
        }).ToList();
        var ex = UiLogic.DemoExamples.Find("cache-repeat")!;
        cfg.Settings!.CacheTtlSeconds = 60;
        var r = UiLogic.DemoExamples.ResolveFor(ex, "demo-app", cfg, apps, null, null);
        var form = new UiLogic.PlaygroundForm();
        Assert.True(UiLogic.DemoExamples.ApplyTo(r, form));
        Assert.True(UiLogic.DemoExamples.StillMatches(ex, form));
        form.ProfileId = "internal-default";                                       // switched workload
        Assert.False(UiLogic.DemoExamples.StillMatches(ex, form));
        Assert.Equal("Custom", UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch).ScenarioLabel);
    }

    // ---------------------------------------------------------------- §2 precise search reasons

    private static UiModels.WebSearchStatusDto Ready() => new()
    {
        KeyConfigured = true, EgressApproved = true, ModeAllowsSearch = true, Enabled = true, PriceConfigured = true,
        ProfilesAllowed = ["public-demo"], Ready = true,
    };

    [Theory]
    [InlineData("local", "Local-only", "policies")]
    [InlineData("disabled", "disabled", "settings#ws-h")]
    [InlineData("price", "price", "settings#ws-h")]
    [InlineData("key", "Tavily API key", "settings#ws-h")]
    public void Each_blocking_cause_is_stated_precisely_even_with_search_off(string gap, string fragment, string href)
    {
        var cfg = Ui(c => c with { Profiles = c.Profiles.Select(p => p.Id == "public-demo" ? p with { WebSearchAllowed = true } : p).ToList() });
        var s = Ready();
        s.Ready = false;
        switch (gap)
        {
            case "local": s.ModeAllowsSearch = false; break;
            case "disabled": s.Enabled = false; break;
            case "price": s.PriceConfigured = false; break;
            case "key": s.KeyConfigured = false; break;
        }
        var app = new UiModels.ApplicationSummaryDto { Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-demo", "internal-default"] };
        var r = UiLogic.WebSourcesText.Readiness(s, null, cfg, app, "public-demo");
        Assert.Contains(fragment, r.QuietLine, StringComparison.Ordinal);       // visible with web sources Off
        Assert.Equal(href, r.Href);
    }

    // ---------------------------------------------------------------- §3 Requests

    private static UiModels.AuditEntryView Rec(string id) => new() { Id = id, SchemaVersion = 2 };

    [Fact]
    public void Newest_page_merges_on_top_in_order_without_duplicates()
    {
        var items = new List<UiModels.AuditEntryView> { Rec("r3"), Rec("r2"), Rec("r1") };
        int added = UiLogic.RequestList.MergeNewest(items, [Rec("r5"), Rec("r4"), Rec("r3")], 10);
        Assert.Equal(2, added);
        Assert.Equal(["r5", "r4", "r3", "r2", "r1"], items.Select(i => i.Id).ToArray());
        Assert.Equal(0, UiLogic.RequestList.MergeNewest(items, [Rec("r5")], 10));
        UiLogic.RequestList.MergeNewest(items, [Rec("r6")], 5);
        Assert.Equal(5, items.Count);                                              // bounded; the oldest is dropped
        Assert.Equal("r6", items[0].Id);
    }

    [Fact]
    public void A_newest_page_that_does_not_reach_the_loaded_records_forces_a_paging_reset()
    {
        var loaded = new List<UiModels.AuditEntryView> { Rec("r3"), Rec("r2") };
        var gap = new List<UiModels.AuditEntryView> { Rec("r400"), Rec("r399") };
        Assert.True(UiLogic.RequestList.NeedsReset(loaded, gap, "cursor-older"));     // more than a page arrived: gap
        Assert.False(UiLogic.RequestList.NeedsReset(loaded, gap, null));              // the page is everything there is
        Assert.False(UiLogic.RequestList.NeedsReset(loaded, [Rec("r4"), Rec("r3")], "cursor-older"));   // overlap: merge
        Assert.False(UiLogic.RequestList.NeedsReset([], gap, "cursor-older"));
    }

    [Fact]
    public void Active_filters_are_described_for_the_visible_summary()
    {
        var c = UiLogic.RequestFilterCriteria.None with { Team = "research", Window = UiLogic.TimeWindow.LastHour, IdSearch = "req_9" };
        var d = UiLogic.RequestFilter.Describe(c);
        Assert.Equal(3, d.Count);
        Assert.Contains("Team: research", d);
        Assert.Contains(d, x => x.StartsWith("Time: ", StringComparison.Ordinal));
        Assert.Empty(UiLogic.RequestFilter.Describe(UiLogic.RequestFilterCriteria.None));
    }

    // ---------------------------------------------------------------- §4/§5 focused entity edits

    [Fact]
    public void A_working_copy_is_isolated_and_dirty_only_when_its_content_differs()
    {
        var cfg = Ui();
        var original = cfg.Policies.Single(p => p.Id == "public");
        var e = UiLogic.EntityEdits.Edit(original);
        Assert.False(e.IsDirty);
        e.Working.Paused = true;
        Assert.True(e.IsDirty);
        Assert.False(original.Paused);                                             // the configuration is untouched until Save
        e.Working.Paused = false;
        Assert.False(e.IsDirty);                                                   // undone ⇒ clean (Cancel needs no confirmation)
        Assert.True(UiLogic.EntityEdits.New(new UiModels.PolicyDto { Id = "x" }).IsDirty);
    }

    [Fact]
    public void Saving_one_entity_changes_only_that_entity()
    {
        var applied = Ui();
        var draft = UiLogic.ConfigCopy.Deep(applied);
        var e = UiLogic.EntityEdits.Edit(applied.Policies.Single(p => p.Id == "public"));
        e.Working.Name = "Public via cloud";
        Assert.Null(UiLogic.EntityEdits.Apply(draft, e));
        Assert.Equal(["policy public changed"], UiLogic.ChangeSummary.Lines(applied, draft).ToArray());

        var b = UiLogic.EntityEdits.Edit(applied.Budgets.Single(x => x.Id == "global"));
        b.Working.CapUsd = 42m;
        var draft2 = UiLogic.ConfigCopy.Deep(applied);
        Assert.Null(UiLogic.EntityEdits.Apply(draft2, b));
        Assert.Equal(["budget global changed"], UiLogic.ChangeSummary.Lines(applied, draft2).ToArray());
    }

    [Fact]
    public void Id_conflicts_and_vanished_entities_are_refused_without_changing_anything()
    {
        var cfg = Ui();
        var before = UiLogic.ConfigCopy.Canonical(cfg);
        var dup = UiLogic.EntityEdits.New(new UiModels.PolicyDto { Id = "public", Classifications = ["Public"] });
        Assert.Contains("already uses", UiLogic.EntityEdits.Apply(cfg, dup), StringComparison.Ordinal);
        var rename = UiLogic.EntityEdits.Edit(cfg.Policies.Single(p => p.Id == "internal"));
        rename.Working.Id = "public";
        Assert.Contains("already uses", UiLogic.EntityEdits.Apply(cfg, rename), StringComparison.Ordinal);
        Assert.Equal(before, UiLogic.ConfigCopy.Canonical(cfg));                 // refusals changed nothing
        var gone = UiLogic.EntityEdits.Edit(cfg.Policies.Single(p => p.Id == "internal"));
        cfg.Policies.RemoveAll(p => p.Id == "internal");
        Assert.Contains("no longer exists", UiLogic.EntityEdits.Apply(cfg, gone), StringComparison.Ordinal);
        Assert.Contains("no longer exists", UiLogic.EntityEdits.Remove(cfg.Policies, "internal", p => p.Id, "route"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 3, false)]
    [InlineData(3, 4, true)]
    [InlineData(null, 4, true)]
    public void A_dialog_opened_on_one_version_cannot_save_onto_another(int? openedAt, int active, bool refused)
    {
        var m = UiLogic.EntityEdits.StaleCheck(openedAt, active);
        Assert.Equal(refused, m is not null);
        if (openedAt is 3 && active == 4) Assert.Contains("opened at v3, now v4", m, StringComparison.Ordinal);
    }

    [Fact]
    public void Primary_and_fallback_keep_pause_flags_and_further_fallbacks()
    {
        var p = new UiModels.PolicyDto
        {
            Id = "r",
            Targets = [new() { ConnectionId = "a", ModelId = "1", Paused = true }, new() { ConnectionId = "b", ModelId = "2" }, new() { ConnectionId = "c", ModelId = "3" }],
        };
        Assert.Null(UiLogic.EntityEdits.SetPrimaryFallback(p, "b/2", "a/1"));
        Assert.Equal(["b/2", "a/1", "c/3"], p.Targets.Select(UiLogic.PolicyRoutes.Key).ToArray());
        Assert.True(p.Targets[1].Paused);                                          // a/1 kept its paused flag
        Assert.NotNull(UiLogic.EntityEdits.SetPrimaryFallback(p, "b/2", "b/2"));
        Assert.NotNull(UiLogic.EntityEdits.SetPrimaryFallback(p, null, "a/1"));
    }

    [Fact]
    public void Settings_sections_are_edited_as_one_unit()
    {
        var applied = Ui();
        var e = UiLogic.EntityEdits.Edit(applied.Settings!);
        e.Working.CacheTtlSeconds = 120;
        var draft = UiLogic.ConfigCopy.Deep(applied);
        Assert.Null(UiLogic.EntityEdits.Apply(draft, e));
        Assert.Equal(["operational settings changed"], UiLogic.ChangeSummary.Lines(applied, draft).ToArray());

        var ws = UiLogic.EntityEdits.Edit((UiModels.WebSearchSettingsDto?)null);
        Assert.True(ws.IsNew);
        Assert.False(ws.Working.Enabled);                                          // a new section starts disabled
    }

    [Fact]
    public void Budget_dialog_rules()
    {
        var cfg = Ui();
        UiLogic.EntityEdit<UiModels.BudgetConfigDto> New(string scope) => UiLogic.EntityEdits.New(new UiModels.BudgetConfigDto { Id = "x", Scope = scope, CapUsd = 1m });
        Assert.Contains("team name", UiLogic.BudgetForm.Problem(New("team:"), "80", cfg), StringComparison.Ordinal);
        Assert.Contains("already exists", UiLogic.BudgetForm.Problem(New("global"), "80", cfg), StringComparison.Ordinal);
        Assert.Contains("Warn at", UiLogic.BudgetForm.Problem(New("team:research"), "eighty", cfg), StringComparison.Ordinal);
        Assert.Null(UiLogic.BudgetForm.Problem(New("team:research"), "80, 95", cfg));
        Assert.Equal("team:research", UiLogic.BudgetForm.Scope("team", " research "));
        Assert.Equal("team-research", UiLogic.BudgetForm.Id("team:research", cfg));
    }

    // ---------------------------------------------------------------- §7 longest measured stage

    private static UiLogic.TraceStepView S(string stage, UiLogic.TraceStepState state, double? ms)
        => new(stage, UiLogic.TraceModel.Label(stage), state, ms, null, null, null, []);

    [Fact]
    public void The_longest_stage_is_the_longest_measured_one_and_shares_cover_only_measured_stages()
    {
        var view = new UiLogic.TraceView(
        [
            S("auth", UiLogic.TraceStepState.Passed, 0.5),
            S("cache", UiLogic.TraceStepState.Skipped, 9999),                     // skipped: no timing, never counted
            S("provider", UiLogic.TraceStepState.Failed, 1500),                     // slow AND failed: keeps its state
            S("attribution", UiLogic.TraceStepState.Pending, null),
        ], true, 4, 1, "live");
        Assert.Equal("provider", UiLogic.TraceModel.LongestStage(view));
        Assert.Equal(1500 / 1500.5, UiLogic.TraceModel.Share(view, view.Steps[2])!.Value, 6);
        Assert.Null(UiLogic.TraceModel.Share(view, view.Steps[1]));
        Assert.Contains("of the measured processing time", UiLogic.TraceModel.LongestText(view), StringComparison.Ordinal);
        Assert.Contains("not total wall time", UiLogic.TraceModel.LongestText(view), StringComparison.Ordinal);

        var none = new UiLogic.TraceView([S("auth", UiLogic.TraceStepState.Skipped, null)], true, 1, 1, "live");
        Assert.Null(UiLogic.TraceModel.LongestStage(none));
        Assert.Null(UiLogic.TraceModel.LongestText(none));
    }

    // ---------------------------------------------------------------- §6 connection facts

    [Fact]
    public void Configured_credential_and_tested_are_separate_facts()
    {
        var conn = new UiModels.ConnectionDto { Id = "anthropic", Enabled = true, SecretRef = "Anthropic:ApiKey", Models = [new() { ModelId = "m" }] };
        var untested = new UiLogic.ConnectionHealthView("x", "hollow", []);
        var facts = UiLogic.ConnectionFacts.Trio(conn, true, false, untested);
        Assert.Equal(["Configured", "Credential", "Tested"], facts.Select(f => f.Label).ToArray());
        Assert.Equal("ok", facts[0].Tone);
        Assert.Contains("Anthropic__ApiKey", facts[1].Value, StringComparison.Ordinal);
        Assert.Equal("bad", facts[1].Tone);
        Assert.StartsWith("Not yet", facts[2].Value, StringComparison.Ordinal);    // listed ≠ tested

        var tested = UiLogic.ConnectionFacts.Trio(conn, false, true, new UiLogic.ConnectionHealthView("x", "ok", []));
        Assert.Equal("bad", tested[0].Tone);                                       // endpoint not allow-listed
        Assert.Equal("Governed call succeeded", tested[2].Value);
    }

    // ---------------------------------------------------------------- stale focused edits recover after reload

    [Fact]
    public void A_412_from_the_Host_is_a_conflict_that_requires_reloading_the_applied_configuration()
    {
        var o = UiLogic.EditOutcomes.FromResponse(412, "{\"error\":\"stale\",\"active_version\":4}");
        Assert.Equal(UiLogic.EditOutcomeKind.Stale, o.Kind);
        Assert.Equal(4, o.Version);
        Assert.True(UiLogic.EditOutcomes.RequiresReload(o));
        Assert.False(UiLogic.EditOutcomes.RequiresReload(UiLogic.EditOutcomes.FromResponse(422, "{\"errors\":[]}")));
        Assert.False(UiLogic.EditOutcomes.RequiresReload(UiLogic.EditOutcomes.FromResponse(200, "{\"version\":5}")));
    }

    [Fact]
    public void After_a_conflict_the_old_dialog_stays_refused_and_a_reopened_one_can_save()
    {
        // Dialog opened on v3; another writer activated v4; the conflict reloads Applied to v4.
        int openedAt = 3, activeAfterReload = 4;
        Assert.NotNull(UiLogic.EntityEdits.StaleCheck(openedAt, activeAfterReload));      // the old working copy is never rebased
        Assert.Null(UiLogic.EntityEdits.StaleCheck(activeAfterReload, activeAfterReload)); // Cancel → reopen starts from v4

        // The reopened dialog's working copy is taken from the reloaded configuration, not the stale one.
        var v4 = Ui();
        v4.WebSearch = new UiModels.WebSearchSettingsDto { Enabled = true, MaxResults = 2, TimeoutSeconds = 10 };
        var reopened = UiLogic.EntityEdits.Edit(v4.WebSearch);
        Assert.Equal(2, reopened.Working.MaxResults);
        Assert.False(reopened.IsDirty);
    }

    // ---------------------------------------------------------------- live version ≠ loaded version

    [Fact]
    public void A_different_live_version_reloads_once_and_keeps_dirty_or_focused_drafts()
    {
        Assert.True(UiLogic.ConfigFreshness.ShouldReload(4, 3, null));     // status says v4, page holds v3
        Assert.False(UiLogic.ConfigFreshness.ShouldReload(4, 4, null));    // up to date
        Assert.False(UiLogic.ConfigFreshness.ShouldReload(null, 3, null)); // live version unknown
        Assert.False(UiLogic.ConfigFreshness.ShouldReload(4, 3, 4));       // already reloaded for v4: no loop

        Assert.True(UiLogic.ConfigFreshness.DropCleanDraft(3, 4, dirty: false, scoped: false));   // clean stale draft: show v4
        Assert.False(UiLogic.ConfigFreshness.DropCleanDraft(3, 4, dirty: true, scoped: false));   // dirty: kept, save refused later
        Assert.False(UiLogic.ConfigFreshness.DropCleanDraft(3, 4, dirty: false, scoped: true));   // open dialog's edit: kept
        Assert.False(UiLogic.ConfigFreshness.DropCleanDraft(4, 4, dirty: false, scoped: false));
    }

    // ---------------------------------------------------------------- presentation sources, coding example bound

    [Fact]
    public void Presentation_sources_collapse_to_a_count_that_says_not_verified()
    {
        var line = UiLogic.WebSourcesText.CompactSummary(3);
        Assert.Contains("3 sources retrieved", line, StringComparison.Ordinal);
        Assert.Contains("not verified", line, StringComparison.Ordinal);
        Assert.Contains("1 source retrieved", UiLogic.WebSourcesText.CompactSummary(1), StringComparison.Ordinal);
    }

    [Fact]
    public void The_coding_example_asks_for_concise_code_and_2048_tokens_without_raising_the_default()
    {
        var ex = UiLogic.DemoExamples.Find("internal-coding")!;
        Assert.Equal(2048, ex.MaxTokens);
        Assert.Contains("code only", ex.Prompt, StringComparison.Ordinal);
        Assert.Equal(1024, new UiLogic.PlaygroundForm().MaxTokens);   // custom requests keep the 1024 default
    }

    [Fact]
    public void Switching_the_same_prompt_to_a_capped_destination_states_the_enforced_bound()
    {
        var cfg = Ui();
        var app = cfg.Applications[0];
        var conn = cfg.Connections[0];
        var model = conn.Models[0];
        var dest = new UiLogic.WorkloadDestinationView(conn.Id + "/" + model.ModelId, "Cloud", null, [], null, null, null, null);

        app.MaxOutputTokens = 1024; model.MaxOutputTokens = null;
        var note = UiLogic.WorkloadDestination.OutputBound(cfg, app.Id, dest, 2048);
        Assert.Contains("2048 requested → 1024 enforced (application cap)", note, StringComparison.Ordinal);
        Assert.Null(UiLogic.WorkloadDestination.OutputBound(cfg, app.Id, dest, 1024));     // enforced in full: no note

        app.MaxOutputTokens = 4096; model.MaxOutputTokens = 1024;
        Assert.Contains("(model cap)", UiLogic.WorkloadDestination.OutputBound(cfg, app.Id, dest, 2048), StringComparison.Ordinal);
        model.MaxOutputTokens = null;
        Assert.Null(UiLogic.WorkloadDestination.OutputBound(cfg, app.Id, dest, 2048));
    }
}
