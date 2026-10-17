using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Scopes one request is charged to (ADR-0014): always <c>global</c>, the caller's <c>team:&lt;t&gt;</c>, and
/// <c>application:&lt;a&gt;</c> when the application is known — independent of which budgets exist, so a budget added or
/// renamed later sees the scope's whole accrued history.</summary>
public static class BudgetScopes
{
    public const string Global = "global";

    public static IReadOnlyList<string> For(InferenceContext ctx)
    {
        var scopes = new List<string> { Global, "team:" + ctx.Identity.Tags.Team };
        if (ctx.ApplicationId is { Length: > 0 } app) scopes.Add("application:" + app);
        return scopes;
    }

    /// <summary>The hard budgets of <paramref name="cfg"/> whose scope this request is charged to.</summary>
    public static IReadOnlyList<BudgetCheck> HardChecks(IRequestConfiguration cfg, IReadOnlyList<string> scopes)
        => cfg.Config.Budgets
            .Where(b => b.Enforcement == BudgetEnforcement.Hard && scopes.Contains(b.Scope, StringComparer.OrdinalIgnoreCase))
            .Select(b => new BudgetCheck(b.Id, b.Scope, b.CapUsd, b.Period))
            .ToList();
}

/// <summary>Conservative reservation estimate for one attempt (ADR-0014): input bound (UTF-8 bytes + 16 per message) at
/// the target's input price + enforced output bound at its output price, connection-scoped. Unpriced ⇒ 0 with
/// Priced = false (an unpriced paid target is ineligible under a hard budget — the engine and validator enforce that).</summary>
public static class ReservationEstimate
{
    public static (decimal AmountUsd, bool Priced) For(InferenceContext ctx, IRequestConfiguration cfg, RouteTarget target)
    {
        if (!ConfigPrices.TryGet(cfg, target.AdapterKey, target.ModelId, out var p)) return (0m, false);
        // The grounded prompt (ADR-0015) is what the model receives; before the search runs, reserve its upper bound.
        long input = RequestBounds.InputTokenBound(ctx.MessagesForModel) + WebSearchGrounding.PendingBytes(ctx);
        long output = RequestBounds.OutputTokenBound(ctx, target) ?? RequestBounds.DefaultOutputTokens;
        return (input / 1000m * p.InputPer1K + output / 1000m * p.OutputPer1K, true);
    }
}

/// <summary>The per-request <see cref="IAttemptGate"/> over the persistent <see cref="BudgetLedger"/> (ADR-0013/0014).
/// Attempt 1 is reserved by the budget stage (<see cref="ReserveFirst"/>); every later attempt re-checks the global
/// pause and reserves before it starts (trace events pause/budget with the attempt id). Settlement is exactly once per
/// attempt (the gate and the ledger are both idempotent); zero-bound attempts (explicit $0 local price) write nothing.
/// Charged scopes are captured when the gate is created — an attempt settles against the scopes that applied when it was
/// reserved, even across a configuration activation.</summary>
public sealed class BudgetAttemptGate : IAttemptGate, IToolCallGate
{
    private sealed class Slot
    {
        public required RouteTarget Target { get; init; }
        public required decimal Reserved { get; init; }
        public required bool Priced { get; init; }
        public bool Closed { get; set; }
        /// <summary>A durable settle/release write is in progress (closed only after it succeeded).</summary>
        public bool InFlight { get; set; }
        public AttemptCharge? Charge { get; set; }
    }

    private readonly BudgetLedger _ledger;
    private readonly IKillSwitch _killSwitch;
    private readonly IRequestConfiguration _cfg;
    private readonly IReadOnlyList<string> _scopes;
    private readonly IReadOnlyList<BudgetCheck> _checks;
    private readonly string _requestId;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public BudgetAttemptGate(BudgetLedger ledger, IKillSwitch killSwitch, IRequestConfiguration cfg, InferenceContext ctx)
    {
        _ledger = ledger;
        _killSwitch = killSwitch;
        _cfg = cfg;
        _scopes = BudgetScopes.For(ctx);
        _checks = BudgetScopes.HardChecks(cfg, _scopes);
        _requestId = ctx.Request.RequestId ?? $"req_{Guid.NewGuid():n}";
    }

