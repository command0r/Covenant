using System.Text;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0014 ledger guarantees, deterministic (barriers, settable clock — no sleeps): atomic all-or-none
/// reservation across scopes, exactly-once settle/release, crash recovery, UTC periods independent of budget ids,
/// overrun flagging, torn-tail repair of the journal.</summary>
public sealed class BudgetLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"covenant-ledger-{Guid.NewGuid():n}.jsonl");

    public void Dispose() => CorrelationTestKit.DeleteWithSiblings(_path);

    private static readonly IReadOnlyList<string> Scopes = ["global", "team:platform", "application:demo-app"];

    private static BudgetCheck Global(decimal cap, BudgetPeriod period = BudgetPeriod.Monthly, string id = "global")
        => new(id, "global", cap, period);

    [Fact]
    public void Concurrent_reservations_cannot_both_take_the_last_allowance()
    {
        var ledger = new BudgetLedger(new InMemoryLedgerStore());
        Assert.True(ledger.TryReserve("seed", "att_1", Scopes, 0.6m, [Global(1m)]).Reserved);
        ledger.Settle("seed", "att_1", 0.6m, Scopes);                // spent 0.6 of a 1.0 cap

        const int contenders = 16;
        using var barrier = new Barrier(contenders);
        var results = new bool[contenders];
        Parallel.For(0, contenders, new ParallelOptions { MaxDegreeOfParallelism = contenders }, i =>
        {
            barrier.SignalAndWait();
            results[i] = ledger.TryReserve($"req_{i}", "att_1", Scopes, 0.3m, [Global(1m)]).Reserved;
        });

        Assert.Single(results, r => r);                     // 0.6 + 0.3 fits; a second 0.3 never does
        Assert.Equal(0.3m, ledger.Reserved("global"));
    }

    [Fact]
    public void Reservation_is_all_or_none_across_every_applicable_hard_budget()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new BudgetLedger(store);
        var checks = new[] { Global(100m), new BudgetCheck("team", "team:platform", 0.5m, BudgetPeriod.Monthly) };

        var refused = ledger.TryReserve("r1", "att_1", Scopes, 0.6m, checks);
        Assert.False(refused.Reserved);
        Assert.Equal("team", refused.RefusedBudgetId);
        Assert.Equal(0m, ledger.Reserved("global"));                  // nothing reserved anywhere
        Assert.Empty(store.Records);

        // A budget on a scope this request is not charged to does not apply.
        Assert.True(ledger.TryReserve("r2", "att_1", ["global", "team:other"], 0.6m, checks).Reserved);
    }

    [Fact]
    public void Settle_and_release_happen_exactly_once()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new BudgetLedger(store);
        Assert.True(ledger.TryReserve("r1", "att_1", Scopes, 0.10m, [Global(1m)]).Reserved);
        Assert.True(ledger.TryReserve("r1", "att_1", Scopes, 0.10m, [Global(1m)]).Reserved);   // idempotent: no second hold
        Assert.Equal(0.10m, ledger.Reserved("global"));

        var first = ledger.Settle("r1", "att_1", 0.04m, Scopes);
        var second = ledger.Settle("r1", "att_1", 0.04m, Scopes);
        Assert.True(first.Settled);
        Assert.False(second.Settled);                                   // double settle ignored
        Assert.False(ledger.Release("r1", "att_1"));                    // remainder already released with the settle
        Assert.Equal(0.04m, ledger.Spent("global", BudgetPeriod.Monthly));
        Assert.Equal(0m, ledger.Reserved("global"));

        var ops = store.Records.Select(r => r.Op).ToList();
        Assert.Equal(new[] { LedgerOps.Reserve, LedgerOps.Settle, LedgerOps.Release }, ops);
        Assert.Equal(0.06m, store.Records[2].AmountUsd);                // the released remainder

        // A never-started attempt is released with no charge, once.
        Assert.True(ledger.TryReserve("r2", "att_1", Scopes, 0.2m, [Global(1m)]).Reserved);
        Assert.True(ledger.Release("r2", "att_1"));
        Assert.False(ledger.Release("r2", "att_1"));
        Assert.False(ledger.Settle("r2", "att_1", 0.2m, Scopes).Settled);
        Assert.Equal(0.04m, ledger.Spent("global", BudgetPeriod.Monthly));
    }

    [Fact]
    public void Reported_cost_above_the_reservation_is_settled_in_full_and_flagged_as_overrun()
    {
        var store = new InMemoryLedgerStore();
        var ledger = new BudgetLedger(store);
        ledger.TryReserve("r1", "att_1", Scopes, 0.01m, [Global(1m)]);
        var s = ledger.Settle("r1", "att_1", 0.05m, Scopes);
        Assert.True(s.Overrun);
        Assert.Equal(0.05m, ledger.Spent("global", BudgetPeriod.None));   // never truncated to the reservation
        Assert.True(store.Records.Single(r => r.Op == LedgerOps.Settle).Overrun);
        Assert.Equal(0m, store.Records.Single(r => r.Op == LedgerOps.Release).AmountUsd);
    }

    [Fact]
    public void Spend_is_computed_per_scope_and_UTC_period_so_budget_id_period_and_cap_edits_never_reset_accrual()
    {
        var clock = new SettableClock(DateTimeOffset.Parse("2026-09-30T23:59:00Z"));
        var ledger = new BudgetLedger(new InMemoryLedgerStore(), clock: clock);
        ledger.TryReserve("sep", "att_1", Scopes, 1m, []);
        ledger.Settle("sep", "att_1", 1m, Scopes);                     // September
        clock.Set(DateTimeOffset.Parse("2026-10-16T08:00:00Z"));
        ledger.TryReserve("oct", "att_1", Scopes, 2m, []);
        ledger.Settle("oct", "att_1", 2m, Scopes);                     // October

        Assert.Equal(2m, ledger.Spent("global", BudgetPeriod.Monthly));
        Assert.Equal(2m, ledger.Spent("global", BudgetPeriod.Daily));
        Assert.Equal(3m, ledger.Spent("global", BudgetPeriod.None));
        Assert.Equal(3m, ledger.Spent("application:demo-app", BudgetPeriod.None));
        Assert.Equal(3m, ledger.Spent("TEAM:Platform", BudgetPeriod.None));   // scopes compare case-insensitively

        // The same scope under another budget id / cap: identical accrual (the ledger never records budget ids).
        Assert.False(ledger.TryReserve("x", "att_1", Scopes, 0.5m, [Global(2.4m, BudgetPeriod.Monthly, id: "renamed")]).Reserved);
        Assert.True(ledger.TryReserve("y", "att_1", Scopes, 0.5m, [Global(2.6m, BudgetPeriod.Monthly, id: "renamed")]).Reserved);

        clock.Set(DateTimeOffset.Parse("2026-11-01T00:00:00Z"));       // period rollover reopens monthly, not all-time
        Assert.Equal(0m, ledger.Spent("global", BudgetPeriod.Monthly));
        Assert.Equal(3m, ledger.Spent("global", BudgetPeriod.None));
        Assert.Equal("2026-11", BudgetPeriods.PeriodId(BudgetPeriod.Monthly, clock.GetUtcNow()));
        Assert.Equal("2026-11-01", BudgetPeriods.PeriodId(BudgetPeriod.Daily, clock.GetUtcNow()));
        Assert.Equal("all", BudgetPeriods.PeriodId(BudgetPeriod.None, clock.GetUtcNow()));
    }

    [Fact]
    public void Restart_replays_the_journal_and_settles_open_reservations_at_their_reserved_amount()
    {
        var store = new FileLedgerStore(_path);
        Assert.False(store.Open().Existed);
        store.EnsureCreated();
        var ledger = new BudgetLedger(store);
        ledger.TryReserve("done", "att_1", Scopes, 0.5m, [Global(10m)]);
        ledger.Settle("done", "att_1", 0.2m, Scopes);
        ledger.TryReserve("crashed", "att_1", Scopes, 0.7m, [Global(10m)]);   // the process "dies" here

        var reopened = new FileLedgerStore(_path);
        var opened = reopened.Open();
        Assert.True(opened.Existed);
        Assert.Empty(opened.Errors);
        var after = new BudgetLedger(reopened, opened.Records);
        Assert.Equal(0.7m, after.Reserved("global"));
        var recovered = after.RecoverUnsettled();
        var r = Assert.Single(recovered);
        Assert.Equal((LedgerOps.SettleRecovered, "crashed", 0.7m), (r.Op, r.RequestId, r.AmountUsd));
        Assert.Equal(0.9m, after.Spent("global", BudgetPeriod.Monthly));     // conservative: never free capacity
        Assert.Equal(0m, after.Reserved("global"));

        // A second restart: nothing left to recover, same spend (exactly once on replay too).
        var third = new FileLedgerStore(_path);
        var again = new BudgetLedger(third, third.Open().Records);
        Assert.Empty(again.RecoverUnsettled());
        Assert.Equal(0.9m, again.Spent("global", BudgetPeriod.Monthly));
    }

    [Fact]
    public void Torn_tail_is_quarantined_and_repaired_before_the_next_append_and_survives_two_more_restarts()
    {
        var store = new FileLedgerStore(_path);
        store.Open();
        store.EnsureCreated();
        var ledger = new BudgetLedger(store);
        ledger.TryReserve("r1", "att_1", Scopes, 0.5m, []);
        ledger.Settle("r1", "att_1", 0.5m, Scopes);
        File.AppendAllText(_path, "{\"op\":\"reserve\",\"request_id\":\"r2\",\"att");   // interrupted append

        for (int restart = 0; restart < 3; restart++)
        {
            var s = new FileLedgerStore(_path);
            var opened = s.Open();
            Assert.Empty(opened.Errors);
            var l = new BudgetLedger(s, opened.Records);
            Assert.Equal(0.5m + restart * 0.25m, l.Spent("global", BudgetPeriod.None));
            l.TryReserve($"after{restart}", "att_1", Scopes, 0.25m, []);
            l.Settle($"after{restart}", "att_1", 0.25m, Scopes);
        }
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".torn-*"));
        Assert.EndsWith("\n", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_line_before_the_tail_refuses_to_start()
    {
        File.WriteAllText(_path, "{\"op\":\"settle\"\n{not json}\n" +
            "{\"op\":\"settle\",\"request_id\":\"a\",\"attempt_id\":\"b\",\"scopes\":[\"global\"],\"amount_usd\":1,\"ts_utc\":\"2026-10-16T00:00:00+00:00\"}\n",
            Encoding.UTF8);
        var opened = new FileLedgerStore(_path).Open();
        Assert.NotEmpty(opened.Errors);
    }

    [Fact]
    public void Legacy_import_charges_global_and_team_scopes_once()
    {
        var ledger = new BudgetLedger(new InMemoryLedgerStore());
        ledger.ImportLegacy(new[] { ("payments", 1.5m), ("data", 0.5m), ("idle", 0m) });
        Assert.Equal(2m, ledger.Spent("global", BudgetPeriod.Monthly));
        Assert.Equal(1.5m, ledger.Spent("team:payments", BudgetPeriod.Monthly));
        Assert.Equal(0m, ledger.Spent("team:idle", BudgetPeriod.Monthly));
    }
}

