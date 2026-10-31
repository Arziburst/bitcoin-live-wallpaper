// Contract #41 (D-35), criterion 3, as changed by #47 (owner, 2026-10-31): when the host shows the crosshair, and when it
// asks for a frame. Pure decisions, no Win32 calls; the host (hook) only feeds them.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Crosshair, src/Wallpaper.Host/Crosshair/.
// #47: the crosshair follows the mouse anywhere on our monitor, whatever is under the cursor (windows, icons): the host no
// longer asks WindowFromPoint / GetClassName and no longer probes explorer.
//
//   // What the host read at the cursor: its position (virtual-screen px, like PixelRect). WindowClass and OverIcon are gone.
//   public readonly record struct CursorSample(int X, int Y);
//
//   public static class CrosshairVisibility
//   {
//       // IsDesktopClass is removed.
//       // True only if: the wallpaper is on (state On), not paused, and the cursor lies in the wallpaper's monitor
//       // (Left <= X < Right, Top <= Y < Bottom). Nothing else.
//       public static bool ShouldShow(CursorSample cursor, PixelRect wallpaperMonitor, bool paused, WallpaperState state);
//       public static bool Contains(PixelRect rect, int x, int y);   // unchanged
//   }
//
//   // The frame-request rule. Fed with the crosshair state the host computed (CrosshairModel.Compute, null = hidden)
//   // each time it looks at the cursor; answers whether a frame must be requested.
//   public sealed class CrosshairFrameGate
//   {
//       // true when what would be drawn differs from the last answered state: hidden <-> shown, a different candle
//       // slot (CandleIndex), a different X pixel column (floor(X); the line follows the cursor, round 2), a different Y
//       // pixel (floor(Y)), or Price present vs null (volume pane). false otherwise (cursor still, or moved inside the
//       // same slot, pixel column and pixel row, or still hidden). The first call after construction compares with "hidden".
//       public bool Update(CrosshairState? state);
//   }

using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Crosshair;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Tests;

public class CrosshairVisibilityTests
{
    // A second monitor to the left of the primary: negative coordinates are real.
    private static readonly PixelRect Monitor = new(-2560, 0, 0, 1440);

    private static CursorSample Sample(int x = -1000, int y = 700) => new(x, y);

    private static bool Show(CursorSample s, bool paused = false, WallpaperState state = WallpaperState.On) =>
        CrosshairVisibility.ShouldShow(s, Monitor, paused, state);

    // Changed by #47: the former "bare desktop shows it" / "another window hides it" / "over an icon hides it" cases
    // (window classes, OverIcon) are gone with those inputs; the decision depends on position, state and pause only,
    // so the same cursor shows wherever it lands on our monitor, over a half-screen window or an icon included.
    [Fact]
    public void Wherever_the_cursor_is_on_the_monitor_it_shows_whatever_is_under_it()
    {
        Assert.True(Show(Sample()));
        Assert.True(Show(Sample(x: -2559, y: 3)));
        Assert.True(Show(Sample(x: -1280, y: 1439)));
    }
    [Fact]
    public void Paused_or_off_hides_it_even_over_the_bare_desktop()
    {
        Assert.True(Show(Sample()));
        Assert.False(Show(Sample(), paused: true));
        Assert.False(Show(Sample(), state: WallpaperState.Off));
        Assert.False(Show(Sample(), paused: true, state: WallpaperState.Off));
    }

    [Fact]
    public void Only_the_wallpapers_monitor_counts_and_its_right_and_bottom_edges_are_outside()
    {
        Assert.True(Show(Sample(x: Monitor.Left, y: Monitor.Top)));
        Assert.True(Show(Sample(x: Monitor.Right - 1, y: Monitor.Bottom - 1)));
        Assert.False(Show(Sample(x: Monitor.Left - 1, y: 10)));
        Assert.False(Show(Sample(x: Monitor.Right, y: 10)));
        Assert.False(Show(Sample(x: -10, y: Monitor.Bottom)));
        Assert.False(Show(Sample(x: -10, y: -1)));
        Assert.False(Show(Sample(x: 500, y: 500))); // the primary monitor, a different one
    }

    // ---- the frame-request rule ------------------------------------------------------------------------------

    private static readonly Candle C = new(new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero), 1, 2, 0.5m, 1.5m, 3, true);

    private static CrosshairState State(int index = 5, double y = 700, double x = 0, bool volume = false) =>
        new(x == 0 ? 100 + (index * 16) : x, y, volume ? null : 65000 + y, index, C, volume);

    [Fact]
    public void A_still_cursor_never_asks_for_a_frame()
    {
        var gate = new CrosshairFrameGate();
        Assert.False(gate.Update(null));
        Assert.False(gate.Update(null));
        Assert.True(gate.Update(State()));
        for (var i = 0; i < 100; i++)
        {
            Assert.False(gate.Update(State()));
        }
    }

    [Fact]
    public void Showing_moving_to_another_slot_or_pixel_row_and_hiding_each_ask_for_one_frame()
    {
        var gate = new CrosshairFrameGate();
        Assert.True(gate.Update(State(index: 5, y: 700)));      // shown
        Assert.True(gate.Update(State(index: 6, y: 700)));      // another slot
        Assert.False(gate.Update(State(index: 6, y: 700)));
        Assert.True(gate.Update(State(index: 6, y: 701)));      // another pixel row
        Assert.True(gate.Update(State(index: 7, y: 650)));      // both
        Assert.True(gate.Update(null));                         // hidden: one frame to hide it
        Assert.False(gate.Update(null));
        Assert.False(gate.Update(null));
        Assert.True(gate.Update(State(index: 7, y: 650)));      // shown again, same place as before hiding
    }

    [Fact]
    public void Moving_inside_the_same_pixel_asks_for_nothing_and_a_new_pixel_column_asks_for_a_frame()
    {
        var gate = new CrosshairFrameGate();
        Assert.True(gate.Update(State(index: 5, y: 700.1)));
        Assert.False(gate.Update(State(index: 5, y: 700.9)));   // same row: floor(y) = 700
        Assert.False(gate.Update(State(index: 5, y: 700.1)));
        Assert.True(gate.Update(State(index: 5, y: 701.0)));    // next row
        Assert.True(gate.Update(State(index: 5, y: 701.0, x: 300.2)));  // the line follows the cursor: new column, same slot
        Assert.False(gate.Update(State(index: 5, y: 701.0, x: 300.9)));
        Assert.True(gate.Update(State(index: 5, y: 701.0, x: 301.0)));
    }

    [Fact]
    public void Over_the_volume_pane_a_move_between_slots_still_counts_and_a_move_inside_one_does_not()
    {
        var gate = new CrosshairFrameGate();
        Assert.True(gate.Update(State(index: 5, y: 1200, volume: true)));
        Assert.False(gate.Update(State(index: 5, y: 1200, volume: true)));
        Assert.True(gate.Update(State(index: 6, y: 1200, volume: true)));
        Assert.True(gate.Update(State(index: 6, y: 1210, volume: true)));
        // from the volume pane back into the plot at the same slot and row: a different picture (price tag appears)
        Assert.True(gate.Update(State(index: 6, y: 1210)));
    }
}