    public IReadOnlyList<string> Scopes => _scopes;

    /// <summary>Budget stage: reserve attempt 1 for the primary candidate (no attempt-level trace — the stage itself is traced).</summary>
    public AttemptAdmission ReserveFirst(InferenceContext ctx, RouteTarget target) => Reserve(ctx, target, "att_1");

    public AttemptAdmission Admit(InferenceContext ctx, RouteTarget target, string attemptId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(attemptId, out var existing) && !existing.Closed && existing.Target == target)
            {
                ctx.ActiveReservationUsd = existing.Reserved;
                return AttemptAdmission.Ok(existing.Reserved);   // pre-reserved by the budget stage
            }
        }

        // A fallback attempt: the global pause is re-checked before it starts (no governance bypass).
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (_killSwitch.IsTripped)
        {
            ctx.Trace("pause", TraceStates.Refused, watch.Elapsed.TotalMilliseconds, TraceReasons.Paused, attemptId);
            return AttemptAdmission.Refused(BudgetStage.PauseReason(_killSwitch), "pause", TraceReasons.Paused);
        }
        ctx.Trace("pause", TraceStates.Passed, watch.Elapsed.TotalMilliseconds, attemptId: attemptId);

        watch.Restart();
        var admission = Reserve(ctx, target, attemptId);
        ctx.Trace("budget", admission.Admitted ? TraceStates.Passed : TraceStates.Refused, watch.Elapsed.TotalMilliseconds,
            admission.Admitted ? null : admission.Code, attemptId);
        return admission;
    }

    private AttemptAdmission Reserve(InferenceContext ctx, RouteTarget target, string attemptId)
    {
        var (amount, priced) = ReservationEstimate.For(ctx, _cfg, target);
        if (amount > 0m)
        {
            var result = _ledger.TryReserve(_requestId, attemptId, _scopes, amount, _checks);
            if (!result.Reserved)
                return AttemptAdmission.Refused(result.Reason ?? "budget refused", "budget", TraceReasons.BudgetExceeded);
        }
        lock (_gate)
            _slots[attemptId] = new Slot { Target = target, Reserved = amount, Priced = priced };
        ctx.ActiveReservationUsd = amount;
        ctx.ReservedUsd = (ctx.ReservedUsd ?? 0m) + amount;
        return AttemptAdmission.Ok(amount);
    }

    // ---- Tool calls (ADR-0015): same ledger, same scopes, same exactly-once slots; ids never collide with attempts. ----

    public AttemptAdmission ReserveTool(InferenceContext ctx, string toolCallId, decimal amountUsd)
    {
        if (amountUsd < 0m) return AttemptAdmission.Refused("negative tool price", "budget", TraceReasons.BudgetExceeded);
        lock (_gate)
            if (_slots.ContainsKey(toolCallId)) return AttemptAdmission.Refused("tool call already reserved", "budget", TraceReasons.BudgetExceeded);
        if (amountUsd > 0m)
        {
            var result = _ledger.TryReserve(_requestId, toolCallId, _scopes, amountUsd, _checks);
            if (!result.Reserved)
                return AttemptAdmission.Refused(result.Reason ?? "budget refused", "budget", TraceReasons.BudgetExceeded);
        }
        lock (_gate)
            _slots[toolCallId] = new Slot { Target = new RouteTarget("tool", toolCallId), Reserved = amountUsd, Priced = true };
        ctx.ReservedUsd = (ctx.ReservedUsd ?? 0m) + amountUsd;
        return AttemptAdmission.Ok(amountUsd);
    }

    public AttemptCharge SettleTool(InferenceContext ctx, string toolCallId, decimal? costUsd) => Settle(ctx, toolCallId, costUsd);

    public void ReleaseTool(InferenceContext ctx, string toolCallId) => Release(ctx, toolCallId);

    /// <summary>Settles a started attempt exactly once. The slot is marked closed only AFTER the durable ledger write
    /// succeeded (review round 2, B4): a failed write leaves the slot — and the ledger reservation — open, so a later call
    /// (the budget stage's unwind) or boot recovery settles it; it is never silently lost. A concurrent duplicate while a
    /// write is in flight is answered as a duplicate (Settled = false) and writes nothing.</summary>
    public AttemptCharge Settle(InferenceContext ctx, string attemptId, decimal? costUsd)
    {
        Slot? slot;
        lock (_gate)
        {
            _slots.TryGetValue(attemptId, out slot);
            if (slot is { Closed: true }) return slot.Charge is { } c ? c with { Settled = false } : new AttemptCharge(0m, "no_call", false, false);
            if (slot is { InFlight: true }) return new AttemptCharge(0m, "no_call", false, false);
            if (slot is not null) slot.InFlight = true;
        }

        bool priced = slot?.Priced ?? false;
        decimal reserved = slot?.Reserved ?? 0m;
        decimal amount = costUsd ?? reserved;
        string basis = costUsd is not null ? "reported" : priced ? "estimated_failed_call" : "unknown_failed_call";
        SettleResult result;
        try
        {
            result = _ledger.Settle(_requestId, attemptId, amount, _scopes);
        }
        catch
        {
            lock (_gate)
                if (slot is not null) slot.InFlight = false;   // still open: retried by Finish, or settled by boot recovery
            throw;
        }
        var charge = new AttemptCharge(amount, basis, result.Overrun || (costUsd is { } c2 && c2 > reserved && reserved > 0m), result.Settled || amount == 0m);
        lock (_gate)
        {
            if (slot is not null)
            {
                slot.Charge = charge;
                slot.Closed = true;
                slot.InFlight = false;
            }
        }
        if (charge.Settled) ctx.SettledUsd = (ctx.SettledUsd ?? 0m) + amount;
        return charge;
    }

    /// <summary>Releases an admitted attempt that never started; closed only after the durable release succeeded.</summary>
    public void Release(InferenceContext ctx, string attemptId)
    {
        Slot? slot;
        lock (_gate)
        {
            if (!_slots.TryGetValue(attemptId, out slot) || slot.Closed || slot.InFlight) return;
            slot.InFlight = true;
        }
        try
        {
            _ledger.Release(_requestId, attemptId);
        }
        catch
        {
            lock (_gate) slot!.InFlight = false;
            throw;
        }
        lock (_gate)
        {
            slot!.Closed = true;
            slot.InFlight = false;
        }
    }

    /// <summary>Unwind safety net (budget stage finally): an attempt that started but was never settled (an exception
    /// escaped, or an earlier durable write failed) is settled at its estimate; an admitted attempt that never started is
    /// released. Every open slot is tried even if one write fails (the first failure is rethrown afterwards). Then the
    /// request-level failed-call charge and cost basis are derived from the attempts (never a silent $0).</summary>
    public void Finish(InferenceContext ctx)
    {
        List<string> open;
        lock (_gate) open = _slots.Where(kv => !kv.Value.Closed).Select(kv => kv.Key).ToList();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        foreach (var id in open)
        {
            try
            {
                // A tool call that was sent but never settled (an exception escaped) is charged at its reservation —
                // never released for free.
                if (ctx.ToolCalls.FirstOrDefault(t => t.ToolCallId == id) is { } tool)
                {
                    var toolCharge = Settle(ctx, id, null);
                    if (toolCharge.Settled)
                    {
                        tool.CostUsd ??= toolCharge.CostUsd;
                        tool.CostBasis ??= "estimated_failed_call";
                    }
                    continue;
                }
                var started = ctx.Attempts.FirstOrDefault(a => a.AttemptId == id);
                if (started is null) { Release(ctx, id); continue; }
                var charge = Settle(ctx, id, null);
                if (charge.Settled)
                {
                    started.CostUsd ??= charge.CostUsd;
                    started.CostBasis ??= charge.CostBasis;
                }
            }
            catch (Exception ex)
            {
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (ctx.Response is null && ctx.Attempts.Count > 0)
        {
            ctx.FailedCallUsage ??= new Usage(0, 0, ctx.SettledUsd ?? 0m);   // tokens NOT reported; cost is an estimate
            ctx.CostBasis ??= ctx.Attempts[^1].CostBasis ?? "unknown_failed_call";
        }
        failure?.Throw();
    }

    /// <summary>Open (unsettled, unreleased) slots — diagnostics/tests.</summary>
    public int OpenSlots
    {
        get { lock (_gate) return _slots.Count(kv => !kv.Value.Closed); }
    }
}
