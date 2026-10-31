namespace BitcoinWallpaper.Chart.Core.Candles;

/// <summary>Which exchange a series comes from. A buffer never holds candles of two sources.</summary>
public enum MarketSource
{
    Binance,
    Kraken,
}
