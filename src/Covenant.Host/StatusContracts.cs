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
    [JsonPropertyName("auth")] public required AuthStatus Auth { get; init; }
    [JsonPropertyName("otel_enabled")] public required bool OtelEnabled { get; init; }
    [JsonPropertyName("routing_threshold_tokens")] public required long RoutingThresholdTokens { get; init; }
}

public sealed class AuthStatus
{
    [JsonPropertyName("allow_anonymous")] public required bool AllowAnonymous { get; init; }
    /// <summary>Count only — key values never leave the config/vault.</summary>
    [JsonPropertyName("key_count")] public required int KeyCount { get; init; }
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
    /// <summary>Savings vs a stated baseline (every allowed request priced on the STRONG model), split
    /// into in-perimeter vs router avoidance. An estimate, labeled as such — never invented.</summary>
    [JsonPropertyName("estimated_savings_usd")] public required decimal EstimatedSavingsUsd { get; init; }
    [JsonPropertyName("savings_local_usd")] public required decimal SavingsLocalUsd { get; init; }
    [JsonPropertyName("savings_router_usd")] public required decimal SavingsRouterUsd { get; init; }
    [JsonPropertyName("cost_by_team_usd")] public required Dictionary<string, decimal> CostByTeamUsd { get; init; }
    [JsonPropertyName("denials_by_reason")] public required Dictionary<string, int> DenialsByReason { get; init; }
    /// <summary>Per-minute request/denial/cost buckets (most recent 60 non-empty), for the activity chart.</summary>
    [JsonPropertyName("activity")] public required List<ActivityBucket> Activity { get; init; }
    [JsonPropertyName("requests_by_model")] public required Dictionary<string, int> RequestsByModel { get; init; }
    /// <summary>Newest-first metadata for the live feed. Deliberately NO message content — the admin
    /// plane never sees prompts or responses; that is a product guarantee, not an omission.</summary>
    [JsonPropertyName("recent")] public required List<RecentRequest> Recent { get; init; }
}

public sealed class ActivityBucket
{
    [JsonPropertyName("t")] public required DateTimeOffset T { get; init; }
    [JsonPropertyName("requests")] public required int Requests { get; init; }
    [JsonPropertyName("denied")] public required int Denied { get; init; }
    [JsonPropertyName("cost_usd")] public required decimal CostUsd { get; init; }
    [JsonPropertyName("avg_ms")] public required double AvgMs { get; init; }
}

public sealed class RecentRequest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("t")] public required DateTimeOffset T { get; init; }
    [JsonPropertyName("principal")] public required string Principal { get; init; }
    [JsonPropertyName("team")] public required string Team { get; init; }
    [JsonPropertyName("workflow")] public required string Workflow { get; init; }
    [JsonPropertyName("use_case")] public required string UseCase { get; init; }
    [JsonPropertyName("classification")] public required string Classification { get; init; }
    [JsonPropertyName("effect")] public required string Effect { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("tokens")] public required long Tokens { get; init; }
    [JsonPropertyName("tokens_in")] public required long TokensIn { get; init; }
    [JsonPropertyName("tokens_out")] public required long TokensOut { get; init; }
    [JsonPropertyName("cost_usd")] public required decimal CostUsd { get; init; }
    [JsonPropertyName("ms")] public required double Ms { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("prompt_chars")] public required int PromptChars { get; init; }
    [JsonPropertyName("prompt_sha256")] public string? PromptSha256 { get; init; }
    [JsonPropertyName("signal")] public string? Signal { get; init; }
    /// <summary>Truncated input preview — present only when the operator set Audit:PromptPreviewChars.</summary>
    [JsonPropertyName("prompt_preview")] public string? PromptPreview { get; init; }
}

public static class FinOps
{
    /// <summary>Dense per-minute series over the last hour of activity — quiet minutes are zero-filled
    /// so the chart's time axis tells the truth (a gap looks like a gap).</summary>
    private static List<ActivityBucket> DenseActivity(
        SortedDictionary<DateTime, (int Requests, int Denied, decimal Cost, double MsSum)> buckets)
    {
        var activity = new List<ActivityBucket>();
        if (buckets.Count == 0) return activity;

        var last = buckets.Keys.Max();
        var first = buckets.Keys.Min();
        var start = last.AddMinutes(-59) > first ? last.AddMinutes(-59) : first;

        for (var t = start; t <= last; t = t.AddMinutes(1))
        {
            var b = buckets.GetValueOrDefault(t);
            activity.Add(new ActivityBucket
            {
                T = new DateTimeOffset(t, TimeSpan.Zero),
                Requests = b.Requests,
                Denied = b.Denied,
                CostUsd = b.Cost,
                AvgMs = b.Requests > 0 ? b.MsSum / b.Requests : 0,
            });
        }
        return activity;
    }

