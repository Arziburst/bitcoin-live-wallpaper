// Contract #18, criterion 4: with 4h / 6h candles the time axis labels days ("27 Oct") at a sensible spacing, the
// countdown shows hh:mm:ss ("closes in 03:12:05"), MA20 / MA50 are computed on the chosen interval, and the 24h
// edge markers work as before. Plus the mockup frames (and so the golden PNGs) show 30 days at the screen's
// interval (D-13).
//
// API SKETCH (written before the code, #237), in src/Chart.Render.D2D/. The renderer stays a pure function of the
// frame; the interval becomes part of the frame:
//
//   public sealed record ChartFrame(...)                // existing positional members unchanged
//   {
//       // The candle interval of Candles: the countdown's close time, the time axis and the caption use it.
//       public TimeSpan Interval { get; init; }
//
//       // New optional last parameter `interval`; null -> 5 minutes (today's frames, the existing tests).
//       public static ChartFrame Create(int widthPx, int heightPx, double dpiScale, IReadOnlyList<Candle> candles,
//           Ticker24h? ticker, DateTimeOffset now, TimeZoneInfo timeZone, string pair = "BTC/USDT",
//           string source = "Binance", bool isStale = false, double shiftPitches = 0, double pulse = 0,
//           bool pulseUp = false, TimeSpan? interval = null);
//   }
//
//   // One time-axis label as drawn: X = the label's centre (its tick) in physical px.
//   public sealed record TimeAxisLabel(double X, string Text);
//
//   // What a frame shows, computed from the frame alone (like DayRangeMarkers.For).
//   public static class FrameView
//   {
//       // The labels actually drawn on the time axis, left to right.
//       public static IReadOnlyList<TimeAxisLabel> TimeAxisLabels(ChartFrame frame);
//
//       // The headline's first line as drawn, e.g. "BTC/USDT  ·  Binance  ·  4h  ·  closes in 03:12:05"
//       // (interval in Binance notation: 5m, 1h, 2h, 4h, 6h, 8h, 12h, 1d).
//       public static string Caption(ChartFrame frame);
//
//       // The averages drawn, one entry per frame candle (MovingAverage.Simple over frame.Candles).
//       public static IReadOnlyList<decimal?> Ma20(ChartFrame frame);
//       public static IReadOnlyList<decimal?> Ma50(ChartFrame frame);
//   }
//
// Rules the tests rely on:
// - Interval >= 1 hour: every time-axis label is a local date "d MMM" (invariant culture, e.g. "27 Oct"), placed
//   within one candle pitch of that day's local midnight on the candle time scale (any time zone, DST included);
//   at least 4 labels for 30 days, consecutive labels 1..7 days apart and at least 4.5 x AxisLabelPx apart (a
//   "27 Oct" in JetBrains Mono is 3.6 em wide), all inside the time axis.
// - Interval >= 1 hour: the countdown is "closes in hh:mm:ss" (rounded up to whole seconds, as today), to the end
//   of the forming candle (OpenTime + Interval) or, when the newest candle is closed, to the next interval
//   boundary (aligned to the Unix epoch, UTC: 6h -> 00/06/12/18). Interval < 1 hour: "closes in mm:ss" as today.
// - The caption names the frame's interval, never "5m" for a 4h frame.

