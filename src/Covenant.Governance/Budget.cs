using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Global stop for all inference. While tripped, every request is denied (and audited).</summary>
public interface IKillSwitch
{
    bool IsTripped { get; }
    string? Reason { get; }
}

/// <summary>Thread-safe in-process kill switch. Trip/Reset are exposed on the concrete type only —
/// pipeline stages get the read-only interface; the control surface lives in the Host.</summary>
public sealed class KillSwitch : IKillSwitch
{
    private volatile string? _reason;
    private volatile bool _tripped;

    public bool IsTripped => _tripped;
    public string? Reason => _reason;

    public void Trip(string reason)
    {
        _reason = reason;
        _tripped = true;
    }

    public void Reset()
    {
        _tripped = false;
        _reason = null;
    }
}

/// <summary>Spend caps in USD. The global cap is required — nothing is unlimited (fail-closed FinOps);
/// team caps are optional refinements inside the global ceiling, keyed by the attribution Team tag.</summary>
public sealed class BudgetConfig
{
    public required decimal GlobalCapUsd { get; init; }
    public IReadOnlyDictionary<string, decimal> TeamCapsUsd { get; init; } =
        new Dictionary<string, decimal>();
}

/// <summary>Accumulated spend. First slice is in-memory (resets on restart — a durable ledger is a
/// later decision that belongs with the audit-store ADR).</summary>
public interface ISpendLedger
{
    decimal GlobalSpendUsd { get; }
    decimal TeamSpendUsd(string team);
    void Record(string team, decimal usd);
    IReadOnlyDictionary<string, decimal> SnapshotByTeam();
    /// <summary>Clears all recorded spend (used with an audit-log rotation — never on its own,
    /// or ledger and evidence would disagree).</summary>
    void Reset();
}

public sealed class InMemorySpendLedger : ISpendLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, decimal> _byTeam = new(StringComparer.OrdinalIgnoreCase);
    private decimal _global;

    public decimal GlobalSpendUsd
    {
        get { lock (_gate) return _global; }
    }

    public decimal TeamSpendUsd(string team)
    {
        lock (_gate) return _byTeam.GetValueOrDefault(team);
    }

    public void Record(string team, decimal usd)
    {
        lock (_gate)
        {
            _global += usd;
            _byTeam[team] = _byTeam.GetValueOrDefault(team) + usd;
        }
    }

    public IReadOnlyDictionary<string, decimal> SnapshotByTeam()
    {
        lock (_gate) return new Dictionary<string, decimal>(_byTeam, StringComparer.OrdinalIgnoreCase);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _global = 0m;
            _byTeam.Clear();
        }
    }
}

/// <summary>Global provider-call pause as its own pipeline stage (trace stage "pause", ADR-0013), placed after the cache
/// (a cache hit is served during a pause — documented semantics) and before the budget. Refusal ⇒ governed denial at
/// stage "pause". The provider stage re-checks the pause before every fallback attempt through the attempt gate.</summary>
public sealed class PauseStage(IKillSwitch killSwitch) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (killSwitch.IsTripped)
        {
            ctx.Deny(BudgetStage.PauseReason(killSwitch), stage: "pause", code: TraceReasons.Paused);
            return;
        }
        await next(ctx, ct);
    }
}

/// <summary>Kill switch + budget enforcement, after policy and before the provider call (nothing that costs money runs past
/// an exhausted cap).
/// <para><b>Configured requests with a v2 ledger (ADR-0014):</b> the stage resolves the charged scopes (global, team,
/// application), installs the per-request <see cref="BudgetAttemptGate"/> and reserves the FIRST candidate's conservative
/// bound against every applicable hard budget, all-or-none (refusal ⇒ denial at stage "budget"). Later attempts are
/// admitted by the provider stage through the same gate; every attempt is settled exactly once and any reservation left
/// open is closed on the unwind (started ⇒ settled at the estimate, never started ⇒ released).</para>
/// <para><b>Legacy compositions</b> (no configuration, or no v2 ledger): check-then-spend against the in-memory ledger
/// (the last admitted request may overshoot by its own cost), unchanged.</para></summary>
public sealed class BudgetStage : IPipelineStage
{
    private readonly IKillSwitch killSwitch;
    private readonly ISpendLedger ledger;
    private readonly BudgetConfig config;
    private readonly IPriceBook? prices;
    private readonly BudgetLedger? v2Ledger;

    public BudgetStage(IKillSwitch killSwitch, ISpendLedger ledger, BudgetConfig config, IPriceBook? prices = null)
    {
        this.killSwitch = killSwitch;
        this.ledger = ledger;
        this.config = config;
        this.prices = prices;
    }

    /// <summary>v2 composition (the Host): reservations against the persistent ledger for configured requests. A context
    /// without a configuration is refused by the zero legacy cap (fail-closed; the Host never produces one).</summary>
    public BudgetStage(IKillSwitch killSwitch, BudgetLedger v2Ledger)
        : this(killSwitch, new InMemorySpendLedger(), new BudgetConfig { GlobalCapUsd = 0m })
    {
        this.v2Ledger = v2Ledger;
    }

    internal static string PauseReason(IKillSwitch killSwitch)
        => killSwitch.Reason is { } r ? $"kill switch engaged: {r}" : "kill switch engaged";

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (killSwitch.IsTripped)
        {
            ctx.Deny(PauseReason(killSwitch), stage: "pause", code: TraceReasons.Paused);
            return;
        }

