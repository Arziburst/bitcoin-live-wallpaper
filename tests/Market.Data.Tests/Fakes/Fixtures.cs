using System.Globalization;
using System.Text.Json;

namespace BitcoinWallpaper.Market.Data.Tests.Fakes;

/// <summary>Recorded fixtures under tests/fixtures (see tests/fixtures/README.md).</summary>
internal static class Fixtures
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>When the REST fixtures were recorded (Binance ticker closeTime): 2026-10-31T02:05:16.001Z.</summary>
    public static readonly DateTimeOffset RecordedAt = DateTimeOffset.FromUnixTimeMilliseconds(1793412316001);

    public static string BinanceKlines => Read("binance/rest_klines_5m.json");
    public static string BinanceTicker => Read("binance/rest_ticker_24hr.json");
    public static string BinanceKlineForming => Read("binance/ws_kline_forming.json");
    public static string BinanceKlineClosed => Read("binance/ws_kline_closed.json");
    public static string BinanceKlineNext => Read("binance/ws_kline_next.json");
    public static string BinanceMiniTicker => Read("binance/ws_miniticker.json");
    public static string BinanceSubscribeResult => Read("binance/ws_subscribe_result.json");

    public static string KrakenOhlc => Read("kraken/rest_ohlc_5m.json");
    public static string KrakenTicker => Read("kraken/rest_ticker.json");
    public static string KrakenWsSnapshot => Read("kraken/ws_ohlc_snapshot.json");
    public static string KrakenWsUpdate => Read("kraken/ws_ohlc_update.json");
    public static string KrakenWsUpdateNext => Read("kraken/ws_ohlc_update_next.json");
    public static string KrakenWsHeartbeat => Read("kraken/ws_heartbeat.json");
    public static string KrakenWsStatus => Read("kraken/ws_status.json");
    public static string KrakenWsSubscribeAck => Read("kraken/ws_subscribe_ack.json");

    public static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root.Value, "tests", "fixtures", relative));

    public static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Independent reading of the Kraken REST rows (open time, O, H, L, C, volume), as the oracle
    /// for "the buffer holds exactly the Kraken history".</summary>
    public static IReadOnlyList<(DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)> KrakenRestRows()
    {
        using var doc = JsonDocument.Parse(KrakenOhlc);
        var rows = doc.RootElement.GetProperty("result").GetProperty("XXBTZUSD");
        var list = new List<(DateTimeOffset, decimal, decimal, decimal, decimal, decimal)>();
        foreach (var row in rows.EnumerateArray())
        {
            list.Add((
                DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()),
                decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[6].GetString()!, CultureInfo.InvariantCulture)));
        }

        return list;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BitcoinWallpaper.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("BitcoinWallpaper.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Sets the current culture for a block (decimal separator traps).</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo previous = CultureInfo.CurrentCulture;
    private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;

    public CultureScope(string name)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = previous;
        CultureInfo.CurrentUICulture = previousUi;
    }
}
