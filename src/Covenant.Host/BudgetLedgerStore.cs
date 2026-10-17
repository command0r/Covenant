using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

/// <summary>Result of opening the budget ledger journal at boot. Errors ⇒ refuse to start (fail-closed).</summary>
public sealed class LedgerOpenResult
{
    public List<LedgerRecord> Records { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>False when no journal file existed before this boot (⇒ the one-time bootstrap/legacy import runs).</summary>
    public bool Existed { get; init; }
}

/// <summary>Append-only JSONL journal of the budget ledger (ADR-0014), <c>Budget:LedgerPath</c> (default
/// <c>&lt;Audit:Path&gt;.ledger.jsonl</c>), independent of audit segments (an audit archive never touches it).
/// <para>Bootstrap (review round 2, B9): a ledger is born ATOMICALLY — the complete initial content (every legacy-import
/// line + a final <c>bootstrap-complete</c> marker) is written to a temp file, fsynced and renamed into place. A crash
/// before the rename leaves no ledger (the next boot simply bootstraps again); a journal that exists WITHOUT the marker is
/// an interrupted pre-marker bootstrap and refuses start — it never silently restores capacity. Exception (upgrade from
/// the unreleased pre-marker build): a journal that already contains post-bootstrap operations (reserve/settle/…) proves
/// the old bootstrap finished; it is accepted with a warning and the marker is appended.</para>
/// <para>Replay validation (B10): every complete line must parse AND pass <see cref="LedgerReplay"/> (op vocabulary,
/// non-negative amounts, ids, scopes, timestamps, lifecycle); any violation refuses start with the line number.</para>
/// <para>Torn-tail handling mirrors the configuration activation journal (ADR-0011): a final line without its newline
/// that does not parse (an interrupted append) is quarantined to <c>…ledger.jsonl.torn-&lt;utc&gt;</c> and the journal is
/// rewritten atomically with only the complete lines BEFORE anything can append; a complete last line whose newline never
/// landed is terminated. Appends are refused onto an unterminated line, and every append is flushed to disk before it
/// returns. A failed append halts the process (like a failed audit write): an appliance that cannot record spend must not
/// keep spending.</para></summary>
public sealed class FileLedgerStore : ILedgerStore
{
    private readonly object _gate = new();
    private bool _terminated;   // unknown until Open() verified the tail (or the first append checks it)

    public FileLedgerStore(string path) => Path = path;

    public string Path { get; }

