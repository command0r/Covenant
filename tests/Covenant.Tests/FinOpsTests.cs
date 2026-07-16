using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Attribution math for the dashboard: the savings estimate and denial grouping must be
/// computed from audit evidence, and the numbers must be exact (decimal, no float drift).</summary>
public class FinOpsTests
{
    private static readonly (decimal InPer1K, decimal OutPer1K) PublicPrice = (0.15m, 0.60m);
    private const string LocalModel = "llama-3.1-8b-instruct";

    private static AuditEntry Entry(
        PolicyEffect effect, string? model, long inTok, long outTok, decimal cost,
        string team = "platform", string reason = "allowed") =>
        new(
            Id: Guid.NewGuid().ToString("n"),
            TimestampUtc: DateTimeOffset.UtcNow,
            Principal: "tester",
            Tags: new AttributionTags(team, "test-workflow", "test-case"),
            Classification: DataClassification.Internal,
            Effect: effect,
            Reason: reason,
            ServedByModel: model,
            Usage: new Usage(inTok, outTok, cost));

    [Fact]
    public void Local_served_tokens_are_priced_at_public_rates_as_estimated_savings()
    {
        var entries = new[]
        {
            Entry(PolicyEffect.Allow, LocalModel, 1_000, 1_000, 0m),          // in-perimeter, cost 0
            Entry(PolicyEffect.Allow, "gpt-4o-mini", 100, 100, 0.075m),       // public route
        };

        var f = FinOps.Build(entries, LocalModel, PublicPrice);

        // 1000/1000*0.15 + 1000/1000*0.60 = 0.75 — exactly, in decimal
        Assert.Equal(0.75m, f.EstimatedSavingsUsd);
        Assert.Equal(1, f.LocalRequests);
        Assert.Equal(2_000, f.LocalTokens);
        Assert.Equal(0.075m, f.TotalCostUsd);
        Assert.Equal(2, f.Allowed);
        Assert.Equal(0, f.Denied);
    }

    [Fact]
    public void Removing_the_savings_rule_would_zero_this_out()
    {
        // No local-served entries → no claimed savings. The estimate must never be invented.
        var entries = new[] { Entry(PolicyEffect.Allow, "gpt-4o-mini", 500, 500, 0.375m) };

        var f = FinOps.Build(entries, LocalModel, PublicPrice);

        Assert.Equal(0m, f.EstimatedSavingsUsd);
        Assert.Equal(0, f.LocalRequests);
    }

    [Fact]
    public void Denials_are_grouped_by_reason_and_cost_is_attributed_per_team()
    {
        var entries = new[]
        {
            Entry(PolicyEffect.Deny, null, 0, 0, 0m, team: "payments", reason: "kill switch engaged: drill"),
            Entry(PolicyEffect.Deny, null, 0, 0, 0m, team: "payments", reason: "kill switch engaged: drill"),
            Entry(PolicyEffect.Deny, null, 0, 0, 0m, team: "risk", reason: "no adapter registered for key 'local'"),
            Entry(PolicyEffect.Allow, "gpt-4o-mini", 100, 100, 0.075m, team: "payments"),
        };

        var f = FinOps.Build(entries, LocalModel, PublicPrice);

        Assert.Equal(3, f.Denied);
        Assert.Equal(2, f.DenialsByReason["kill switch engaged: drill"]);
        Assert.Equal(1, f.DenialsByReason["no adapter registered for key 'local'"]);
        Assert.Equal(0.075m, f.CostByTeamUsd["payments"]);
        Assert.Equal(0m, f.CostByTeamUsd["risk"]);
    }
}
