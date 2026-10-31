using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Crosshair;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host;

/// <summary>
/// The crosshair (D-35, D-38) in the host: <see cref="MouseHook"/> posts <see cref="CursorMessage"/> when the mouse
/// moves; the host decides whether it shows (<see cref="CrosshairVisibility"/>: anywhere on our monitor, whatever is
/// under the cursor), hands the cursor to the scene and presents a frame only when the picture changes
/// (<see cref="CrosshairFrameGate"/>). No timer: while the mouse is still nothing runs. The hook runs only while the
/// crosshair can show (on, attached, not paused); turning it off hides the crosshair with one frame. Windows may remove
/// the hook silently (LowLevelHooksTimeout): the existing watchdog checks it with one GetCursorPos
/// (<see cref="HookReinstallDecider"/>) and installs it again.
/// </summary>
internal sealed partial class WallpaperHost
{
    /// <summary>Posted by the mouse hook's thread: the cursor moved (coalesced: one pending at a time).</summary>
    private const uint CursorMessage = Native.WM_APP + 41;

    private readonly CrosshairFrameGate _crosshairGate = new();
    private readonly HookReinstallDecider _hookReinstall = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
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
                    Log.Info("crosshair: following the mouse on the wallpaper's monitor");
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
        ShowCursorAt(null);
        Log.Info("crosshair: stopped following the mouse");
    }

    /// <summary>The watchdog (every 2 s, only while on): the hook is expected but has been silent while the cursor
    /// moved, so Windows removed it; install it again. One GetCursorPos; nothing while the hook is not running.</summary>
    private void CheckMouseHook()
    {
        if (_mouseHook is not { } hook || !CrosshairCanShow || !Native.GetCursorPos(out var cursor))
        {
            return;
        }

        var (x, y, at) = hook.LastReport();
        var now = System.Diagnostics.Stopwatch.GetElapsedTime(0, System.Diagnostics.Stopwatch.GetTimestamp());
        if (!_hookReinstall.ShouldReinstall(true, x, y, at, cursor.X, cursor.Y, now))
        {
            return;
        }

        Log.Warn($"crosshair: the mouse hook reported nothing for {(now - at).TotalSeconds:0.0} s while the cursor moved " +
                 "(Windows removed it); installing it again");
        hook.Dispose();
        _mouseHook = null;
        _mouseHookFailed = false;
        UpdateCrosshair();
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
            if (CrosshairVisibility.ShouldShow(new CursorSample(x, y), monitor, _paused, _enabled ? WallpaperState.On : WallpaperState.Off))
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
    }
}
