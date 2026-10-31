using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>Everything one drawn frame depends on. The renderer is a pure function of this: no clock, no
/// time zone and no data are read anywhere else.</summary>
/// <param name="Layout">Computed for Candles.Count candles.</param>
/// <param name="Candles">Oldest first, one source.</param>
/// <param name="Ticker">The 24 h ticker; null hides the change and the 24h numbers.</param>
/// <param name="Now">For the "closes in mm:ss" (hh:mm:ss for an hour and longer) countdown.</param>
/// <param name="TimeZone">For the time axis labels.</param>
/// <param name="Pair">For the headline, e.g. "BTC/USDT".</param>
/// <param name="Source">For the headline, e.g. "Binance".</param>
/// <param name="IsStale">No fresh data for a while: the headline is dimmed and marked "delayed".</param>
/// <param name="ShiftPitches">The new-bar shift (D-10): the series is drawn this many candle pitches right of its
/// place; 0 for a still frame.</param>
/// <param name="Pulse">The price colour pulse (D-10), 0..1: the headline price is tinted up or down this much.</param>
/// <param name="PulseUp">The direction of the price change that started the pulse.</param>
public sealed record ChartFrame(
    ChartLayout Layout,
    IReadOnlyList<Candle> Candles,
    Ticker24h? Ticker,
    DateTimeOffset Now,
    TimeZoneInfo TimeZone,
    string Pair,
    string Source,
    bool IsStale = false,
    double ShiftPitches = 0,
    double Pulse = 0,
    bool PulseUp = false)
{
    /// <summary>The candle interval of Candles: the countdown's close time, the time axis and the caption use it.</summary>
    public TimeSpan Interval { get; init; } = CandleInterval.FiveMinutes;

    /// <summary>The mouse cursor in screen px for the crosshair (D-35), or null: no crosshair, and the frame is
    /// pixel-identical to one without the feature. The host passes null while paused; a cursor where the crosshair is
    /// hidden (<see cref="CrosshairModel.Compute"/>) draws nothing either.</summary>
    public CursorPx? Cursor { get; init; }

    /// <param name="interval">The candle interval; null: 5 minutes.</param>
    public static ChartFrame Create(
        int widthPx,
        int heightPx,
        double dpiScale,
        IReadOnlyList<Candle> candles,
        Ticker24h? ticker,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        string pair = "BTC/USDT",
        string source = "Binance",
        bool isStale = false,
        double shiftPitches = 0,
        double pulse = 0,
        bool pulseUp = false,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var candleInterval = interval ?? CandleInterval.FiveMinutes;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(candleInterval, TimeSpan.Zero, nameof(interval));
        return new ChartFrame(
            LayoutEngine.Compute(widthPx, heightPx, dpiScale, candles.Count),
            candles,
            ticker,
            now,
            timeZone,
            pair,
            source,
            isStale,
            shiftPitches,
            pulse,
            pulseUp)
        {
            Interval = candleInterval,
        };
    }
}
