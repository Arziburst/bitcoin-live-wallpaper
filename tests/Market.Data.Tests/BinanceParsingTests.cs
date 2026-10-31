using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Binance;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #3, criterion: Binance REST klines and 24h ticker JSON parse into Candle (open time UTC, OHLC,
// volume, closed flag) and Ticker24h (last, open, high, low, change abs and %, volume).
// Fixtures recorded 2026-10-31 02:05:15 UTC from data-api.binance.vision (tests/fixtures/binance/rest_*).
public class BinanceParsingTests
{
    [Fact]
    public void Klines_parse_open_time_utc_ohlc_and_volume()
    {
        var candles = BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.RecordedAt);

        Assert.Equal(5, candles.Count);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T01:45:00Z"), 77464.02m, 77492.85m, 77424.33m, 77424.34m, 9.4334m, true),
            candles[0]);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:00:00Z"), 77405.98m, 77414.01m, 77369.00m, 77385.25m, 41.98314m, true),
            candles[3]);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77385.25m, 77404.54m, 77385.24m, 77404.53m, 1.94413m, false),
            candles[4]);
        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.OpenTime.Offset));
        Assert.Equal(
            Enumerable.Range(0, 5).Select(i => Fixtures.Utc("2026-10-31T01:45:00Z").AddMinutes(5 * i)),
            candles.Select(c => c.OpenTime));
    }

    [Fact]
    public void Kline_closed_flag_follows_close_time_against_now()
    {
        // At recording time only the 02:05 candle (close time 02:09:59.999) is still forming.
        var atRecording = BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.RecordedAt);
        Assert.Equal(new[] { true, true, true, true, false }, atRecording.Select(c => c.IsClosed));

        // From 02:10:00 on, every candle of the fixture is closed.
        var later = BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.Utc("2026-10-31T02:10:00Z"));
        Assert.All(later, c => Assert.True(c.IsClosed));
    }

    [Fact]
    public void Ticker_24h_parses_last_open_high_low_change_and_volume()
    {
        var ticker = BinanceParser.ParseTicker24h(Fixtures.BinanceTicker);

        Assert.Equal(new Ticker24h(
            Last: 77404.54m,
            Open: 76559.39m,
            High: 78804.01m,
            Low: 76325.98m,
            ChangeAbs: 845.15m,
            ChangePercent: 1.104m,
            Volume: 18988.74199m), ticker);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("uk-UA")]
    [InlineData("fr-FR")]
    public void Parsing_does_not_depend_on_the_machine_culture(string culture)
    {
        var expectedCandles = BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.RecordedAt);
        var expectedTicker = BinanceParser.ParseTicker24h(Fixtures.BinanceTicker);

        using (new CultureScope(culture))
        {
            Assert.Equal(expectedCandles, BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.RecordedAt));
            Assert.Equal(expectedTicker, BinanceParser.ParseTicker24h(Fixtures.BinanceTicker));
            Assert.Equal(77464.02m, BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.RecordedAt)[0].Open);
        }
    }
}
