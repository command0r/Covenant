using Xunit;
using UiModels = Covenant.Ui.Models;
using UiLogic = Covenant.Ui.Logic;

namespace Covenant.Tests;

/// <summary>Browser-review corrections (docs/CONSOLE-DEMO-CLARITY-TASKS.md, checkpoint 3): no stale or mismatched search
/// readiness, tool cost shown apart from inference, short readable select labels, bounded authoritative search example.</summary>
public sealed class DemoCheckpoint3Tests
{
    private static UiModels.WebSearchStatusDto Ready(int version) => new()
    {
        KeyConfigured = true, EgressApproved = true, ModeAllowsSearch = true, Enabled = true, PriceConfigured = true,
        ProfilesAllowed = ["public-research"], Ready = true, ConfigVersion = version,
    };

    [Fact]
    public void Readiness_for_another_configuration_version_is_never_shown()
    {
        var (status, error, mismatch) = UiLogic.WebSourcesText.Reconcile(5, Ready(4), null);
        Assert.Null(status);
        Assert.True(mismatch);
        Assert.Contains("v4", error, StringComparison.Ordinal);
        Assert.Contains("v5", error, StringComparison.Ordinal);

        var same = UiLogic.WebSourcesText.Reconcile(5, Ready(5), null);
        Assert.NotNull(same.Status);
        Assert.False(same.Mismatch);
    }

    [Fact]
    public void A_failed_status_refresh_never_presents_an_old_ready()
    {
        var (status, error, mismatch) = UiLogic.WebSourcesText.Reconcile(5, null, "Web search status could not be loaded: timeout");
        Assert.Null(status);
        Assert.False(mismatch);
        Assert.Contains("timeout", error, StringComparison.Ordinal);

        // With no status the composer does not claim Ready; the runtime stays authoritative.
        var readiness = UiLogic.WebSourcesText.Readiness(status, error, null, null, "public-research");
        Assert.False(readiness.Known);
        Assert.Null(UiLogic.WebSourcesText.Reconcile(5, null, null).Status);
    }

    private static UiModels.AuditEntryView SearchedLocalAnswer()
    {
        var r = new UiModels.AuditEntryView
        {
            SchemaVersion = 2, CostBasis = "reported", Effect = "Allow",
            Attempts = [new() { AttemptId = "att_1", Outcome = "served", CostUsd = 0m, CostBasis = "zero_priced" }],
            ToolCalls = [new() { ToolCallId = "tool_1", Tool = "web_search", Provider = "tavily", Outcome = "succeeded", CostUsd = 0.008m, CostBasis = "reported", Credits = 1 }],
        };
        r.Usage.CostUsd = 0.008m;
        return r;
    }

    [Fact]
    public void A_search_plus_local_answer_is_not_called_a_provider_charge()
    {
        var r = SearchedLocalAnswer();
        var text = UiLogic.CostText.Cost(r);
        Assert.DoesNotContain("estimated provider charge", text, StringComparison.Ordinal);
        Assert.Contains("web search $0.008 (credits × configured price)", text, StringComparison.Ordinal);
        Assert.Contains("inference $0", text, StringComparison.Ordinal);
        Assert.Contains("configured $0 local model", text, StringComparison.Ordinal);
        Assert.Contains("incl. search", UiLogic.CostText.CostShort(r), StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_without_tool_calls_keeps_its_existing_wording()
    {
        var r = SearchedLocalAnswer();
        r.ToolCalls = null;
        r.Attempts![0].CostBasis = "reported";
        Assert.Null(UiLogic.CostText.Split(r));
        Assert.Contains("estimated provider charge", UiLogic.CostText.Cost(r), StringComparison.Ordinal);
    }

    [Fact]
    public void Application_options_are_short_names_with_the_reason_only()
    {
        var app = new UiModels.ApplicationSummaryDto { Id = "communications-assistant", DisplayName = "Communications", Team = "comms", Enabled = true, ConsoleTesting = true };
        Assert.Equal("Communications", new UiLogic.ApplicationChoice(app, true, null).Label);
        var off = new UiModels.ApplicationSummaryDto { Id = "batch", Team = "data", Enabled = false };
        Assert.Equal("batch — disabled", new UiLogic.ApplicationChoice(off, false, "disabled").Label);
    }

    [Fact]
    public void The_composer_default_skips_an_exhausted_budget_demo_app_but_keeps_it_selectable()
    {
        UiModels.ApplicationSummaryDto App(string id, string team) => new() { Id = id, Team = team, Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-writing"] };
        var apps = new List<UiModels.ApplicationSummaryDto> { App("budget-demo", "budget-demo"), App("communications-assistant", "communications") };
        var budgets = new List<UiModels.BudgetStateDto>
        {
            new() { Id = "global", Scope = "global", CapUsd = 25m, RemainingUsd = 24m, Enforcement = "hard" },
            new() { Id = "budget-demo", Scope = "application:budget-demo", CapUsd = 0.0001m, RemainingUsd = 0.0001m, Enforcement = "hard" },
        };

        Assert.Equal("communications-assistant", UiLogic.ComposerRules.DefaultApplication(apps, budgets)!.Id);
        Assert.Equal("budget-demo", UiLogic.ComposerRules.Applications(apps)[0].App.Id);          // still listed, first
        var form = new UiLogic.PlaygroundForm();
        Assert.True(UiLogic.ComposerRules.SelectApplication(form, apps, "budget-demo"));           // explicit choice works
        Assert.Equal("budget-demo", form.ApplicationId);

        // When it is the only usable application it is still the default; without budget facts, plain order applies.
        Assert.Equal("budget-demo", UiLogic.ComposerRules.DefaultApplication([apps[0]], budgets)!.Id);
        Assert.Equal("budget-demo", UiLogic.ComposerRules.DefaultApplication(apps, null)!.Id);
        // A warn-only budget does not refuse, so it does not disqualify the default.
        budgets[1].Enforcement = "warn-only";
        Assert.Equal("budget-demo", UiLogic.ComposerRules.DefaultApplication(apps, budgets)!.Id);
    }

    [Fact]
    public void Research_example_is_bounded_to_an_official_source_and_honest_about_verification()
    {
        var ex = UiLogic.DemoExamples.Find("public-research")!;
        Assert.Contains("site:learn.microsoft.com", ex.Prompt, StringComparison.Ordinal);
        Assert.Contains("not stated in the sources", ex.Prompt, StringComparison.Ordinal);
        Assert.Contains("not independently verified", ex.Observe, StringComparison.Ordinal);
        Assert.True(ex.Prompt.Length <= 300);   // the Host bounds the search query to 300 characters
    }
}
