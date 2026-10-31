using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Motion;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #8, criterion 2: the wiring logic (pure where possible) — data update -> frame request; pause -> no
// frames (the data client keeps its socket); resume -> one immediate frame with the latest data; stale feed ->
// the headline dims and shows "delayed"; requests made before the host window exists are kept and applied at
// start (the #6 verifier's Minor: SetPaused/RequestRedraw before Run were dropped).
//
// API SKETCH (written before the code, #237). In src/Chart.Core/Motion/, namespace
// BitcoinWallpaper.Chart.Core.Motion, next to ChartMotion (see MotionTests.cs). Pure: no Win32, no
// Market.Data reference (Market.Data references Chart.Core, so FeedState cannot be named here: the host maps
// MarketDataClient.State == FeedState.Stale to OnFeedStale(true) and Live/Connecting to OnFeedStale(false)).
//
//   public interface IFrameHost                  // the host side; WallpaperHost implements it
//   {
//       void RequestFrame();                     // = WallpaperHost.RequestRedraw: present one frame soon
//       void SetPaused(bool paused);             // = WallpaperHost.SetPaused
//   }
//
//   public sealed record LiveFrame(
//       IReadOnlyList<Candle> Candles,  // the latest snapshot, oldest first; its last candle at its animated OHLC
//       Ticker24h? Ticker,              // the latest ticker
//       bool HeadlineDimmed,            // feed stale: the headline is drawn dimmed
//       bool ShowsDelayed,              // feed stale: the headline caption shows "delayed"
//       double ShiftPitches,            // ChartMotion.ShiftPitches at the frame's time
//       double Pulse,                   // ChartMotion.Pulse
//       bool PulseUp);                  // ChartMotion.PulseUp
//
//   public sealed class LiveScene
//   {
//       public LiveScene(TimeProvider time, IFrameHost host);
//       public void OnData(IReadOnlyList<Candle> candles, Ticker24h? ticker); // the feed's current snapshot
//       public void OnFeedStale(bool stale);
//       public void OnPlayback(PlaybackState state);
//       public void OnHostStarted();          // the host window exists: requests can be delivered from now on
//       public bool NeedsFrame { get; }       // false while paused; otherwise ChartMotion.NeedsFrame
//       public LiveFrame TakeFrame();         // the frame to draw at time.GetUtcNow(); marks it drawn
//   }
//
// Rules the tests rely on:
// - OnData feeds the snapshot's last candle to a ChartMotion (forming tween, new-bar shift, pulse) and keeps
//   the snapshot and ticker for TakeFrame. A snapshot equal by value to the previous one (same candles, same
//   ticker; a new list instance) changes nothing and requests nothing.
// - Started and running: every OnData that changes the candles or the ticker, and every OnFeedStale that
//   changes the flag, calls host.RequestFrame() exactly once, synchronously. Further frames of a tween are
//   pulled by the host through NeedsFrame/TakeFrame at each display refresh.
// - OnPlayback(Paused) while running calls host.SetPaused(true) once. While paused: no RequestFrame (data and
//   stale changes are still applied), NeedsFrame is false, also mid-tween.
// - OnPlayback(Running) after Paused calls host.SetPaused(false), then host.RequestFrame() exactly once,
//   synchronously; the next TakeFrame shows the latest data and flags. A repeated state calls nothing.
// - Before OnHostStarted the host gets no calls. OnHostStarted applies what was asked meanwhile: if paused,
//   host.SetPaused(true) and no frame; if running and anything arrived (data, stale flag), exactly one
//   RequestFrame and no SetPaused(true).
// - Stale: HeadlineDimmed and ShowsDelayed are both true while the feed is stale, both false otherwise
//   (also before any OnFeedStale call).
public class LiveSceneTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarA = new(2026, 10, 31, 19, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarB = BarA.AddMinutes(5);
    private static readonly DateTimeOffset BarC = BarB.AddMinutes(5);

    private readonly ManualClock clock = new(Start);
    private readonly RecordingHost host = new();

    /// <summary>A manual clock: time moves only when the test sets it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The live scene must not use timers: the host drives it.");

        public void AtMs(double milliseconds) => AtTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

        public void AtTicks(long ticksSinceStart)
        {
            var next = Start.AddTicks(ticksSinceStart);
            if (next < now)
            {
                throw new InvalidOperationException("The test clock only moves forward.");
            }

            now = next;
        }
    }

    /// <summary>Records every call the scene makes on the host, in order.</summary>
    private sealed class RecordingHost : IFrameHost
    {
        public List<string> Calls { get; } = [];

        public int Frames => Calls.Count(c => c == "frame");

        public void RequestFrame() => Calls.Add("frame");

        public void SetPaused(bool paused) => Calls.Add(paused ? "pause" : "resume");

        public List<string> Since(int index) => Calls.Skip(index).ToList();
    }

    private static Candle Bar(DateTimeOffset openTime, decimal o, decimal h, decimal l, decimal c, bool closed = false) =>
        new(openTime, o, h, l, c, Volume: 3m, IsClosed: closed);

    private static Ticker24h Ticker(decimal last) => new(last, 100m, 120m, 80m, last - 100m, last - 100m, 1000m);

    private static readonly Candle ClosedA = Bar(BarA, 100m, 104m, 99m, 103m, closed: true);

    private static List<Candle> Snapshot(Candle last) => [ClosedA, last];

    private static decimal[] Ohlc(Candle c) => [c.Open, c.High, c.Low, c.Close];

    private LiveScene StartedScene()
    {
        var scene = new LiveScene(clock, host);
        scene.OnHostStarted();
        return scene;
    }

    /// <summary>Draws at 120 Hz while the scene asks for frames, up to the given time; returns the last frame drawn.</summary>
    private static LiveFrame? DrawUntil(LiveScene scene, ManualClock clock, double fromMs, double untilMs, out int frames)
    {
        frames = 0;
        LiveFrame? last = null;
        for (var ms = fromMs; ms <= untilMs; ms += 1000.0 / 120)
        {
            clock.AtMs(ms);
            if (scene.NeedsFrame)
            {
                last = scene.TakeFrame();
                frames++;
            }
        }

        return last;
    }

    // ---- data update -> frame request ----

    [Fact]
    public void First_data_requests_one_frame_showing_it()
    {
        var scene = StartedScene();
        var forming = Bar(BarB, 103m, 105m, 102m, 104m);

        scene.OnData(Snapshot(forming), Ticker(104m));

        Assert.Equal(["frame"], host.Calls);
        var frame = scene.TakeFrame();
        Assert.Equal(2, frame.Candles.Count);
        Assert.Equal(ClosedA, frame.Candles[0]);
        Assert.Equal(forming, frame.Candles[1]);
        Assert.Equal(Ticker(104m), frame.Ticker);
        Assert.False(frame.HeadlineDimmed);
        Assert.False(frame.ShowsDelayed);
    }

    [Fact]
    public void A_data_update_requests_a_frame_then_frames_follow_the_tween_and_stop()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(2000);
        Assert.False(scene.NeedsFrame);
        var before = host.Calls.Count;

        var update = Bar(BarB, 103m, 107m, 102m, 106.5m);
        scene.OnData(Snapshot(update), Ticker(106.5m));

        Assert.Equal(["frame"], host.Since(before));
        Assert.True(scene.NeedsFrame);
        var first = scene.TakeFrame();
        Assert.Equal(104m, first.Candles[^1].Close); // the tween starts from the shown value
        Assert.Equal(1.0, first.Pulse);
        Assert.True(first.PulseUp);

        var last = DrawUntil(scene, clock, 2000 + (1000.0 / 120), 4000, out var frames);

        Assert.InRange(frames, 35, 38); // 300 ms at 120 Hz, plus the end-values frame
        Assert.NotNull(last);
        Assert.Equal(Ohlc(update), Ohlc(last.Candles[^1]));
        Assert.Equal(ClosedA, last.Candles[0]);
        Assert.Equal(0.0, last.Pulse);
        Assert.False(scene.NeedsFrame);
        Assert.Equal(["frame"], host.Since(before)); // tween frames are pulled, not requested
    }

    [Fact]
    public void A_ticker_only_change_requests_a_frame()
    {
        var scene = StartedScene();
        var forming = Bar(BarB, 103m, 105m, 102m, 104m);
        scene.OnData(Snapshot(forming), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(2000);
        var before = host.Calls.Count;

        scene.OnData(Snapshot(forming), Ticker(104.25m));

        Assert.Equal(["frame"], host.Since(before));
        Assert.Equal(Ticker(104.25m), scene.TakeFrame().Ticker);
    }

    [Fact]
    public void An_unchanged_snapshot_requests_nothing()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(2000);
        var before = host.Calls.Count;

        // A new list with equal candles and an equal ticker: nothing moved, nothing is drawn.
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));

        Assert.Empty(host.Since(before));
        Assert.False(scene.NeedsFrame);
    }

    [Fact]
    public void A_new_bar_reaches_the_frame_as_a_shift()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(2000);
        var newBar = Bar(BarC, 104m, 104m, 104m, 104m);

        scene.OnData([ClosedA, Bar(BarB, 103m, 105m, 102m, 104m, closed: true), newBar], Ticker(104m));

        Assert.Equal("frame", host.Calls[^1]);
        var frame = scene.TakeFrame();
        Assert.Equal(3, frame.Candles.Count);
        Assert.Equal(newBar, frame.Candles[^1]);
        Assert.Equal(1.0, frame.ShiftPitches);

        var last = DrawUntil(scene, clock, 2000 + (1000.0 / 120), 3000, out _);
        Assert.NotNull(last);
        Assert.Equal(0.0, last.ShiftPitches);
        Assert.False(scene.NeedsFrame);
    }

    // ---- pause ----

    [Fact]
    public void Paused_no_frame_requests_even_on_data_updates_but_data_is_applied()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(1000);
        var before = host.Calls.Count;

        scene.OnPlayback(PlaybackState.Paused);
        Assert.Equal(["pause"], host.Since(before));

        for (var i = 1; i <= 5; i++)
        {
            clock.AtMs(1000 + (i * 2000));
            scene.OnData(Snapshot(Bar(BarB, 103m, 105m + i, 102m, 104m + i)), Ticker(104m + i));
            Assert.False(scene.NeedsFrame, $"a frame was asked for while paused, update {i}");
            clock.AtMs(1000 + (i * 2000) + 100);
            Assert.False(scene.NeedsFrame, $"a frame was asked for mid-tween while paused, update {i}");
        }

        scene.OnFeedStale(true);
        scene.OnFeedStale(false);

        Assert.Equal(["pause"], host.Since(before));
    }

    [Fact]
    public void Pausing_mid_tween_stops_the_frames_at_once()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(2000);
        scene.OnData(Snapshot(Bar(BarB, 103m, 107m, 102m, 106m)), Ticker(106m));
        scene.TakeFrame();
        clock.AtMs(2050);
        Assert.True(scene.NeedsFrame);

        scene.OnPlayback(PlaybackState.Paused);

        Assert.False(scene.NeedsFrame);
        clock.AtMs(2200);
        Assert.False(scene.NeedsFrame);
        clock.AtMs(3000);
        Assert.False(scene.NeedsFrame);
    }

    [Fact]
    public void Resume_gives_one_immediate_frame_with_the_latest_data()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(1000);
        scene.OnPlayback(PlaybackState.Paused);
        var latest = Bar(BarB, 103m, 109m, 101m, 108m);
        clock.AtMs(3000);
        scene.OnData(Snapshot(Bar(BarB, 103m, 106m, 102m, 105m)), Ticker(105m));
        clock.AtMs(5000);
        scene.OnData(Snapshot(latest), Ticker(108m));
        scene.OnFeedStale(true);
        clock.AtMs(9000);
        var before = host.Calls.Count;

        scene.OnPlayback(PlaybackState.Running);

        Assert.Equal(["resume", "frame"], host.Since(before));
        var frame = scene.TakeFrame();
        Assert.Equal(Ohlc(latest), Ohlc(frame.Candles[^1]));
        Assert.Equal(Ticker(108m), frame.Ticker);
        Assert.True(frame.HeadlineDimmed);
        Assert.True(frame.ShowsDelayed);
        Assert.False(scene.NeedsFrame); // nothing moves: one frame, not a tween replay
    }

    [Fact]
    public void Resume_without_new_data_still_gives_one_frame()
    {
        var scene = StartedScene();
        var forming = Bar(BarB, 103m, 105m, 102m, 104m);
        scene.OnData(Snapshot(forming), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(1000);
        scene.OnPlayback(PlaybackState.Paused);
        clock.AtMs(60_000);
        var before = host.Calls.Count;

        scene.OnPlayback(PlaybackState.Running);

        Assert.Equal(["resume", "frame"], host.Since(before));
        Assert.Equal(forming, scene.TakeFrame().Candles[^1]);
    }

    [Fact]
    public void A_repeated_playback_state_calls_nothing()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        var before = host.Calls.Count;

        scene.OnPlayback(PlaybackState.Running);
        Assert.Empty(host.Since(before));

        scene.OnPlayback(PlaybackState.Paused);
        scene.OnPlayback(PlaybackState.Paused);
        Assert.Equal(["pause"], host.Since(before));

        scene.OnPlayback(PlaybackState.Running);
        scene.OnPlayback(PlaybackState.Running);
        Assert.Equal(["pause", "resume", "frame"], host.Since(before));
    }

    // ---- stale feed ----

    [Fact]
    public void A_stale_feed_dims_the_headline_and_shows_delayed()
    {
        var scene = StartedScene();
        scene.OnData(Snapshot(Bar(BarB, 103m, 105m, 102m, 104m)), Ticker(104m));
        var live = scene.TakeFrame();
        Assert.False(live.HeadlineDimmed);
        Assert.False(live.ShowsDelayed);
        clock.AtMs(31_000);
        var before = host.Calls.Count;

        scene.OnFeedStale(true);

        Assert.Equal(["frame"], host.Since(before));
        var stale = scene.TakeFrame();
        Assert.True(stale.HeadlineDimmed);
        Assert.True(stale.ShowsDelayed);

        scene.OnFeedStale(true);
        Assert.Equal(["frame"], host.Since(before)); // unchanged: no new frame

        clock.AtMs(40_000);
        scene.OnFeedStale(false);
        Assert.Equal(["frame", "frame"], host.Since(before));
        var back = scene.TakeFrame();
        Assert.False(back.HeadlineDimmed);
        Assert.False(back.ShowsDelayed);
    }

    // ---- requests before the host window exists ----

    [Fact]
    public void Before_start_nothing_reaches_the_host_and_data_gives_one_frame_at_start()
    {
        var scene = new LiveScene(clock, host);
        var forming = Bar(BarB, 103m, 105m, 102m, 104m);

        scene.OnData(Snapshot(Bar(BarB, 103m, 104m, 102m, 103m)), Ticker(103m));
        clock.AtMs(500);
        scene.OnData(Snapshot(forming), Ticker(104m));
        scene.OnFeedStale(true);
        Assert.Empty(host.Calls);

        clock.AtMs(2000);
        scene.OnHostStarted();

        Assert.Equal(["frame"], host.Calls);
        var frame = scene.TakeFrame();
        Assert.Equal(Ohlc(forming), Ohlc(frame.Candles[^1]));
        Assert.Equal(Ticker(104m), frame.Ticker);
        Assert.True(frame.HeadlineDimmed);
        Assert.True(frame.ShowsDelayed);
    }

    [Fact]
    public void A_pause_before_start_is_applied_at_start_and_resume_then_draws()
    {
        var scene = new LiveScene(clock, host);
        var forming = Bar(BarB, 103m, 105m, 102m, 104m);
        scene.OnData(Snapshot(forming), Ticker(104m));
        scene.OnPlayback(PlaybackState.Paused);
        Assert.Empty(host.Calls);

        clock.AtMs(1000);
        scene.OnHostStarted();

        Assert.Equal(["pause"], host.Calls);
        Assert.False(scene.NeedsFrame);

        clock.AtMs(3000);
        scene.OnPlayback(PlaybackState.Running);

        Assert.Equal(["pause", "resume", "frame"], host.Calls);
        Assert.Equal(forming, scene.TakeFrame().Candles[^1]);
    }

    [Fact]
    public void A_pause_and_resume_before_start_leave_the_host_running_with_one_frame()
    {
        var scene = new LiveScene(clock, host);
        var forming = Bar(BarB, 103m, 105m, 102m, 104m);
        scene.OnPlayback(PlaybackState.Paused);
        scene.OnData(Snapshot(forming), Ticker(104m));
        scene.OnPlayback(PlaybackState.Running);
        Assert.Empty(host.Calls);

        scene.OnHostStarted();

        Assert.DoesNotContain("pause", host.Calls);
        Assert.Equal(1, host.Frames);
        Assert.Equal("frame", host.Calls[^1]);
        Assert.Equal(forming, scene.TakeFrame().Candles[^1]);
    }
}