        if (ctx.Configuration is { } configured && v2Ledger is not null)
        {
            await ReserveAndRunAsync(ctx, configured, v2Ledger, next, ct);
            return;
        }

        // Caps come from the request's bound configuration when present (ADR-0011): hard "global" and
        // "team:<id>" budgets against the in-memory ledger (pure compositions without a v2 ledger).
        decimal? globalCap = config.GlobalCapUsd;
        decimal? teamCapFromConfig = null;
        var team = ctx.Identity.Tags.Team;
        if (ctx.Configuration is { } cfg)
        {
            globalCap = cfg.Config.Budgets.FirstOrDefault(b => b.Scope == "global" && b.Enforcement == BudgetEnforcement.Hard)?.CapUsd;
            teamCapFromConfig = cfg.Config.Budgets.FirstOrDefault(b =>
                b.Enforcement == BudgetEnforcement.Hard && b.Scope.StartsWith("team:", StringComparison.Ordinal)
                && string.Equals(b.Scope["team:".Length..], team, StringComparison.OrdinalIgnoreCase))?.CapUsd;
        }

        if (globalCap is { } gcap && ledger.GlobalSpendUsd >= gcap)
        {
            ctx.Deny($"global budget exhausted (cap {gcap} USD)", code: TraceReasons.BudgetExceeded);
            return;
        }

        decimal? teamCap = ctx.Configuration is not null
            ? teamCapFromConfig
            : config.TeamCapsUsd.TryGetValue(team, out var legacyTeamCap) ? (decimal?)legacyTeamCap : null;
        if (teamCap is { } tcap && ledger.TeamSpendUsd(team) >= tcap)
        {
            ctx.Deny($"budget exhausted for team '{team}' (cap {tcap} USD)", code: TraceReasons.BudgetExceeded);
            return;
        }

        try
        {
            await next(ctx, ct);
        }
        finally
        {
            // Unwind: the attribution stage (inner) has priced a served call by now.
            if (ctx.Attribution is { } spent)
                ledger.Record(spent.Tags.Team, spent.Usage.CostUsd);
            else if (ctx.AttemptedModel is { } attempted && ctx.Response is null)
                ChargeFailedCall(ctx, attempted);
        }
    }

    private async Task ReserveAndRunAsync(InferenceContext ctx, IRequestConfiguration cfg, BudgetLedger budgetLedger, PipelineDelegate next, CancellationToken ct)
    {
        var gate = new BudgetAttemptGate(budgetLedger, killSwitch, cfg, ctx);
        ctx.AttemptGate = gate;
        var first = ctx.Policy is { } p ? (p.Candidates.Count > 0 ? p.Candidates[0] : p.Route) : null;
        if (first is not null)
        {
            var admission = gate.ReserveFirst(ctx, first);
            if (!admission.Admitted)
            {
                ctx.Deny(admission.Reason ?? "budget refused", stage: "budget", code: admission.Code);
                return;
            }
        }
        try
        {
            await next(ctx, ct);
        }
        finally
        {
            gate.Finish(ctx);
        }
    }

    /// <summary>ADR-0009 fail-closed accounting: a provider call that was started but produced no
    /// response (upstream failure, mid-stream failure, cancellation) may still be billed. It is never
    /// recorded as $0: input (+ any streamed output) is estimated at chars/4 and charged when the model
    /// is priced; otherwise it is flagged unknown.</summary>
    private void ChargeFailedCall(InferenceContext ctx, string model)
    {
        bool priced;
        (decimal InputPer1K, decimal OutputPer1K) p;
        if (ctx.Configuration is { } cfg)
            priced = ConfigPrices.TryGet(cfg, ctx.Attempts.Count > 0 ? ctx.Attempts[^1].ConnectionId : ctx.Policy?.Route?.AdapterKey ?? "", model, out p);
        else if (prices is not null)
            priced = prices.TryGetPrice(model, out p);
        else { priced = false; p = default; }

        if (priced)
        {
            long inTok = TokenEstimate.FromChars(ctx.Request.Messages.Sum(m => m.Content.Length));
            long outTok = TokenEstimate.FromChars(ctx.PartialOutputChars);
            var cost = inTok / 1000m * p.InputPer1K + outTok / 1000m * p.OutputPer1K;
            ctx.FailedCallUsage = new Usage(0, 0, cost);   // tokens NOT reported; cost is an estimate
            ctx.CostBasis = "estimated_failed_call";
            if (cost > 0m) ledger.Record(ctx.Identity.Tags.Team, cost);
        }
        else
        {
            ctx.CostBasis = "unknown_failed_call";
        }
    }
}

/// <summary>Connection-scoped price lookup in a bound configuration (USD per 1K, matching IPriceBook).
/// A model with no price entry is UNPRICED — never a silent $0.</summary>
public static class ConfigPrices
{
    public static bool TryGet(IRequestConfiguration cfg, string connectionId, string modelId, out (decimal InputPer1K, decimal OutputPer1K) price)
    {
        if (cfg.FindModel(connectionId, modelId)?.Price is { } p)
        {
            price = (p.InputPer1M / 1000m, p.OutputPer1M / 1000m);
            return true;
        }
        price = default;
        return false;
    }
}

/// <summary>Conservative token estimate for unreported usage: ceil(chars / 4). An estimate, labelled
/// as such wherever it surfaces (CostBasis); never presented as provider-reported counts.</summary>
public static class TokenEstimate
{
    public static long FromChars(long chars) => chars <= 0 ? 0 : (chars + 3) / 4;
}
