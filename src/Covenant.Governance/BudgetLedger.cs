using System.Globalization;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Ledger operations (ADR-0014). reserve → settle (exactly once) → release (the unused remainder, exactly once);
/// release without settle closes a reservation whose attempt never started; settle-recovered closes a reservation that
/// was still open at boot (crash) at its reserved amount; legacy-import is the one-time import of pre-ledger spend.</summary>
public static class LedgerOps
{
    public const string Reserve = "reserve";
    public const string Settle = "settle";
    public const string Release = "release";
    public const string SettleRecovered = "settle-recovered";
    public const string LegacyImport = "legacy-import";
    /// <summary>Review round 2 (B9): the last line of the atomically written initial ledger. A journal without it is an
    /// interrupted bootstrap ⇒ refuse to start (never silently restore capacity).</summary>
    public const string BootstrapComplete = "bootstrap-complete";
    public const string BootstrapRequestId = "bootstrap";

    public static bool IsCharge(string op) => op is Settle or SettleRecovered or LegacyImport;

    public static bool IsKnown(string op) => op is Reserve or Settle or Release or SettleRecovered or LegacyImport or BootstrapComplete;
}

/// <summary>Replay validation of a ledger journal (review round 2, B10). Corrupt accounting must never silently disappear:
/// every record is checked for the op vocabulary, non-negative amounts, non-empty ids, scope syntax, a real timestamp, and
/// the per-(request, attempt) lifecycle — reserve once; settle once (after a reserve, or a direct overrun settle of an
/// unreserved zero-bound attempt); release once (after a reserve, possibly after its settle); settle-recovered once, only
/// for an open reservation; nothing after release/settle-recovered. legacy-import is idempotent per scope key and never
/// after the bootstrap-complete marker; the marker appears at most once.</summary>
public static class LedgerReplay
{
    private enum State { Reserved, Settled, Closed }

    /// <summary>Null when valid, else (record index, diagnostic) of the FIRST violation.</summary>
    public static (int Index, string Error)? Validate(IReadOnlyList<LedgerRecord> records)
    {
        var states = new Dictionary<(string, string), State>();
        bool marker = false;
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (Check(r, states, ref marker) is { } error) return (i, error);
        }
        return null;
    }

    public static bool IsValidScope(string? scope)
    {
        if (scope is null) return false;
        foreach (var ch in scope) if (char.IsControl(ch)) return false;
        if (scope == BudgetScopes.Global) return true;
        foreach (var prefix in new[] { "team:", "application:" })
            if (scope.StartsWith(prefix, StringComparison.Ordinal) && scope.Length > prefix.Length && scope.Length <= 200) return true;
        return false;
    }

    private static string? Check(LedgerRecord r, Dictionary<(string, string), State> states, ref bool marker)
    {
        if (r.Op is null || !LedgerOps.IsKnown(r.Op)) return $"unknown op '{Evidence.Bounded(r.Op)}'";
        if (r.AmountUsd < 0m) return $"negative amount {r.AmountUsd.ToString(CultureInfo.InvariantCulture)}";
        if (string.IsNullOrWhiteSpace(r.RequestId)) return "empty request_id";
        if (string.IsNullOrWhiteSpace(r.AttemptId)) return "empty attempt_id";
        if (r.TsUtc == default || r.TsUtc.Year < 2000) return "missing or invalid ts_utc";
        if (r.Scopes is null) return "missing scopes";

        if (r.Op == LedgerOps.BootstrapComplete)
        {
            if (marker) return "duplicate bootstrap-complete marker";
            if (r.AmountUsd != 0m || r.Scopes.Count != 0) return "bootstrap-complete marker must carry no amount and no scopes";
            marker = true;
            return null;
        }
        if (r.Scopes.Count == 0) return $"{r.Op} without scopes";
        foreach (var scope in r.Scopes)
            if (!IsValidScope(scope)) return $"invalid scope '{Evidence.Bounded(scope)}'";

        var key = (r.RequestId, r.AttemptId);
        bool known = states.TryGetValue(key, out var state);
        switch (r.Op)
        {
            case LedgerOps.LegacyImport:
                if (marker) return "legacy-import after the bootstrap-complete marker";
                if (r.RequestId != LedgerOps.LegacyImport) return "legacy-import must use request_id 'legacy-import'";
                states[key] = State.Closed;   // idempotent per scope key (a duplicate is ignored on replay)
                return null;
            case LedgerOps.Reserve:
                if (known) return $"second reserve for {Describe(key)}";
                states[key] = State.Reserved;
                return null;
            case LedgerOps.Settle:
                if (!known)
                {
                    if (r.Overrun != true) return $"settle without a reservation for {Describe(key)}";
                    states[key] = State.Closed;   // direct overrun settle of an unreserved (zero-bound) attempt
                    return null;
                }
                if (state != State.Reserved) return $"second settle for {Describe(key)}";
                states[key] = State.Settled;
                return null;
            case LedgerOps.Release:
                if (!known) return $"release without a reservation for {Describe(key)}";
                if (state == State.Closed) return $"release after the attempt was closed for {Describe(key)}";
                states[key] = State.Closed;
                return null;
            case LedgerOps.SettleRecovered:
                if (!known) return $"settle-recovered without a reservation for {Describe(key)}";
                if (state != State.Reserved) return $"settle-recovered of an already settled attempt {Describe(key)}";
                states[key] = State.Closed;
                return null;
            default:
                return $"unknown op '{Evidence.Bounded(r.Op)}'";
        }
    }

    private static string Describe((string Req, string Att) key) => $"({Evidence.Bounded(key.Req)}, {Evidence.Bounded(key.Att)})";
}

