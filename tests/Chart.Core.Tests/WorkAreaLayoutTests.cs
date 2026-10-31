// Contract #47 (D-37), criterion 1: the layout takes a content rectangle (the work area above the taskbar).
//
// API SKETCH (written before the code, #237). Optional parameters, defaulting to the full screen:
//
//   namespace BitcoinWallpaper.Chart.Core.Layout;
//
//   public static class LayoutEngine
//   {
//       // content: the rectangle (physical px, relative to the screen's top-left) inside which the chart content
//       // lies; null = the full screen RectPx(0, 0, widthPx, heightPx). A content that is not inside the screen or
//       // has no area: ArgumentOutOfRangeException. With content = full screen (or null) the layout is exactly the
//       // one the engine has always given.
//       public static ChartLayout Compute(int widthPx, int heightPx, double dpiScale, int candleCount,
//                                         RectPx? content = null);
//   }
//
//   public sealed class ChartLayout
//   {
//       public RectPx Content { get; }   // the content rectangle the layout was computed for (full screen if none)
//       // WidthPx / HeightPx stay the SCREEN size (the surface and the background are full screen).
//       // Plot, VolumePane, TimeAxis, PriceAxis and Headline all lie inside Content; TimeAxis.Bottom <= Content.Bottom.
//       // A taskbar on one side shrinks the content on that side only: the horizontal extents (Plot.X, Plot.Width,
//       // PriceAxis) are unchanged for a bar at the bottom or top; PriceAxis.Right == WidthPx for a bar at the
//       // bottom, top or left.
//   }
//
//   public static class IntervalPolicy
//   {
//       // Trailing optional content (null = full screen): the layout the slots come from is Compute(..., content).
//       public static TimeSpan ForScreen(int widthPx, int heightPx, double dpiScale, TimeSpan period, RectPx? content = null);
//       public static IntervalDecision OnDisplay(TimeSpan? current, int widthPx, int heightPx, double dpiScale,
//                                                TimeSpan period, RectPx? content = null);
//   }
//
//   // The crosshair model is unchanged: it accepts any cursor on the full screen (lines full screen); the price and the
//   // candle follow the plot (hence the content).

