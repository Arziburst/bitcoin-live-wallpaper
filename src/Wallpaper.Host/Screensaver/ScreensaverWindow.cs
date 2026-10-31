using System.Diagnostics;
using System.Runtime.InteropServices;
using BitcoinWallpaper.Chart.Core.Desktop;
using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// The /s windows (D-21): a topmost borderless full-screen window with the live chart on the monitor
/// <see cref="DisplayDecisions.SelectActive"/> picks, a black topmost window on every other monitor, no cursor. Closes
/// by <see cref="CloseOnInput"/>, on WM_CLOSE and at the end of the session. Its own window class: the wallpaper's
/// instance lookup never finds it.
/// </summary>
internal sealed unsafe class ScreensaverWindow : IDisposable
{
    public const string WindowClass = "BitcoinWallpaper.Screensaver";
    private const nuint PollTimer = 1;

    private static ScreensaverWindow? _current;

    private readonly ScreensaverScene _scene;
    private readonly List<nint> _windows = [];
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly CloseOnInput _input = new(TimeSpan.Zero);
    private nint _chart;
    private SurfacePlacement? _placement;
    private DCompPresenter? _presenter;
    private bool _dirty;
    private bool _closing;

    public ScreensaverWindow(MarketDataClient client)
    {
        _scene = new ScreensaverScene(client, TimeProvider.System, () => _dirty = true);
    }

    /// <summary>Shows the windows and runs until closed: 0 on a normal close, 1 when the windows cannot be made.</summary>
    public int Run()
    {
        _current = this;
        var monitors = DesktopProbe.Monitors();
        var active = DisplayDecisions.SelectActive(monitors);
        if (active is null)
        {
            ScreensaverLog.Error("no monitor found");
            return 1;
        }

        if (!RegisterClass())
        {
            return 1;
        }

        foreach (var monitor in monitors)
        {
            var b = monitor.Bounds;
            var hwnd = Native.CreateWindowEx(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW, WindowClass, "Bitcoin Wallpaper",
                Native.WS_POPUP | Native.WS_VISIBLE, b.Left, b.Top, b.Width, b.Height, 0, 0, Native.GetModuleHandle(null), 0);
            if (hwnd == 0)
            {
                ScreensaverLog.Error($"CreateWindowEx on {monitor.DeviceName} {b} failed: error {Marshal.GetLastPInvokeError()}");
                if (monitor == active)
                {
                    return 1;
                }

                continue;
            }

            _windows.Add(hwnd);
            if (monitor == active)
            {
                _chart = hwnd;
            }

            ScreensaverLog.Info($"{(monitor == active ? "chart" : "black")} window {Native.Hex(hwnd)} on {monitor.DeviceName} {b}");
        }

        var dpi = active.Dpi == 0 ? 96u : active.Dpi;
        var bounds = active.Bounds;
        _placement = new SurfacePlacement(active.DeviceName, bounds.Left, bounds.Top, bounds.Width, bounds.Height, dpi,
            DisplayDecisions.DpiScale(dpi));
        _presenter = new DCompPresenter();
        _presenter.Bind(_chart, bounds.Width, bounds.Height);
        Native.SetForegroundWindow(_chart);
        Native.ShowCursor(false);
        Native.SetTimer(_chart, PollTimer, (uint)ScreensaverScene.PollInterval.TotalMilliseconds, 0);
        _scene.Start();
        _dirty = true;
        RunMessageLoop();
        return 0;
    }

    public void Dispose()
    {
        if (_chart != 0)
        {
            Native.KillTimer(_chart, PollTimer);
        }

        _presenter?.Dispose();
        _presenter = null;
        foreach (var hwnd in _windows)
        {
            Native.DestroyWindow(hwnd);
        }

        _windows.Clear();
        _scene.Dispose();
        if (_current == this)
        {
            _current = null;
        }
    }

    /// <summary>Blocks in GetMessage while nothing moves; presents one frame per refresh while the chart animates.</summary>
    private void RunMessageLoop()
    {
        while (true)
        {
            if (WantsFrame())
            {
                while (Native.PeekMessage(out var pending, 0, 0, 0, Native.PM_REMOVE))
                {
                    if (pending.message == Native.WM_QUIT)
                    {
                        return;
                    }

                    Native.TranslateMessage(pending);
                    Native.DispatchMessage(pending);
                }

                if (WantsFrame())
                {
                    Render();
                }

                continue;
            }

            if (Native.GetMessage(out var msg, 0, 0, 0) <= 0)
            {
                return;
            }

            Native.TranslateMessage(msg);
            Native.DispatchMessage(msg);
        }
    }

    private bool WantsFrame() => !_closing && (_dirty || _scene.NeedsFrame) && _presenter is { IsBound: true };

    private void Render()
    {
        _dirty = false;
        if (_presenter!.Render(_scene, _placement!) || !_presenter.DeviceLost)
        {
            return;
        }

        ScreensaverLog.Warn($"the GPU device was lost (0x{_presenter.DeviceRemovedCode:X8}); building a new presenter");
        _presenter.Dispose();
        _presenter = new DCompPresenter();
        _presenter.Bind(_chart, _placement!.Width, _placement.Height);
        _dirty = true;
    }

    private void Close(string why)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        ScreensaverLog.Info($"closing: {why}");
        Native.PostQuitMessage(0);
    }

    private nint OnMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_SETCURSOR:
                Native.SetCursor(0);
                return 1;
            case Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN:
                if (_input.OnKey())
                {
                    Close("a key");
                }

                return 0;
            case Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN:
                if (_input.OnMouseButton())
                {
                    Close("a mouse button");
                }

                return 0;
            case Native.WM_MOUSEMOVE:
                if (Native.GetCursorPos(out var point)
                    && _input.OnMouseMove(point.X, point.Y, Stopwatch.GetElapsedTime(_origin)))
                {
                    Close("the mouse moved");
                }

                return 0;
            case Native.WM_TIMER when wParam == PollTimer:
                _scene.Poll();
                return 0;
            case Native.WM_CLOSE:
                Close("WM_CLOSE");
                return 0;
            case Native.WM_QUERYENDSESSION:
                return 1;
            case Native.WM_ENDSESSION:
                if (wParam != 0)
                {
                    Close("end of session");
                }

                return 0;
            case Native.WM_DISPLAYCHANGE:
                Close("the display changed");
                return 0;
            default:
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static bool RegisterClass()
    {
        fixed (char* name = WindowClass)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = Native.GetModuleHandle(null),
                hbrBackground = Native.GetStockObject(Native.BLACK_BRUSH),
                lpszClassName = name,
            };
            if (Native.RegisterClassEx(&wc) == 0)
            {
                ScreensaverLog.Error($"RegisterClassEx({WindowClass}) failed: error {Marshal.GetLastPInvokeError()}");
                return false;
            }
        }

        return true;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (_current is { } window)
            {
                return window.OnMessage(hwnd, msg, wParam, lParam);
            }
        }
        catch (Exception e)
        {
            ScreensaverLog.Error($"message 0x{msg:X4}: {e}");
            _current?.Close("an error");
            return 0;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