/// <summary>ADR-0014 through the real pipeline stages (budget stage + gate + provider + attribution + audit).</summary>
public sealed class BudgetPipelineTests
{
    private readonly StubChatClient _mini = new("mini reply");
    private readonly StubChatClient _local = new("local reply");

    private V2Rig Rig(decimal cap = 100m, Func<CovenantConfig, CovenantConfig>? tweak = null)
        => V2Rig.Build(V2Rig.FallbackConfig(new RetrySettings { MaxAttempts = 1, DeadlineMs = 60_000 }, cap, tweak),
            new Dictionary<string, MEAI.IChatClient>
            {
                [$"openai:{V2.CloudModel}"] = _mini,
                [$"ollama:{V2.LocalTag}"] = _local,
            });

    private static decimal Bound(string prompt, int outputTokens = RequestBounds.DefaultOutputTokens)
        => (Encoding.UTF8.GetByteCount(prompt) + RequestBounds.PerMessageOverheadTokens) / 1_000_000m * 0.15m + outputTokens / 1_000_000m * 0.60m;

    [Fact]
    public async Task Reservation_uses_utf8_bytes_plus_16_per_message_and_the_enforced_output_bound()
    {
        var rig = Rig();
        const string prompt = "Résumé — public naïve café";   // multi-byte: bytes > chars
        var (ctx, e) = await rig.RunAsync(prompt, profile: "public-demo", maxTokens: 200);
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(Bound(prompt, 200), e.ReservedUsd);
        Assert.Equal(10 / 1_000_000m * 0.15m + 5 / 1_000_000m * 0.60m, e.SettledUsd);   // reported usage
        Assert.Equal("reported", e.CostBasis);
        Assert.Equal(e.SettledUsd, rig.Ledger.Spent("application:demo-app", BudgetPeriod.Monthly));
        Assert.Equal(e.SettledUsd, rig.Ledger.Spent("team:platform", BudgetPeriod.Monthly));
        Assert.Equal(0m, rig.Ledger.Reserved("global"));
    }