/// <summary>One append-only ledger line. Keyed by (RequestId, AttemptId); records the SCOPES charged (global,
/// team:&lt;t&gt;, application:&lt;a&gt;) and a UTC timestamp — never a budget id or a period id, so editing a budget's id,
/// period or cap never resets accrual (ADR-0014).</summary>
public sealed record LedgerRecord
{
    public required string Op { get; init; }
    public required string RequestId { get; init; }
    public required string AttemptId { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required decimal AmountUsd { get; init; }
    public required DateTimeOffset TsUtc { get; init; }
    /// <summary>Settle only: the reported charge exceeded the reservation (settled in full anyway).</summary>
    public bool? Overrun { get; init; }
}

/// <summary>Durable append seam (the Host's file store fsyncs; tests use <see cref="InMemoryLedgerStore"/>). Append must
/// be durable when it returns, or throw.</summary>
public interface ILedgerStore
{
    void Append(IReadOnlyList<LedgerRecord> records);
}

public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly object _gate = new();
    private readonly List<LedgerRecord> _records = [];

    public IReadOnlyList<LedgerRecord> Records
    {
        get { lock (_gate) return _records.ToArray(); }
    }

    public void Append(IReadOnlyList<LedgerRecord> records)
    {
        lock (_gate) _records.AddRange(records);
    }
}

/// <summary>One hard-budget check applied at reservation time.</summary>
public sealed record BudgetCheck(string BudgetId, string Scope, decimal CapUsd, BudgetPeriod Period);

public sealed record ReservationResult(bool Reserved, string? RefusedBudgetId = null, string? Reason = null)
{
    public static readonly ReservationResult Ok = new(true);
}

public readonly record struct SettleResult(bool Settled, decimal AmountUsd, decimal ReservedUsd, bool Overrun);

/// <summary>A settled charge that contributes to a scope (drill-down: GET /admin/budgets/{id}/requests).</summary>
public sealed record LedgerContribution(string RequestId, string AttemptId, string Op, decimal AmountUsd, DateTimeOffset TsUtc, bool Overrun);

