using System.Globalization;
using BitcoinWallpaper.Chart.Core.Desktop;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Host.LockScreenChart;
using BitcoinWallpaper.Host.Playback;
using BitcoinWallpaper.Host.Scene;
using BitcoinWallpaper.Host.Screensaver;
using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.Tests;

// Contract #30, criterion 3 (D-25): the three renderers draw with the exchange's now. With the exchange clock 90 s
// ahead of / behind the PC clock, the frame each of them builds carries ChartFrame.Now = the exchange time (the
// countdown and the candle boundary come from it); the tests read it off the frame.
//
// API SKETCH (written before the code, #237). The worker implements these access points as written:
//   1. src/Wallpaper.Host/Wallpaper.Host.csproj: <InternalsVisibleTo Include="Wallpaper.Host.Tests" /> (the three
//      classes below are internal; they stay internal).
//   2. MarketDataClient.Clock (ExchangeClock) and MarketDataClient.ExchangeNow (DateTimeOffset): see
//      tests/Market.Data.Tests/ExchangeTimeClientTests.cs.
//   3. ScreensaverScene: internal ChartFrame BuildFrame(SurfacePlacement placement) - the frame Draw hands to the
//      renderer (Draw = BuildFrame + renderer.Draw). Now = _client.ExchangeNow.
//   4. LiveChartScene: internal ChartFrame BuildFrame(SurfacePlacement placement) - the same for the wallpaper, the
//      cursor excluded; also for a scene that was not started (the empty frame). Now = _client.ExchangeNow.
//   5. LockScreenUpdater: the primary constructor gets a last optional parameter
//        Action<ChartFrame>? frameSink = null
//      called with the frame (inside Tick, on the host thread) just before it is queued for rendering; the frame's
//      Now = client.ExchangeNow. The refresh timing (CheckEvery, LockScreenRefreshPolicy) keeps `time`.
// The client runs on the real clock against an in-process fake exchange (no network): its /time answer is the
// local time + the skew.
public sealed class ExchangeClockHostTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly SurfacePlacement Surface = new("DISPLAY1", 0, 0, 1280, 720, 96, 1.0);

    public static TheoryData<int> Skews => [90, -90, 0];

    private static async Task<(MarketDataClient Client, CancellationTokenSource Stop, Task Run)> LiveClientAsync(int skewSeconds)
    {
        var client = new MarketDataClient(new SkewedExchange(TimeSpan.FromSeconds(skewSeconds)), TimeProvider.System, () => 0.5);
        var stop = new CancellationTokenSource();
        var run = Task.Run(() => client.RunAsync(stop.Token));
        var until = DateTime.UtcNow + Window;
        while ((client.State != FeedState.Live || !client.Clock.HasOffset) && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FeedState.Live, client.State);
        Assert.True(client.Clock.HasOffset, "the client took no time sample from the fake exchange");
        return (client, stop, run);
    }

    private static void AssertExchangeNow(ChartFrame frame, int skewSeconds, string who)
    {
        var ahead = frame.Now - DateTimeOffset.UtcNow;
        Assert.True(
            (ahead - TimeSpan.FromSeconds(skewSeconds)).Duration() < TimeSpan.FromSeconds(5),
            $"{who}: the frame's Now is {ahead.TotalSeconds:0.#} s ahead of the PC clock, the exchange is {skewSeconds} s ahead");
    }

    [Theory]
    [MemberData(nameof(Skews))]
    public async Task The_screensaver_frame_carries_the_exchange_time(int skewSeconds)
    {
        var (client, stop, run) = await LiveClientAsync(skewSeconds);
        try
        {
            using var scene = new ScreensaverScene(client, TimeProvider.System, () => { });
            scene.Start();
            AssertExchangeNow(scene.BuildFrame(Surface), skewSeconds, "screensaver");
        }
        finally
        {
            await StopAsync(stop, run);
        }
    }

    [Theory]
    [MemberData(nameof(Skews))]
    public async Task The_wallpaper_frame_carries_the_exchange_time(int skewSeconds)
    {
        var (client, stop, run) = await LiveClientAsync(skewSeconds);
        try
        {
            using var scene = new LiveChartScene(client, new IdleMonitor(), TimeProvider.System);
            AssertExchangeNow(scene.BuildFrame(Surface), skewSeconds, "wallpaper (not started)");
        }
        finally
        {
            await StopAsync(stop, run);
        }
    }

    [Theory]
    [MemberData(nameof(Skews))]
    public async Task The_lock_screen_snapshot_frame_carries_the_exchange_time(int skewSeconds)
    {
        var (client, stop, run) = await LiveClientAsync(skewSeconds);
        var folder = Path.Combine(Path.GetTempPath(), "bw-exchange-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            ChartFrame? seen = null;
            var images = new LockScreenImages(new NullApi(), new DiskFiles(), folder, _ => { });
            var updater = new LockScreenUpdater(client, images, TimeProvider.System, featureOn: true, frameSink: f => seen = f);

            var until = DateTime.UtcNow + Window;
            while (seen is null && DateTime.UtcNow < until)
            {
                updater.Tick(Surface, wallpaperOn: true, tweenRunning: false);
                await Task.Delay(50);
            }

            updater.WaitIdle(TimeSpan.FromSeconds(10));
            Assert.NotNull(seen);
            AssertExchangeNow(seen!, skewSeconds, "lock screen snapshot");
        }
        finally
        {
            await StopAsync(stop, run);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static async Task StopAsync(CancellationTokenSource stop, Task run)
    {
        await stop.CancelAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            // The client ended (or is ending) with the cancellation.
        }

        stop.Dispose();
    }

    private sealed class IdleMonitor : IPlaybackMonitor
    {
        public PlaybackState State => default;

        public PauseVerdict LastVerdict => default;

#pragma warning disable CS0067
        public event EventHandler<PausedChangedEventArgs>? PausedChanged;

        public event EventHandler<PlaybackSample>? Sampled;
#pragma warning restore CS0067

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullApi : ILockScreenApi
    {
        public string? OriginalImageFile => null;

        public Task SetImageFileAsync(string path) => Task.CompletedTask;
    }

    private sealed class DiskFiles : ILockScreenFiles
    {
        public bool Exists(string path) => File.Exists(path);

        public void CreateDirectory(string folder) => Directory.CreateDirectory(folder);

        public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: true);

        public void Delete(string path) => File.Delete(path);

        public IReadOnlyList<string> List(string folder) =>
            Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

        public string ReadAllText(string path) => File.ReadAllText(path);

        public void WriteAllText(string path, string text) => File.WriteAllText(path, text);
    }

    /// <summary>A healthy Binance, in process: history, ticker, a silent socket, and /time = the PC clock + skew.</summary>
    private sealed class SkewedExchange(TimeSpan skew) : IMarketTransport
    {
        public Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            var path = url.AbsolutePath;
            if (path.EndsWith("/time", StringComparison.OrdinalIgnoreCase))
            {
                var server = (DateTimeOffset.UtcNow + skew).ToUnixTimeMilliseconds();
                return Task.FromResult(new HttpResult(200, "{\"serverTime\":" + server.ToString(CultureInfo.InvariantCulture) + "}"));
            }

            if (path.Contains("klines", StringComparison.OrdinalIgnoreCase))
            {
                var five = TimeSpan.FromMinutes(5).Ticks;
                var open = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / five * five, TimeSpan.Zero);
                var rows = Enumerable.Range(0, 20).Select(i =>
                {
                    var t = open.AddMinutes(5 * (i - 19));
                    var ms = t.ToUnixTimeMilliseconds();
                    var price = (70000 + i).ToString(CultureInfo.InvariantCulture) + ".00";
                    return $"[{ms},\"{price}\",\"{price}\",\"{price}\",\"{price}\",\"1.0\",{ms + 299999},\"1.0\",1,\"1.0\",\"1.0\",\"0\"]";
                });
                return Task.FromResult(new HttpResult(200, "[" + string.Join(",", rows) + "]"));
            }

            return Task.FromResult(new HttpResult(200,
                "{\"lastPrice\":\"70019.00\",\"openPrice\":\"69000.00\",\"highPrice\":\"71000.00\",\"lowPrice\":\"68000.00\"," +
                "\"priceChange\":\"1019.00\",\"priceChangePercent\":\"1.5\",\"volume\":\"100.0\"}"));
        }

        public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult<IMarketSocket>(new SilentSocket());
    }

    private sealed class SilentSocket : IMarketSocket
    {
        public Task SendAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
