using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Market.Data;
using BitcoinWallpaper.Market.Data.Binance;

namespace BitcoinWallpaper.Tools.RenderMockup;

/// <summary>A Binance REST snapshot as received: 5m klines, the 24 h ticker and the moment it was taken.</summary>
public sealed record Snapshot(string KlinesJson, string TickerJson, DateTimeOffset TakenAt)
{
    public const string KlinesFile = "binance_klines_5m.json";
    public const string TickerFile = "binance_ticker_24hr.json";
    public const string TakenAtFile = "taken_at.txt";

    public static readonly Uri KlinesUrl = new("https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=5m&limit=1000");
    public static readonly Uri TickerUrl = new("https://data-api.binance.vision/api/v3/ticker/24hr?symbol=BTCUSDT");

    /// <summary>The recorded snapshot the offline mockups and the golden-image test use.</summary>
    public static string FixtureDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "tests", "Chart.Render.Tests", "Fixtures");

    public static async Task<Snapshot> FetchAsync(CancellationToken cancellationToken)
    {
        using var transport = new HttpMarketTransport();
        var klines = await GetOkAsync(transport, KlinesUrl, cancellationToken);
        var ticker = await GetOkAsync(transport, TickerUrl, cancellationToken);
        return new Snapshot(klines, ticker, DateTimeOffset.UtcNow);
    }

    public static Snapshot Load(string directory) =>
        new(
            File.ReadAllText(Path.Combine(directory, KlinesFile)),
            File.ReadAllText(Path.Combine(directory, TickerFile)),
            DateTimeOffset.Parse(File.ReadAllText(Path.Combine(directory, TakenAtFile)).Trim(), CultureInfo.InvariantCulture));

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, KlinesFile), KlinesJson);
        File.WriteAllText(Path.Combine(directory, TickerFile), TickerJson);
        File.WriteAllText(Path.Combine(directory, TakenAtFile), TakenAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "\n");
    }

    public IReadOnlyList<Candle> Candles => BinanceParser.ParseKlines(KlinesJson, TakenAt);

    public Ticker24h Ticker => BinanceParser.ParseTicker24h(TickerJson);

    /// <summary>One frame per mockup screen, at the moment the snapshot was taken.</summary>
    public IEnumerable<(MockupScreen Screen, ChartFrame Frame)> Frames(TimeZoneInfo timeZone)
    {
        var candles = Candles;
        var ticker = Ticker;
        foreach (var screen in MockupScreens.All)
        {
            yield return (screen, ChartFrame.Create(screen.WidthPx, screen.HeightPx, screen.DpiScale, candles, ticker, TakenAt, timeZone));
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
