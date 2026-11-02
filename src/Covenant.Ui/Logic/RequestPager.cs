using System.Globalization;

namespace Covenant.Ui.Logic;

/// <summary>Visible pagination of the LOADED request records (usability review). The server still pages the audit segment
/// with stable cursors (<see cref="RequestPaging"/>); this pager slices what is loaded into pages of 10/25/50/100 and asks
/// for the next server batch only when "Next" runs past the loaded records. Totals are never invented: the label says
/// "of N loaded" (plus "more available" while a cursor remains). Pure.</summary>
public sealed class RequestPager
{
    public static readonly IReadOnlyList<int> Sizes = [10, 25, 50, 100];
    public const int DefaultSize = 25;

    public int Size { get; private set; } = DefaultSize;
    /// <summary>Zero-based page index into the filtered loaded records.</summary>
    public int Index { get; private set; }
    /// <summary>New records arrived while an older page was shown: they are NOT merged (rows never shift under the
    /// operator); the page offers "Show newest" instead.</summary>
    public bool NewAvailable { get; set; }

    /// <summary>Changes whenever what is shown changes (page, size, filters, history, reload). An async continuation captures
    /// it before awaiting and acts only if it is unchanged afterwards — an old "Next" or refresh never moves a list the
    /// operator has since re-filtered, re-sized, paged or switched.</summary>
    public int Epoch { get; private set; }

    /// <summary>The operator changed filters or history: anything started for the old list must not act on the new one.</summary>
    public void Invalidate() => Epoch++;

    public bool IsCurrent(int epoch) => epoch == Epoch;

    /// <summary>Decides, AFTER a refresh fetch completed, whether its newest records may be merged now: only on the first
    /// page. Otherwise they are announced (<see cref="NewAvailable"/>) and nothing moves.</summary>
    public bool AdmitLiveArrivals()
    {
        if (MergeLiveArrivals) return true;
        NewAvailable = true;
        return false;
    }

    /// <summary>Changes the page size, keeping the first visible row on screen (the page index is recomputed).</summary>
    public void SetSize(int size)
    {
        if (!Sizes.Contains(size)) size = DefaultSize;
        int first = Index * Size;
        Size = size;
        Index = first / size;
        Epoch++;
    }

    /// <summary>Filters, segment or reload changed what is listed: back to the first page.</summary>
    public void Reset()
    {
        Index = 0;
        NewAvailable = false;
        Epoch++;
    }

    /// <summary>Keeps the index inside the available pages (e.g. after records were filtered out).</summary>
    public void Clamp(int total)
    {
        int last = total <= 0 ? 0 : (total - 1) / Size;
        if (Index > last) Index = last;
        if (Index < 0) Index = 0;
    }

    public bool CanPrevious => Index > 0;

    public void Previous()
    {
        if (Index > 0) { Index--; Epoch++; }
    }

    /// <summary>The loaded (filtered) records already contain a further page.</summary>
    public bool HasMoreLoaded(int total) => (Index + 1) * Size < total;

    /// <summary>Next is possible: more loaded rows, or the server can send an older batch (cursor present, browser bound not reached).</summary>
    public bool CanNext(int total, bool canLoadOlder, bool atLoadLimit) => HasMoreLoaded(total) || (canLoadOlder && !atLoadLimit);

    /// <summary>Advances when the loaded records reach the next page; false = a server batch is needed first (or none exists).</summary>
    public bool TryNext(int total)
    {
        if (!HasMoreLoaded(total)) return false;
        Index++;
        Epoch++;
        return true;
    }

    /// <summary>Live arrivals are merged on top only while the first page is shown.</summary>
    public bool MergeLiveArrivals => Index == 0;

    public IReadOnlyList<T> Slice<T>(IReadOnlyList<T> rows)
    {
        Clamp(rows.Count);
        return rows.Skip(Index * Size).Take(Size).ToList();
    }

    /// <summary>"Showing 26–50 of 112 loaded · more available" / "Showing 1–25 of 40 matching (of 112 loaded)".</summary>
    public string Label(int filtered, int loaded, bool filtersActive, bool moreOnServer)
    {
        if (filtered == 0) return filtersActive ? "No loaded records match (" + N(loaded) + " loaded)" : "No records loaded";
        Clamp(filtered);
        int from = Index * Size + 1, to = Math.Min(filtered, (Index + 1) * Size);
        var head = "Showing " + N(from) + "–" + N(to) + " of " + N(filtered) + (filtersActive ? " matching (of " + N(loaded) + " loaded)" : " loaded");
        return head + (moreOnServer ? " · older records available" : "");
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
