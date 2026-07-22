using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Savings math for the dashboard. Baseline is stated, not implied: every allowed request
/// priced as if it had run on the STRONG model; savings split between in-perimeter serving and the
/// complexity router. Exact decimal, never invented — all-strong traffic must claim zero.</summary>
public class FinOpsTests
{
    private const string LocalModel = "llama-3.1-8b-instruct";
    private const string StrongModel = "gpt-4o";

    private static readonly Dictionary<string, (decimal, decimal)> Prices = new()
    {
        ["gpt-4o-mini"] = (0.15m, 0.60m),
        [StrongModel] = (2.50m, 10.00m),
        [LocalModel] = (0m, 0m),
    };

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
    public void Savings_are_measured_against_the_all_strong_baseline_and_split_by_cause()
    {
        var entries = new[]
        {
            Entry(PolicyEffect.Allow, LocalModel, 1_000, 1_000, 0m),        // in-perimeter, free
            Entry(PolicyEffect.Allow, "gpt-4o-mini", 100, 100, 0.075m),     // router kept it cheap
            Entry(PolicyEffect.Allow, StrongModel, 100, 100, 1.25m),        // ran strong: no savings
        };

        var f = FinOps.Build(entries, LocalModel, StrongModel, Prices);

        // local: 1000/1000 tokens at full strong delta → 1×2.50 + 1×10.00 = 12.50
        Assert.Equal(12.50m, f.SavingsLocalUsd);
        // router: 100/1000×(2.50−0.15) + 100/1000×(10.00−0.60) = 0.235 + 0.940 = 1.175
        Assert.Equal(1.175m, f.SavingsRouterUsd);
        Assert.Equal(13.675m, f.EstimatedSavingsUsd);
        Assert.Equal(1, f.LocalRequests);
        Assert.Equal(2_000, f.LocalTokens);
        Assert.Equal(1, f.RequestsByModel[StrongModel]);
    }

    [Fact]
    public void All_strong_traffic_claims_zero_savings()
    {
        var entries = new[] { Entry(PolicyEffect.Allow, StrongModel, 500, 500, 6.25m) };

        var f = FinOps.Build(entries, LocalModel, StrongModel, Prices);

        Assert.Equal(0m, f.EstimatedSavingsUsd);
        Assert.Equal(0m, f.SavingsLocalUsd);
        Assert.Equal(0m, f.SavingsRouterUsd);
    }

    [Fact]
    public void Denials_are_grouped_by_reason_and_cost_is_attributed_per_team()
    {
        var entries = new[]
        {
            Entry(PolicyEffect.Deny, null, 0, 0, 0m, team: "payments", reason: "kill switch engaged: drill"),
            Entry(PolicyEffect.Deny, null, 0, 0, 0m, team: "payments", reason: "kill switch engaged: drill"),
            Entry(PolicyEffect.Deny, null, 0, 0, 0m, team: "risk", reason: "no adapter registered for 'local:x'"),
            Entry(PolicyEffect.Allow, "gpt-4o-mini", 100, 100, 0.075m, team: "payments"),
        };

        var f = FinOps.Build(entries, LocalModel, StrongModel, Prices);

        Assert.Equal(3, f.Denied);
        Assert.Equal(2, f.DenialsByReason["kill switch engaged: drill"]);
        Assert.Equal(1, f.DenialsByReason["no adapter registered for 'local:x'"]);
        Assert.Equal(0.075m, f.CostByTeamUsd["payments"]);
        Assert.Equal(0m, f.CostByTeamUsd["risk"]);
    }
}