    public static FinOpsSummary Build(
        IReadOnlyList<AuditEntry> entries,
        string localModelId,
        string strongModelId,
        IReadOnlyDictionary<string, (decimal InPer1K, decimal OutPer1K)> prices)
    {
        var strongPrice = prices.GetValueOrDefault(strongModelId);
        int allowed = 0, denied = 0, localRequests = 0;
        long localTokens = 0;
        decimal totalCost = 0m, savingsLocal = 0m, savingsRouter = 0m;
        var costByTeam = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var denialsByReason = new Dictionary<string, int>();
        var requestsByModel = new Dictionary<string, int>();
        var buckets = new SortedDictionary<DateTime, (int Requests, int Denied, decimal Cost, double MsSum)>();

        foreach (var e in entries)
        {
            var ts = e.TimestampUtc.UtcDateTime;
            var minute = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);
            var b = buckets.GetValueOrDefault(minute);
            buckets[minute] = (b.Requests + 1, b.Denied + (e.Effect == PolicyEffect.Deny ? 1 : 0),
                b.Cost + e.Usage.CostUsd, b.MsSum + e.DurationMs);

            if (e.ServedByModel is { Length: > 0 } served)
                requestsByModel[served] = requestsByModel.GetValueOrDefault(served) + 1;

            totalCost += e.Usage.CostUsd;
            costByTeam[e.Tags.Team] = costByTeam.GetValueOrDefault(e.Tags.Team) + e.Usage.CostUsd;

            if (e.Effect == PolicyEffect.Allow)
            {
                allowed++;
                // Baseline: this request runs on the strong model. Savings = baseline − actual price,
                // never below zero, attributed to in-perimeter serving or the complexity router.
                if (e.ServedByModel is { Length: > 0 } model
                    && !string.Equals(model, strongModelId, StringComparison.OrdinalIgnoreCase))
                {
                    var actual = prices.GetValueOrDefault(model);
                    var saving =
                        e.Usage.InputTokens / 1000m * Math.Max(0m, strongPrice.InPer1K - actual.InPer1K)
                        + e.Usage.OutputTokens / 1000m * Math.Max(0m, strongPrice.OutPer1K - actual.OutPer1K);

                    if (string.Equals(model, localModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        localRequests++;
                        localTokens += e.Usage.TotalTokens;
                        savingsLocal += saving;
                    }
                    else
                    {
                        savingsRouter += saving;
                    }
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
            EstimatedSavingsUsd = savingsLocal + savingsRouter,
            SavingsLocalUsd = savingsLocal,
            SavingsRouterUsd = savingsRouter,
            CostByTeamUsd = costByTeam,
            DenialsByReason = denialsByReason,
            Activity = DenseActivity(buckets),
            RequestsByModel = requestsByModel,
            Recent = entries.Reverse().Take(50)
                .Select(e => new RecentRequest
                {
                    Id = e.Id,
                    T = e.TimestampUtc,
                    Principal = e.Principal,
                    Team = e.Tags.Team,
                    Workflow = e.Tags.Workflow,
                    UseCase = e.Tags.UseCase,
                    Classification = e.Classification.ToString(),
                    Effect = e.Effect.ToString(),
                    Model = e.ServedByModel,
                    Tokens = e.Usage.TotalTokens,
                    TokensIn = e.Usage.InputTokens,
                    TokensOut = e.Usage.OutputTokens,
                    CostUsd = e.Usage.CostUsd,
                    Ms = e.DurationMs,
                    Reason = e.Reason,
                    PromptChars = e.PromptChars,
                    PromptSha256 = e.PromptSha256,
                    Signal = e.Signal,
                    PromptPreview = e.PromptPreview,
                })
                .ToList(),
        };
    }
}
