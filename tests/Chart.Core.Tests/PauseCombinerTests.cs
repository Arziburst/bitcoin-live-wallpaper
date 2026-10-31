using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract 22.1 (D-15), criterion 2: display off is a pause reason combined with the pause monitor's verdict in
// one pure public type: paused while either says paused, running only when both say running; the reason
// reported is the monitor's while the monitor pauses, else the new PauseReason.DisplayOff.
//
// API SKETCH (written before the code, #237). The worker implements exactly these public members in
// src/Chart.Core/Pause/, namespace BitcoinWallpaper.Chart.Core.Pause. Pure: no Win32, no timers.
//
//   public enum PauseReason { ..., DisplayOff }        // NEW value in PauseTypes.cs; existing values unchanged
//
//   public sealed class PauseCombiner
//   {
//       public PauseCombiner();                        // monitor Running/None, display on: Verdict is Running
//       public void SetMonitor(PlaybackState state, PauseReason reason);  // the pause monitor's (debounced) verdict
//       public void SetDisplayOn(bool displayOn);      // from DisplayPower.IsOn(...); false = display off
//       public PauseVerdict Verdict { get; }           // the combined verdict, see the rules below
//   }
//
// Rules the tests rely on:
// - Verdict.State is Paused when the monitor state is Paused OR the display is off; Running only when the
//   monitor state is Running AND the display is on.
// - Verdict.Reason: monitor Paused -> the monitor's reason as given (also while the display is off too);
//   else display off -> PauseReason.DisplayOff; else (Running) -> PauseReason.None, whatever reason was passed
//   with a Running monitor state.
// - Setting the same value again changes nothing; the order of the two setters does not matter.
public class PauseCombinerTests
{
    [Fact]
    public void A_new_combiner_is_running_with_no_reason()
    {
        var combiner = new PauseCombiner();

        Assert.Equal(PauseVerdict.Running, combiner.Verdict);
        Assert.Equal(PlaybackState.Running, combiner.Verdict.State);
        Assert.Equal(PauseReason.None, combiner.Verdict.Reason);
    }

    [Fact]
    public void Display_off_alone_pauses_with_display_off()
    {
        var combiner = new PauseCombiner();

        combiner.SetDisplayOn(false);

        Assert.Equal(PlaybackState.Paused, combiner.Verdict.State);
        Assert.Equal(PauseReason.DisplayOff, combiner.Verdict.Reason);
        Assert.Equal(PauseVerdict.Paused(PauseReason.DisplayOff), combiner.Verdict);
    }

    [Fact]
    public void Display_off_with_a_running_monitor_pauses_with_display_off()
    {
        var combiner = new PauseCombiner();
        combiner.SetMonitor(PlaybackState.Running, PauseReason.None);

        combiner.SetDisplayOn(false);

        Assert.Equal(PauseVerdict.Paused(PauseReason.DisplayOff), combiner.Verdict);
    }

    [Fact]
    public void Display_on_again_runs_with_no_reason()
    {
        var combiner = new PauseCombiner();
        combiner.SetDisplayOn(false);

        combiner.SetDisplayOn(true);

        Assert.Equal(PauseVerdict.Running, combiner.Verdict);
    }

    [Theory]
    [InlineData(PauseReason.Fullscreen)]
    [InlineData(PauseReason.Maximized)]
    [InlineData(PauseReason.SessionLocked)]
    [InlineData(PauseReason.NotificationCorroborated)]
    [InlineData(PauseReason.WindowsCover)]
    public void A_pausing_monitor_alone_keeps_its_own_reason(PauseReason reason)
    {
        var combiner = new PauseCombiner();

        combiner.SetMonitor(PlaybackState.Paused, reason);

        Assert.Equal(PauseVerdict.Paused(reason), combiner.Verdict);
    }

