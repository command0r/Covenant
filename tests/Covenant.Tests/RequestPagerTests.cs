using Xunit;
using UiLogic = Covenant.Ui.Logic;
using UiModels = Covenant.Ui.Models;

namespace Covenant.Tests;

/// <summary>Visible request pagination over LOADED records (usability review): sizes 10/25/50/100 (default 25), honest
/// "of N loaded" labels, server batches only when Next runs past what is loaded, cursor exhaustion/expiry, filters and size
/// changes reset/clamp, and live arrivals never shift rows of an older page.</summary>
public sealed class RequestPagerTests
{
    private static List<int> Rows(int n) => Enumerable.Range(1, n).ToList();

    [Fact]
    public void Defaults_to_25_and_slices_with_honest_bounds()
    {
        var p = new UiLogic.RequestPager();
        Assert.Equal(25, p.Size);
        Assert.Equal(new[] { 10, 25, 50, 100 }, UiLogic.RequestPager.Sizes.ToArray());
        var rows = Rows(60);
        Assert.Equal(Enumerable.Range(1, 25), p.Slice(rows));
        Assert.Equal("Showing 1–25 of 60 loaded · older records available", p.Label(60, 60, false, moreOnServer: true));
        Assert.False(p.CanPrevious);
        Assert.True(p.TryNext(60));
        Assert.True(p.TryNext(60));
        Assert.Equal(Enumerable.Range(51, 10), p.Slice(rows));
        Assert.Equal("Showing 51–60 of 60 loaded", p.Label(60, 60, false, false));
        Assert.False(p.TryNext(60));                                   // past the loaded records: needs a server batch
        Assert.True(p.CanNext(60, canLoadOlder: true, atLoadLimit: false));
        Assert.False(p.CanNext(60, canLoadOlder: false, atLoadLimit: false));   // cursor exhausted (or expired): no Next
        Assert.False(p.CanNext(60, canLoadOlder: true, atLoadLimit: true));     // browser bound reached
        p.Previous();
        Assert.Equal(1, p.Index);
    }

    [Fact]
    public void Size_100_and_size_changes_keep_the_first_visible_row()
    {
        var p = new UiLogic.RequestPager();
        p.SetSize(10);
        for (int i = 0; i < 7; i++) p.TryNext(250);                    // page 8: rows 71–80
        p.SetSize(100);
        Assert.Equal(0, p.Index);                                       // row 71 lives on page 1 of 100
        Assert.Equal(Enumerable.Range(1, 100), p.Slice(Rows(250)));
        p.SetSize(7);                                                   // not an offered size → default
        Assert.Equal(25, p.Size);
    }

    [Fact]
    public void Filters_reset_and_shrinking_results_clamp_the_page()
    {
        var p = new UiLogic.RequestPager();
        p.SetSize(10);
        p.TryNext(40); p.TryNext(40); p.TryNext(40);                    // page 4
        Assert.Equal(Enumerable.Range(31, 5), p.Slice(Rows(35)));       // fewer rows: same page still valid
        Assert.Equal(Enumerable.Range(1, 5), p.Slice(Rows(5)));         // clamped to the last existing page
        Assert.Equal(0, p.Index);
        p.TryNext(40); p.Reset();
        Assert.Equal(0, p.Index);
        Assert.Equal("No loaded records match (120 loaded)", p.Label(0, 120, filtersActive: true, false));
        Assert.Equal("No records loaded", p.Label(0, 0, false, false));
        Assert.Equal("Showing 1–10 of 12 matching (of 400 loaded)", p.Label(12, 400, true, false));
    }

    [Fact]
    public void Live_arrivals_never_shift_an_older_page()
    {
        var p = new UiLogic.RequestPager();
        Assert.True(p.MergeLiveArrivals);                               // first page: newest are merged on top
        p.TryNext(100);
        Assert.False(p.MergeLiveArrivals);                              // older page: only announced
        var shownBefore = p.Slice(Rows(100));
        p.NewAvailable = true;
        Assert.Equal(shownBefore, p.Slice(Rows(100)));                  // nothing merged ⇒ the same rows stay on screen
        p.Reset();                                                      // "Show newest"
        Assert.False(p.NewAvailable);
        Assert.Equal(0, p.Index);
    }

    [Fact]
    public void Server_paging_expiry_and_exhaustion_stop_next()
    {
        var paging = new UiLogic.RequestPaging();
        paging.Reset(null);
        var seg = new UiModels.SegmentInfo { Id = "current", IsCurrent = true };
        Assert.Equal(UiLogic.PageResult.Accepted, paging.ApplyPage(new UiModels.RequestsResponse { Segment = seg, Items = [new() { Id = "r1" }], NextCursor = "c1" }, older: false));
        Assert.True(paging.CanLoadOlder);
        Assert.Equal(UiLogic.PageResult.CursorExpired, paging.ApplyError(410, "cursor_expired", null));
        var p = new UiLogic.RequestPager();
        Assert.False(p.CanNext(1, paging.CanLoadOlder, false));         // expired: no further batch, never redirected

        paging.Reset(null);
        paging.ApplyPage(new UiModels.RequestsResponse { Segment = seg, Items = [new() { Id = "r1" }], NextCursor = null }, older: false);
        Assert.False(p.CanNext(1, paging.CanLoadOlder, false));         // exhausted
    }

    // ---------------------------------------------------------------- async races (decided after the await)

    [Fact]
    public void A_refresh_that_completes_after_the_operator_paged_on_is_announced_not_merged()
    {
        var p = new UiLogic.RequestPager();
        Assert.True(p.MergeLiveArrivals);                  // the refresh STARTED on page 1 …
        p.TryNext(100);                                    // … the operator clicked Next while it was in flight
        Assert.False(p.AdmitLiveArrivals());               // decided after the await: no merge, no reset
        Assert.True(p.NewAvailable);
        Assert.Equal(1, p.Index);

        var restored = new UiLogic.RequestPager();          // an older page restored after navigation (initial refresh)
        restored.TryNext(60);
        Assert.False(restored.AdmitLiveArrivals());
        Assert.True(restored.NewAvailable);

        var first = new UiLogic.RequestPager();
        Assert.True(first.AdmitLiveArrivals());             // on page 1 the newest records are merged
        Assert.False(first.NewAvailable);
    }

    [Fact]
    public void An_old_next_continuation_never_advances_a_changed_list()
    {
        var p = new UiLogic.RequestPager();
        int epoch = p.Epoch;                                // captured when Next started a server fetch
        p.Reset();                                          // filters changed (or history switched / reloaded) meanwhile
        Assert.False(p.IsCurrent(epoch));

        epoch = p.Epoch; p.SetSize(50); Assert.False(p.IsCurrent(epoch));
        p.TryNext(200);
        epoch = p.Epoch; p.Previous(); Assert.False(p.IsCurrent(epoch));
        epoch = p.Epoch; p.TryNext(200); Assert.False(p.IsCurrent(epoch));
        epoch = p.Epoch; p.Invalidate(); Assert.False(p.IsCurrent(epoch));

        epoch = p.Epoch;
        Assert.True(p.IsCurrent(epoch));                    // nothing changed: the continuation may advance
        _ = p.Slice(Enumerable.Range(1, 10).ToList());      // rendering (clamping) is not a change of list
        Assert.True(p.IsCurrent(epoch));
    }
}
