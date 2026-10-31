using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Crosshair;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host;

/// <summary>
/// The crosshair (D-35) in the host: <see cref="MouseHook"/> posts <see cref="CursorMessage"/> when the mouse moves;
/// the host reads what is under the cursor (<see cref="DesktopCursorProbe"/>), decides whether it shows
/// (<see cref="CrosshairVisibility"/>), hands the cursor to the scene and presents a frame only when the picture changes
/// (<see cref="CrosshairFrameGate"/>). No timer: while the mouse is still nothing runs. The hook runs only while the
/// crosshair can show (on, attached, not paused); turning it off hides the crosshair with one frame.
/// </summary>
internal sealed partial class WallpaperHost
{
    /// <summary>Posted by the mouse hook's thread: the cursor moved (coalesced: one pending at a time).</summary>
    private const uint CursorMessage = Native.WM_APP + 41;

    private readonly CrosshairFrameGate _crosshairGate = new();
    private DesktopCursorProbe? _cursorProbe;
    private MouseHook? _mouseHook;
    private bool _mouseHookFailed;

    private bool CrosshairCanShow =>
        _scene is ICrosshairScene && _enabled && !_paused && !_shutDown && _window != 0
        && _placement is not null && _layer?.State is not null;

    /// <summary>Starts or stops the mouse hook to match <see cref="CrosshairCanShow"/>; stopping hides the crosshair.
    /// Called wherever the on, attach or pause state changes.</summary>
    private void UpdateCrosshair()
    {
        if (CrosshairCanShow)
        {
            if (_mouseHook is null && !_mouseHookFailed)
            {
                _mouseHook = MouseHook.Start(_window, CursorMessage);
                _mouseHookFailed = _mouseHook is null;
                if (_mouseHook is not null)
                {
                    Log.Info("crosshair: following the mouse over the desktop");
                }
            }

            return;
        }

        _mouseHookFailed = false; // tried again the next time it can show
        if (_mouseHook is null)
        {
            return;
        }

        _mouseHook.Dispose();
        _mouseHook = null;
        _cursorProbe?.Dispose();
        _cursorProbe = null;
        ShowCursorAt(null);
        Log.Info("crosshair: stopped following the mouse");
    }

    private void OnCursorMessage()
    {
        if (_mouseHook is null || !_mouseHook.TryTake(out var x, out var y))
        {
            return;
        }

        CursorPx? cursor = null;
        if (CrosshairCanShow && Native.GetWindowRect(_layer!.Window, out var rect))
        {
            // Our layer covers the wallpaper's monitor; its rect is what is drawn (surface px = screen px - its origin).
            var monitor = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var sample = CrosshairVisibility.Contains(monitor, x, y)
                ? (_cursorProbe ??= new DesktopCursorProbe()).Sample(x, y)
                : new CursorSample(x, y, null, false); // another monitor: no need to look under it
            if (CrosshairVisibility.ShouldShow(sample, monitor, _paused, _enabled ? WallpaperState.On : WallpaperState.Off))
            {
                cursor = new CursorPx(x - rect.Left, y - rect.Top);
            }
        }

        ShowCursorAt(cursor);
    }

    // The scene gets the cursor; a frame is asked for only when the picture changes. Inside a tween the message loop
    // draws the next frame anyway.
    private void ShowCursorAt(CursorPx? cursor)
    {
        if (_scene is not ICrosshairScene scene || !_crosshairGate.Update(scene.SetCursor(cursor)))
        {
            return;
        }

        _dirty = true;
        if (!WantsTweenFrame())
        {
            RenderIfDirty();
        }
    }

    private void DisposeCrosshair()
    {
        _mouseHook?.Dispose();
        _mouseHook = null;
        _cursorProbe?.Dispose();
        _cursorProbe = null;
    }
}
