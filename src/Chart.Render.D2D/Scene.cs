using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Indicators;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Render.D2D;

public enum DayExtreme
{
    High,
    Low,
}

/// <summary>A 24h high or low beyond the visible price range, shown as a text marker at the plot's top (high) or
/// bottom (low) edge instead of a dotted line (D-11). Bounds are in physical px.</summary>
public sealed record DayRangeMarker(DayExtreme Extreme, decimal Price, string Text, RectPx Bounds);

public static class DayRangeMarkers
{
    /// <summary>The edge markers of a frame: one for each 24h extreme outside the visible price range, none for an
    /// extreme the scale shows (that one is drawn as a dotted line). The price scale is never stretched for them.</summary>
    public static IReadOnlyList<DayRangeMarker> For(ChartFrame frame) => new Scene(frame).EdgeMarkers;
}

/// <summary>One time-axis label as drawn: X = the label's centre (its tick) in physical px.</summary>
public sealed record TimeAxisLabel(double X, string Text);

/// <summary>What a frame shows, computed from the frame alone (like <see cref="DayRangeMarkers.For"/>).</summary>
public static class FrameView
{
    /// <summary>The labels drawn on the time axis, left to right.</summary>
    public static IReadOnlyList<TimeAxisLabel> TimeAxisLabels(ChartFrame frame) =>
        new Scene(frame).TimeLabels.Select(t => new TimeAxisLabel(t.X, t.Text)).ToList();

    /// <summary>The headline's first line as drawn, e.g. "BTC/USDT  ·  Binance  ·  4h  ·  closes in 03:12:05".</summary>
    public static string Caption(ChartFrame frame) => new Scene(frame).Caption;

    /// <summary>The 20-candle average drawn, one entry per frame candle.</summary>
    public static IReadOnlyList<decimal?> Ma20(ChartFrame frame) => new Scene(frame).Ma20;

    /// <summary>The 50-candle average drawn, one entry per frame candle.</summary>
    public static IReadOnlyList<decimal?> Ma50(ChartFrame frame) => new Scene(frame).Ma50;
}

/// <summary>What a frame derives to, computed from the frame alone.</summary>
internal sealed class Scene
{
    /// <summary>From this interval on the time axis labels days and the countdown shows hours.</summary>
    private static readonly TimeSpan LongInterval = TimeSpan.FromHours(1);

    public Scene(ChartFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Frame = frame;
        Layout = frame.Layout;
        Candles = frame.Candles;
        Interval = frame.Interval;
        Last = Candles.Count > 0 ? Candles[^1] : null;
        ShiftPx = frame.ShiftPitches * Layout.CandlePitchPx;

        // The candle just left of the drawn ones is cut by the screen's left edge (D-30); while the series slides
        // in, it slides out there.
        FirstIndex = Math.Max(0, Layout.FirstDrawnIndex - 1);
        Ma20 = MovingAverage.Simple(Candles, 20);
        Ma50 = MovingAverage.Simple(Candles, 50);
        DayHighLow = frame.Ticker is { } ticker
            ? DayStats.HighLowFromTicker(ticker)
            : Candles.Count > 0 ? DayStats.HighLowFromCandles(Candles, frame.Now) : null;

        Scale = PriceScale.For(Layout, Candles, [Ma20, Ma50]);
        (PriceStep, PriceLevels) = Ticks.Prices(Scale, Layout.Text.AxisLabelPx * 4.5);
        TimeTicks = Interval >= LongInterval ? DayTicksFor(frame, FirstIndex, ShiftPx) : TimeTicksFor(frame, FirstIndex, ShiftPx);
        TimeLabels = TimeTicks.Where(LabelFits).ToList();
        EdgeMarkers = EdgeMarkersFor();

        if (Last is { } last)
        {
            var close = last.IsClosed ? NextBoundary(frame.Now, Interval) : last.OpenTime + Interval;
            Countdown = Interval >= LongInterval ? LongCountdown(close, frame.Now) : CloseCountdown.Text(close, frame.Now);
        }

        var caption = $"{frame.Pair}  ·  {frame.Source}  ·  {CandleInterval.Code(Interval)}";
        if (Countdown is { } countdown)
        {
            caption += $"  ·  {countdown}";
        }

        Caption = frame.IsStale ? caption + "  ·  delayed" : caption;
    }

