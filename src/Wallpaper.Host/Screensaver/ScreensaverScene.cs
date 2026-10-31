using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Desktop;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Core.Motion;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Market.Data;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// The screensaver's live chart (D-21): the same <see cref="LiveScene"/> motion and <see cref="ChartRenderer"/> as the
/// wallpaper's LiveChartScene, fed by this process's own market data client, with no pause rules (the screensaver is
/// the foreground). Host thread only.
/// </summary>
internal sealed class ScreensaverScene : IWallpaperScene, IFrameHost, IDisposable
{
    private readonly MarketDataClient _client;
    private readonly TimeProvider _time;
    private readonly Action _requestFrame;
    private readonly IDWriteFactory5 _dwrite;
    private readonly ChartFonts _fonts;
    private readonly ChartRenderer _renderer;
    private readonly LiveScene _live;

    private FeedState? _feedState;
    private TimeSpan? _interval;
    private (int Width, int Height, double Scale)? _screen;

    public ScreensaverScene(MarketDataClient client, TimeProvider time, Action requestFrame)
    {
        _client = client;
        _time = time;
        _requestFrame = requestFrame;
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory5>(Vortice.DirectWrite.FactoryType.Isolated);
        _fonts = ChartFonts.Load(_dwrite);
        _renderer = new ChartRenderer(_dwrite, _fonts);
        _live = new LiveScene(time, this);
    }

    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(250);

    public bool NeedsFrame => _live.NeedsFrame;

    /// <summary>The window exists: what arrived before is applied and one frame is asked for.</summary>
    public void Start()
    {
        Poll();
        _live.OnHostStarted();
    }

    public void Poll()
    {
        var state = _client.State;
        if (state != _feedState)
        {
            ScreensaverLog.Info($"market data: {_feedState?.ToString() ?? "start"} -> {state}");
            _feedState = state;
        }

        _live.OnData(_client.Candles.Snapshot(), _client.Ticker);
        _live.OnFeedStale(state == FeedState.Stale);
    }

    public void Draw(ID2D1DeviceContext context, SurfacePlacement placement)
    {
        OnDisplay(placement);
        var frame = _live.TakeFrame();
        var kraken = _client.Candles.Source == MarketSource.Kraken;
        _renderer.Draw(context, ChartFrame.Create(
            placement.Width,
            placement.Height,
            placement.Scale,
            frame.Candles,
            frame.Ticker,
            _time.GetUtcNow(),
            TimeZoneInfo.Local,
            pair: kraken ? "BTC/USD" : "BTC/USDT",
            source: kraken ? "Kraken" : "Binance",
            isStale: frame.HeadlineDimmed,
            shiftPitches: frame.ShiftPitches,
            pulse: frame.Pulse,
            pulseUp: frame.PulseUp,
            interval: _interval,
            content: SurfaceContent.FullMonitor(placement.Width, placement.Height))); // D-37: the screensaver covers the taskbar
    }

    void IFrameHost.RequestFrame() => _requestFrame();

    // The screensaver has no pause rules; LiveScene is never told about playback, so this is never called.
    void IFrameHost.SetPaused(bool paused)
    {
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _fonts.Dispose();
        _dwrite.Dispose();
    }

    // The same interval choice as the wallpaper (D-13): decided for the screen on the first frame.
    private void OnDisplay(SurfacePlacement placement)
    {
        var screen = (placement.Width, placement.Height, placement.Scale);
        if (screen == _screen)
        {
            return;
        }

        _screen = screen;
        var decision = IntervalPolicy.OnDisplay(_interval, placement.Width, placement.Height, placement.Scale, IntervalPolicy.DefaultPeriod);
        if (!decision.Reload)
        {
            return;
        }

        ScreensaverLog.Info($"candle interval {CandleInterval.Code(decision.Interval)} for {placement.Width}x{placement.Height} " +
                            $"@ {placement.Scale:0.##}");
        _interval = decision.Interval;
        _client.ChangeInterval(decision.Interval);
        _live.Reset();
    }
}