    [Fact]
    public async Task Unreported_usage_settles_the_conservative_reservation_bound()
    {
        _mini.Then(StubChatClient.Mode.ServeNoUsage);
        var rig = Rig();
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(e.UsageReported);
        Assert.Equal("estimated_from_text", e.CostBasis);
        Assert.Equal(Bound(V2Rig.PublicPrompt), e.SettledUsd);
        Assert.Equal(e.ReservedUsd, e.SettledUsd);
        Assert.Equal(e.SettledUsd, e.Usage.CostUsd);
    }

    [Fact]
    public async Task Overrun_is_settled_in_full_and_flagged_in_the_attempt_evidence()
    {
        _mini.Then(StubChatClient.Mode.ServeHugeUsage);
        var rig = Rig();
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        var a = Assert.Single(e.Attempts!);
        Assert.True(a.Overrun);
        Assert.Equal(0.15m + 0.60m, a.CostUsd);                    // 1M in + 1M out, never truncated to the bound
        Assert.True(a.CostUsd > a.ReservedUsd);
        Assert.Equal(0.75m, rig.Ledger.Spent("global", BudgetPeriod.Monthly));
    }

    [Fact]
    public async Task Hard_cap_refuses_at_the_budget_stage_before_any_provider_call_and_local_zero_price_still_runs()
    {
        var rig = Rig(cap: 0.0001m);                                 // below one cloud bound
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.True(ctx.IsDenied);
        Assert.Equal("budget", e.StoppedAtStage);
        Assert.Contains("would be exceeded", e.Reason, StringComparison.Ordinal);
        Assert.Equal(0, _mini.Calls);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "budget" && t.State == TraceStates.Refused && t.ReasonCode == TraceReasons.BudgetExceeded);

