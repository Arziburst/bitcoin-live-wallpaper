// Contract #47 (D-37), criterion 2: which rectangle each surface uses, and when a work-area change re-lays out.
// Pure decisions, no Win32 calls; the host feeds them (GetMonitorInfo's rcMonitor / rcWork, the window messages).
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host, src/Wallpaper.Host/WorkArea.cs.
//
//   public enum SurfaceKind { DesktopWallpaper, LockScreenSnapshot, Screensaver }
//
//   public static class SurfaceContent
//   {
//       // The chart's content rectangle for the surface, relative to the monitor's top-left (physical px).
//       // monitor / workArea: rcMonitor / rcWork in virtual-screen px (PixelRect, Right/Bottom exclusive).
//       //  DesktopWallpaper: workArea intersected with monitor, moved by -monitor origin; an empty intersection
//       //                    falls back to the full monitor.
//       //  LockScreenSnapshot, Screensaver: the full monitor, RectPx(0, 0, monitor width, monitor height).
//       public static RectPx For(SurfaceKind kind, PixelRect monitor, PixelRect workArea);
//   }
//
//   // Re-layout trigger. Remembers the work area of the last answered check (constructed with the one in use).
//   public sealed class WorkAreaRelayout
//   {
//       public const uint WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, SPI_SETWORKAREA = 0x002F;
//       public WorkAreaRelayout(PixelRect current);
//       // True (and the area is remembered) only when the message is WM_DISPLAYCHANGE, or WM_SETTINGCHANGE with
//       // wParam == SPI_SETWORKAREA, AND currentWorkArea differs from the remembered one. Any other message, or an
//       // unchanged area: false, nothing remembered.
//       public bool ShouldRelayout(uint message, nuint wParam, PixelRect currentWorkArea);
//   }

using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host;

namespace BitcoinWallpaper.Host.Tests;

public class WorkAreaSurfaceTests
{
    private const uint SettingChange = 0x001A, DisplayChange = 0x007E, SetWorkArea = 0x002F;

    // A second monitor to the right of the primary: virtual-screen coordinates are not monitor-relative.
    private static readonly PixelRect Monitor = new(2560, 0, 7680, 1440);
    private static readonly PixelRect WorkBottomBar = new(2560, 0, 7680, 1440 - 84);

    [Fact]
    public void The_desktop_wallpaper_uses_the_work_area_relative_to_the_monitor()
    {
        Assert.Equal(new RectPx(0, 0, 5120, 1356), SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, WorkBottomBar));
        Assert.Equal(new RectPx(0, 96, 5120, 1344),
            SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, new PixelRect(2560, 96, 7680, 1440)));
        Assert.Equal(new RectPx(100, 0, 5020, 1440),
            SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, new PixelRect(2660, 0, 7680, 1440)));
        Assert.Equal(new RectPx(0, 0, 5020, 1440),
            SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, new PixelRect(2560, 0, 7580, 1440)));
    }

    [Fact]
    public void Without_a_taskbar_the_desktop_content_is_the_full_monitor()
    {
        Assert.Equal(new RectPx(0, 0, 5120, 1440), SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, Monitor));
    }

    [Fact]
    public void The_lock_screen_snapshot_and_the_screensaver_use_the_full_monitor_whatever_the_work_area()
    {
        foreach (var kind in new[] { SurfaceKind.LockScreenSnapshot, SurfaceKind.Screensaver })
        {
            Assert.Equal(new RectPx(0, 0, 5120, 1440), SurfaceContent.For(kind, Monitor, WorkBottomBar));
            Assert.Equal(new RectPx(0, 0, 5120, 1440), SurfaceContent.For(kind, Monitor, Monitor));
        }
    }

    [Fact]
    public void A_work_area_that_leaves_the_monitor_is_cut_to_it_and_an_empty_one_falls_back_to_the_full_monitor()
    {
        Assert.Equal(new RectPx(0, 0, 5120, 1440),
            SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, new PixelRect(0, -50, 9000, 1500)));
        Assert.Equal(new RectPx(0, 0, 5120, 1440),
            SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, new PixelRect(0, 0, 100, 100)));
        Assert.Equal(new RectPx(0, 0, 5120, 1440),
            SurfaceContent.For(SurfaceKind.DesktopWallpaper, Monitor, new PixelRect(3000, 500, 3000, 900)));
    }

    // ---- re-layout trigger --------------------------------------------------------------------------------------

    [Fact]
    public void A_changed_work_area_on_SPI_SETWORKAREA_leads_to_one_relayout()
    {
        var r = new WorkAreaRelayout(Monitor);
        Assert.True(r.ShouldRelayout(SettingChange, SetWorkArea, WorkBottomBar));
        Assert.False(r.ShouldRelayout(SettingChange, SetWorkArea, WorkBottomBar)); // the same again: none
        Assert.False(r.ShouldRelayout(DisplayChange, 0, WorkBottomBar));
    }

    [Fact]
    public void A_changed_work_area_on_a_display_change_leads_to_one_relayout()
    {
        var r = new WorkAreaRelayout(Monitor);
        Assert.True(r.ShouldRelayout(DisplayChange, 32, WorkBottomBar));
        Assert.False(r.ShouldRelayout(DisplayChange, 32, WorkBottomBar));
        Assert.True(r.ShouldRelayout(DisplayChange, 32, Monitor)); // back: one more
    }

    [Fact]
    public void An_unchanged_work_area_leads_to_no_relayout_on_either_message()
    {
        var r = new WorkAreaRelayout(WorkBottomBar);
        Assert.False(r.ShouldRelayout(SettingChange, SetWorkArea, WorkBottomBar));
        Assert.False(r.ShouldRelayout(DisplayChange, 0, WorkBottomBar));
    }

    [Fact]
    public void Another_setting_change_never_relayouts_and_does_not_swallow_the_change()
    {
        var r = new WorkAreaRelayout(Monitor);
        Assert.False(r.ShouldRelayout(SettingChange, 0x0014 /* SPI_SETDESKWALLPAPER */, WorkBottomBar));
        Assert.False(r.ShouldRelayout(0x0002 /* WM_DESTROY */, SetWorkArea, WorkBottomBar));
        Assert.True(r.ShouldRelayout(SettingChange, SetWorkArea, WorkBottomBar)); // not remembered by the ignored ones
    }
}
