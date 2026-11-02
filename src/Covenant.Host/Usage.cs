using System.Globalization;
using System.Text.Json.Serialization;
using Covenant.Core;

namespace Covenant.Host;

// Usage (demo journey §3): an authenticated, read-only aggregation of RECORDED evidence (the audit segment) — never a second
// source of truth, never prompt content. One segment per call (current by default), a bounded time window, admin-only and
// off the request hot path. The segment file is size-checked BEFORE it is read: a segment above MaxSegmentBytes is refused
// (rotate/archive and select a smaller one) — there is no silent partial scan. Local/external is judged with the
// configuration version each record was bound to (memoized per report); a version that cannot be loaded is "unknown". Charges are summed from the per-attempt and per-tool-call
// records exactly once (the request-level total is NOT added again). Unknown is counted as unknown, never as $0.

public sealed class UsageQuery
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    /// <summary>team | application | profile</summary>
    public string GroupBy { get; init; } = "team";
}

public sealed class UsageReport
{
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required string GroupBy { get; init; }
    public required string SegmentId { get; init; }
    public required string SegmentLabel { get; init; }
    public required bool ChainValid { get; init; }
    public DateTimeOffset? SegmentFirstUtc { get; init; }
    public DateTimeOffset? SegmentLastUtc { get; init; }
    /// <summary>Request records read from the segment (all of them) before the window filter.</summary>
    public required int ScannedRecords { get; init; }
    /// <summary>Size of the segment file that was read (bytes); always ≤ <see cref="UsageAggregator.MaxSegmentBytes"/>.</summary>
    public required long SegmentBytes { get; init; }
    /// <summary>Records whose configuration version could not be loaded, so their call boundaries are unknown.</summary>
    public required int UnknownVersionRecords { get; init; }
    public required UsageRow Totals { get; init; }
    public required List<UsageRow> Groups { get; init; }
    public required List<string> Limitations { get; init; }
}

public sealed class UsageRow
{
    public required string Key { get; init; }
    public int Requests { get; set; }
    public int Served { get; set; }
    /// <summary>Refused by governance (auth, shape, policy, redaction, rate, pause, budget, search permission).</summary>
    public int Refused { get; set; }
    /// <summary>Provider or search failures after admission.</summary>
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public int CacheHits { get; set; }
    /// <summary>Provider attempts actually started, by the connection's boundary in the configuration version the record was bound to.</summary>
    public int LocalCalls { get; set; }
    public int ExternalCalls { get; set; }
    public int UnknownBoundaryCalls { get; set; }
    /// <summary>External retrievals (web search tool calls actually made).</summary>
    public int ExternalRetrievals { get; set; }
    /// <summary>The detector found a sensitive signal (e.g. an SSN pattern) — content-based.</summary>
    public int SensitiveDetected { get; set; }
    /// <summary>The workload's trusted baseline label is PII/PHI — configuration-based, not a detection.</summary>
    public int SensitiveBaseline { get; set; }
    public int RedactionsApplied { get; set; }
    public int RedactionsRefused { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>Known provider charges (reported or estimated), USD.</summary>
    public decimal InferenceChargeUsd { get; set; }
    /// <summary>Known web-search tool charges, USD.</summary>
    public decimal SearchChargeUsd { get; set; }
    public decimal TotalChargeUsd => InferenceChargeUsd + SearchChargeUsd;
    /// <summary>Requests containing at least one estimated charge component.</summary>
    public int EstimatedChargeRequests { get; set; }
    /// <summary>Requests containing at least one charge component whose cost is unknown (not counted as $0).</summary>
    public int UnknownChargeRequests { get; set; }
    public List<UsageDay> Daily { get; set; } = [];
}

public sealed class UsageDay
{
    public required string Date { get; init; }
    public int Requests { get; set; }
    public decimal ChargeUsd { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = false)]
[JsonSerializable(typeof(UsageReport))]
public partial class UsageJsonContext : JsonSerializerContext;

public static class UsageAggregator
{
    /// <summary>On-demand read bound: larger segments are refused before reading (never partially scanned).</summary>
    public const long MaxSegmentBytes = 64L * 1024 * 1024;
    public const int MaxDays = 62;

    public static readonly IReadOnlyList<string> GroupKinds = ["team", "application", "profile"];

    public static List<string> Limitations(int unknownVersionRecords) =>
    [
        "Built from the selected audit segment only, read in full on demand (segments above 64 MiB are refused — rotate and select a smaller one); earlier segments are separate.",
        "Charges are estimates: reported usage × configured price, or a conservative estimate when usage was not reported. Unknown charges are counted separately, never as $0.",
        "Local calls are charged at their configured price (often $0 provider charge); infrastructure, hardware and energy are excluded — local is not free.",
        "Local/external is the connection's boundary in the configuration version each request was bound to; when that version cannot be loaded (or the record predates per-attempt evidence) the call is counted as unknown.",
        "'Sensitive detected' counts detector signals in content; 'sensitive baseline' counts requests whose workload is labelled PII/PHI. They are different facts.",
        "Usage per team is an operational signal for budgeting and governance, not evidence about any individual's conduct.",
        .. (unknownVersionRecords > 0 ? new[] { $"{unknownVersionRecords:N0} record(s) reference a configuration version that could not be loaded; their call boundaries are unknown." } : Array.Empty<string>()),
    ];

