using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Covenant.Core;

namespace Covenant.Host;

/// <summary>One audit segment: the current log or an archived sibling (spec §5). The id is opaque and server-generated;
/// a caller can only ever name a segment from the enumerated set — never a path.</summary>
public sealed record AuditSegment(string Id, string Label, bool Current, string FilePath, string? AnchorPath, DateTimeOffset? ArchivedUtc);

public sealed class SegmentView
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("current")] public required bool Current { get; init; }
    [JsonPropertyName("entries")] public required int Entries { get; init; }
    [JsonPropertyName("events")] public int Events { get; init; }
    [JsonPropertyName("first_utc")] public DateTimeOffset? FirstUtc { get; init; }
    [JsonPropertyName("last_utc")] public DateTimeOffset? LastUtc { get; init; }
    [JsonPropertyName("chain_valid")] public required bool ChainValid { get; init; }
    [JsonPropertyName("archived_utc")] public DateTimeOffset? ArchivedUtc { get; init; }
}

public sealed class ExportIntegrity
{
    [JsonPropertyName("segment_id")] public required string SegmentId { get; init; }
    [JsonPropertyName("chain_valid")] public required bool ChainValid { get; init; }
    [JsonPropertyName("chain_failure")] public string? ChainFailure { get; init; }
    /// <summary>1-based line of the record in its segment.</summary>
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("previous_hash")] public string? PreviousHash { get; init; }
    [JsonPropertyName("entry_hash")] public string? EntryHash { get; init; }
    [JsonPropertyName("anchor_configured")] public required bool AnchorConfigured { get; init; }
    /// <summary>"verified" (record inside a valid chain) | "unverified" (chain broken — do not trust).</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
}

/// <summary>GET /admin/requests/{id}/export: a self-contained evidence export of one request. Never prompt/response content:
/// the opt-in PromptPreview is removed; the prompt SHA-256 fingerprint stays (it proves which prompt, not what).</summary>
public sealed class RequestExport
{
    [JsonPropertyName("exported_utc")] public required DateTimeOffset ExportedUtc { get; init; }
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    [JsonPropertyName("segment")] public required SegmentView Segment { get; init; }
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; init; }
    [JsonPropertyName("record")] public required AuditEntry Record { get; init; }
    [JsonPropertyName("attempts")] public IReadOnlyList<AuditAttempt>? Attempts { get; init; }
    [JsonPropertyName("trace")] public IReadOnlyList<AuditTraceStep>? Trace { get; init; }
    [JsonPropertyName("integrity")] public required ExportIntegrity Integrity { get; init; }
    [JsonPropertyName("content_included")] public bool ContentIncluded { get; init; }
}

/// <summary>Enumerates audit segments: the current file plus <c>&lt;audit file&gt;.yyyyMMdd-HHmmss.archived</c> siblings in the same
/// directory (the names <see cref="FileAuditSink.RotateAsync"/> produces). Ids: "current", or "a-" + 16 hex of SHA-256 over
/// the archive's file name. Cursors are opaque base64url tokens bound to one segment.</summary>
public sealed partial class AuditSegments(string auditPath, string? anchorPath)
{
    public const string CurrentId = "current";

    [GeneratedRegex(@"^\d{8}-\d{6}$")]
    private static partial Regex StampPattern();