using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class WorkAreaLayoutTests
{
    private const double Eps = 1e-6;
    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    private static RectPx Full(int w, int h) => new(0, 0, w, h);

    private static RectPx BottomBar(int w, int h, int bar) => new(0, 0, w, h - bar);

    private static RectPx TopBar(int w, int h, int bar) => new(0, bar, w, h - bar);

    private static RectPx LeftBar(int w, int h, int bar) => new(bar, 0, w - bar, h);

    private static RectPx RightBar(int w, int h, int bar) => new(0, 0, w - bar, h);

    private static void AssertInside(RectPx inner, RectPx outer, string what)
    {
        Assert.True(inner.X >= outer.X - Eps, $"{what} {inner} left of {outer}");
        Assert.True(inner.Y >= outer.Y - Eps, $"{what} {inner} above {outer}");
        Assert.True(inner.Right <= outer.Right + Eps, $"{what} {inner} right of {outer}");
        Assert.True(inner.Bottom <= outer.Bottom + Eps, $"{what} {inner} below {outer}");
    }

    private static void AssertAllInside(ChartLayout l, RectPx content)
    {
        AssertInside(l.Plot, content, "plot");
        AssertInside(l.VolumePane, content, "volume pane");
        AssertInside(l.TimeAxis, content, "time axis");
        AssertInside(l.PriceAxis, content, "price axis");
        AssertInside(l.Headline, content, "headline");
        Assert.True(l.TimeAxis.Bottom <= content.Bottom + Eps, $"time axis bottom {l.TimeAxis.Bottom} > content bottom {content.Bottom}");
    }

    public static TheoryData<int, int, double, int> Screens => new()
    {
        { 5120, 1440, 1.75, 84 },
        { 3840, 2160, 1.0, 96 },
        { 1920, 1080, 1.0, 48 },
    };

    // ---- content = full screen: nothing changes ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void With_the_content_the_full_screen_the_layout_is_exactly_the_one_without_a_content(int w, int h, double dpi, int bar)
    {
        _ = bar;
        var plain = LayoutEngine.Compute(w, h, dpi, 180);
        var full = LayoutEngine.Compute(w, h, dpi, 180, Full(w, h));
        var nullContent = LayoutEngine.Compute(w, h, dpi, 180, null);
        foreach (var l in new[] { full, nullContent })
        {
            Assert.Equal(plain.Plot, l.Plot);
            Assert.Equal(plain.VolumePane, l.VolumePane);
            Assert.Equal(plain.TimeAxis, l.TimeAxis);
            Assert.Equal(plain.PriceAxis, l.PriceAxis);
            Assert.Equal(plain.Headline, l.Headline);
            Assert.Equal(plain.Text, l.Text);
            Assert.Equal(plain.CandlePitchPx, l.CandlePitchPx);
            Assert.Equal(plain.CandleBodyWidthPx, l.CandleBodyWidthPx);
                Assert.Equal(plain.Profile, l.Profile);
            Assert.Equal(w, l.WidthPx);
            Assert.Equal(h, l.HeightPx);
            Assert.Equal(Full(w, h), l.Content);
        }
    }

    // ---- a taskbar on each side --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_taskbar_at_the_bottom_puts_everything_above_it_and_keeps_the_horizontal_extents(int w, int h, double dpi, int bar)
    {
        var content = BottomBar(w, h, bar);
        var plain = LayoutEngine.Compute(w, h, dpi, 180);
        var l = LayoutEngine.Compute(w, h, dpi, 180, content);

        Assert.Equal(w, l.WidthPx);
        Assert.Equal(h, l.HeightPx);
        Assert.Equal(content, l.Content);
        AssertAllInside(l, content);
        Assert.True(l.TimeAxis.Bottom <= h - bar + Eps);

        // Only the bottom shrank: the same columns as without the bar.
        Assert.Equal(plain.Plot.X, l.Plot.X);
        Assert.True(Math.Abs(plain.Plot.Width - l.Plot.Width) <= 0.015 * w, "plot width ${l.Plot.Width} vs ${plain.Plot.Width}");
        Assert.Equal(w, l.PriceAxis.Right, 6);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_taskbar_at_the_top_puts_everything_below_it_and_keeps_the_horizontal_extents(int w, int h, double dpi, int bar)
    {
        var content = TopBar(w, h, bar);
        var plain = LayoutEngine.Compute(w, h, dpi, 180);
        var l = LayoutEngine.Compute(w, h, dpi, 180, content);

        AssertAllInside(l, content);
        Assert.True(l.Plot.Y >= bar - Eps);
        Assert.True(l.Headline.Y >= bar - Eps);
        Assert.Equal(plain.Plot.X, l.Plot.X);
        Assert.True(Math.Abs(plain.Plot.Width - l.Plot.Width) <= 0.015 * w, "plot width ${l.Plot.Width} vs ${plain.Plot.Width}");
        Assert.Equal(w, l.PriceAxis.Right, 6);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_taskbar_at_the_left_moves_the_chart_right_and_keeps_the_right_and_vertical_extents(int w, int h, double dpi, int bar)
    {
        var content = LeftBar(w, h, bar);
        var plain = LayoutEngine.Compute(w, h, dpi, 180);
        var l = LayoutEngine.Compute(w, h, dpi, 180, content);

        AssertAllInside(l, content);
        Assert.True(l.Plot.X >= bar - Eps);
        Assert.True(l.Plot.X <= bar + (0.01 * w), "the plot starts at the content's left edge (D-30), not somewhere inside");
        Assert.Equal(w, l.PriceAxis.Right, 6);
        Assert.Equal(plain.Plot.Y, l.Plot.Y, 6);
        Assert.Equal(plain.TimeAxis.Bottom, l.TimeAxis.Bottom, 6);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_taskbar_at_the_right_ends_the_price_axis_at_the_bar_and_keeps_the_vertical_extents(int w, int h, double dpi, int bar)
    {
        var content = RightBar(w, h, bar);
        var plain = LayoutEngine.Compute(w, h, dpi, 180);
        var l = LayoutEngine.Compute(w, h, dpi, 180, content);

        AssertAllInside(l, content);
        Assert.True(l.PriceAxis.Right <= w - bar + Eps);
        Assert.True(l.PriceAxis.Right >= w - bar - (0.02 * w), "the price axis column runs to the content's right edge");
        Assert.Equal(plain.Plot.X, l.Plot.X);
        Assert.Equal(plain.Plot.Y, l.Plot.Y, 6);
        Assert.Equal(plain.TimeAxis.Bottom, l.TimeAxis.Bottom, 6);
    }

    [Fact]
    public void A_wide_headline_top_right_and_a_narrow_one_top_left_both_stay_in_the_content()
    {
        // 32:9 puts the headline top-right, 1080x1920 top-left; with a bar on the right / left each stays in the content.
        var wide = LayoutEngine.Compute(5120, 1440, 1.75, 180, RightBar(5120, 1440, 120));
        AssertAllInside(wide, RightBar(5120, 1440, 120));
        var tall = LayoutEngine.Compute(1080, 1920, 1.0, 30, LeftBar(1080, 1920, 80));
        AssertAllInside(tall, LeftBar(1080, 1920, 80));
    }

    [Fact]
    public void A_content_outside_the_screen_or_without_area_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutEngine.Compute(1920, 1080, 1.0, 10, new RectPx(0, 0, 1920, 1081)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutEngine.Compute(1920, 1080, 1.0, 10, new RectPx(-1, 0, 100, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutEngine.Compute(1920, 1080, 1.0, 10, new RectPx(0, 0, 0, 500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutEngine.Compute(1920, 1080, 1.0, 10, new RectPx(0, 0, 500, 0)));
    }

    // ---- the interval (D-13) follows the content ----------------------------------------------------------------

    [Fact]
    public void The_interval_is_chosen_from_the_content_slots()
    {
        // Full 5120x1440 @1.75 is 4h (~305 slots). The right half only (2560 wide) has ~143 slots: 4h's 180 do not fit, 6h's 120 do.
        var half = new RectPx(2560, 0, 2560, 1440);
        Assert.Equal(TimeSpan.FromHours(4), IntervalPolicy.ForScreen(5120, 1440, 1.75, ThirtyDays));
        Assert.Equal(TimeSpan.FromHours(4), IntervalPolicy.ForScreen(5120, 1440, 1.75, ThirtyDays, Full(5120, 1440)));
        Assert.Equal(TimeSpan.FromHours(6), IntervalPolicy.ForScreen(5120, 1440, 1.75, ThirtyDays, half));

        var layout = LayoutEngine.Compute(5120, 1440, 1.75, 0, half);
        Assert.Equal(
            IntervalPolicy.ForSlots(layout.CandleSlots, layout.RightOffsetBars, ThirtyDays),
            IntervalPolicy.ForScreen(5120, 1440, 1.75, ThirtyDays, half));
    }

    [Fact]
    public void A_bottom_taskbar_alone_does_not_change_the_interval()
    {
        Assert.Equal(
            IntervalPolicy.ForScreen(5120, 1440, 1.75, ThirtyDays),
            IntervalPolicy.ForScreen(5120, 1440, 1.75, ThirtyDays, BottomBar(5120, 1440, 84)));
    }

    [Fact]
    public void OnDisplay_with_a_content_reloads_when_the_content_changes_the_interval()
    {
        var half = new RectPx(2560, 0, 2560, 1440);
        var same = IntervalPolicy.OnDisplay(TimeSpan.FromHours(4), 5120, 1440, 1.75, ThirtyDays, null);
        Assert.False(same.Reload);
        var narrowed = IntervalPolicy.OnDisplay(TimeSpan.FromHours(4), 5120, 1440, 1.75, ThirtyDays, half);
        Assert.Equal(TimeSpan.FromHours(6), narrowed.Interval);
        Assert.True(narrowed.Reload);
        Assert.True(IntervalPolicy.OnDisplay(null, 5120, 1440, 1.75, ThirtyDays, half).Reload);
    }

    // ---- the crosshair stays whole-monitor, chart features follow the content ---------------------------------

    [Fact]
    public void The_crosshair_accepts_a_cursor_over_the_taskbar_area_while_the_chart_values_follow_the_content()
    {
        const int w = 5120, h = 1440, bar = 84;
        var l = LayoutEngine.Compute(w, h, 1.75, 100, BottomBar(w, h, bar));
        var candles = Enumerable.Range(0, 100)
            .Select(i => new Candle(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddHours(4 * i), 65000, 65100, 64900, 65050, 10, true))
            .ToList();

        // Over the bar's rows: shown, exactly at the cursor, no price (not in the plot's rows), not over the volume.
        var inBar = CrosshairModel.Compute(new CursorPx(w - 1, h - 10), l, candles, 66000, 64000, paused: false);
        Assert.NotNull(inBar);
        Assert.Equal(w - 1, inBar!.X);
        Assert.Equal(h - 10, inBar.Y);
        Assert.Null(inBar.Price);
        Assert.False(inBar.OverVolume);

        // Over the plot the price is still there.
        var inPlot = CrosshairModel.Compute(new CursorPx(l.Plot.X + 40, l.Plot.Y + (l.Plot.Height / 2)), l, candles, 66000, 64000, paused: false);
        Assert.NotNull(inPlot);
        Assert.NotNull(inPlot!.Price);

        // The screen edge is still the edge.
        Assert.Null(CrosshairModel.Compute(new CursorPx(w, 10), l, candles, 66000, 64000, paused: false));
        Assert.Null(CrosshairModel.Compute(new CursorPx(10, h), l, candles, 66000, 64000, paused: false));
    }
}