    /// <summary>Pure aggregation over request records (control-plane events excluded) inside [From, To).</summary>
    /// <param name="boundaryOf">(connection id, the record's configuration version) → boundary in THAT version; null = unknown.</param>
    public static (UsageRow Totals, List<UsageRow> Groups) Aggregate(IEnumerable<AuditEntry> records, UsageQuery q, Func<string, int?, Boundary?> boundaryOf)
    {
        var totals = new UsageRow { Key = "all" };
        var groups = new Dictionary<string, UsageRow>(StringComparer.Ordinal);
        foreach (var e in records)
        {
            if (!e.IsRequest() || e.TimestampUtc < q.From || e.TimestampUtc >= q.To) continue;
            var key = q.GroupBy switch
            {
                "application" => e.ApplicationId ?? "not recorded",
                "profile" => e.ProfileId ?? "not recorded",
                _ => string.IsNullOrWhiteSpace(e.Tags.Team) ? "not recorded" : e.Tags.Team,
            };
            if (!groups.TryGetValue(key, out var row)) groups[key] = row = new UsageRow { Key = key };
            Add(totals, e, boundaryOf);
            Add(row, e, boundaryOf);
        }
        var ordered = groups.Values.OrderByDescending(r => r.TotalChargeUsd).ThenByDescending(r => r.Requests).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        return (totals, ordered);
    }

    private static void Add(UsageRow r, AuditEntry e, Func<string, int?, Boundary?> boundaryOf)
    {
        r.Requests++;
        bool served = e.Effect == PolicyEffect.Allow;
        if (served) r.Served++;
        else if (e.DenialKind == nameof(DenialKind.Cancelled)) r.Cancelled++;
        else if (e.DenialKind == nameof(DenialKind.UpstreamFailure)) r.Failed++;
        else r.Refused++;
        if (e.CacheHit) r.CacheHits++;
        if (e.Signal is not null && e.Classification >= DataClassification.Pii) r.SensitiveDetected++;
        if (e.BaselineClassification is { } b && b >= DataClassification.Pii) r.SensitiveBaseline++;
        if (e.Redaction is { } red)
        {
            if (red.Outcome == "applied") r.RedactionsApplied++;
            else r.RedactionsRefused++;
        }
        r.InputTokens += e.Usage.InputTokens;
        r.OutputTokens += e.Usage.OutputTokens;

        bool estimated = false, unknown = false;
        decimal inference = 0m, search = 0m;
        if (e.Attempts is { } attempts)
        {
            foreach (var a in attempts)
            {
                switch (boundaryOf(a.ConnectionId, e.ConfigVersion))
                {
                    case Boundary.Local: r.LocalCalls++; break;
                    case Boundary.Cloud: r.ExternalCalls++; break;
                    default: r.UnknownBoundaryCalls++; break;
                }
                Charge(a.CostUsd, a.CostBasis, ref inference, ref estimated, ref unknown);
            }
        }
        else if (!e.CacheHit && (e.AttemptedModel is not null || e.ServedByModel is not null || e.Usage.CostUsd != 0m
                                 || (e.CostBasis is { } lb && lb != "no_call")))
        {
            // Legacy (pre-attempt) record that made a call: one call of unknown boundary, its request-level charge (a served
            // record's Usage.CostUsd), never a silent zero; a missing basis on a served legacy record is "reported".
            r.UnknownBoundaryCalls++;
            Charge(e.Usage.CostUsd, e.CostBasis ?? (e.Effect == PolicyEffect.Allow ? "reported" : "unknown_failed_call"), ref inference, ref estimated, ref unknown);
        }
        foreach (var t in e.ToolCalls ?? Array.Empty<AuditToolCall>())
        {
            if (t.CostBasis != "no_call") r.ExternalRetrievals++;
            Charge(t.CostUsd, t.CostBasis, ref search, ref estimated, ref unknown);
        }
        r.InferenceChargeUsd += inference;
        r.SearchChargeUsd += search;
        if (estimated) r.EstimatedChargeRequests++;
        if (unknown) r.UnknownChargeRequests++;

        var date = e.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = r.Daily.FirstOrDefault(d => d.Date == date);
        if (day is null)
        {
            if (r.Daily.Count >= MaxDays) return;
            r.Daily.Add(day = new UsageDay { Date = date });
            r.Daily.Sort((x, y) => string.CompareOrdinal(x.Date, y.Date));
        }
        day.Requests++;
        day.ChargeUsd += inference + search;
    }

    private static void Charge(decimal? cost, string? basis, ref decimal sum, ref bool estimated, ref bool unknown)
    {
        if (cost is null || basis is "unknown_failed_call" or "unpriced") { unknown = true; return; }
        sum += cost.Value;
        if (basis is not null && basis.StartsWith("estimated", StringComparison.Ordinal)) estimated = true;
    }