    public LedgerOpenResult Open()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return new LedgerOpenResult { Existed = false };
            var result = new LedgerOpenResult { Existed = true };
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path));
            bool endsWithNewline = text.Length == 0 || text[^1] == '\n';
            var lines = text.Split('\n');
            int last = lines.Length - 1;
            while (last >= 0 && lines[last].Trim().Length == 0) last--;
            string? tornTail = null;
            var good = new StringBuilder();
            var lineNumbers = new List<int>();
            for (int i = 0; i <= last; i++)
            {
                var raw = lines[i];
                var line = raw.Trim();
                if (line.Length == 0) continue;
                LedgerRecord? rec = null;
                try { rec = JsonSerializer.Deserialize(line, LedgerJsonContext.Default.LedgerRecord); }
                catch (JsonException) { }
                if (rec is { Op: not null, RequestId: not null, AttemptId: not null, Scopes: not null })
                {
                    result.Records.Add(rec);
                    lineNumbers.Add(i + 1);
                    good.Append(line).Append('\n');
                    continue;
                }
                if (i == last && !endsWithNewline) tornTail = raw;
                else
                {
                    result.Errors.Add($"Budget:LedgerPath {Path} — line {i + 1} is corrupt (not a ledger record); accrued spend cannot be trusted. Restore the ledger from backup (never delete it).");
                    return result;
                }
            }

            // B10: semantic replay validation of every complete record — corrupt accounting refuses start, precisely.
            if (Covenant.Governance.LedgerReplay.Validate(result.Records) is { } violation)
            {
                result.Errors.Add($"Budget:LedgerPath {Path} — line {lineNumbers[violation.Index]} is invalid: {violation.Error}; accrued spend cannot be trusted. Restore the ledger from backup (never delete or hand-edit it).");
                return result;
            }

            // B9: the bootstrap-complete marker must be present.
            bool marked = result.Records.Any(r => r.Op == LedgerOps.BootstrapComplete);
            bool appendMarker = false;
            if (!marked)
            {
                bool postBootstrap = result.Records.Any(r => r.Op is not LedgerOps.LegacyImport);
                if (!postBootstrap)
                {
                    result.Errors.Add($"Budget:LedgerPath {Path} — the ledger has no bootstrap-complete marker: its one-time legacy import was interrupted "
                        + "(created but not completed). Refusing to start rather than silently restoring budget capacity. Move the file aside (keep it as evidence) "
                        + "and restart: the import is rebuilt atomically from the current audit segment.");
                    return result;
                }
                result.Warnings.Add("budget ledger: pre-marker journal with recorded operations accepted (its bootstrap evidently completed); bootstrap-complete marker appended");
                appendMarker = true;
            }

            if (tornTail is null && !endsWithNewline && last >= 0) tornTail = "";   // parsed, but its newline never landed
            if (tornTail is not null)
            {
                try
                {
                    if (tornTail.Length > 0)
                    {
                        var quarantine = $"{Path}.torn-{DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}";
                        WriteDurable(quarantine, Encoding.UTF8.GetBytes(tornTail));
                        result.Warnings.Add($"budget ledger: the last line was torn (interrupted append); preserved in {System.IO.Path.GetFileName(quarantine)} and removed from the journal");
                    }
                    var tmp = Path + ".tmp";
                    WriteDurable(tmp, Encoding.UTF8.GetBytes(good.ToString()));
                    File.Move(tmp, Path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"Budget:LedgerPath {Path} — torn tail could not be repaired ({ex.Message}); refusing to start.");
                    return result;
                }
            }
            _terminated = true;
            if (appendMarker)
            {
                var m = BudgetLedger.BootstrapMarker(DateTimeOffset.UtcNow);
                AppendLocked(Serialize([m]));
                result.Records.Add(m);
            }
            return result;
        }
    }

    /// <summary>Atomic bootstrap (B9): writes <paramref name="initial"/> (legacy-import records) plus the bootstrap-complete
    /// marker to <c>&lt;path&gt;.bootstrap.tmp</c>, fsyncs, then renames it into place. Refuses (throws) when a ledger
    /// already exists — never overwrites accounting.</summary>
    public void Bootstrap(IReadOnlyList<LedgerRecord> initial, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (File.Exists(Path)) throw new IOException($"budget ledger {Path} already exists; refusing to bootstrap over it");
            if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            var records = initial.Where(r => r.Op == LedgerOps.LegacyImport).Append(BudgetLedger.BootstrapMarker(now)).ToList();
            var tmp = Path + ".bootstrap.tmp";
            WriteDurable(tmp, Serialize(records));
            File.Move(tmp, Path, overwrite: false);
            _terminated = true;
        }
    }

    /// <summary>Creates an empty, COMPLETE journal (marker only) atomically — kept for callers that need a fresh ledger
    /// with no legacy spend.</summary>
    public void EnsureCreated()
    {
        if (File.Exists(Path)) return;
        Bootstrap([], DateTimeOffset.UtcNow);
    }

    public void Append(IReadOnlyList<LedgerRecord> records)
    {
        if (records.Count == 0) return;
        var bytes = Serialize(records);
        lock (_gate) AppendLocked(bytes);
    }

    private static byte[] Serialize(IReadOnlyList<LedgerRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var r in records) sb.Append(JsonSerializer.Serialize(r, LedgerJsonContext.Default.LedgerRecord)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private void AppendLocked(byte[] bytes)
    {
        try
        {
            // Defence in depth: never append onto an unterminated line (Open() repairs torn tails first).
            if (!_terminated && File.Exists(Path))
            {
                var info = new FileInfo(Path);
                if (info.Length > 0)
                {
                    using var check = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    check.Seek(-1, SeekOrigin.End);
                    if (check.ReadByte() != '\n')
                        throw new IOException("ledger does not end with a newline; refusing to append (restart to repair the torn tail)");
                }
            }
            _terminated = false;
            using (var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            _terminated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Environment.FailFast($"covenant: budget ledger write failed ({ex.Message}) — halting rather than spending unrecorded.");
        }
    }

    private static void WriteDurable(string path, byte[] content)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(content);
        fs.Flush(flushToDisk: true);
    }
}

