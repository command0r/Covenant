using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Scope wording for request lists (spec §5 Requests): which audit segment a list covers (from the Host's
/// descriptor), and that filtering applies only to what was loaded — never "all history".</summary>
public static class SegmentScope
{
    public const string CurrentId = "current";

    /// <summary>The Host's explicit flag (SegmentView.current) — never inferred from the id or the list order.</summary>
    public static bool IsCurrent(AuditSegmentDto seg) => seg.Current;

    /// <summary>"Current segment · since 2026-10-16 09:00:00 UTC" or "Archived segment 2026-10-01 · read-only".</summary>
    public static string Label(AuditSegmentDto seg) => Label(seg.Current, seg.Label, seg.Id, seg.FirstUtc);

    /// <summary>Scope of a requests PAGE, from the page's own segment descriptor (Host SegmentInfo: id, label,
    /// is_current) — never from the first row or from what the console asked for.</summary>
    public static string Label(SegmentInfo seg) => Label(seg.IsCurrent, seg.Label, seg.Id, seg.FirstUtc);

    private static string Label(bool current, string? label, string? id, DateTimeOffset? firstUtc)
    {
        if (current) return "Current segment" + (firstUtc is { } f ? " · since " + Utc(f) : "");
        var name = !string.IsNullOrWhiteSpace(label) ? label! : !string.IsNullOrWhiteSpace(id) ? id! : "(unidentified)";
        return "Archived segment " + name + " · read-only";
    }

    /// <summary>Selector option text with the entry count.</summary>
    public static string Option(AuditSegmentDto seg)
    {
        var label = seg.Current ? "Current segment" : "Archived · " + (string.IsNullOrWhiteSpace(seg.Label) ? seg.Id : seg.Label);
        return label + " (" + seg.Entries.ToString("N0", CultureInfo.InvariantCulture) + " records)";
    }

    /// <summary>The coverage sentence above the table.</summary>
    public static string Coverage(string scopeLabel, int loaded, int? segmentEntries, bool moreAvailable, bool filtersActive)
    {
        var n = loaded.ToString("N0", CultureInfo.InvariantCulture);
        var of = segmentEntries is { } total && total > loaded ? " of " + total.ToString("N0", CultureInfo.InvariantCulture) : "";
        var more = moreAvailable ? " Older records: use “Load older”." : "";
        var filter = filtersActive ? " Filters apply only to these loaded records, not to the whole segment or other segments." : "";
        return scopeLabel + ": " + n + of + " records loaded, newest first." + more + filter;
    }

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}

public enum EvidenceCompletenessState { Pending, Unavailable, Recorded, Verified, Unverifiable }

/// <summary>Evidence completeness for one request (spec §4.3/§5 Audit): Pending while the async audit write may
/// still land, Unavailable after the bounded wait, Verified only when the record exists AND its segment's chain
/// verified. A record in a segment whose chain fails is "Unverifiable" — never shown as verified. Integrity is
/// about the record, never about answer quality.</summary>
public static class EvidenceCompleteness
{
    public static EvidenceCompletenessState State(bool recordFound, bool? segmentChainValid, bool stillWithinPendingWindow)
    {
        if (!recordFound) return stillWithinPendingWindow ? EvidenceCompletenessState.Pending : EvidenceCompletenessState.Unavailable;
        return segmentChainValid switch
        {
            true => EvidenceCompletenessState.Verified,
            false => EvidenceCompletenessState.Unverifiable,
            null => EvidenceCompletenessState.Recorded,
        };
    }

    public static (string Label, string Tone) Text(EvidenceCompletenessState s) => s switch
    {
        EvidenceCompletenessState.Pending => ("Evidence pending — audit write in progress", "hollow"),
        EvidenceCompletenessState.Unavailable => ("Evidence unavailable — not found after the bounded wait", "warn"),
        EvidenceCompletenessState.Recorded => ("Recorded — chain verification not run", "hollow"),
        EvidenceCompletenessState.Verified => ("Audit integrity verified (record present, chain consistent)", "ok"),
        _ => ("Recorded, but the segment's chain verification FAILS", "bad"),
    };

    public const string QualityDisclaimer = "Integrity says the record is present and unaltered in the chain. It says nothing about the quality or correctness of the model's answer.";
}
