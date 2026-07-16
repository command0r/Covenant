using System.Text.Json.Serialization;
using Covenant.Core;

namespace Covenant.Host;

// The /admin/status payload: what the dashboard renders. Read-only view over config, the spend
// ledger, and the audit log — the dashboard never becomes a second source of truth.

public sealed class StatusReport
{
    [JsonPropertyName("generated_utc")] public required DateTimeOffset GeneratedUtc { get; init; }
    [JsonPropertyName("started_utc")] public required DateTimeOffset StartedUtc { get; init; }
    [JsonPropertyName("kill_switch")] public required KillSwitchState KillSwitch { get; init; }
    [JsonPropertyName("budget")] public required BudgetStatus Budget { get; init; }
    [JsonPropertyName("routes")] public required List<RouteView> Routes { get; init; }
    [JsonPropertyName("finops")] public required FinOpsSummary FinOps { get; init; }
    [JsonPropertyName("chain_valid")] public required bool ChainValid { get; init; }
    [JsonPropertyName("audit_entries")] public required int AuditEntries { get; init; }
}

public sealed class BudgetStatus
{
    [JsonPropertyName("global_cap_usd")] public required decimal GlobalCapUsd { get; init; }
    [JsonPropertyName("global_spend_usd")] public required decimal GlobalSpendUsd { get; init; }
    [JsonPropertyName("teams")] public required List<TeamBudgetStatus> Teams { get; init; }
}

public sealed class TeamBudgetStatus
{
    [JsonPropertyName("team")] public required string Team { get; init; }
    /// <summary>Null = no team cap; bounded by the global ceiling only.</summary>
    [JsonPropertyName("cap_usd")] public decimal? CapUsd { get; init; }
    [JsonPropertyName("spend_usd")] public required decimal SpendUsd { get; init; }
}

public sealed class RouteView
{
    [JsonPropertyName("classification")] public required string Classification { get; init; }
    [JsonPropertyName("adapter")] public required string Adapter { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
}

public sealed class FinOpsSummary
{
    [JsonPropertyName("requests")] public required int Requests { get; init; }
    [JsonPropertyName("allowed")] public required int Allowed { get; init; }
    [JsonPropertyName("denied")] public required int Denied { get; init; }
    [JsonPropertyName("total_cost_usd")] public required decimal TotalCostUsd { get; init; }
    [JsonPropertyName("local_requests")] public required int LocalRequests { get; init; }
    [JsonPropertyName("local_tokens")] public required long LocalTokens { get; init; }
    /// <summary>Local-served tokens priced at the PUBLIC route's rates. An estimate and labeled as
    /// such in the UI — the honest "what in-perimeter routing avoided" number.</summary>
    [JsonPropertyName("estimated_savings_usd")] public required decimal EstimatedSavingsUsd { get; init; }
    [JsonPropertyName("cost_by_team_usd")] public required Dictionary<string, decimal> CostByTeamUsd { get; init; }
    [JsonPropertyName("denials_by_reason")] public required Dictionary<string, int> DenialsByReason { get; init; }
}

public static class FinOps
{
    public static FinOpsSummary Build(
        IReadOnlyList<AuditEntry> entries,
        string localModelId,
        (decimal InPer1K, decimal OutPer1K) publicPrice)
    {
        int allowed = 0, denied = 0, localRequests = 0;
        long localTokens = 0;
        decimal totalCost = 0m, estimatedSavings = 0m;
        var costByTeam = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var denialsByReason = new Dictionary<string, int>();

        foreach (var e in entries)
        {
            totalCost += e.Usage.CostUsd;
            costByTeam[e.Tags.Team] = costByTeam.GetValueOrDefault(e.Tags.Team) + e.Usage.CostUsd;

            if (e.Effect == PolicyEffect.Allow)
            {
                allowed++;
                if (string.Equals(e.ServedByModel, localModelId, StringComparison.OrdinalIgnoreCase))
                {
                    localRequests++;
                    localTokens += e.Usage.TotalTokens;
                    estimatedSavings += e.Usage.InputTokens / 1000m * publicPrice.InPer1K
                                      + e.Usage.OutputTokens / 1000m * publicPrice.OutPer1K;
                }
            }
            else
            {
                denied++;
                denialsByReason[e.Reason] = denialsByReason.GetValueOrDefault(e.Reason) + 1;
            }
        }

        return new FinOpsSummary
        {
            Requests = entries.Count,
            Allowed = allowed,
            Denied = denied,
            TotalCostUsd = totalCost,
            LocalRequests = localRequests,
            LocalTokens = localTokens,
            EstimatedSavingsUsd = estimatedSavings,
            CostByTeamUsd = costByTeam,
            DenialsByReason = denialsByReason,
        };
    }
}
