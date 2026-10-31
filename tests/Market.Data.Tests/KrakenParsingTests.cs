using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Kraken;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #3, criterion: Kraken OHLC REST and WS v2 ohlc parse into the same Candle model.
// REST fixture recorded 2026-10-31 02:05:15 UTC from api.kraken.com (721 rows); WS v2 messages written in
// the official format, continuing the REST data (tests/fixtures/kraken/ws_*).
public class KrakenParsingTests
{
    [Fact]
    public void Rest_ohlc_parses_every_row_with_utc_open_time_ohlc_and_volume()
    {
        var candles = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, Fixtures.RecordedAt);

        Assert.Equal(721, candles.Count);
        // Row: time, open, high, low, close, vwap, volume, count. Volume is column 6, not the vwap.
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-28T14:05:00Z"), 80723.4m, 80739.9m, 80569.1m, 80579.2m, 10.96651075m, true),
            candles[0]);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:00:00Z"), 77243.7m, 77243.7m, 77218.6m, 77218.7m, 3.58917722m, true),
            candles[^2]);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77218.7m, 77225.2m, 77218.7m, 77225.2m, 0.59508496m, false),
            candles[^1]);
        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.OpenTime.Offset));
    }

    [Fact]
    public void Rest_ohlc_rows_match_the_raw_fixture_in_time_order()
    {
        var candles = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, Fixtures.RecordedAt);

        Assert.Equal(
            Fixtures.KrakenRestRows(),
            candles.Select(c => (c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume)));
        for (var i = 1; i < candles.Count; i++)
        {
            Assert.Equal(TimeSpan.FromMinutes(5), candles[i].OpenTime - candles[i - 1].OpenTime);
        }
    }

    [Fact]
    public void Rest_ohlc_closed_flag_follows_interval_end_against_now()
    {
        var atRecording = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, Fixtures.RecordedAt);
        Assert.All(atRecording.Take(720), c => Assert.True(c.IsClosed));
        Assert.False(atRecording[^1].IsClosed);

        var later = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, Fixtures.Utc("2026-10-31T02:10:00Z"));
        Assert.All(later, c => Assert.True(c.IsClosed));
    }

    [Fact]
    public void Ws_v2_ohlc_snapshot_parses_with_nanosecond_timestamps()
    {
        var candles = KrakenParser.ParseWsOhlc(Fixtures.KrakenWsSnapshot, Fixtures.Utc("2026-10-31T02:05:16.418Z"));

        Assert.Equal(
            new[]
            {
                new Candle(Fixtures.Utc("2026-10-31T01:55:00Z"), 77258.4m, 77259.3m, 77243.6m, 77243.7m, 1.00482762m, true),
                new Candle(Fixtures.Utc("2026-10-31T02:00:00Z"), 77243.7m, 77243.7m, 77218.6m, 77218.7m, 3.58917722m, true),
                new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77218.7m, 77225.2m, 77218.7m, 77225.2m, 0.59508496m, false),
            },
            candles);
        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.OpenTime.Offset));
    }

    [Fact]
    public void Ws_v2_ohlc_update_parses_the_forming_candle()
    {
        var candles = KrakenParser.ParseWsOhlc(Fixtures.KrakenWsUpdate, Fixtures.Utc("2026-10-31T02:05:31.01Z"));

        var candle = Assert.Single(candles);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77218.7m, 77231.0m, 77218.7m, 77229.9m, 0.81234567m, false),
            candle);
    }

    [Fact]
    public void Ws_v2_ohlc_candle_is_closed_once_its_interval_has_ended()
    {
        var candles = KrakenParser.ParseWsOhlc(Fixtures.KrakenWsUpdate, Fixtures.Utc("2026-10-31T02:10:00Z"));

        Assert.True(Assert.Single(candles).IsClosed);
    }

    [Fact]
    public void Ws_v2_messages_other_than_ohlc_give_no_candles()
    {
        var now = Fixtures.Utc("2026-10-31T02:05:16Z");

        Assert.Empty(KrakenParser.ParseWsOhlc(Fixtures.KrakenWsHeartbeat, now));
        Assert.Empty(KrakenParser.ParseWsOhlc(Fixtures.KrakenWsStatus, now));
        Assert.Empty(KrakenParser.ParseWsOhlc(Fixtures.KrakenWsSubscribeAck, now));
    }

    [Fact]
    public void Rest_and_ws_give_the_same_candle_for_the_same_interval()
    {
        var now = Fixtures.Utc("2026-10-31T02:05:16.418Z");
        var rest = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, now);
        var ws = KrakenParser.ParseWsOhlc(Fixtures.KrakenWsSnapshot, now);

        Assert.Equal(rest.TakeLast(3), ws);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("uk-UA")]
    public void Parsing_does_not_depend_on_the_machine_culture(string culture)
    {
        var now = Fixtures.RecordedAt;
        var rest = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, now);
        var ws = KrakenParser.ParseWsOhlc(Fixtures.KrakenWsSnapshot, now);

        using (new CultureScope(culture))
        {
            Assert.Equal(rest, KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, now));
            Assert.Equal(ws, KrakenParser.ParseWsOhlc(Fixtures.KrakenWsSnapshot, now));
            Assert.Equal(80723.4m, KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, now)[0].Open);
        }
    }
}