    public static (UsageQuery? Query, string? Error) Parse(string? from, string? to, string? window, string? group, DateTimeOffset now)
    {
        var groupBy = string.IsNullOrWhiteSpace(group) ? "team" : group.Trim().ToLowerInvariant();
        if (!GroupKinds.Contains(groupBy)) return (null, "group must be team, application or profile");
        DateTimeOffset t = now, f;
        if (to is not null && !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t))
            return (null, "to must be an ISO-8601 timestamp");
        if (from is not null)
        {
            if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out f))
                return (null, "from must be an ISO-8601 timestamp");
        }
        else
        {
            TimeSpan? span = (window ?? "24h") switch
            {
                "1h" => TimeSpan.FromHours(1),
                "24h" => TimeSpan.FromDays(1),
                "7d" => TimeSpan.FromDays(7),
                "30d" => TimeSpan.FromDays(30),
                "all" => TimeSpan.MaxValue,
                _ => null,
            };
            if (span is not { } back) return (null, "window must be 1h, 24h, 7d, 30d or all");
            // Never underflow: a relative window that would start before the earliest representable time starts there
            // (e.g. to=0001-01-01 with 7d) instead of throwing.
            f = t.UtcTicks - DateTimeOffset.MinValue.UtcTicks <= back.Ticks ? DateTimeOffset.MinValue : t.Subtract(back);
        }
        if (f >= t) return (null, "from must be before to");
        return (new UsageQuery { From = f, To = t, GroupBy = groupBy }, null);
    }

    /// <summary>(connection, version) → boundary, loading each configuration version at most once per report.</summary>
    public static Func<string, int?, Boundary?> VersionedBoundaries(Func<int, CovenantConfig?> versionOf)
    {
        var memo = new Dictionary<int, CovenantConfig?>();
        return (connectionId, version) =>
        {
            if (version is not { } v) return null;
            if (!memo.TryGetValue(v, out var cfg)) memo[v] = cfg = versionOf(v);
            return cfg?.Connections.FirstOrDefault(c => string.Equals(c.Id, connectionId, StringComparison.Ordinal))?.Boundary;
        };
    }

    /// <summary>The size check that runs BEFORE any read: null when the file may be read; else the refusal reason.</summary>
    public static string? SizeRefusal(string path, long cap, out long bytes)
    {
        bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        return bytes > cap
            ? $"this segment is {bytes:N0} bytes; usage reads at most {cap:N0} bytes on demand — rotate the audit log and select a smaller segment"
            : null;
    }

    public static void MapUsage(this WebApplication app, AuditSegments segments, Func<int, CovenantConfig?> versionOf, Func<HttpContext, bool> authorized,
        long maxSegmentBytes = MaxSegmentBytes)
    {
        app.MapGet("/admin/usage", (HttpContext http, string? from, string? to, string? window, string? group, string? segment) =>
        {
            if (!authorized(http))
                return Results.Json(new ErrorResponse { Error = "unauthorized", Reason = "invalid admin token" }, CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            var now = DateTimeOffset.UtcNow;
            var (query, error) = Parse(from, to, window, group, now);
            if (query is null)
                return Results.Json(new ErrorResponse { Error = "invalid_request", Reason = error! }, CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            if (segments.Resolve(segment) is not { } seg)
                return Results.Json(new ErrorResponse { Error = "unknown_segment", Reason = "segment must be an id from GET /admin/audit/segments" }, CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status400BadRequest);

            if (SizeRefusal(seg.FilePath, maxSegmentBytes, out long bytes) is { } tooLarge)
                return Results.Json(new ErrorResponse { Error = "segment_too_large", Reason = tooLarge },
                    CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status413PayloadTooLarge);

            var (verification, all) = AuditSegments.Read(seg);
            var view = AuditSegments.View(seg, verification, all);
            var requests = all.Where(e => e.IsRequest()).ToList();
            var memo = new Dictionary<int, bool>();
            var boundaries = VersionedBoundaries(v => { var c = versionOf(v); memo[v] = c is not null; return c; });
            var (totals, groups) = Aggregate(requests, query, boundaries);
            int unknownVersions = requests.Count(e => e.TimestampUtc >= query.From && e.TimestampUtc < query.To
                                                      && e.ConfigVersion is { } v && memo.TryGetValue(v, out var ok) && !ok);
            var report = new UsageReport
            {
                GeneratedUtc = now,
                From = query.From == DateTimeOffset.MinValue ? (view.FirstUtc ?? query.From) : query.From,
                To = query.To,
                GroupBy = query.GroupBy,
                SegmentId = seg.Id,
                SegmentLabel = seg.Label,
                ChainValid = view.ChainValid,
                SegmentFirstUtc = view.FirstUtc,
                SegmentLastUtc = view.LastUtc,
                ScannedRecords = requests.Count,
                SegmentBytes = bytes,
                UnknownVersionRecords = unknownVersions,
                Totals = totals,
                Groups = groups,
                Limitations = Limitations(unknownVersions),
            };
            return Results.Json(report, UsageJsonContext.Default.UsageReport);
        });
    }
}
