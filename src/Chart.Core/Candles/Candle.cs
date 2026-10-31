namespace BitcoinWallpaper.Chart.Core.Candles;

/// <summary>One candle. OpenTime is UTC (Offset == 0). IsClosed is false for the forming candle.</summary>
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsClosed);