    [Theory]
    [InlineData(PauseReason.Fullscreen)]
    [InlineData(PauseReason.SessionLocked)]
    [InlineData(PauseReason.SessionDisconnected)]
    [InlineData(PauseReason.NotificationUncorroborated)]
    public void The_monitors_reason_wins_while_the_monitor_pauses_even_with_the_display_off(PauseReason reason)
    {
        var combiner = new PauseCombiner();
        combiner.SetDisplayOn(false);

        combiner.SetMonitor(PlaybackState.Paused, reason);

        Assert.Equal(PauseVerdict.Paused(reason), combiner.Verdict);
    }

    [Fact]
    public void The_order_of_the_setters_does_not_matter()
    {
        var monitorFirst = new PauseCombiner();
        monitorFirst.SetMonitor(PlaybackState.Paused, PauseReason.Fullscreen);
        monitorFirst.SetDisplayOn(false);

        var displayFirst = new PauseCombiner();
        displayFirst.SetDisplayOn(false);
        displayFirst.SetMonitor(PlaybackState.Paused, PauseReason.Fullscreen);

        Assert.Equal(PauseVerdict.Paused(PauseReason.Fullscreen), monitorFirst.Verdict);
        Assert.Equal(monitorFirst.Verdict, displayFirst.Verdict);
    }

    [Fact]
    public void Monitor_released_while_the_display_is_off_falls_back_to_display_off()
    {
        var combiner = new PauseCombiner();
        combiner.SetMonitor(PlaybackState.Paused, PauseReason.Fullscreen);
        combiner.SetDisplayOn(false);

        combiner.SetMonitor(PlaybackState.Running, PauseReason.None);

        Assert.Equal(PauseVerdict.Paused(PauseReason.DisplayOff), combiner.Verdict);
    }

    [Fact]
    public void Display_on_while_the_monitor_pauses_stays_paused_with_the_monitors_reason()
    {
        var combiner = new PauseCombiner();
        combiner.SetMonitor(PlaybackState.Paused, PauseReason.Fullscreen);
        combiner.SetDisplayOn(false);

        combiner.SetDisplayOn(true);

        Assert.Equal(PauseVerdict.Paused(PauseReason.Fullscreen), combiner.Verdict);
    }

    [Fact]
    public void Both_released_runs_with_no_reason()
    {
        var combiner = new PauseCombiner();
        combiner.SetMonitor(PlaybackState.Paused, PauseReason.Fullscreen);
        combiner.SetDisplayOn(false);

        combiner.SetDisplayOn(true);
        combiner.SetMonitor(PlaybackState.Running, PauseReason.None);

        Assert.Equal(PauseVerdict.Running, combiner.Verdict);
    }

    [Fact]
    public void A_running_monitor_reports_no_reason_whatever_reason_came_with_it()
    {
        var combiner = new PauseCombiner();

        combiner.SetMonitor(PlaybackState.Running, PauseReason.Fullscreen);

        Assert.Equal(PauseVerdict.Running, combiner.Verdict);
    }

    [Fact]
    public void Setting_the_same_values_again_changes_nothing()
    {
        var combiner = new PauseCombiner();
        combiner.SetDisplayOn(false);
        combiner.SetDisplayOn(false);
        Assert.Equal(PauseVerdict.Paused(PauseReason.DisplayOff), combiner.Verdict);

        combiner.SetMonitor(PlaybackState.Paused, PauseReason.WindowsCover);
        combiner.SetMonitor(PlaybackState.Paused, PauseReason.WindowsCover);
        Assert.Equal(PauseVerdict.Paused(PauseReason.WindowsCover), combiner.Verdict);

        combiner.SetDisplayOn(true);
        combiner.SetDisplayOn(true);
        combiner.SetMonitor(PlaybackState.Running, PauseReason.None);
        combiner.SetMonitor(PlaybackState.Running, PauseReason.None);
        Assert.Equal(PauseVerdict.Running, combiner.Verdict);
    }
}