    /// <summary>The candle interval of the frame.</summary>
    public TimeSpan Interval { get; }

    /// <summary>The headline's first line: pair, source, interval, countdown, "delayed" when stale.</summary>
    public string Caption { get; }

    /// <summary>The time ticks whose label fits inside the time axis; those labels are drawn.</summary>
    public IReadOnlyList<TimeTick> TimeLabels { get; }

    public ChartFrame Frame { get; }

    public ChartLayout Layout { get; }

    public IReadOnlyList<Candle> Candles { get; }

    public Candle? Last { get; }

    /// <summary>The new-bar shift in px: every candle, average, volume bar and time tick is drawn this far right.</summary>
    public double ShiftPx { get; }

    /// <summary>The oldest candle drawn: the one before Layout.FirstDrawnIndex (cut by the plot's left edge, D-30, or
    /// sliding out under it while shifting), or 0 when every candle is drawn.</summary>
    public int FirstIndex { get; }

    /// <summary>The candle's centre as drawn in this frame (with the shift).</summary>
    public double CenterX(int index) => Layout.CandleCenterX(index) + ShiftPx;

    public IReadOnlyList<decimal?> Ma20 { get; }

    public IReadOnlyList<decimal?> Ma50 { get; }

    public HighLow? DayHighLow { get; }

    public PriceScale Scale { get; }

    public double PriceStep { get; }

    public IReadOnlyList<double> PriceLevels { get; }

    public IReadOnlyList<TimeTick> TimeTicks { get; }

    public IReadOnlyList<DayRangeMarker> EdgeMarkers { get; }

    public string? Countdown { get; }

    /// <summary>JetBrains Mono advances every glyph by 0.6 em, so a marker's width is known without DirectWrite.</summary>
    public const double MonoAdvance = 0.6;

    /// <summary>JetBrains Mono's line height in em (ascent 1.02 + descent 0.3).</summary>
    public const double MonoLineHeight = 1.32;

    public double MarkerSize => Layout.Text.AxisLabelPx;

    /// <summary>Whether the 24h extreme lies on the visible price scale (and is drawn as a dotted line).</summary>
    public bool OnScale(decimal price)
    {
        var y = Scale.Y(price);
        return y >= Layout.Plot.Y && y <= Layout.Plot.Bottom;
    }

    private List<DayRangeMarker> EdgeMarkersFor()
    {
        var markers = new List<DayRangeMarker>();
        if (DayHighLow is not { } hl || Candles.Count == 0)
        {
            return markers;
        }

        var plot = Layout.Plot;
        var size = MarkerSize;
        var pad = 0.6 * size;
        var height = Math.Ceiling(MonoLineHeight * size);
        foreach (var (extreme, price, text) in new[]
                 {
                     (DayExtreme.High, hl.High, $"24h high ↑ {ChartText.Price(hl.High)}"),
                     (DayExtreme.Low, hl.Low, $"24h low ↓ {ChartText.Price(hl.Low)}"),
                 })
        {
            if (OnScale(price))
            {
                continue;
            }

            var width = Math.Ceiling(text.Length * MonoAdvance * size);
            var y = extreme == DayExtreme.High ? plot.Y + (0.5 * pad) : plot.Bottom - (0.5 * pad) - height;
            var bounds = new RectPx(plot.X + pad, y, width, height);
            if (Intersects(bounds, Layout.Headline))
            {
                // The headline holds that corner (16:9: top-left); the marker goes to the other side.
                bounds = bounds with { X = plot.Right - pad - width };
            }

            markers.Add(new DayRangeMarker(extreme, price, text, bounds));
        }

        return markers;
    }

