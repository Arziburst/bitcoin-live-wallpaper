using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data;

/// <summary>A parsed stream message.</summary>
public abstract record MarketEvent;

public sealed record CandleUpdate(Candle Candle) : MarketEvent;

public sealed record TickerUpdate(Ticker24h Ticker) : MarketEvent;

/// <summary>Connecting: no market data received yet. Live: data within the last 30 s.
/// Stale: more than 30 s since the last market data (REST or stream).</summary>
public enum FeedState
{
    Connecting,
    Live,
    Stale,
}