        var (local, le) = await rig.RunAsync("an internal question", profile: "internal-default");   // explicit $0 local price
        Assert.False(local.IsDenied, local.DenialReason);
        Assert.Equal(0m, le.ReservedUsd ?? 0m);
        Assert.Equal(1, _local.Calls);
    }

    [Fact]
    public async Task Warn_only_budget_never_blocks()
    {
        var rig = Rig(tweak: c => c with
        {
            Budgets = [new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = 0m, Enforcement = BudgetEnforcement.WarnOnly }],
        });
        var (ctx, _) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(1, _mini.Calls);
    }

    [Fact]
    public async Task Application_scope_budget_is_enforced()
    {
        var rig = Rig(tweak: c => c with
        {
            Budgets =
            [
                new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = 100m },
                new BudgetConfigV2 { Id = "demo", Scope = "application:demo-app", CapUsd = 0.00001m },
            ],
        });
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal("budget", e.StoppedAtStage);
        Assert.Contains("'demo'", e.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_settles_the_started_attempt_once_and_releases_the_reservation()
    {
        _mini.Then(StubChatClient.Mode.Hang);
        var rig = Rig();
        using var cts = new CancellationTokenSource();
        var run = rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", ct: cts.Token);
        await _mini.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rig.Ledger.Reserved("global") > 0m);            // held while in flight
        cts.Cancel();
        var (_, e) = await run;

        Assert.Equal("Cancelled", e.DenialKind);
        Assert.Equal(0, rig.Ledger.OpenReservations);
        Assert.Equal(0m, rig.Ledger.Reserved("global"));
        var ops = rig.Store.Records.Select(r => r.Op).ToList();
        Assert.Equal(new[] { LedgerOps.Reserve, LedgerOps.Settle, LedgerOps.Release }, ops);   // exactly once each
        Assert.Equal(e.SettledUsd, rig.Ledger.Spent("global", BudgetPeriod.Monthly));
        Assert.Equal("estimated_failed_call", e.CostBasis);
    }

    [Fact]
    public async Task Unpriced_cloud_target_is_ineligible_under_a_hard_budget()
    {
        var rig = Rig(tweak: c => c with
        {
            Connections = c.Connections.Select(x => x.Id != "openai" ? x : x with
            {
                Models = x.Models.Select(m => m with { Price = null }).ToList(),
            }).ToList(),
        });
        var (ctx, _) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        // The cloud primary is excluded ("unpriced paid destination under a hard budget"); the local fallback serves.
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Contains(ctx.Policy!.Excluded, x => x.Reason.Contains("unpriced", StringComparison.Ordinal));
        Assert.Equal(0, _mini.Calls);
        Assert.Equal(1, _local.Calls);

        var invalid = ConfigValidator.Validate(V2Rig.FallbackConfig(tweak: c => c with
        {
            Connections = c.Connections.Select(x => x.Id != "openai" ? x : x with { Models = x.Models.Select(m => m with { Price = null }).ToList() }).ToList(),
        }), V2.Env());
        Assert.Contains(invalid.Errors, i => i.Code == "unpriced_paid_target_under_hard_budget");
    }
}
