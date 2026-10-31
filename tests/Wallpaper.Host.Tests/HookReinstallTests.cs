// Contract #47 (D-37), criterion 3: the host notices that Windows silently removed the low-level mouse hook
// (LowLevelHooksTimeout) and reinstalls it. Pure decision; the host's existing watchdog feeds it GetCursorPos samples
// (cheap, no idle cost: nothing runs while the hook is not expected to be installed).
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Crosshair,
// src/Wallpaper.Host/Crosshair/HookReinstallDecider.cs.
//
//   public sealed class HookReinstallDecider
//   {
//       // silence: how long the hook may report nothing while the cursor moved; minInterval: at least this long
//       // between two reinstalls. Both > 0 else ArgumentOutOfRangeException.
//       public HookReinstallDecider(TimeSpan silence, TimeSpan minInterval);
//
//       // hookExpected: the hook is supposed to be installed (CrosshairCanShow).
//       // hookX/hookY/hookAt: the last point the hook reported and when (a monotonic clock, e.g. Stopwatch time); when
//       //   it has reported nothing yet, the point is the cursor position at install time and hookAt the install time.
//       // cursorX/cursorY: the watchdog's GetCursorPos sample at now.
//       // True ("reinstall") only if: hookExpected, the cursor point differs from the hook's last point (it moved),
//       // now - hookAt > silence (strictly), and no reinstall was answered within minInterval (now - last >=
//       // minInterval, or none yet). Answering true remembers now as the last reinstall.
//       public bool ShouldReinstall(bool hookExpected, int hookX, int hookY, TimeSpan hookAt,
//                                   int cursorX, int cursorY, TimeSpan now);
//   }

using BitcoinWallpaper.Host.Crosshair;

namespace BitcoinWallpaper.Host.Tests;

public class HookReinstallTests
{
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private static HookReinstallDecider Make() => new(Silence, Interval);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_cursor_moved_and_the_hook_was_silent_longer_than_the_threshold_reinstalls()
    {
        Assert.True(Make().ShouldReinstall(true, 100, 100, S(10), 400, 300, S(12.5)));
    }

    [Fact]
    public void A_still_cursor_never_reinstalls_however_long_the_silence()
    {
        Assert.False(Make().ShouldReinstall(true, 100, 100, S(0), 100, 100, S(3600)));
    }

    [Fact]
    public void A_hook_that_is_not_supposed_to_be_installed_never_reinstalls()
    {
        Assert.False(Make().ShouldReinstall(false, 100, 100, S(0), 400, 300, S(3600)));
    }

    [Fact]
    public void A_recent_report_or_a_silence_not_longer_than_the_threshold_does_not_reinstall()
    {
        var d = Make();
        Assert.False(d.ShouldReinstall(true, 100, 100, S(10), 400, 300, S(10.5)));
        Assert.False(d.ShouldReinstall(true, 100, 100, S(10), 400, 300, S(12))); // exactly the threshold: not longer
        Assert.True(d.ShouldReinstall(true, 100, 100, S(10), 400, 300, S(12.001)));
    }

    [Fact]
    public void A_move_of_one_axis_only_counts_as_moved()
    {
        Assert.True(Make().ShouldReinstall(true, 100, 100, S(0), 101, 100, S(10)));
        Assert.True(Make().ShouldReinstall(true, 100, 100, S(0), 100, 99, S(10)));
    }

    [Fact]
    public void At_most_one_reinstall_per_interval()
    {
        var d = Make();
        Assert.True(d.ShouldReinstall(true, 100, 100, S(0), 400, 300, S(10)));
        Assert.False(d.ShouldReinstall(true, 100, 100, S(0), 500, 300, S(11)));   // the hook is still mute, too soon
        Assert.False(d.ShouldReinstall(true, 100, 100, S(0), 600, 300, S(39.9)));
        Assert.True(d.ShouldReinstall(true, 100, 100, S(0), 700, 300, S(40)));    // a full interval after the last one
        Assert.False(d.ShouldReinstall(true, 100, 100, S(0), 800, 300, S(41)));
    }

    [Fact]
    public void A_refused_check_does_not_start_the_interval()
    {
        var d = Make();
        Assert.False(d.ShouldReinstall(false, 100, 100, S(0), 400, 300, S(10)));
        Assert.False(d.ShouldReinstall(true, 100, 100, S(0), 100, 100, S(11)));
        Assert.True(d.ShouldReinstall(true, 100, 100, S(0), 400, 300, S(12)));
    }

    [Fact]
    public void A_non_positive_threshold_or_interval_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HookReinstallDecider(TimeSpan.Zero, Interval));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HookReinstallDecider(Silence, TimeSpan.Zero));
    }
}
