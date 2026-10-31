namespace BitcoinWallpaper.Chart.Core.Pause;

/// <summary>
/// The pause monitor's verdict and the display power state in one verdict (D-15): paused while either says
/// paused, running only when both say running. The monitor's reason wins while the monitor pauses; else a display
/// that is off reports <see cref="PauseReason.DisplayOff"/>.
/// </summary>
public sealed class PauseCombiner
{
    private PlaybackState monitorState = PlaybackState.Running;
    private PauseReason monitorReason = PauseReason.None;
    private bool isDisplayOn = true;

    /// <summary>The pause monitor's (debounced) state and the reason that came with it.</summary>
    public void SetMonitor(PlaybackState state, PauseReason reason)
    {
        monitorState = state;
        monitorReason = reason;
    }

    /// <summary>From <see cref="DisplayPower.IsOn"/>; false = the display is off.</summary>
    public void SetDisplayOn(bool displayOn) => isDisplayOn = displayOn;

    public PauseVerdict Verdict =>
        monitorState == PlaybackState.Paused ? PauseVerdict.Paused(monitorReason)
        : !isDisplayOn ? PauseVerdict.Paused(PauseReason.DisplayOff)
        : PauseVerdict.Running;
}
