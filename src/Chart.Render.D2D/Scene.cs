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

/// <summary>What a frame derives to, computed from the frame alone.</summary>
internal sealed class Scene
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public Scene(ChartFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Frame = frame;
        Layout = frame.Layout;
        Candles = frame.Candles;
        Last = Candles.Count > 0 ? Candles[^1] : null;
        ShiftPx = frame.ShiftPitches * Layout.CandlePitchPx;

        // While the series slides in, the candle just left of the drawn ones slides out under the plot's left edge.
        FirstIndex = ShiftPx > 0 ? Math.Max(0, Layout.FirstDrawnIndex - 1) : Layout.FirstDrawnIndex;
        Ma20 = MovingAverage.Simple(Candles, 20);
        Ma50 = MovingAverage.Simple(Candles, 50);
        DayHighLow = frame.Ticker is { } ticker
            ? DayStats.HighLowFromTicker(ticker)
            : Candles.Count > 0 ? DayStats.HighLowFromCandles(Candles, frame.Now) : null;

        Scale = PriceScale.For(Layout, Candles, [Ma20, Ma50]);
        (PriceStep, PriceLevels) = Ticks.Prices(Scale, Layout.Text.AxisLabelPx * 4.5);
        TimeTicks = TimeTicksFor(frame, FirstIndex, ShiftPx);
        EdgeMarkers = EdgeMarkersFor();

        if (Last is { } last)
        {
            var close = last.IsClosed ? NextBoundary(frame.Now) : last.OpenTime + Interval;
            Countdown = CloseCountdown.Text(close, frame.Now);
        }
    }

    public ChartFrame Frame { get; }

    public ChartLayout Layout { get; }

    public IReadOnlyList<Candle> Candles { get; }

    public Candle? Last { get; }

    /// <summary>The new-bar shift in px: every candle, average, volume bar and time tick is drawn this far right.</summary>
    public double ShiftPx { get; }

    /// <summary>The oldest candle drawn: Layout.FirstDrawnIndex, one earlier while shifting.</summary>
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

    private static DateTimeOffset NextBoundary(DateTimeOffset now)
    {
        var ticks = now.UtcTicks - (now.UtcTicks % Interval.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero) + Interval;
    }

    // A tick on every candle (and every future slot right of the newest) whose local time is a whole interval.
    // While shifting, a tick that slid off the plot is left out.
    private static List<TimeTick> TimeTicksFor(ChartFrame frame, int first, double shiftPx)
    {
        var l = frame.Layout;
        var ticks = new List<TimeTick>();
        if (frame.Candles.Count == 0)
        {
            return ticks;
        }

        var minutes = D2D.Ticks.TimeIntervalMinutes(l.CandlePitchPx, 6.6 * l.Text.AxisLabelPx);
        var last = frame.Candles[^1].OpenTime;
        for (var i = first; i < frame.Candles.Count + l.RightOffsetBars; i++)
        {
            var open = i < frame.Candles.Count ? frame.Candles[i].OpenTime : last + ((i - frame.Candles.Count + 1) * Interval);
            var local = TimeZoneInfo.ConvertTime(open, frame.TimeZone);
            var minuteOfDay = (local.Hour * 60) + local.Minute;
            var x = l.CandleCenterX(i) + shiftPx;
            if (minuteOfDay % minutes == 0 && (shiftPx == 0 || (x >= l.Plot.X && x < l.Plot.Right)))
            {
                ticks.Add(new TimeTick(x, local, minuteOfDay == 0));
            }
        }

        return ticks;
    }
}

internal readonly record struct TimeTick(double X, DateTimeOffset Local, bool IsDay);

internal static class ChartText
{
    public static string Price(decimal price) => price.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}