    private static bool Intersects(RectPx a, RectPx b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static DateTimeOffset NextBoundary(DateTimeOffset now, TimeSpan interval)
    {
        var ticks = now.UtcTicks - (now.UtcTicks % interval.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero) + interval;
    }

    /// <summary>"closes in hh:mm:ss": rounded up to whole seconds, never negative.</summary>
    private static string LongCountdown(DateTimeOffset closeTime, DateTimeOffset now)
    {
        var ticks = Math.Max(0, (closeTime - now).Ticks);
        var seconds = (ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"closes in {seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}");
    }

    // The mono label centred on its tick lies inside the time axis.
    private bool LabelFits(TimeTick tick)
    {
        var axis = Layout.TimeAxis;
        var half = tick.Text.Length * MonoAdvance * Layout.Text.AxisLabelPx / 2;
        return tick.X - half >= axis.X && tick.X + half <= axis.Right;
    }

    // The open time of slot i: a candle's, or for the future slots right of the newest one, extrapolated.
    private static DateTimeOffset SlotOpen(ChartFrame frame, int i) =>
        i < frame.Candles.Count
            ? frame.Candles[i].OpenTime
            : frame.Candles[^1].OpenTime + ((i - frame.Candles.Count + 1) * frame.Interval);

    // A tick that slid off the plot (while shifting, or the cut candle at the left edge) is left out.
    private static bool OnPlot(ChartLayout l, double x) => x >= l.Plot.X && x < l.Plot.Right;

    // A tick on every candle (and every future slot right of the newest) whose local time is a whole interval.
    private static List<TimeTick> TimeTicksFor(ChartFrame frame, int first, double shiftPx)
    {
        var l = frame.Layout;
        var ticks = new List<TimeTick>();
        if (frame.Candles.Count == 0)
        {
            return ticks;
        }

        var minutes = D2D.Ticks.TimeIntervalMinutes(l.CandlePitchPx, frame.Interval.TotalMinutes, 6.6 * l.Text.AxisLabelPx);
        for (var i = first; i < frame.Candles.Count + l.RightOffsetBars; i++)
        {
            var local = TimeZoneInfo.ConvertTime(SlotOpen(frame, i), frame.TimeZone);
            var minuteOfDay = (local.Hour * 60) + local.Minute;
            var x = l.CandleCenterX(i) + shiftPx;
            if (minuteOfDay % minutes == 0 && OnPlot(l, x))
            {
                var day = minuteOfDay == 0;
                ticks.Add(new TimeTick(x, local, day, local.ToString(day ? "d MMM" : "HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return ticks;
    }

    // Candles of an hour and longer: a "d MMM" tick at the local midnight of every n-th day (n = 1, 2, 3 or 7, the
    // smallest whose labels are far enough apart; weeks start on Monday), placed on the candle time scale.
    private static List<TimeTick> DayTicksFor(ChartFrame frame, int first, double shiftPx)
    {
        var l = frame.Layout;
        var ticks = new List<TimeTick>();
        if (frame.Candles.Count == 0)
        {
            return ticks;
        }

        var dayPx = TimeSpan.FromDays(1) / frame.Interval * l.CandlePitchPx;
        var step = new[] { 1, 2, 3, 7 }.FirstOrDefault(n => n * dayPx >= 6.6 * l.Text.AxisLabelPx, 7);
        for (var i = first; i < frame.Candles.Count + l.RightOffsetBars; i++)
        {
            var open = SlotOpen(frame, i);
            var end = open + frame.Interval;
            var date = TimeZoneInfo.ConvertTime(open, frame.TimeZone).Date;
            for (var day = date; ; day = day.AddDays(1))
            {
                var midnight = new DateTimeOffset(day, frame.TimeZone.GetUtcOffset(day));
                if (midnight >= end)
                {
                    break;
                }

                if (midnight < open || DateOnly.FromDateTime(day).DayNumber % step != 0)
                {
                    continue;
                }

                var x = l.CandleCenterX(i) - (l.CandlePitchPx / 2) + ((midnight - open) / frame.Interval * l.CandlePitchPx) + shiftPx;
                if (OnPlot(l, x))
                {
                    var local = TimeZoneInfo.ConvertTime(midnight, frame.TimeZone);
                    ticks.Add(new TimeTick(x, local, false, day.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        return ticks;
    }
}

/// <param name="IsDay">A day change of the intraday axis: drawn brighter.</param>
internal readonly record struct TimeTick(double X, DateTimeOffset Local, bool IsDay, string Text);

internal static class ChartText
{
    public static string Price(decimal price) => price.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}