/// <summary>UTC budget periods (ADR-0014): none ⇒ all time ("all"); daily ⇒ "yyyy-MM-dd"; monthly ⇒ "yyyy-MM".</summary>
public static class BudgetPeriods
{
    public static string PeriodId(BudgetPeriod period, DateTimeOffset utc)
    {
        var u = utc.ToUniversalTime();
        return period switch
        {
            BudgetPeriod.Daily => u.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BudgetPeriod.Monthly => u.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => "all",
        };
    }

    /// <summary>Bucket key a charge at <paramref name="utc"/> accrues to for <paramref name="period"/>.</summary>
    internal static string Bucket(BudgetPeriod period, DateTimeOffset utc) => period switch
    {
        BudgetPeriod.Daily => "d:" + PeriodId(period, utc),
        BudgetPeriod.Monthly => "m:" + PeriodId(period, utc),
        _ => "all",
    };

    /// <summary>[start, end) of the period containing <paramref name="utc"/>; (MinValue, MaxValue) for none.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Window(BudgetPeriod period, DateTimeOffset utc)
    {
        var u = utc.ToUniversalTime();
        return period switch
        {
            BudgetPeriod.Daily => (new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1)),
            BudgetPeriod.Monthly => (new DateTimeOffset(u.Year, u.Month, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(u.Year, u.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1)),
            _ => (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
        };
    }
}

/// <summary>Persistent budget ledger (ADR-0014), independent of audit segments. State is rebuilt by replaying the
/// journal at boot; every mutation is appended durably BEFORE memory changes (a refused/failed write changes nothing).
/// One lock serializes reservations across all applicable hard budgets: <c>spent(period) + reserved + estimate ≤ cap</c>
/// for every check, all-or-none — concurrent requests cannot both take the last allowance. Spend per budget is
/// computed at read time from settled amounts of its scope in its current UTC period window. Scopes compare
/// case-insensitively (team names come from key configuration).</summary>
public sealed class BudgetLedger
{
    private sealed record Open(decimal Amount, IReadOnlyList<string> Scopes, DateTimeOffset TsUtc);

    private const int MaxContributions = 20_000;
    private const int MaxClosedKeys = 200_000;

    private readonly object _gate = new();
    private readonly ILedgerStore _store;
    private readonly TimeProvider _clock;
    private readonly Dictionary<(string Req, string Att), Open> _open = new();
    private readonly HashSet<(string Req, string Att)> _closed = new();
    private readonly Queue<(string Req, string Att)> _closedOrder = new();
    private readonly Dictionary<string, decimal> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Scope, string Bucket), decimal> _spent = new(ScopeBucketComparer.Instance);
    private readonly LinkedList<(LedgerContribution C, IReadOnlyList<string> Scopes)> _contributions = new();

    public BudgetLedger(ILedgerStore store, IEnumerable<LedgerRecord>? existing = null, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        if (existing is not null)
            foreach (var r in existing)
            {
                // Replay never silently drops accounting (B10): an unknown operation is corruption, not a no-op.
                if (!LedgerOps.IsKnown(r.Op)) throw new InvalidDataException($"budget ledger: unknown operation '{Evidence.Bounded(r.Op)}' on replay");
                Apply(r);
            }
    }

    /// <summary>The one-time legacy import records (B9): one per team with spend, charged to global + team, keyed
    /// (legacy-import, team:&lt;t&gt;) so a replayed duplicate is idempotent. Built by the Host into the atomic bootstrap.</summary>
    public static IReadOnlyList<LedgerRecord> LegacyImportRecords(IEnumerable<(string Team, decimal AmountUsd)> spendByTeam, DateTimeOffset now)
        => spendByTeam
            .Where(t => t.AmountUsd > 0m)
            .GroupBy(t => t.Team, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LedgerRecord
            {
                Op = LedgerOps.LegacyImport, RequestId = LedgerOps.LegacyImport, AttemptId = "team:" + g.First().Team,
                Scopes = [BudgetScopes.Global, "team:" + g.First().Team], AmountUsd = g.Sum(t => t.AmountUsd), TsUtc = now,
            })
            .ToList();

