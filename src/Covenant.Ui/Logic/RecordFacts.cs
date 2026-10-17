using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Review-round-2 facts of one audit record (docs/ui-api-contract.md "Additive v3 fields"): the requested vs
/// the effective (enforced) output bound, truncation at the bound, a stream that ended without a terminal completion
/// (<c>incomplete_stream</c>) and the connection that actually served. Older records lack these fields → "Not
/// recorded", never a guess.</summary>
public static class RecordFacts
{
    public const string NotRecorded = "Not recorded";
    public const string IncompleteStream = "incomplete_stream";

    /// <summary>True when the record carries any review-round-2 field, so an absent requested bound means "none
    /// requested" rather than "older record".</summary>
    public static bool HasBoundFields(AuditEntryView r)
        => r.EffectiveMaxOutputTokens is not null || r.RequestedMaxOutputTokens is not null || r.PartialOutputChars is not null
           || r.ServedConnectionId is not null || r.Attempts?.Any(a => a.EffectiveMaxOutputTokens is not null || a.Truncated is not null) == true;

    /// <summary>"256", "None requested — application/model bound applies", or "Not recorded" (older record).</summary>
    public static string RequestedMaxOutputTokens(AuditEntryView r)
    {
        if (r.RequestedMaxOutputTokens is { } n) return Int(n);
        return HasBoundFields(r) ? "None requested — the application/model bound applies" : NotRecorded;
    }

    /// <summary>The bound Covenant sent to the provider for the SERVED attempt; for a request nothing served, the last
    /// started attempt's bound (labelled). "Not recorded" for older records or when no attempt started.</summary>
    public static string EffectiveMaxOutputTokens(AuditEntryView r)
    {
        if (r.EffectiveMaxOutputTokens is { } n) return Int(n);
        if (r.Attempts is { Count: > 0 } attempts && attempts[^1].EffectiveMaxOutputTokens is { } last)
            return Int(last) + " (last attempt — not served)";
        return NotRecorded;
    }

    /// <summary>"Requested 256 · enforced 64" — the compact form for one line.</summary>
    public static string BoundsLine(AuditEntryView r)
        => "Requested " + RequestedMaxOutputTokens(r) + " · enforced " + EffectiveMaxOutputTokens(r);

    /// <summary>The attempt that served finished with "length" (output cut at the enforced bound).</summary>
    public static bool Truncated(AuditEntryView r)
        => r.Attempts?.Any(a => a.Truncated == true && AttemptFacts.IsSuccess(a)) == true;

    /// <summary>"Yes — …" / "No" / "Not recorded" for the drawer.</summary>
    public static string TruncatedText(AuditEntryView r)
    {
        if (Truncated(r))
            return "Yes — the provider stopped at the enforced output bound (finish_reason length); the answer is cut short";
        return r.Attempts is { Count: > 0 } && HasBoundFields(r) ? "No" : NotRecorded;
    }

    /// <summary>A stream that ended without the provider's terminal completion (review round 2, B1): never served.</summary>
    public static bool IsIncompleteStream(AuditEntryView r)
        => r.Attempts?.Any(a => string.Equals(a.ErrorClass, IncompleteStream, StringComparison.Ordinal)) == true;

    /// <summary>Plain-language explanation of an incomplete stream, with the partial output the client already saw.</summary>
    public static string? IncompleteStreamText(AuditEntryView r)
    {
        if (!IsIncompleteStream(r)) return null;
        var partial = r.PartialOutputChars is { } c
            ? " " + Int(c) + " characters had already been streamed to the client before it ended."
            : "";
        return "The provider stream ended without a terminal completion (incomplete_stream): the request is NOT served, never cached and not continued by a fallback; the attempt was charged its estimate." + partial;
    }

    /// <summary>"N characters" emitted before a stream failed, or "None" / "Not recorded".</summary>
    public static string PartialOutput(AuditEntryView r)
        => r.PartialOutputChars is { } c ? Int(c) + " characters streamed before the failure" : NotRecorded;

    /// <summary>The connection of the attempt that served (a fallback when one served; a cache hit's ORIGINAL
    /// provenance), or "Not recorded".</summary>
    public static string ServedConnection(AuditEntryView r)
        => string.IsNullOrWhiteSpace(r.ServedConnectionId) ? NotRecorded
            : r.CacheHit ? r.ServedConnectionId + " (produced the cached entry)" : r.ServedConnectionId;

    private static string Int(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