    public IReadOnlyList<AuditSegment> List()
    {
        var full = Path.GetFullPath(auditPath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        var list = new List<AuditSegment> { new(CurrentId, "Current segment", true, full, anchorPath, null) };
        if (!Directory.Exists(dir)) return list;
        var archived = new List<AuditSegment>();
        foreach (var file in Directory.EnumerateFiles(dir, name + ".*.archived"))
        {
            var fileName = Path.GetFileName(file);
            var stamp = fileName[(name.Length + 1)..^".archived".Length];
            if (!StampPattern().IsMatch(stamp)) continue;   // e.g. an anchor archive that shares the prefix — not a segment
            DateTimeOffset? at = DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? new DateTimeOffset(t, TimeSpan.Zero) : null;
            string? anchors = anchorPath is not null && File.Exists($"{anchorPath}.{stamp}.archived") ? $"{anchorPath}.{stamp}.archived" : null;
            var id = "a-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName))).ToLowerInvariant()[..16];
            var label = at is { } a ? $"Archived {a.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC" : "Archived segment";
            archived.Add(new AuditSegment(id, label, false, file, anchors, at));
        }
        list.AddRange(archived.OrderByDescending(s => s.ArchivedUtc ?? DateTimeOffset.MinValue).ThenByDescending(s => s.FilePath, StringComparer.Ordinal));
        return list;
    }

    /// <summary>Only ids from the enumerated set resolve (null ⇒ current). Anything else — a path, "..", an unknown id — is null.</summary>
    public AuditSegment? Resolve(string? id)
    {
        var all = List();
        if (string.IsNullOrEmpty(id)) return all[0];
        return all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    public static (ChainVerification Verification, IReadOnlyList<AuditEntry> Entries) Read(AuditSegment segment)
        => AuditChainVerifier.VerifyAndRead(segment.FilePath, segment.AnchorPath);

    public static SegmentView View(AuditSegment segment, ChainVerification verification, IReadOnlyList<AuditEntry> all)
    {
        var requests = all.Where(e => e.IsRequest()).ToList();
        return new SegmentView
        {
            Id = segment.Id,
            Label = segment.Label,
            Current = segment.Current,
            Entries = requests.Count,
            Events = all.Count - requests.Count,
            FirstUtc = requests.Count > 0 ? requests[0].TimestampUtc : null,
            LastUtc = requests.Count > 0 ? requests[^1].TimestampUtc : null,
            ChainValid = verification.Valid,
            ArchivedUtc = segment.ArchivedUtc,
        };
    }

    public static string Cursor(string segmentId, int offset)
        => Base64Url(Encoding.UTF8.GetBytes($"c1|{segmentId}|{offset.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>Opaque newest-first position: (segment id, number of newest items already returned). Null when malformed.</summary>
    public static (string SegmentId, int Offset)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 200) return null;
        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(s)).Split('|');
            if (parts.Length != 3 || parts[0] != "c1") return null;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0) return null;
            return (parts[1], offset);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Hashes and 1-based line of the request record <paramref name="id"/> in <paramref name="segment"/> (last match).</summary>
    public static (int Line, string PreviousHash, string EntryHash)? Locate(AuditSegment segment, string id)
    {
        if (!File.Exists(segment.FilePath)) return null;
        (int, string, string)? found = null;
        int lineNo = 0;
        using var fs = new FileStream(segment.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lineNo++;
            var parts = line.Split(AuditChain.Separator, 3);
            if (parts.Length != 3 || !parts[2].Contains(id, StringComparison.Ordinal)) continue;
            AuditEntry? e;
            try { e = JsonSerializer.Deserialize(parts[2], AuditJsonContext.Default.AuditEntry); }
            catch (JsonException) { continue; }
            if (e is not null && e.IsRequest() && e.Id == id) found = (lineNo, parts[0], parts[1]);
        }
        return found;
    }
}

/// <summary>Archive navigation and evidence export (docs/ui-api-contract.md v2 "Requests archive").</summary>
public static class ArchiveApi
{
    private static IResult Error(string error, string reason, int status)
        => Results.Json(new ErrorResponse { Error = error, Reason = reason }, CovenantJsonContext.Default.ErrorResponse, statusCode: status);

    public static void MapArchive(this WebApplication app, AuditSegments segments, Func<HttpContext, bool> authorized, string? anchorPath)
    {
        app.MapGet("/admin/audit/segments", (HttpContext http) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            var views = segments.List().Select(s =>
            {
                var (v, entries) = AuditSegments.Read(s);
                return AuditSegments.View(s, v, entries);
            }).ToList();
            return Results.Json(views, AuditJsonContext.Default.ListSegmentView);
        });