    /// <summary>The bootstrap-complete marker record (B9).</summary>
    public static LedgerRecord BootstrapMarker(DateTimeOffset now) => new()
    {
        Op = LedgerOps.BootstrapComplete, RequestId = LedgerOps.BootstrapRequestId, AttemptId = LedgerOps.BootstrapComplete,
        Scopes = [], AmountUsd = 0m, TsUtc = now,
    };

    public TimeProvider Clock => _clock;

    /// <summary>Open reservations (diagnostics/tests).</summary>
    public int OpenReservations
    {
        get { lock (_gate) return _open.Count; }
    }

    /// <summary>Reserves <paramref name="amountUsd"/> for (request, attempt) against every check whose scope is charged,
    /// all-or-none under the ledger lock. A key that is already reserved returns Ok (idempotent); a closed key is refused.</summary>
    public ReservationResult TryReserve(string requestId, string attemptId, IReadOnlyList<string> scopes, decimal amountUsd, IReadOnlyList<BudgetCheck> checks)
    {
        lock (_gate)
        {
            var key = (requestId, attemptId);
            if (_open.ContainsKey(key)) return ReservationResult.Ok;
            if (_closed.Contains(key)) return new ReservationResult(false, null, "attempt already settled");
            var now = _clock.GetUtcNow();
            foreach (var check in checks)
            {
                if (!scopes.Contains(check.Scope, StringComparer.OrdinalIgnoreCase)) continue;
                var spent = SpentLocked(check.Scope, check.Period, now);
                var reserved = _reserved.GetValueOrDefault(check.Scope);
                if (spent + reserved + amountUsd > check.CapUsd)
                    return new ReservationResult(false, check.BudgetId,
                        $"budget '{Evidence.Bounded(check.BudgetId)}' ({Evidence.Bounded(check.Scope)}, period {BudgetPeriods.PeriodId(check.Period, now)}) would be exceeded: "
                        + $"spent {Usd(spent)} + reserved {Usd(reserved)} + this attempt's bound {Usd(amountUsd)} > cap {Usd(check.CapUsd)} USD");
            }
            var record = new LedgerRecord
            {
                Op = LedgerOps.Reserve, RequestId = requestId, AttemptId = attemptId, Scopes = scopes.ToArray(), AmountUsd = amountUsd, TsUtc = now,
            };
            _store.Append([record]);
            Apply(record);
            return ReservationResult.Ok;
        }
    }

    /// <summary>Settles (request, attempt) exactly once with <paramref name="amountUsd"/> and releases the unused remainder
    /// in the same durable write. A second settle (or a settle after release) is ignored. A charge with no reservation
    /// (zero-bound attempt) is recorded against <paramref name="scopesIfUnreserved"/> and flagged as an overrun when &gt; 0.</summary>
    public SettleResult Settle(string requestId, string attemptId, decimal amountUsd, IReadOnlyList<string> scopesIfUnreserved)
    {
        lock (_gate)
        {
            var key = (requestId, attemptId);
            if (_closed.Contains(key)) return new SettleResult(false, 0m, 0m, false);
            var now = _clock.GetUtcNow();
            if (_open.TryGetValue(key, out var open))
            {
                bool overrun = amountUsd > open.Amount;
                var remainder = overrun ? 0m : open.Amount - amountUsd;
                var settle = new LedgerRecord
                {
                    Op = LedgerOps.Settle, RequestId = requestId, AttemptId = attemptId, Scopes = open.Scopes, AmountUsd = amountUsd, TsUtc = now,
                    Overrun = overrun ? true : null,
                };
                var release = new LedgerRecord
                {
                    Op = LedgerOps.Release, RequestId = requestId, AttemptId = attemptId, Scopes = open.Scopes, AmountUsd = remainder, TsUtc = now,
                };
                _store.Append([settle, release]);
                Apply(settle);
                Apply(release);
                return new SettleResult(true, amountUsd, open.Amount, overrun);
            }
            if (amountUsd <= 0m)
            {
                Close(key);
                return new SettleResult(true, 0m, 0m, false);
            }
            var direct = new LedgerRecord
            {
                Op = LedgerOps.Settle, RequestId = requestId, AttemptId = attemptId, Scopes = scopesIfUnreserved.ToArray(), AmountUsd = amountUsd, TsUtc = now,
                Overrun = true,
            };
            _store.Append([direct]);
            Apply(direct);
            return new SettleResult(true, amountUsd, 0m, true);
        }
    }