/// <summary>Boot sequence of the ledger (B9), shared by Program and the tests: open; when no journal exists, build the
/// complete initial content from the current audit segment's recorded spend and bootstrap it atomically, then reopen.
/// Errors ⇒ the caller refuses to start.</summary>
public static class LedgerBootstrap
{
    public static LedgerOpenResult OpenOrBootstrap(FileLedgerStore store, Func<IEnumerable<(string Team, decimal AmountUsd)>> legacySpend, DateTimeOffset now)
    {
        var opened = store.Open();
        if (opened.Errors.Count > 0 || opened.Existed) return opened;
        try
        {
            store.Bootstrap(BudgetLedger.LegacyImportRecords(legacySpend(), now), now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failed = new LedgerOpenResult { Existed = false };
            failed.Errors.Add($"Budget:LedgerPath {store.Path} — the initial ledger could not be written atomically ({ex.Message}); refusing to start.");
            return failed;
        }
        var reopened = store.Open();
        return new LedgerOpenResult { Existed = false }.With(reopened);
    }

    private static LedgerOpenResult With(this LedgerOpenResult target, LedgerOpenResult source)
    {
        target.Records.AddRange(source.Records);
        target.Errors.AddRange(source.Errors);
        target.Warnings.AddRange(source.Warnings);
        return target;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LedgerRecord))]
public partial class LedgerJsonContext : JsonSerializerContext;

/// <summary>Budget state views (docs/ui-api-contract.md v2 "Budgets").</summary>
public sealed class BudgetStateView
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("period")] public required string Period { get; init; }
    [JsonPropertyName("period_id")] public required string PeriodId { get; init; }
    [JsonPropertyName("period_start_utc")] public DateTimeOffset? PeriodStartUtc { get; init; }
    [JsonPropertyName("period_end_utc")] public DateTimeOffset? PeriodEndUtc { get; init; }
    [JsonPropertyName("cap_usd")] public required decimal CapUsd { get; init; }
    [JsonPropertyName("spent_usd")] public required decimal SpentUsd { get; init; }
    [JsonPropertyName("reserved_usd")] public required decimal ReservedUsd { get; init; }
    [JsonPropertyName("remaining_usd")] public required decimal RemainingUsd { get; init; }
    [JsonPropertyName("warn_at")] public required List<decimal> WarnAt { get; init; }
    /// <summary>none | warn | critical | exceeded (status only — warnings never block; warn-only budgets never block).</summary>
    [JsonPropertyName("warning")] public required string Warning { get; init; }
    [JsonPropertyName("enforcement")] public required string Enforcement { get; init; }
}

public sealed class BudgetContributionView
{
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    [JsonPropertyName("attempt_id")] public required string AttemptId { get; init; }
    /// <summary>settle | settle-recovered | legacy-import.</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("amount_usd")] public required decimal AmountUsd { get; init; }
    [JsonPropertyName("ts_utc")] public required DateTimeOffset TsUtc { get; init; }
    [JsonPropertyName("overrun")] public bool Overrun { get; init; }
}

public sealed class BudgetRequestsView
{
    [JsonPropertyName("budget_id")] public required string BudgetId { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("period_id")] public required string PeriodId { get; init; }
    [JsonPropertyName("items")] public required List<BudgetContributionView> Items { get; init; }
}

