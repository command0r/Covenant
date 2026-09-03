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

/// <summary>Kill switch + budget enforcement, after policy and before the provider call (nothing that costs money runs past an exhausted cap); records actual attributed cost on the unwind.
/// Check-then-spend: the last admitted request may overshoot by its own cost — deliberate first-slice trade-off (pre-call estimation needs a tokenizer).</summary>
public sealed class BudgetStage(IKillSwitch killSwitch, ISpendLedger ledger, BudgetConfig config) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (killSwitch.IsTripped)
        {
            ctx.Deny(killSwitch.Reason is { } r ? $"kill switch engaged: {r}" : "kill switch engaged");
            return;
        }

        if (ledger.GlobalSpendUsd >= config.GlobalCapUsd)
        {
            ctx.Deny($"global budget exhausted (cap {config.GlobalCapUsd} USD)");
            return;
        }

        var team = ctx.Identity.Tags.Team;
        if (config.TeamCapsUsd.TryGetValue(team, out var teamCap) && ledger.TeamSpendUsd(team) >= teamCap)
        {
            ctx.Deny($"budget exhausted for team '{team}' (cap {teamCap} USD)");
            return;
        }

        await next(ctx, ct);

        // Unwind: the attribution stage (inner) has priced the call by now.
        if (ctx.Attribution is { } spent)
            ledger.Record(spent.Tags.Team, spent.Usage.CostUsd);
    }
}