    /// <summary>Releases an open reservation whose attempt never started (no charge). Idempotent.</summary>
    public bool Release(string requestId, string attemptId)
    {
        lock (_gate)
        {
            var key = (requestId, attemptId);
            if (!_open.TryGetValue(key, out var open)) return false;
            var release = new LedgerRecord
            {
                Op = LedgerOps.Release, RequestId = requestId, AttemptId = attemptId, Scopes = open.Scopes, AmountUsd = open.Amount, TsUtc = _clock.GetUtcNow(),
            };
            _store.Append([release]);
            Apply(release);
            return true;
        }
    }

    /// <summary>Boot recovery (ADR-0014): every reservation still open (process crashed mid-attempt) is settled at its
    /// reserved amount — conservative, never free capacity. Returns what was recovered.</summary>
    public IReadOnlyList<LedgerRecord> RecoverUnsettled()
    {
        lock (_gate)
        {
            if (_open.Count == 0) return [];
            var now = _clock.GetUtcNow();
            var records = _open.Select(kv => new LedgerRecord
            {
                Op = LedgerOps.SettleRecovered, RequestId = kv.Key.Req, AttemptId = kv.Key.Att, Scopes = kv.Value.Scopes, AmountUsd = kv.Value.Amount, TsUtc = now,
            }).ToList();
            _store.Append(records);
            foreach (var r in records) Apply(r);
            return records;
        }
    }

