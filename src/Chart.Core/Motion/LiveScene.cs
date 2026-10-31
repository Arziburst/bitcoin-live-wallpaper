using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Motion;

/// <summary>The host side of <see cref="LiveScene"/>; the wallpaper host implements it.</summary>
public interface IFrameHost
{
    /// <summary>Present one frame soon.</summary>
    void RequestFrame();

    /// <summary>While paused nothing is presented.</summary>
    void SetPaused(bool paused);
}

/// <summary>One frame to draw.</summary>
/// <param name="Candles">The latest snapshot, oldest first; its last candle at its animated OHLC.</param>
/// <param name="Ticker">The latest 24 h ticker.</param>
/// <param name="HeadlineDimmed">Feed stale: the headline is drawn dimmed.</param>
/// <param name="ShowsDelayed">Feed stale: the headline caption shows "delayed".</param>
/// <param name="ShiftPitches">The new-bar shift at the frame's time, in candle pitches.</param>
/// <param name="Pulse">The price colour pulse strength, 0..1.</param>
/// <param name="PulseUp">The direction of the close change that started the pulse.</param>
public sealed record LiveFrame(
    IReadOnlyList<Candle> Candles,
    Ticker24h? Ticker,
    bool HeadlineDimmed,
    bool ShowsDelayed,
    double ShiftPitches,
    double Pulse,
    bool PulseUp);

/// <summary>
/// The wiring between the feed, the pause monitor and the host (contract #8): data changes ask the host for a
/// frame, the frames of a tween are pulled by the host through <see cref="NeedsFrame"/> / <see cref="TakeFrame"/>,
/// a pause stops the frames (the data is still applied), a resume gives one frame with the latest data, and
/// everything asked before the host window exists is applied in <see cref="OnHostStarted"/>. Single-threaded:
/// the host calls every member on its own thread.
/// </summary>
public sealed class LiveScene
{
    private readonly TimeProvider time;
    private readonly IFrameHost host;
    private ChartMotion motion;

    private IReadOnlyList<Candle> candles = [];
    private Ticker24h? ticker;
    private bool stale;
    private bool paused;
    private bool started;

    /// <summary>Something arrived before the host started: one frame is requested at start.</summary>
    private bool pendingFrame;

    /// <summary>The last candle given to the motion.</summary>
    private Candle? applied;

    /// <summary>A forming-candle update waiting for the coalescing interval to end.</summary>
    private Candle? held;

    private DateTimeOffset? lastFormingTween;

    public LiveScene(TimeProvider time, IFrameHost host)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(host);
        this.time = time;
        this.host = host;
        motion = new ChartMotion(time);
    }

    /// <summary>False while paused; otherwise whether a tween still needs frames.</summary>
    public bool NeedsFrame => !paused && motion.NeedsFrame;

    public bool IsPaused => paused;

    /// <summary>The feed's current snapshot, oldest first.</summary>
    public void OnData(IReadOnlyList<Candle> snapshot, Ticker24h? latestTicker)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Equals(latestTicker, ticker) && snapshot.SequenceEqual(candles) && held is null)
        {
            return;
        }

        var tickerChanged = !Equals(latestTicker, ticker);
        var restChanged = !SameButLast(snapshot, candles);
        candles = snapshot;
        ticker = latestTicker;
        var moved = snapshot.Count > 0 && ApplyCoalesced(snapshot[^1]);
        if (tickerChanged || restChanged || moved)
        {
            Changed();
        }
    }

    /// <summary>At most one forming-candle tween starts per this interval, on any feed (#23, D-6).</summary>
    public static TimeSpan FormingMinInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Feeds the latest candle to the motion, coalescing forming-candle tweens: inside the interval after a tween
    /// started the candle is held (the newest replaces the older), and the first OnData after the interval starts
    /// the tween towards the last held value. A new bar, an older one or a candle with the shown OHLC applies at
    /// once and drops what is held. True when the motion changed.
    /// </summary>
    private bool ApplyCoalesced(Candle latest)
    {
        if (applied is null || latest.OpenTime != applied.OpenTime || SameOhlc(latest, applied))
        {
            held = null;
            if (latest == applied)
            {
                return false;
            }

            motion.Apply(latest);
            applied = latest;
            return true;
        }

        var now = time.GetUtcNow();
        if (lastFormingTween is { } last && now - last < FormingMinInterval)
        {
            held = latest;
            return false;
        }

        held = null;
        motion.Apply(latest);
        applied = latest;
        lastFormingTween = now;
        return true;
    }

    private static bool SameOhlc(Candle a, Candle b) =>
        a.Open == b.Open && a.High == b.High && a.Low == b.Low && a.Close == b.Close;

    private static bool SameButLast(IReadOnlyList<Candle> a, IReadOnlyList<Candle> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count - 1; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A full reload (another candle interval): the series shown and the motion state are forgotten, so the
    /// next snapshot is shown at once, without a tween, shift or pulse from the old series, and the coalescing
    /// starts clean (nothing held, no wait left from the old series' last tween). Until then a frame has no candles.
    /// Pause, stale and started state are kept.</summary>
    public void Reset()
    {
        candles = [];
        ticker = null;
        motion = new ChartMotion(time);
        applied = null;
        held = null;
        lastFormingTween = null;
    }

    public void OnFeedStale(bool isStale)
    {
        if (isStale == stale)
        {
            return;
        }

        stale = isStale;
        Changed();
    }

    public void OnPlayback(PlaybackState state)
    {
        var pause = state == PlaybackState.Paused;
        if (pause == paused)
        {
            return;
        }

        paused = pause;
        if (!started)
        {
            return;
        }

        host.SetPaused(pause);
        if (!pause)
        {
            host.RequestFrame();
        }
    }

    /// <summary>The host window exists: requests are delivered from now on.</summary>
    public void OnHostStarted()
    {
        if (started)
        {
            return;
        }

        started = true;
        if (paused)
        {
            host.SetPaused(true);
        }
        else if (pendingFrame)
        {
            host.RequestFrame();
        }

        pendingFrame = false;
    }

    /// <summary>The frame to draw at the clock's now; marks it drawn.</summary>
    public LiveFrame TakeFrame()
    {
        var shown = candles;
        if (motion.Forming is { } forming && shown.Count > 0 && !ReferenceEquals(forming, shown[^1]))
        {
            var copy = new Candle[shown.Count];
            for (var i = 0; i < copy.Length - 1; i++)
            {
                copy[i] = shown[i];
            }

            copy[^1] = forming;
            shown = copy;
        }

        var frame = new LiveFrame(shown, ticker, stale, stale, motion.ShiftPitches, motion.Pulse, motion.PulseUp);
        motion.FrameTaken();
        return frame;
    }

    private void Changed()
    {
        if (!started)
        {
            pendingFrame = true;
        }
        else if (!paused)
        {
            host.RequestFrame();
        }
    }
}
