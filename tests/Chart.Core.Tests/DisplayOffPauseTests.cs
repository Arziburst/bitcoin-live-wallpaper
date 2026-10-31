using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Motion;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract 22.1 (D-15), criterion 3: display off pauses exactly like a fullscreen pause through the existing
// pause path (LiveScene.OnPlayback / IFrameHost.SetPaused): no frames and no tween frames while off, the data
// keeps being applied, and on display on exactly one frame is requested and it shows the latest data.
//
// API SKETCH (written before the code, #237). Nothing new beyond PauseCombiner / DisplayPower / the new
// PauseReason.DisplayOff (see PauseCombinerTests.cs and DisplayPowerTests.cs); LiveScene is NOT changed. The
// host does, on every change of the monitor verdict or of the display state:
//
//   combiner.SetMonitor(state, reason) / combiner.SetDisplayOn(DisplayPower.IsOn(value));
//   scene.OnPlayback(combiner.Verdict.State);
//
// These tests drive exactly that and watch the host calls (same idiom as LiveSceneTests: manual clock,
// recording IFrameHost). They also pin the existing OnPlayback behaviour the display state relies on:
// - Display off while running: host.SetPaused(true) once, nothing else.
// - While off: OnData / OnFeedStale request no frame, NeedsFrame is false (also mid-tween), the data is applied.
// - Display on (and the monitor running): host.SetPaused(false), then host.RequestFrame() exactly once; the next
//   TakeFrame shows the latest data, no tween replay (NeedsFrame false).
// - Display on while the monitor still pauses: the scene stays paused, no host call; the monitor releasing
//   later gives the one resume + one frame.
// - An "on" at start (the first notification Windows sends after the registration) calls nothing.
public class DisplayOffPauseTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarA = new(2026, 10, 31, 19, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarB = BarA.AddMinutes(5);

    private readonly ManualClock clock = new(Start);
    private readonly RecordingHost host = new();
    private readonly PauseCombiner combiner = new();
    private readonly LiveScene scene;

    public DisplayOffPauseTests()
    {
        scene = new LiveScene(clock, host);
        scene.OnHostStarted();
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The live scene must not use timers: the host drives it.");

        public void AtMs(double milliseconds)
        {
            var next = Start.AddTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));
            if (next < now)
            {
                throw new InvalidOperationException("The test clock only moves forward.");
            }

            now = next;
        }
    }

    private sealed class RecordingHost : IFrameHost
    {
        public List<string> Calls { get; } = [];

        public void RequestFrame() => Calls.Add("frame");

        public void SetPaused(bool paused) => Calls.Add(paused ? "pause" : "resume");

        public List<string> Since(int index) => Calls.Skip(index).ToList();
    }

    private static Candle Bar(decimal o, decimal h, decimal l, decimal c) =>
        new(BarB, o, h, l, c, Volume: 3m, IsClosed: false);

    private static readonly Candle ClosedA = new(BarA, 100m, 104m, 99m, 103m, Volume: 3m, IsClosed: true);

    private static List<Candle> Snapshot(Candle last) => [ClosedA, last];

    private static Ticker24h Ticker(decimal last) => new(last, 100m, 120m, 80m, last - 100m, last - 100m, 1000m);

    private static decimal[] Ohlc(Candle c) => [c.Open, c.High, c.Low, c.Close];

    // What the host does on a display power notification.
    private void Display(uint value)
    {
        combiner.SetDisplayOn(DisplayPower.IsOn(value));
        scene.OnPlayback(combiner.Verdict.State);
    }

    // What the host does on a (debounced) pause monitor verdict.
    private void Monitor(PlaybackState state, PauseReason reason)
    {
        combiner.SetMonitor(state, reason);
        scene.OnPlayback(combiner.Verdict.State);
    }

    private void ShowFirstFrame()
    {
        scene.OnData(Snapshot(Bar(103m, 105m, 102m, 104m)), Ticker(104m));
        scene.TakeFrame();
        clock.AtMs(1000);
    }

    [Fact]
    public void Display_off_pauses_the_host_once_through_the_pause_path()
    {
        ShowFirstFrame();
        var before = host.Calls.Count;

        Display(0);

        Assert.Equal(["pause"], host.Since(before));
        Assert.True(scene.IsPaused);
        Assert.False(scene.NeedsFrame);
    }

    [Fact]
    public void While_off_no_frames_are_requested_and_none_are_needed_even_with_new_data()
    {
        ShowFirstFrame();
        Display(0);
        var before = host.Calls.Count;

        for (var i = 1; i <= 5; i++)
        {
            clock.AtMs(1000 + (i * 2000));
            scene.OnData(Snapshot(Bar(103m, 105m + i, 102m, 104m + i)), Ticker(104m + i));
            Assert.False(scene.NeedsFrame, $"a frame was asked for while the display is off, update {i}");
            clock.AtMs(1000 + (i * 2000) + 100);
            Assert.False(scene.NeedsFrame, $"a tween frame was asked for while the display is off, update {i}");
        }

        scene.OnFeedStale(true);
        scene.OnFeedStale(false);

        Assert.Empty(host.Since(before));
    }

    [Fact]
    public void Display_off_mid_tween_stops_the_tween_frames_at_once()
    {
        ShowFirstFrame();
        clock.AtMs(2000);
        scene.OnData(Snapshot(Bar(103m, 107m, 102m, 106m)), Ticker(106m));
        scene.TakeFrame();
        clock.AtMs(2050);
        Assert.True(scene.NeedsFrame);

        Display(0);

        Assert.False(scene.NeedsFrame);
        clock.AtMs(2200);
        Assert.False(scene.NeedsFrame);
        clock.AtMs(5000);
        Assert.False(scene.NeedsFrame);
    }

    [Fact]
    public void Display_on_gives_exactly_one_frame_showing_the_latest_data()
    {
        ShowFirstFrame();
        Display(0);
        var latest = Bar(103m, 109m, 101m, 108m);
        clock.AtMs(3000);
        scene.OnData(Snapshot(Bar(103m, 106m, 102m, 105m)), Ticker(105m));
        clock.AtMs(5000);
        scene.OnData(Snapshot(latest), Ticker(108m));
        scene.OnFeedStale(true);
        clock.AtMs(9000);
        var before = host.Calls.Count;

        Display(1);

        Assert.Equal(["resume", "frame"], host.Since(before));
        Assert.False(scene.IsPaused);
        var frame = scene.TakeFrame();
        Assert.Equal(Ohlc(latest), Ohlc(frame.Candles[^1]));
        Assert.Equal(Ticker(108m), frame.Ticker);
        Assert.True(frame.HeadlineDimmed);
        Assert.False(scene.NeedsFrame); // one frame, not a tween replay
        Assert.Equal(["resume", "frame"], host.Since(before));
    }

    [Fact]
    public void Dimmed_after_off_counts_as_on_and_resumes_with_one_frame()
    {
        ShowFirstFrame();
        Display(0);
        var before = host.Calls.Count;

        Display(2);

        Assert.Equal(["resume", "frame"], host.Since(before));
    }

    [Fact]
    public void Repeated_off_or_on_notifications_call_nothing_more()
    {
        ShowFirstFrame();
        var before = host.Calls.Count;

        Display(1); // already on: Windows repeats the state, also right after the registration
        Assert.Empty(host.Since(before));

        Display(0);
        Display(0);
        Assert.Equal(["pause"], host.Since(before));

        Display(1);
        Display(1);
        Display(2);
        Assert.Equal(["pause", "resume", "frame"], host.Since(before));
    }

    [Fact]
    public void An_on_notification_at_start_costs_no_frame_and_no_pause()
    {
        var before = host.Calls.Count;

        Display(1);

        Assert.Empty(host.Since(before));
        Assert.False(scene.IsPaused);
    }

    [Fact]
    public void Monitor_paused_and_display_on_stays_paused_without_a_host_call()
    {
        ShowFirstFrame();
        Monitor(PlaybackState.Paused, PauseReason.Fullscreen);
        Display(0);
        var before = host.Calls.Count;
        scene.OnData(Snapshot(Bar(103m, 110m, 101m, 109m)), Ticker(109m));

        Display(1);

        Assert.Empty(host.Since(before));
        Assert.True(scene.IsPaused);
        Assert.False(scene.NeedsFrame);
        Assert.Equal(PauseVerdict.Paused(PauseReason.Fullscreen), combiner.Verdict);
    }

    [Fact]
    public void Both_released_runs_with_one_resume_and_one_frame_with_the_latest_data()
    {
        ShowFirstFrame();
        Monitor(PlaybackState.Paused, PauseReason.Fullscreen);
        Display(0);
        var latest = Bar(103m, 110m, 101m, 109m);
        scene.OnData(Snapshot(latest), Ticker(109m));
        Display(1); // still paused by the monitor
        clock.AtMs(9000);
        var before = host.Calls.Count;

        Monitor(PlaybackState.Running, PauseReason.None);

        Assert.Equal(["resume", "frame"], host.Since(before));
        Assert.False(scene.IsPaused);
        Assert.Equal(Ohlc(latest), Ohlc(scene.TakeFrame().Candles[^1]));
    }

    [Fact]
    public void Monitor_released_while_the_display_is_off_stays_paused_until_the_display_is_on()
    {
        ShowFirstFrame();
        Monitor(PlaybackState.Paused, PauseReason.Fullscreen);
        Display(0);
        var before = host.Calls.Count;

        Monitor(PlaybackState.Running, PauseReason.None);

        Assert.Empty(host.Since(before));
        Assert.True(scene.IsPaused);
        Assert.False(scene.NeedsFrame);
        scene.OnData(Snapshot(Bar(103m, 111m, 101m, 110m)), Ticker(110m));
        Assert.Empty(host.Since(before));
        clock.AtMs(9000);

        Display(1);

        Assert.Equal(["resume", "frame"], host.Since(before));
        Assert.Equal(110m, scene.TakeFrame().Candles[^1].Close);
    }

    [Fact]
    public void Display_off_while_the_monitor_already_pauses_adds_no_host_call()
    {
        ShowFirstFrame();
        Monitor(PlaybackState.Paused, PauseReason.Maximized);
        var before = host.Calls.Count;

        Display(0);

        Assert.Empty(host.Since(before));
        Assert.True(scene.IsPaused);
    }
}
