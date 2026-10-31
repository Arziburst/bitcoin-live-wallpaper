using System.Diagnostics;
using System.Runtime.InteropServices;
using BitcoinWallpaper.Chart.Core.Desktop;

namespace BitcoinWallpaper.Host;

/// <summary>
/// The wallpaper process: a hidden top-level message window (display, DPI, taskbar, session and power
/// notifications, the tray icon, timers), the desktop layer and the presenter. Everything runs on the thread
/// that calls <see cref="Run"/>; the message loop blocks, so nothing runs while nothing happens. A frame is
/// presented only when something changed (attach, placement, device loss, a redraw request).
/// </summary>
internal sealed unsafe class WallpaperHost : IDisposable
{
    private const string WindowClass = "BitcoinWallpaper.Host";

    private const uint TrayMessage = Native.WM_APP + 1;
    private const uint RedrawMessage = Native.WM_APP + 2;
    private const uint PauseMessage = Native.WM_APP + 3;

    private const nuint WatchdogTimer = 1;
    private const nuint CheckSoonTimer = 2;
    private const nuint MemoryTimer = 3;
    private const uint WatchdogMs = 2000;
    private const uint CheckSoonMs = 300;
    private const uint FirstMemoryLogMs = 60_000;
    private const uint MemoryLogMs = 10 * 60_000;

    /// <summary>Attach failures are logged the first time and then every this many attempts (about once a minute).</summary>
    private const int AttachFailureLogEvery = 30;

    private static WallpaperHost? _current;

    private readonly IWallpaperScene _scene;
    private readonly uint _taskbarCreatedMessage;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private nint _window;
    private DesktopLayer? _layer;
    private DCompPresenter? _presenter;
    private TrayIcon? _tray;
    private SurfacePlacement? _placement;
    private bool _dirty;
    private bool _paused;
    private bool _sessionRegistered;
    private bool _shutDown;
    private bool _noMonitorLogged;
    private bool _firstMemoryLogDone;
    private int _attachFailures;
    private string? _pendingTrigger;
    private TimeSpan _pendingSince;
    private long _presentsBefore;

    public WallpaperHost(IWallpaperScene scene)
    {
        _scene = scene;
        _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
        _current = this;
    }

    /// <summary>Thread-safe: ask for one new frame (the scene's data changed).</summary>
    public void RequestRedraw()
    {
        if (_window != 0)
        {
            Native.PostMessage(_window, RedrawMessage, 0, 0);
        }
    }

    /// <summary>Thread-safe: while paused nothing is presented; a redraw requested meanwhile is presented on resume.</summary>
    public void SetPaused(bool paused)
    {
        if (_window != 0)
        {
            Native.PostMessage(_window, PauseMessage, paused ? 1u : 0u, 0);
        }
    }

    /// <summary>Runs until Exit from the tray, WM_CLOSE or the end of the session; returns the exit code.</summary>
    public int Run()
    {
        _window = CreateMessageWindow();
        if (_window == 0)
        {
            return 1;
        }

        _sessionRegistered = Native.WTSRegisterSessionNotification(_window, Native.NOTIFY_FOR_THIS_SESSION);
        if (!_sessionRegistered)
        {
            Log.Warn("WTSRegisterSessionNotification failed; lock/unlock is covered by the watchdog only");
        }

        _tray = new TrayIcon(_window, TrayMessage);
        _tray.Add();
        _layer = new DesktopLayer(OnLayerLost);

        Native.SetTimer(_window, WatchdogTimer, WatchdogMs, 0);
        Native.SetTimer(_window, MemoryTimer, FirstMemoryLogMs, 0);
        Check("start");

        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(msg);
            Native.DispatchMessage(msg);
        }

