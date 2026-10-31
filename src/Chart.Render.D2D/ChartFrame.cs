using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>Everything one drawn frame depends on. The renderer is a pure function of this: no clock, no
/// time zone and no data are read anywhere else.</summary>
/// <param name="Layout">Computed for Candles.Count candles.</param>
/// <param name="Candles">Oldest first, one source.</param>
/// <param name="Ticker">The 24 h ticker; null hides the change and the 24h numbers.</param>
/// <param name="Now">For the "closes in mm:ss" countdown.</param>
/// <param name="TimeZone">For the time axis labels.</param>
/// <param name="Pair">For the headline, e.g. "BTC/USDT".</param>
/// <param name="Source">For the headline, e.g. "Binance".</param>
/// <param name="IsStale">No fresh data for a while: the headline is dimmed and marked "delayed".</param>
public sealed record ChartFrame(
    ChartLayout Layout,
    IReadOnlyList<Candle> Candles,
    Ticker24h? Ticker,
    DateTimeOffset Now,
    TimeZoneInfo TimeZone,
    string Pair,
    string Source,
    bool IsStale = false)
{
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
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(candles);
        return new ChartFrame(
            LayoutEngine.Compute(widthPx, heightPx, dpiScale, candles.Count),
            candles,
            ticker,
            now,
            timeZone,
            pair,
            source,
            isStale);
    }
}