using System.Globalization;
using System.Text.RegularExpressions;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Indicators;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class LongIntervalRenderTests(RendererFixture fx)
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Regex DayLabel = new(@"^\d{1,2} (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)$");

    private static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>A smooth synthetic series of <paramref name="count"/> candles, the newest opening at
    /// <paramref name="newestOpen"/>; the newest is forming unless <paramref name="newestClosed"/>.</summary>
    private static Candle[] Series(TimeSpan interval, DateTimeOffset newestOpen, int count, bool newestClosed = false)
    {
        var candles = new Candle[count];
        var close = 68000m;
        for (var i = 0; i < count; i++)
        {
            var open = close;
            close = Math.Round(68000m + (decimal)((3000 * Math.Sin(i / 15.0)) + (800 * Math.Sin(i / 4.3)) + (i * 5)), 2);
            var high = Math.Max(open, close) + 50m + ((i % 7) * 10m);
            var low = Math.Min(open, close) - 50m - ((i % 5) * 10m);
            candles[i] = new Candle(newestOpen - ((count - 1 - i) * interval), open, high, low, close, 500m + ((i % 9) * 40m),
                IsClosed: i < count - 1 || newestClosed);
        }

        return candles;
    }

    private static Ticker24h TickerFor(IReadOnlyList<Candle> candles)
    {
        var last = candles[^1].Close;
        var open = candles[^7].Open;
        var day = candles.TakeLast(7).ToArray();
        return new Ticker24h(last, open, day.Max(c => c.High), day.Min(c => c.Low), last - open, (last - open) / open * 100m, 15000m);
    }

    // The owner's screens with their D-13 intervals: 180 x 4h and 120 x 6h, newest forming at 2026-10-31 00:00 UTC.
    public static TheoryData<int, int, double, double, int> OwnerScreens => new()
    {
        { 5120, 1440, 1.75, 4, 180 },
        { 3840, 2160, 1.0, 6, 120 },
    };

    private static readonly DateTimeOffset NewestOpen = Utc("2026-10-31T00:00:00Z");

    private static ChartFrame ThirtyDays(int w, int h, double s, double hours, int count, TimeZoneInfo zone, DateTimeOffset? now = null)
    {
        var interval = TimeSpan.FromHours(hours);
        var candles = Series(interval, NewestOpen, count);
        return ChartFrame.Create(w, h, s, candles, TickerFor(candles), now ?? NewestOpen.AddMinutes(47).AddSeconds(55), zone,
            interval: interval);
    }

    private static TimeZoneInfo Zone(string name) => name switch
    {
        "UTC" => TimeZoneInfo.Utc,
        "UTC+02 fixed" => TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02"),
        "UTC+05:30 fixed" => TimeZoneInfo.CreateCustomTimeZone("Test+0530", new TimeSpan(5, 30, 0), "Test+0530", "Test+0530"),
        _ => TimeZoneInfo.FindSystemTimeZoneById(name), // Kyiv: DST ends on 25 Oct 2026, inside the 30 days
    };

    // ---- the frame carries the interval ---------------------------------------------------------

    [Fact]
    public void A_frame_carries_its_interval_and_defaults_to_5_minutes()
    {
        var candles = Series(FourHours, NewestOpen, 30);
        Assert.Equal(FourHours, ChartFrame.Create(5120, 1440, 1.75, candles, null, NewestOpen, TimeZoneInfo.Utc, interval: FourHours).Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), ChartFrame.Create(5120, 1440, 1.75, candles, null, NewestOpen, TimeZoneInfo.Utc).Interval);
    }

    [Theory]
    [MemberData(nameof(OwnerScreens))]
    public void Thirty_days_render_whole_on_the_owner_screens(int w, int h, double s, double hours, int count)
    {
        var frame = ThirtyDays(w, h, s, hours, count, TimeZoneInfo.Utc);
        Assert.Equal(count, frame.Layout.DrawnCandleCount);
        var image = fx.Renderer.Render(frame);
        Assert.Equal((w, h), (image.Width, image.Height));
    }

    // ---- time axis: days ------------------------------------------------------------------------

    public static TheoryData<int, int, double, double, int, string> ScreensAndZones()
    {
        var data = new TheoryData<int, int, double, double, int, string>();
        foreach (var zone in new[] { "UTC", "UTC+02 fixed", "FLE Standard Time", "UTC+05:30 fixed" })
        {
            data.Add(5120, 1440, 1.75, 4, 180, zone);
            data.Add(3840, 2160, 1.0, 6, 120, zone);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScreensAndZones))]
    public void The_time_axis_labels_days_at_local_midnight(int w, int h, double s, double hours, int count, string zoneName)
    {
        var zone = Zone(zoneName);
        var frame = ThirtyDays(w, h, s, hours, count, zone);
        var labels = FrameView.TimeAxisLabels(frame);
        var l = frame.Layout;
        var interval = TimeSpan.FromHours(hours);

        Assert.True(labels.Count >= 4, $"{labels.Count} time labels for 30 days: [{string.Join(", ", labels.Select(x => x.Text))}]");

        // The local midnights on the candle time scale (oldest candle's open .. the last offset slot).
        var first = frame.Candles[0].OpenTime;
        var end = frame.Candles[^1].OpenTime + ((l.RightOffsetBars + 1) * interval);
        double XOf(DateTimeOffset t) => l.CandleCenterX(0) - (l.CandlePitchPx / 2) + ((t - first) / interval * l.CandlePitchPx);
        var midnights = new List<(string Text, double X, DateTime Date)>();
        for (var d = TimeZoneInfo.ConvertTime(first, zone).Date.AddDays(-1); d <= TimeZoneInfo.ConvertTime(end, zone).Date.AddDays(1); d = d.AddDays(1))
        {
            var utc = new DateTimeOffset(d, zone.GetUtcOffset(d));
            midnights.Add((d.ToString("d MMM", Inv), XOf(utc), d));
        }

        DateTime? previousDate = null;
        double? previousX = null;
        foreach (var label in labels)
        {
            Assert.Matches(DayLabel, label.Text);
            Assert.True(label.X >= l.TimeAxis.X && label.X <= l.TimeAxis.Right, $"label '{label.Text}' at {label.X} is outside the time axis {l.TimeAxis}");

            var match = midnights.Where(m => m.Text == label.Text).OrderBy(m => Math.Abs(m.X - label.X)).FirstOrDefault();
            Assert.True(match.Text is not null, $"label '{label.Text}' is not a date of the chart");
            Assert.True(Math.Abs(match.X - label.X) <= l.CandlePitchPx,
                $"label '{label.Text}' at x {label.X:0.#}, its local midnight is at x {match.X:0.#} (pitch {l.CandlePitchPx})");

            if (previousDate is { } pd && previousX is { } px)
            {
                var days = (match.Date - pd).TotalDays;
                Assert.InRange(days, 1, 7);
                Assert.True(label.X - px >= 4.5 * l.Text.AxisLabelPx,
                    $"labels {pd:d MMM} and '{label.Text}' are {label.X - px:0.#} px apart, under 4.5 x {l.Text.AxisLabelPx} px");
            }

            previousDate = match.Date;
            previousX = label.X;
        }
    }

    // ---- countdown ------------------------------------------------------------------------------

    [Theory]
    [InlineData(4, "2026-10-31T00:00:00Z", false, "2026-10-31T00:47:55Z", "closes in 03:12:05")]
    [InlineData(4, "2026-10-31T00:00:00Z", false, "2026-10-31T03:50:00Z", "closes in 00:10:00")]
    [InlineData(4, "2026-10-31T00:00:00Z", false, "2026-10-31T00:00:00.4Z", "closes in 04:00:00")]
    [InlineData(6, "2026-10-30T18:00:00Z", false, "2026-10-30T18:00:00Z", "closes in 06:00:00")]
    [InlineData(6, "2026-10-30T18:00:00Z", false, "2026-10-30T23:59:59.5Z", "closes in 00:00:01")]
    [InlineData(4, "2026-10-31T00:00:00Z", true, "2026-10-31T04:00:03Z", "closes in 03:59:57")]  // closed: next 4h boundary 08:00
    [InlineData(6, "2026-10-30T12:00:00Z", true, "2026-10-30T18:00:01Z", "closes in 05:59:59")]  // closed: next 6h boundary 00:00
    public void Long_candles_count_down_in_hh_mm_ss(double hours, string newestOpen, bool newestClosed, string now, string expected)
    {
        var interval = TimeSpan.FromHours(hours);
        var candles = Series(interval, Utc(newestOpen), 60, newestClosed);
        var frame = ChartFrame.Create(5120, 1440, 1.75, candles, TickerFor(candles), Utc(now), TimeZoneInfo.Utc, interval: interval);

        var caption = FrameView.Caption(frame);
        Assert.Contains(expected, caption);
        Assert.Matches(@"closes in \d{2}:\d{2}:\d{2}", caption);
    }

    [Theory]
    [InlineData(4, "4h")]
    [InlineData(6, "6h")]
    public void The_caption_names_the_interval(double hours, string code)
    {
        var interval = TimeSpan.FromHours(hours);
        var candles = Series(interval, NewestOpen, 60);
        var frame = ChartFrame.Create(3840, 2160, 1.0, candles, TickerFor(candles), NewestOpen.AddHours(1), TimeZoneInfo.Utc, interval: interval);

        var caption = FrameView.Caption(frame);
        Assert.Matches($@"(^|\s){code}(\s|$)", caption);
        Assert.DoesNotContain("5m", caption);
        Assert.StartsWith("BTC/USDT", caption);
        Assert.Contains("Binance", caption);
    }

    [Fact]
    public void Five_minute_frames_keep_mm_ss()
    {
        var fiveMinutes = TimeSpan.FromMinutes(5);
        var candles = Series(fiveMinutes, Utc("2026-10-31T12:00:00Z"), 60);
        var frame = ChartFrame.Create(5120, 1440, 1.75, candles, TickerFor(candles), Utc("2026-10-31T12:01:52Z"), TimeZoneInfo.Utc);

        var caption = FrameView.Caption(frame);
        Assert.Contains("closes in 03:08", caption);
        Assert.DoesNotMatch(@"closes in \d{2}:\d{2}:\d{2}", caption);
        Assert.Matches(@"(^|\s)5m(\s|$)", caption);
    }

    [Fact]
    public void The_drawn_countdown_follows_the_frame_time_on_a_6h_frame()
    {
        // An hour later the 6h countdown text differs, in the caption and in the pixels.
        var frame = ThirtyDays(3840, 2160, 1.0, 6, 120, TimeZoneInfo.Utc);
        var a = fx.Renderer.Render(frame);
        var b = fx.Renderer.Render(frame with { Now = frame.Now.AddHours(1) });
        Assert.True(ImageDiff.Compare(a, b).MaxDelta > 0, "the countdown did not change an hour later");
        Assert.NotEqual(FrameView.Caption(frame), FrameView.Caption(frame with { Now = frame.Now.AddHours(1) }));
    }

    // ---- moving averages on the chosen interval -------------------------------------------------

    [Theory]
    [MemberData(nameof(OwnerScreens))]
    public void Ma20_and_ma50_are_computed_on_the_frames_candles(int w, int h, double s, double hours, int count)
    {
        var frame = ThirtyDays(w, h, s, hours, count, TimeZoneInfo.Utc);
        var candles = frame.Candles;

        var ma20 = FrameView.Ma20(frame);
        var ma50 = FrameView.Ma50(frame);
        Assert.Equal(candles.Count, ma20.Count);
        Assert.Equal(candles.Count, ma50.Count);
        Assert.Equal(MovingAverage.Simple(candles, 20), ma20);
        Assert.Equal(MovingAverage.Simple(candles, 50), ma50);
        Assert.Equal(candles.TakeLast(20).Average(c => c.Close), ma20[^1]);
        Assert.Equal(candles.TakeLast(50).Average(c => c.Close), ma50[^1]);
        Assert.Null(ma50[48]);
        Assert.NotNull(ma50[49]);
    }

    // ---- 24h markers unchanged ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(OwnerScreens))]
    public void Off_scale_24h_extremes_still_become_edge_markers(int w, int h, double s, double hours, int count)
    {
        var frame = ThirtyDays(w, h, s, hours, count, TimeZoneInfo.Utc);
        var hi = frame.Candles.Max(c => c.High);
        var lo = frame.Candles.Min(c => c.Low);
        var range = hi - lo;
        var off = frame with { Ticker = frame.Ticker! with { High = Math.Round(hi + (3 * range), 2), Low = Math.Round(lo - (3 * range), 2) } };
        var on = frame with { Ticker = frame.Ticker! with { High = frame.Candles[^1].Close, Low = frame.Candles[^1].Close } };

        Assert.Equal(2, DayRangeMarkers.For(off).Count);
        Assert.Empty(DayRangeMarkers.For(on));
    }

    // ---- the mockups / golden frames show 30 days ------------------------------------------------

    [Theory]
    [InlineData("btc-5120x1440.png")]
    [InlineData("btc-3840x2160.png")]
    public void Mockup_frames_show_30_days_at_the_screen_interval(string fileName)
    {
        var screen = MockupScreens.All.Single(m => m.FileName == fileName);
        var frame = fx.Frame(fileName);
        var interval = IntervalPolicy.ForScreen(screen.WidthPx, screen.HeightPx, screen.DpiScale, IntervalPolicy.DefaultPeriod);

        Assert.Equal(interval, frame.Interval);
        for (var i = 1; i < frame.Candles.Count; i++)
        {
            Assert.Equal(interval, frame.Candles[i].OpenTime - frame.Candles[i - 1].OpenTime);
        }

        var thirtyDays = IntervalPolicy.CandleCount(IntervalPolicy.DefaultPeriod, interval);
        Assert.True(frame.Layout.DrawnCandleCount >= thirtyDays,
            $"{fileName}: {frame.Layout.DrawnCandleCount} candles drawn, 30 days of {interval} are {thirtyDays}");
        Assert.All(FrameView.TimeAxisLabels(frame), label => Assert.Matches(DayLabel, label.Text));
    }
}