/// <summary>Read side of the ledger for the admin API and status (spend at read time, by scope and UTC period).</summary>
public static class BudgetViews
{
    public static string PeriodName(BudgetPeriod p) => p switch { BudgetPeriod.Daily => "daily", BudgetPeriod.Monthly => "monthly", _ => "none" };

    public static BudgetStateView State(BudgetConfigV2 b, BudgetLedger ledger)
    {
        var now = ledger.Clock.GetUtcNow();
        var spent = ledger.Spent(b.Scope, b.Period);
        var reserved = ledger.Reserved(b.Scope);
        var (start, end) = BudgetPeriods.Window(b.Period, now);
        return new BudgetStateView
        {
            Id = b.Id,
            Scope = b.Scope,
            Period = PeriodName(b.Period),
            PeriodId = BudgetPeriods.PeriodId(b.Period, now),
            PeriodStartUtc = b.Period == BudgetPeriod.None ? null : start,
            PeriodEndUtc = b.Period == BudgetPeriod.None ? null : end,
            CapUsd = b.CapUsd,
            SpentUsd = spent,
            ReservedUsd = reserved,
            RemainingUsd = Math.Max(0m, b.CapUsd - spent - reserved),
            WarnAt = b.WarnAt.ToList(),
            Warning = Warning(b, spent),
            Enforcement = b.Enforcement == BudgetEnforcement.WarnOnly ? "warn-only" : "hard",
        };
    }

    /// <summary>exceeded when spent ≥ cap (with any spend); critical at the highest warn fraction; warn at the lowest.</summary>
    public static string Warning(BudgetConfigV2 b, decimal spent)
    {
        if (b.CapUsd <= 0m) return spent > 0m ? "exceeded" : "none";
        if (spent >= b.CapUsd) return "exceeded";
        var ratio = spent / b.CapUsd;
        var thresholds = b.WarnAt.OrderBy(x => x).ToList();
        if (thresholds.Count == 0) return "none";
        if (ratio >= thresholds[^1]) return "critical";
        if (ratio >= thresholds[0]) return "warn";
        return "none";
    }
}

/// <summary>GET /admin/budgets and /admin/budgets/{id}/requests (docs/ui-api-contract.md v2 "Budgets"). Caps are edited
/// through /admin/config (an audited activation); these endpoints are read-only views of the ledger.</summary>
public static class BudgetApi
{
    private static IResult Error(string error, string reason, int status)
        => Results.Json(new ErrorResponse { Error = error, Reason = reason }, CovenantJsonContext.Default.ErrorResponse, statusCode: status);

    public static void MapBudgets(this WebApplication app, IRuntimeConfig runtime, BudgetLedger ledger, Func<HttpContext, bool> authorized)
    {
        app.MapGet("/admin/budgets", (HttpContext http) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            var list = runtime.Current.Config.Budgets.Select(b => BudgetViews.State(b, ledger)).ToList();
            return Results.Json(list, CovenantJsonContext.Default.ListBudgetStateView);
        });

        app.MapGet("/admin/budgets/{id}/requests", (HttpContext http, string id, int? limit) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            var budget = runtime.Current.Config.Budgets.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));
            if (budget is null) return Error("not_found", "no budget with this id in the active configuration", StatusCodes.Status404NotFound);
            var items = ledger.Contributions(budget.Scope, budget.Period, Math.Clamp(limit ?? 100, 1, 1000))
                .Select(c => new BudgetContributionView
                {
                    RequestId = c.RequestId, AttemptId = c.AttemptId, Kind = c.Op, AmountUsd = c.AmountUsd, TsUtc = c.TsUtc, Overrun = c.Overrun,
                }).ToList();
            return Results.Json(new BudgetRequestsView
            {
                BudgetId = budget.Id,
                Scope = budget.Scope,
                PeriodId = BudgetPeriods.PeriodId(budget.Period, ledger.Clock.GetUtcNow()),
                Items = items,
            }, CovenantJsonContext.Default.BudgetRequestsView);
        });
    }
}