    /// <summary>One-time legacy migration: pre-ledger spend (from the current audit segment) per team, charged to the
    /// global and team scopes in the current period.</summary>
    public IReadOnlyList<LedgerRecord> ImportLegacy(IEnumerable<(string Team, decimal AmountUsd)> spendByTeam)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var records = LegacyImportRecords(spendByTeam, now)
                .Where(r => !_closed.Contains((r.RequestId, r.AttemptId)))   // idempotent per scope key
                .ToList();
            if (records.Count == 0) return [];
            _store.Append(records);
            foreach (var r in records) Apply(r);
            return records;
        }
    }

    /// <summary>Settled amount for <paramref name="scope"/> in the period of <paramref name="period"/> containing now.</summary>
    public decimal Spent(string scope, BudgetPeriod period)
    {
        lock (_gate) return SpentLocked(scope, period, _clock.GetUtcNow());
    }

    public decimal Reserved(string scope)
    {
        lock (_gate) return _reserved.GetValueOrDefault(scope);
    }

    /// <summary>Scopes with a prefix (e.g. "team:") that have any settled spend in the period containing now.</summary>
    public IReadOnlyDictionary<string, decimal> SpentByScopePrefix(string prefix, BudgetPeriod period)
    {
        lock (_gate)
        {
            var bucket = BudgetPeriods.Bucket(period, _clock.GetUtcNow());
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in _spent)
                if (k.Bucket == bucket && k.Scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && v != 0m)
                    map[k.Scope] = v;
            return map;
        }
    }

    /// <summary>Newest-first settled charges for <paramref name="scope"/> inside the current window of <paramref name="period"/>
    /// (bounded to the most recent 20 000 charges kept in memory).</summary>
    public IReadOnlyList<LedgerContribution> Contributions(string scope, BudgetPeriod period, int limit)
    {
        lock (_gate)
        {
            var (start, end) = BudgetPeriods.Window(period, _clock.GetUtcNow());
            var list = new List<LedgerContribution>();
            for (var node = _contributions.Last; node is not null && list.Count < limit; node = node.Previous)
            {
                var (c, scopes) = node.Value;
                if (c.TsUtc >= start && c.TsUtc < end && scopes.Contains(scope, StringComparer.OrdinalIgnoreCase)) list.Add(c);
            }
            return list;
        }
    }

    private decimal SpentLocked(string scope, BudgetPeriod period, DateTimeOffset now)
        => _spent.GetValueOrDefault((scope, BudgetPeriods.Bucket(period, now)));

    /// <summary>Replays one record into memory (boot) or applies a just-appended one.</summary>
    private void Apply(LedgerRecord r)
    {
        var key = (r.RequestId, r.AttemptId);
        switch (r.Op)
        {
            case LedgerOps.Reserve:
                if (_open.ContainsKey(key) || _closed.Contains(key)) return;
                _open[key] = new Open(r.AmountUsd, r.Scopes, r.TsUtc);
                foreach (var s in r.Scopes) _reserved[s] = _reserved.GetValueOrDefault(s) + r.AmountUsd;
                return;
            case LedgerOps.Settle:
            case LedgerOps.SettleRecovered:
                if (_closed.Contains(key)) return;   // exactly once, also on replay
                Unreserve(key);
                Charge(r);
                Close(key);
                return;
            case LedgerOps.Release:
                Unreserve(key);
                Close(key);
                return;
            case LedgerOps.LegacyImport:
                if (_closed.Contains(key)) return;   // idempotent per (legacy-import, team:<t>) — B9
                Charge(r);
                Close(key);
                return;
            case LedgerOps.BootstrapComplete:
                return;   // marker only
        }
    }

    private void Unreserve((string, string) key)
    {
        if (!_open.Remove(key, out var open)) return;
        foreach (var s in open.Scopes)
        {
            var left = _reserved.GetValueOrDefault(s) - open.Amount;
            if (left <= 0m) _reserved.Remove(s); else _reserved[s] = left;
        }
    }

    private void Charge(LedgerRecord r)
    {
        if (r.AmountUsd == 0m) return;
        foreach (var s in r.Scopes)
            foreach (var period in AllPeriods)
            {
                var k = (s, BudgetPeriods.Bucket(period, r.TsUtc));
                _spent[k] = _spent.GetValueOrDefault(k) + r.AmountUsd;
            }
        _contributions.AddLast((new LedgerContribution(r.RequestId, r.AttemptId, r.Op, r.AmountUsd, r.TsUtc, r.Overrun == true), r.Scopes));
        while (_contributions.Count > MaxContributions) _contributions.RemoveFirst();
    }

    private void Close((string, string) key)
    {
        if (!_closed.Add(key)) return;
        _closedOrder.Enqueue(key);
        while (_closedOrder.Count > MaxClosedKeys) _closed.Remove(_closedOrder.Dequeue());
    }

    private static readonly BudgetPeriod[] AllPeriods = [BudgetPeriod.None, BudgetPeriod.Daily, BudgetPeriod.Monthly];

    private static string Usd(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

    private sealed class ScopeBucketComparer : IEqualityComparer<(string Scope, string Bucket)>
    {
        public static readonly ScopeBucketComparer Instance = new();
        public bool Equals((string Scope, string Bucket) x, (string Scope, string Bucket) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Scope, y.Scope) && StringComparer.Ordinal.Equals(x.Bucket, y.Bucket);
        public int GetHashCode((string Scope, string Bucket) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Scope), StringComparer.Ordinal.GetHashCode(obj.Bucket));
    }
}
