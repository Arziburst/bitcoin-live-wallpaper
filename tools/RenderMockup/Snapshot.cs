using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Market.Data;
using BitcoinWallpaper.Market.Data.Binance;

namespace BitcoinWallpaper.Tools.RenderMockup;

/// <summary>A Binance REST snapshot as received: the klines of each mockup screen's interval (D-13: 4h on the desk
/// monitor, 6h on the TV), the 24 h ticker and the moment it was taken.</summary>
public sealed record Snapshot(IReadOnlyDictionary<TimeSpan, string> KlinesJson, string TickerJson, DateTimeOffset TakenAt)
{
    public const string TickerFile = "binance_ticker_24hr.json";
    public const string TakenAtFile = "taken_at.txt";

    public static readonly Uri TickerUrl = new("https://data-api.binance.vision/api/v3/ticker/24hr?symbol=BTCUSDT");

    /// <summary>The intervals the mockup screens show (IntervalPolicy.ForScreen at 30 days), ascending.</summary>
    public static IReadOnlyList<TimeSpan> Intervals { get; } =
        MockupScreens.All.Select(IntervalFor).Distinct().Order().ToList();

    /// <summary>"binance_klines_4h.json".</summary>
    public static string KlinesFile(TimeSpan interval) => $"binance_klines_{CandleInterval.Code(interval)}.json";

    /// <summary>The Binance maximum: enough for 30 days and every drawable slot (D-29).</summary>
    public static Uri KlinesUrl(TimeSpan interval) =>
        new($"https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval={CandleInterval.Code(interval)}&limit=1000");

    public static TimeSpan IntervalFor(MockupScreen screen) =>
        IntervalPolicy.ForScreen(screen.WidthPx, screen.HeightPx, screen.DpiScale, IntervalPolicy.DefaultPeriod);

    /// <summary>The recorded snapshot the offline mockups and the golden-image test use.</summary>
    public static string FixtureDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "tests", "Chart.Render.Tests", "Fixtures");

    public static async Task<Snapshot> FetchAsync(CancellationToken cancellationToken)
    {
        using var transport = new HttpMarketTransport();
        var klines = new Dictionary<TimeSpan, string>();
        foreach (var interval in Intervals)
        {
            klines[interval] = await GetOkAsync(transport, KlinesUrl(interval), cancellationToken);
        }

        var ticker = await GetOkAsync(transport, TickerUrl, cancellationToken);
        return new Snapshot(klines, ticker, DateTimeOffset.UtcNow);
    }

    public static Snapshot Load(string directory) =>
        new(
            Intervals.ToDictionary(i => i, i => File.ReadAllText(Path.Combine(directory, KlinesFile(i)))),
            File.ReadAllText(Path.Combine(directory, TickerFile)),
            DateTimeOffset.Parse(File.ReadAllText(Path.Combine(directory, TakenAtFile)).Trim(), CultureInfo.InvariantCulture));

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (interval, json) in KlinesJson)
        {
            File.WriteAllText(Path.Combine(directory, KlinesFile(interval)), json);
        }

        File.WriteAllText(Path.Combine(directory, TickerFile), TickerJson);
        File.WriteAllText(Path.Combine(directory, TakenAtFile), TakenAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "\n");
    }

    public IReadOnlyList<Candle> Candles(TimeSpan interval) => BinanceParser.ParseKlines(KlinesJson[interval], TakenAt);

    public Ticker24h Ticker => BinanceParser.ParseTicker24h(TickerJson);

    /// <summary>One frame per mockup screen, at its interval, at the moment the snapshot was taken.</summary>
    public IEnumerable<(MockupScreen Screen, ChartFrame Frame)> Frames(TimeZoneInfo timeZone)
    {
        var ticker = Ticker;
        foreach (var screen in MockupScreens.All)
        {
            var interval = IntervalFor(screen);
            yield return (screen, ChartFrame.Create(
                screen.WidthPx, screen.HeightPx, screen.DpiScale, Candles(interval), ticker, TakenAt, timeZone, interval: interval));
        }
    }

    private static async Task<string> GetOkAsync(IMarketTransport transport, Uri url, CancellationToken cancellationToken)
    {
        var result = await transport.GetAsync(url, cancellationToken);
        if (result.StatusCode != 200)
        {
            throw new HttpRequestException($"{url} answered {result.StatusCode}: {result.Body[..Math.Min(200, result.Body.Length)]}");
        }

        return result.Body;
    }
}