        // Newest-first records of ONE segment (current by default), bounded, with an opaque cursor for the next page.
        app.MapGet("/admin/requests", (HttpContext http, int? limit, string? cursor, string? segment) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            int offset = 0;
            string? segmentId = segment;
            if (cursor is not null)
            {
                if (AuditSegments.ParseCursor(cursor) is not { } c)
                    return Error("invalid_cursor", "cursor is malformed (use next_cursor from a previous page)", StatusCodes.Status400BadRequest);
                if (segmentId is not null && segmentId != c.SegmentId)
                    return Error("invalid_cursor", "cursor belongs to another segment", StatusCodes.Status400BadRequest);
                segmentId = c.SegmentId;
                offset = c.Offset;
            }
            if (segments.Resolve(segmentId) is not { } seg)
                return Error("unknown_segment", "segment must be an id from GET /admin/audit/segments", StatusCodes.Status400BadRequest);

            var (verification, all) = AuditSegments.Read(seg);
            var entries = all.Where(e => e.IsRequest()).ToList();   // v3: control-plane events → GET /admin/config/versions
            int take = Math.Clamp(limit ?? 200, 1, 1000);
            var items = Enumerable.Reverse(entries).Skip(offset).Take(take).ToList();
            var view = AuditSegments.View(seg, verification, all);
            var page = new RequestsPage
            {
                Segment = new SegmentInfo
                {
                    Path = seg.FilePath,
                    Entries = view.Entries,
                    ChainValid = view.ChainValid,
                    FirstUtc = view.FirstUtc,
                    LastUtc = view.LastUtc,
                    Events = view.Events,
                    Id = seg.Id,
                    Label = seg.Label,
                },
                Items = items,
                NextCursor = offset + items.Count < entries.Count ? AuditSegments.Cursor(seg.Id, offset + items.Count) : null,
            };
            return Results.Json(page, AuditJsonContext.Default.RequestsPage);
        });

        app.MapGet("/admin/requests/{id}/export", (HttpContext http, string id, string? segment) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            http.Response.Headers.CacheControl = "no-store";
            if (!RequestCorrelation.IsWellFormed(id))
                return Error("invalid_id", "request id must be ≤64 chars of [A-Za-z0-9_-]", StatusCodes.Status400BadRequest);
            IEnumerable<AuditSegment> search;
            if (segment is not null)
            {
                if (segments.Resolve(segment) is not { } only)
                    return Error("unknown_segment", "segment must be an id from GET /admin/audit/segments", StatusCodes.Status400BadRequest);
                search = [only];
            }
            else search = segments.List();

            foreach (var seg in search)
            {
                var (verification, all) = AuditSegments.Read(seg);
                AuditEntry? entry = null;
                for (int i = all.Count - 1; i >= 0; i--)
                    if (all[i].IsRequest() && all[i].Id == id) { entry = all[i]; break; }
                if (entry is null) continue;
                var located = AuditSegments.Locate(seg, id);
                var export = new RequestExport
                {
                    ExportedUtc = DateTimeOffset.UtcNow,
                    RequestId = id,
                    Segment = AuditSegments.View(seg, verification, all),
                    ConfigVersion = entry.ConfigVersion,
                    Record = entry with { PromptPreview = null },   // default export excludes content
                    Attempts = entry.Attempts,
                    Trace = entry.Trace,
                    Integrity = new ExportIntegrity
                    {
                        SegmentId = seg.Id,
                        ChainValid = verification.Valid,
                        ChainFailure = verification.Failure,
                        Line = located?.Line,
                        PreviousHash = located?.PreviousHash,
                        EntryHash = located?.EntryHash,
                        AnchorConfigured = seg.AnchorPath is not null || (seg.Current && anchorPath is not null),
                        Status = verification.Valid ? "verified" : "unverified",
                    },
                    ContentIncluded = false,
                };
                return Results.Json(export, AuditJsonContext.Default.RequestExport);
            }
            return Error("not_found", "no request record with this id in any audit segment", StatusCodes.Status404NotFound);
        });
    }
}
