using Xunit;
using UiLogic = Covenant.Ui.Logic;
using UiModels = Covenant.Ui.Models;

namespace Covenant.Tests;

/// <summary>Budgets presentation (usability review): Global covers every application; a team/application cap is an
/// additional ceiling over the SAME charges (never separate spend); application names come from the configuration; a
/// very low hard cap is labelled neutrally ("Low spending cap") — intent is never inferred.</summary>
public sealed class BudgetClarityTests
{
    private static UiModels.CovenantConfigDto Cfg() => new()
    {
        Applications =
        [
            new() { Id = "budget-demo", Team = "demo-controls", DisplayName = "Budget limit example" },
            new() { Id = "research-assistant", Team = "research" },
        ],
    };

    [Fact]
    public void Scope_names_use_the_configured_application_display_name()
    {
        Assert.Equal("Application · Budget limit example", UiLogic.BudgetText.ScopeName("application:budget-demo", Cfg()));
        Assert.Equal("Application · research-assistant", UiLogic.BudgetText.ScopeName("application:research-assistant", Cfg()));   // no name: the id, never invented
        Assert.Equal("Team · research", UiLogic.BudgetText.ScopeName("team:research", Cfg()));
        Assert.Equal("Global", UiLogic.BudgetText.ScopeName("global", Cfg()));
    }

    [Fact]
    public void Coverage_explains_ceilings_over_the_same_charges()
    {
        Assert.Contains("every application", UiLogic.BudgetText.Coverage("global", Cfg()), StringComparison.Ordinal);
        var app = UiLogic.BudgetText.Coverage("application:budget-demo", Cfg());
        Assert.Contains("inside Global", app, StringComparison.Ordinal);
        Assert.Contains("same charge counts against both", app, StringComparison.Ordinal);
        Assert.Contains("never added as separate spend", app, StringComparison.Ordinal);
        Assert.Contains("team demo-controls", app, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_low_hard_cap_gets_a_neutral_badge_with_its_real_effect()
    {
        Assert.True(UiLogic.BudgetText.IsLowCap("application:budget-demo", 0.0001m, "hard"));
        Assert.True(UiLogic.BudgetText.IsLowCap("team:research", 0.01m, "hard"));            // a real team's low cap: same neutral fact
        Assert.False(UiLogic.BudgetText.IsLowCap("application:budget-demo", 0.0001m, "warn-only"));   // never refuses
        Assert.False(UiLogic.BudgetText.IsLowCap("application:research-assistant", 25m, "hard"));
        Assert.False(UiLogic.BudgetText.IsLowCap("global", 0.0001m, "hard"));
        var effect = UiLogic.BudgetText.LowCapEffect(0.0001m);
        Assert.Contains("refused before any provider call", effect, StringComparison.Ordinal);
        Assert.DoesNotContain("demo", effect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Table_lines_stay_short_and_details_move_to_tooltips()
    {
        Assert.Equal("All applications", UiLogic.BudgetText.ShortCoverage("global"));
        Assert.Equal("Additional cap within Global", UiLogic.BudgetText.ShortCoverage("application:budget-demo"));
        Assert.Equal("demo-controls", UiLogic.BudgetText.TeamOf("application:budget-demo", Cfg()));
        Assert.Null(UiLogic.BudgetText.TeamOf("global", Cfg()));
        Assert.Equal("Hard cap · alerts 80%, 95%", UiLogic.BudgetText.StateLine("hard", [0.80m, 0.95m]));
        Assert.Equal("Warn only · no alerts", UiLogic.BudgetText.StateLine("warn-only", []));
        Assert.Equal("Monthly (UTC)", UiLogic.BudgetText.PeriodName("monthly"));
    }
}
