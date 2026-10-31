using System.Diagnostics;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Desktop;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.LockScreenChart;

/// <summary>
/// Keeps the chart on the lock screen (D-34). The host calls <see cref="Tick"/> on its thread after every scene poll;
/// at most once a second (and never while a tween runs) it asks <see cref="LockScreenRefreshPolicy"/>, which covers
/// the first live data, a new candle and the 30-minute refresh. A due refresh takes the client's candles as they are
/// (the resting picture, no tween), and renders them offscreen with the wallpaper's own renderer at the wallpaper's
/// monitor size, writes the PNG and sets it on the thread pool: the host thread never waits for it. Turning the tray
/// item off restores the user's picture. One lock screen job at a time, in order.
/// </summary>
internal sealed class LockScreenUpdater(MarketDataClient client, LockScreenImages images, TimeProvider time, bool featureOn)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly LockScreenRefreshPolicy _policy = new() { FeatureOn = featureOn };
    private Task _work = Task.CompletedTask;
    private bool _busy;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private SurfacePlacement? _placement;
    private string? _error;

    /// <summary>The last failure for the tray tooltip; null after a success or when the item is turned off.</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>Host thread, after each scene poll. <paramref name="placement"/>: the wallpaper's surface (the last one
    /// seen is kept while it is detached).</summary>
    public void Tick(SurfacePlacement? placement, bool wallpaperOn, bool tweenRunning)
    {
        lock (_gate)
        {
            _policy.WallpaperOn = wallpaperOn;
            _placement = placement ?? _placement;
            var now = time.GetUtcNow();
            if (_busy || tweenRunning || _placement is not { } surface || now - _lastCheck < CheckEvery)
            {
                return;
            }

            _lastCheck = now;
            var live = client.State == FeedState.Live;
            if (!live || !_policy.FeatureOn || !wallpaperOn)
            {
                return;   // the policy would say no; skip the candle copy
            }

            var candles = client.Candles.Snapshot();
            DateTimeOffset? candleOpen = candles.Count > 0 ? candles[^1].OpenTime : null;
            if (candles.Count == 0 || !_policy.ShouldRefresh(now, live, candleOpen))
            {
                return;
            }

            var kraken = client.Candles.Source == MarketSource.Kraken;
            var frame = ChartFrame.Create(
                surface.Width,
                surface.Height,
                surface.Scale,
                candles,
                client.Ticker,
                now,
                TimeZoneInfo.Local,
                pair: kraken ? "BTC/USD" : "BTC/USDT",
                source: kraken ? "Kraken" : "Binance",
                interval: client.Interval);
            _busy = true;
            Enqueue(() => SetAsync(frame, now, candleOpen));
        }
    }

    /// <summary>The tray item changed (host thread). Off restores the user's picture; on refreshes at the next tick.</summary>
    public void FeatureChanged(bool on)
    {
        lock (_gate)
        {
            _policy.FeatureOn = on;
            _lastCheck = DateTimeOffset.MinValue;
            _error = null;
        }

        Log.Info($"lock screen: chart on the lock screen turned {(on ? "on" : "off")}");
        if (!on)
        {
            Enqueue(RestoreAsync);
        }
    }

    /// <summary>On exit: lets a running job finish (a set half done would leave a temporary file).</summary>
    public void WaitIdle(TimeSpan timeout)
    {
        Task work;
        lock (_gate)
        {
            work = _work;
        }

        if (!work.Wait(timeout))
        {
            Log.Warn($"lock screen: a job did not finish within {timeout.TotalSeconds:0} s");
        }
    }

    private async Task SetAsync(ChartFrame frame, DateTimeOffset now, DateTimeOffset? candleOpen)
    {
        var clock = Stopwatch.StartNew();
        LockScreenResult result;
        try
        {
            result = await images.SetSnapshotAsync(path =>
            {
                // The software renderer is made, used and released on this pool thread: nothing stays in memory
                // between the refreshes.
                using var renderer = new HeadlessRenderer();
                renderer.RenderToPng(frame, path);
                return Task.CompletedTask;
            }, now).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            result = new LockScreenResult(false, null, e.Message);
        }

        lock (_gate)
        {
            _busy = false;
            if (result.Ok)
            {
                _policy.Refreshed(now, candleOpen);
                _error = null;
            }
            else
            {
                _policy.Failed(now);
                _error = _policy.FeatureOn ? result.Error : null;
            }
        }

        if (result.Ok)
        {
            Log.Info($"lock screen: set {result.Path} ({frame.Layout.WidthPx}x{frame.Layout.HeightPx}, {clock.ElapsedMilliseconds} ms)");
        }
        else
        {
            Log.Warn($"lock screen: the snapshot was not set, next try in {LockScreenRefreshPolicy.Interval.TotalMinutes:0} minutes: {result.Error}");
        }
    }

    private async Task RestoreAsync()
    {
        var result = await images.RestoreAsync(time.GetUtcNow()).ConfigureAwait(false);
        lock (_gate)
        {
            _error = result.Ok || _policy.FeatureOn ? null : result.Error;
        }
    }

    private void Enqueue(Func<Task> job)
    {
        lock (_gate)
        {
            _work = _work
                .ContinueWith(_ => job(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap()
                .ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                        {
                            Log.Error($"lock screen: a job failed: {t.Exception?.GetBaseException()}");
                        }
                    },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