        Shutdown("exit requested");
        return 0;
    }

    public void Dispose()
    {
        Shutdown("dispose");
        if (_current == this)
        {
            _current = null;
        }
    }

    /// <summary>Destroys our wallpaper window (the shell repaints its own), removes the tray icon. Idempotent.</summary>
    private void Shutdown(string why)
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;
        Log.Info($"shutting down ({why}); {_presenter?.Presents ?? 0} presents with the current device, " +
                 $"{_presentsBefore} before");
        if (_window != 0)
        {
            Native.KillTimer(_window, WatchdogTimer);
            Native.KillTimer(_window, CheckSoonTimer);
            Native.KillTimer(_window, MemoryTimer);
        }

        Try(() => _presenter?.Unbind());
        Try(() => _layer?.Dispose());
        Try(() => _presenter?.Dispose());
        _presenter = null;
        Try(() => _tray?.Dispose());
        if (_sessionRegistered)
        {
            Native.WTSUnRegisterSessionNotification(_window);
            _sessionRegistered = false;
        }

        if (_window != 0)
        {
            Native.DestroyWindow(_window);
            _window = 0;
        }

        Log.Info("cleaned up; the desktop shows the normal wallpaper again");
    }

    /// <summary>
    /// The one place that brings the wallpaper in line with the world: the active monitor and its placement,
    /// the attach state (<see cref="ReattachDecider"/>), the GPU device; then presents if something changed.
    /// </summary>
    private void Check(string trigger)
    {
        if (_shutDown || _layer is null)
        {
            return;
        }

        var monitors = DesktopProbe.Monitors();
        var active = DisplayDecisions.SelectActive(monitors);
        var placement = DisplayDecisions.Layout(monitors);
        if (active is null || placement is null)
        {
            if (!_noMonitorLogged)
            {
                Log.Warn($"{trigger}: no monitor; waiting");
                _noMonitorLogged = true;
            }

            return;
        }

        _noMonitorLogged = false;

        if (_presenter is { DeviceLost: true })
        {
            Log.Warn($"{trigger}: the GPU device was lost (reason 0x{_presenter.DeviceRemovedCode:X8}); recreating the presenter");
            DropPresenter();
        }

        if (_layer.State is not { } state)
        {
            TryAttach(placement, active.Bounds, trigger);
        }
        else
        {
            var reason = ReattachDecider.Decide(state, DesktopProbe.Observe(state));
            if (reason == ReattachReason.ZOrderDrift && _layer.FixZOrder()
                && ReattachDecider.Decide(state, DesktopProbe.Observe(state)) == ReattachReason.None)
            {
                Log.Warn($"{trigger}: z-order drift (something above us is not SHELLDLL_DefView); put back below DefView");
            }
            else if (reason != ReattachReason.None)
            {
                Log.Warn($"{trigger}: re-attaching, reason {reason}");
                _presenter?.Unbind();
                TryAttach(placement, active.Bounds, trigger);
            }
            else if (placement != _placement)
            {
                Log.Info($"{trigger}: re-layout {Describe(_placement)} -> {Describe(placement)}");
                if (_layer.Move(placement, active.Bounds))
                {
                    _placement = placement;
                    _dirty = true;
                    try
                    {
                        _presenter?.Resize(placement.Width, placement.Height);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"resizing the swap chain failed; recreating the presenter: {e.Message}");
                        DropPresenter();
                    }
                }
            }
        }

        EnsurePresenter();
        RenderIfDirty();
        if (!_dirty)
        {
            _pendingSince = TimeSpan.Zero;
        }
    }

    private void TryAttach(SurfacePlacement placement, ScreenRect monitorBounds, string trigger)
    {
        if (_attachFailures % AttachFailureLogEvery == 0)
        {
            Log.Info($"{trigger}: attaching on {Describe(placement)}, monitor {monitorBounds}");
        }

        // The swap chain belongs to the window being replaced; it is bound again to the new one.
        _presenter?.Unbind();
        if (!_layer!.Attach(placement, monitorBounds))
        {
            if (_attachFailures % AttachFailureLogEvery == 0)
            {
                Log.Warn($"{trigger}: attach failed (attempt {_attachFailures + 1}); the watchdog tries again every {WatchdogMs / 1000} s");
            }

            _attachFailures++;
            _placement = null;
            return;
        }

        var waited = _pendingSince == TimeSpan.Zero ? "" : $" ({(_clock.Elapsed - _pendingSince).TotalSeconds:0.0} s after the event)";
        Log.Info($"{trigger}: attached{waited}{(_attachFailures > 0 ? $" after {_attachFailures} failed attempts" : "")}");
        _attachFailures = 0;
        _placement = placement;
        _dirty = true;
    }

    private void EnsurePresenter()
    {
        if (_layer?.State is null || _placement is null || _presenter is { IsBound: true })
        {
            return;
        }

        try
        {
            _presenter ??= new DCompPresenter();
            _presenter.Bind(_layer.Window, _placement.Width, _placement.Height);
            _dirty = true;
        }
        catch (Exception e)
        {
            // A half-built presenter may leave a composition target on our window; start over with a new window.
            Log.Error($"creating the presenter failed; the watchdog re-attaches and tries again: {e.Message}");
            DropPresenter();
            _layer.Detach("presenter failed");
            _placement = null;
        }
    }

    private void RenderIfDirty()
    {
        if (!_dirty || _paused || _placement is null || _presenter is not { IsBound: true } presenter)
        {
            return;
        }

        try
        {
            if (presenter.Render(_scene, _placement))
            {
                _dirty = false;
                if (presenter.Presents == 1 || _pendingSince != TimeSpan.Zero)
                {
                    var after = _pendingSince == TimeSpan.Zero ? "" : $", {(_clock.Elapsed - _pendingSince).TotalSeconds:0.0} s after the event";
                    Log.Info($"presented {_placement.Width}x{_placement.Height} on {_placement.DeviceName}{after}");
                }

                _pendingSince = TimeSpan.Zero;
                return;
            }
        }
        catch (Exception e)
        {
            Log.Error($"drawing failed: {e.Message}");
        }

        // Drawing or Present failed (device lost): rebuild the presenter on the next check and draw again.
        DropPresenter();
        CheckSoon("present failed");
    }

    private void DropPresenter()
    {
        if (_presenter is null)
        {
            return;
        }

        _presentsBefore += _presenter.Presents;
        Try(() => _presenter.Dispose());
        _presenter = null;
        _dirty = true;
    }

    /// <summary>Debounced check: events come in bursts (a display switch sends several); 300 ms after the last one.</summary>
    private void CheckSoon(string trigger)
    {
        _pendingTrigger = _pendingTrigger is null || _pendingTrigger == trigger ? trigger : $"{_pendingTrigger} + {trigger}";
        if (_pendingSince == TimeSpan.Zero)
        {
            _pendingSince = _clock.Elapsed;
        }

        Native.SetTimer(_window, CheckSoonTimer, CheckSoonMs, 0);
    }

    private void OnLayerLost(string what)
    {
        Log.Warn(what);
        CheckSoon("shell window destroyed");
    }

    private void LogMemory()
    {
        using var process = Process.GetCurrentProcess();
        const double mb = 1024 * 1024;
        Log.Info($"memory: working set {process.WorkingSet64 / mb:0.0} MB, private {process.PrivateMemorySize64 / mb:0.0} MB, " +
                 $"managed heap {GC.GetTotalMemory(false) / mb:0.0} MB; CPU time {process.TotalProcessorTime.TotalSeconds:0.0} s; " +
                 $"presents {_presentsBefore + (_presenter?.Presents ?? 0)}; up {_clock.Elapsed:d\\.hh\\:mm\\:ss}");
    }

    private nint OnMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
                Log.Info($"WM_DISPLAYCHANGE: {lParam & 0xFFFF}x{(lParam >> 16) & 0xFFFF}, {wParam} bpp");
                CheckSoon("display change");
                return 0;
            case Native.WM_DPICHANGED:
                Log.Info($"WM_DPICHANGED: {wParam & 0xFFFF} dpi");
                CheckSoon("dpi change");
                return 0;
            case Native.WM_WTSSESSION_CHANGE:
                OnSessionChange((uint)wParam);
                return 0;
            case Native.WM_POWERBROADCAST:
                OnPower((uint)wParam);
                return 1;
            case Native.WM_TIMER:
                OnTimer(wParam);
                return 0;
            case TrayMessage:
                _tray?.OnCallback(lParam);
                return 0;
            case Native.WM_COMMAND when (wParam & 0xFFFF) == TrayIcon.ExitCommand:
                Log.Info("Exit chosen in the tray menu");
                Native.PostQuitMessage(0);
                return 0;
            case RedrawMessage:
                _dirty = true;
                RenderIfDirty();
                return 0;
            case PauseMessage:
                _paused = wParam != 0;
                Log.Info(_paused ? "paused: no Present until resumed" : "resumed");
                RenderIfDirty();
                return 0;
            case Native.WM_CLOSE:
                Log.Info("WM_CLOSE received (taskkill or a close request)");
                Native.PostQuitMessage(0);
                return 0;
            case Native.WM_QUERYENDSESSION:
                return 1;
            case Native.WM_ENDSESSION when wParam != 0:
                Shutdown("the session is ending (sign-out, restart or shutdown)");
                return 0;
        }

        if (msg == _taskbarCreatedMessage && msg != 0)
        {
            Log.Info($"TaskbarCreated (explorer pid now {DesktopProbe.ExplorerPid()})");
            _tray?.Add();
            CheckSoon("TaskbarCreated");
            return 0;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnTimer(nuint id)
    {
        switch (id)
        {
            case WatchdogTimer:
                Check("watchdog");
                break;
            case CheckSoonTimer:
                Native.KillTimer(_window, CheckSoonTimer);
                var trigger = _pendingTrigger ?? "event";
                _pendingTrigger = null;
                Check(trigger);
                break;
            case MemoryTimer:
                LogMemory();
                if (!_firstMemoryLogDone)
                {
                    _firstMemoryLogDone = true;
                    Native.SetTimer(_window, MemoryTimer, MemoryLogMs, 0);
                }

                break;
        }
    }

    private void OnSessionChange(uint change)
    {
        var name = change switch
        {
            Native.WTS_SESSION_LOCK => "lock",
            Native.WTS_SESSION_UNLOCK => "unlock",
            Native.WTS_SESSION_LOGON => "logon",
            Native.WTS_CONSOLE_CONNECT => "console connect",
            Native.WTS_CONSOLE_DISCONNECT => "console disconnect",
            Native.WTS_REMOTE_CONNECT => "remote connect",
            Native.WTS_REMOTE_DISCONNECT => "remote disconnect",
            _ => $"0x{change:X}",
        };
        Log.Info($"session change: {name}");
        if (change is Native.WTS_SESSION_UNLOCK or Native.WTS_SESSION_LOGON or Native.WTS_CONSOLE_CONNECT or Native.WTS_REMOTE_CONNECT)
        {
            CheckSoon($"session {name}");
        }
    }

    private void OnPower(uint change)
    {
        switch (change)
        {
            case Native.PBT_APMSUSPEND:
                Log.Info("power: suspending");
                break;
            case Native.PBT_APMRESUMEAUTOMATIC or Native.PBT_APMRESUMESUSPEND:
                Log.Info($"power: resumed ({(change == Native.PBT_APMRESUMEAUTOMATIC ? "automatic" : "by the user")})");
                CheckSoon("resume");
                break;
        }
    }

    private nint CreateMessageWindow()
    {
        var instance = Native.GetModuleHandle(null);
        fixed (char* name = WindowClass)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                lpszClassName = name,
            };
            if (Native.RegisterClassEx(&wc) == 0)
            {
                Log.Error($"RegisterClassEx({WindowClass}) failed: error {Marshal.GetLastPInvokeError()}");
                return 0;
            }
        }

        // A hidden top-level window, not HWND_MESSAGE: message-only windows get no broadcasts
        // (WM_DISPLAYCHANGE, TaskbarCreated, WM_POWERBROADCAST).
        var hwnd = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, WindowClass, "Bitcoin Wallpaper",
            Native.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        if (hwnd == 0)
        {
            Log.Error($"CreateWindowEx(message window) failed: error {Marshal.GetLastPInvokeError()}");
        }

        return hwnd;
    }

    private static string Describe(SurfacePlacement? p) =>
        p is null ? "nothing" : $"{p.DeviceName} {p.Width}x{p.Height} at ({p.X},{p.Y}) {p.Dpi} dpi";

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error($"cleanup step failed: {e.Message}");
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (_current is { } host && (host._window == 0 || host._window == hwnd))
            {
                return host.OnMessage(hwnd, msg, wParam, lParam);
            }
        }
        catch (Exception e)
        {
            Log.Error($"message 0x{msg:X4}: {e}");
            return 0;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
