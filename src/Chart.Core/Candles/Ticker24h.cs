namespace BitcoinWallpaper.Chart.Core.Candles;

/// <summary>24 h ticker (in Chart.Core so the renderer can use it without Market.Data).
/// ChangeAbs = Last - Open; ChangePercent = ChangeAbs / Open * 100. Volume is the base-asset (BTC) volume.</summary>
public sealed record Ticker24h(
    decimal Last,
    decimal Open,
    decimal High,
    decimal Low,
    decimal ChangeAbs,
    decimal ChangePercent,
    decimal Volume);
