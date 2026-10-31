using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Desktop;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Core.Motion;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Host.Playback;
using BitcoinWallpaper.Host.Settings;
using BitcoinWallpaper.Market.Data;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace BitcoinWallpaper.Host.Scene;

/// <summary>
/// The live BTC chart (contract #8): the market data client's candles and ticker, read on the host thread every
/// 250 ms, go through <see cref="LiveScene"/> (forming tween, new-bar shift, price pulse, stale headline) into
/// <see cref="ChartRenderer"/>. The pause monitor's events come from its timer thread and are posted to the host
/// thread first. The data client keeps running while paused; the frames stop.
/// </summary>
internal sealed partial class LiveChartScene : IAnimatedScene
{
    private readonly MarketDataClient _client;
    private readonly IPlaybackMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly IDWriteFactory5 _dwrite;
    private readonly ChartFonts _fonts;
    private readonly ChartRenderer _renderer;
    private readonly PauseCombiner _pause = new();

    private WallpaperHost? _host;
    private LiveScene? _live;
    private FeedState? _feedState;
    private MarketSource? _source;
    private FeedMode? _mode;
    private bool _firstDataLogged;
    private DateTimeOffset? _lastLiveAt;

    /// <summary>The candle interval shown (D-13), chosen for the screen on the first frame and on display changes;
    /// null before the first frame.</summary>
    private TimeSpan? _interval;

    private (int Width, int Height, double Scale)? _screen;

    public LiveChartScene(MarketDataClient client, IPlaybackMonitor monitor, TimeProvider time)
    {
        _client = client;
        _monitor = monitor;
        _time = time;
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory5>(Vortice.DirectWrite.FactoryType.Isolated);
        _fonts = ChartFonts.Load(_dwrite);
        _renderer = new ChartRenderer(_dwrite, _fonts);
    }

    public TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(250);

    public bool NeedsFrame => _live?.NeedsFrame ?? false;

    public void Start(WallpaperHost host)
    {
        if (_live is not null)
        {
            return;
        }

        _host = host;
        _live = new LiveScene(_time, host);
        _monitor.PausedChanged += OnPausedChanged;
        _monitor.Start();

        // What happened before the window existed is applied at once: the current pause state and data.
        ApplyMonitor(_monitor.State, _monitor.LastVerdict.Reason);
        Poll();
        _live.OnHostStarted();
    }

    public void Poll()
    {
        if (_live is null)
        {
            return;
        }

        var state = _client.State;
        if (state != _feedState)
        {
            Log.Info($"market data: {_feedState?.ToString() ?? "start"} -> {state}");
            _feedState = state;
        }

        if (_client.Source != _source || _client.Mode != _mode)
        {
            _source = _client.Source;
            _mode = _client.Mode;
            Log.Info($"market data source: {_source} over {(_mode == FeedMode.RestPoll ? "REST polling" : "WebSocket")}");
        }

        var candles = _client.Candles.Snapshot();
        if (!_firstDataLogged && candles.Count > 0)
        {
            _firstDataLogged = true;
            Log.Info($"market data: first {candles.Count} candles, latest {candles[^1].OpenTime:HH:mm}Z close {candles[^1].Close}");
        }

        _live.OnData(candles, _client.Ticker);
        _live.OnFeedStale(state == FeedState.Stale);
        NoteLive(state);
    }

    // D-26: "offline since HH:mm" is the last poll that saw the feed live (Market.Data keeps no such time).
    private void NoteLive(FeedState state)
    {
        if (state == FeedState.Live)
        {
            _lastLiveAt = _time.GetUtcNow();
        }
    }

    public TrayStatus TrayStatus => new(
        WallpaperState.On,
        _pause.Verdict.State == PlaybackState.Paused ? _pause.Verdict.Reason : PauseReason.None,
        _feedState ?? FeedState.Connecting,
        _source ?? _client.Source,
        _client.Ticker?.Last,
        _lastLiveAt);

    public void Draw(ID2D1DeviceContext context, SurfacePlacement placement)
    {
        var now = _time.GetUtcNow();
        if (_live is null)
        {
            _renderer.Draw(context, ChartFrame.Create(placement.Width, placement.Height, placement.Scale, [], null, now, TimeZoneInfo.Local));
            return;
        }

        OnDisplay(placement);
        var frame = _live.TakeFrame();
        var kraken = _client.Candles.Source == MarketSource.Kraken;
        _lastFrame = ChartFrame.Create(
            placement.Width,
            placement.Height,
            placement.Scale,
            frame.Candles,
            frame.Ticker,
            now,
            TimeZoneInfo.Local,
            pair: kraken ? "BTC/USD" : "BTC/USDT",
            source: kraken ? "Kraken" : "Binance",
            isStale: frame.HeadlineDimmed,
            shiftPitches: frame.ShiftPitches,
            pulse: frame.Pulse,
            pulseUp: frame.PulseUp,
            interval: _interval);
        _renderer.Draw(context, _lastFrame with { Cursor = _cursor });
    }

    public void Dispose()
    {
        _monitor.PausedChanged -= OnPausedChanged;
        _renderer.Dispose();
        _fonts.Dispose();
        _dwrite.Dispose();
    }

    // The first frame and every display change: the screen's interval for 30 days (D-13). Another interval is a full
    // reload: the client drops the old series at once and fetches the new one, the scene forgets what it showed, so
    // intervals are never mixed - not in the buffer and not on screen.
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

        Log.Info($"candle interval: {(_interval is { } old ? CandleInterval.Code(old) : "none")} -> {CandleInterval.Code(decision.Interval)} " +
                 $"for {placement.Width}x{placement.Height} @ {placement.Scale:0.##}; reloading");
        _interval = decision.Interval;
        _client.ChangeInterval(decision.Interval);
        _live!.Reset();
    }

    // The monitor's timer thread: only post; LiveScene is touched on the host thread.
    private void OnPausedChanged(object? sender, PausedChangedEventArgs e)
    {
        var state = e.State;
        var reason = e.Verdict.Reason;
        _host?.Post(() =>
        {
            Log.Info(state == PlaybackState.Paused ? $"pause monitor: paused ({reason})" : "pause monitor: running");
            ApplyMonitor(state, reason);
        });
    }

    /// <summary>The combined pause (the monitor's reason, else DisplayOff); Running when neither pauses.</summary>
    public PauseVerdict Pause => _pause.Verdict;

    // D-15: the display state and the monitor's verdict go through one PauseCombiner into the one pause path.
    public void SetDisplayOn(bool displayOn)
    {
        _pause.SetDisplayOn(displayOn);
        _live?.OnPlayback(_pause.Verdict.State);
    }

    // #23: Off from the tray stops the pause monitor's polling; a scene not started yet has nothing to stop or start.
    public void Suspend()
    {
        if (_live is not null)
        {
            _monitor.Stop();
        }
    }

    public void Resume()
    {
        if (_live is not null)
        {
            _monitor.Start();
        }
    }

    private void ApplyMonitor(PlaybackState state, PauseReason reason)
    {
        _pause.SetMonitor(state, reason);
        _live?.OnPlayback(_pause.Verdict.State);
    }
}
