using System.Runtime.InteropServices;

namespace DesktopSpike;

/// <summary>
/// Puts one window of ours behind the desktop icons, on either layout, and takes it away again.
/// Written from the Win32 docs and the research notes (coordinator/research/stack.md); one instance at a time.
/// </summary>
internal sealed unsafe class DesktopAttacher : IDisposable
{
    /// <summary>Undocumented Progman message that makes the shell create the wallpaper WorkerW.</summary>
    private const uint SpawnWorkerWMessage = 0x052C;

    private const string WindowClass = "BitcoinWallpaper.DesktopSpike";

    private static DesktopAttacher? _current;
    private static bool _classRegistered;

    private nint _hook;
    private bool _detaching;

    public nint Window { get; private set; }

    public nint Parent { get; private set; }

    /// <summary>Raised layout: the shell's WorkerW kept below us; legacy: our parent WorkerW.</summary>
    public nint ShellWorkerW { get; private set; }

    public DesktopLayout Layout { get; private set; }

    /// <summary>
    /// Raised layout: a WS_EX_LAYERED child (alpha 255, the research notes' path) when true, a plain child
    /// (the featherwall notes' path) when false.
    /// </summary>
    public bool LayeredChild { get; init; } = true;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Set when the shell destroyed the WorkerW or our window; the main loop re-attaches.</summary>
    public bool Lost { get; private set; }

    /// <summary>
    /// Diagnostics only: an ordinary top-level window on the monitor, not attached to the desktop, to check
    /// the DirectComposition/Direct2D path without touching the shell.
    /// </summary>
    public bool CreateWindowed(DesktopProbe.MonitorTarget monitor)
    {
        _current = this;
        Window = CreateWindow(layered: false);
        if (Window == 0)
        {
            return false;
        }

        Native.SetWindowLongPtr(Window, Native.GWL_STYLE, unchecked((nint)Native.WS_POPUP));
        Width = monitor.Bounds.Width / 4;
        Height = monitor.Bounds.Height / 4;
        Native.SetWindowPos(Window, 0, monitor.Bounds.Left + 100, monitor.Bounds.Top + 100, Width, Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Log.Info($"windowed: top-level window {Native.Hex(Window)} {Width}x{Height} px, nothing attached to the desktop");
        return true;
    }

    public bool Attach(DesktopLayout layout, DesktopProbe.MonitorTarget monitor)
    {
        _current = this;
        Layout = layout;
        Lost = false;

        var progman = Native.FindWindow(DesktopLayoutDetector.ProgmanClass, null);
        if (progman == 0)
        {
            Log.Error("attach: no Progman window");
            return false;
        }

        // Ask the shell for its wallpaper WorkerW (both layouts). wParam 0xD / lParam 0x1 as in the research notes.
        var sent = Native.SendMessageTimeout(progman, SpawnWorkerWMessage, 0xD, 0x1, Native.SMTO_NORMAL, 1000, out _);
        Log.Info($"sent 0x052C(0xD, 0x1) to Progman {Native.Hex(progman)}: {(sent != 0 ? "ok" : "timed out/failed")}");

        nint insertAfter;
        if (layout == DesktopLayout.Legacy)
        {
            var workerW = DesktopLayoutDetector.FindLegacyWorkerW(DesktopProbe.Children(0));
            if (workerW == 0)
            {
                Native.SendMessageTimeout(progman, SpawnWorkerWMessage, 0, 0, Native.SMTO_NORMAL, 1000, out _);
                Log.Info("legacy: no WorkerW after the DefView host yet; sent 0x052C(0, 0) as well");
                workerW = DesktopLayoutDetector.FindLegacyWorkerW(DesktopProbe.Children(0));
            }

            if (workerW == 0)
            {
                Log.Error("legacy: no top-level WorkerW behind the SHELLDLL_DefView host");
                return false;
            }

            Parent = workerW;
            ShellWorkerW = workerW;
            insertAfter = 0; // HWND_TOP among the WorkerW's children
        }
        else
        {
            var targets = DesktopLayoutDetector.FindRaisedTargets(DesktopProbe.Children(progman));
            if (targets is null)
            {
                Log.Error("raised: no SHELLDLL_DefView child of Progman");
                return false;
            }

            if (targets.WorkerW == 0)
            {
                Log.Warn("raised: Progman has no WorkerW child; attaching below DefView anyway");
            }

            Parent = progman;
            ShellWorkerW = targets.WorkerW;
            insertAfter = targets.DefView;
        }

        Window = CreateWindow(layout == DesktopLayout.Raised && LayeredChild);
        if (Window == 0)
        {
            return false;
        }

        // SetParent docs: clear WS_POPUP and set WS_CHILD before parenting a former top-level window.
        Native.SetWindowLongPtr(Window, Native.GWL_STYLE, (nint)(Native.WS_CHILD | Native.WS_CLIPSIBLINGS));
        if (Native.SetParent(Window, Parent) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            Log.Error($"SetParent failed: error {Marshal.GetLastPInvokeError()}");
            return false;
        }

        // Our rectangle in the parent's client coordinates: the parent spans the virtual screen.
        var corners = stackalloc Native.POINT[2];
        corners[0] = new Native.POINT { X = monitor.Bounds.Left, Y = monitor.Bounds.Top };
        corners[1] = new Native.POINT { X = monitor.Bounds.Right, Y = monitor.Bounds.Bottom };
        Native.MapWindowPoints(0, Parent, corners, 2);
        Width = corners[1].X - corners[0].X;
        Height = corners[1].Y - corners[0].Y;

        if (!Native.SetWindowPos(Window, insertAfter, corners[0].X, corners[0].Y, Width, Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW))
        {
            Log.Error($"SetWindowPos failed: error {Marshal.GetLastPInvokeError()}");
            return false;
        }

        if (layout == DesktopLayout.Raised && ShellWorkerW != 0)
        {
            // Keep the shell's wallpaper WorkerW below us: at the bottom of Progman's children.
            Native.SetWindowPos(ShellWorkerW, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        Log.Info(layout == DesktopLayout.Raised
            ? $"attach path: raised - {(LayeredChild ? "layered" : "plain")} child {Native.Hex(Window)} of Progman {Native.Hex(Parent)}, " +
              $"below SHELLDLL_DefView {Native.Hex(insertAfter)}, above WorkerW {Native.Hex(ShellWorkerW)}"
            : $"attach path: legacy - child {Native.Hex(Window)} of top-level WorkerW {Native.Hex(Parent)} " +
              "behind the SHELLDLL_DefView host");
        Log.Info($"our window {DesktopProbe.Describe(Window)} at ({corners[0].X},{corners[0].Y}) in parent, {Width}x{Height} px");
        Log.Info("Progman children after attach (z-order, top first):");
        foreach (var child in DesktopProbe.Children(progman))
        {
            Log.Info($"  {child.ClassName,-30} {Native.Hex(child.Handle)}");
        }

        CheckDpi();
        HookShellWorkerW();
        return true;
    }

    public void Detach()
    {
        _detaching = true;
        if (_hook != 0)
        {
            Native.UnhookWinEvent(_hook);
            _hook = 0;
        }

        if (Window != 0 && Native.IsWindow(Window))
        {
            Native.DestroyWindow(Window);
            Log.Info($"destroyed our window {Native.Hex(Window)}");
        }

        Window = 0;

        // Let the shell repaint what we covered (no SPI_SETDESKWALLPAPER: on the raised layout it destroys the WorkerW).
        var repaint = ShellWorkerW != 0 && Native.IsWindow(ShellWorkerW) ? ShellWorkerW : Parent;
        if (repaint != 0 && Native.IsWindow(repaint))
        {
            Native.RedrawWindow(repaint, 0, 0,
                Native.RDW_INVALIDATE | Native.RDW_ERASE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
            Log.Info($"asked the shell to repaint {Native.Hex(repaint)}");
        }

        _detaching = false;
    }

    public void Dispose()
    {
        Detach();
        if (_current == this)
        {
            _current = null;
        }
    }

    private nint CreateWindow(bool layered)
    {
        var instance = Native.GetModuleHandle(null);
        if (!_classRegistered)
        {
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
                    Log.Error($"RegisterClassEx failed: error {Marshal.GetLastPInvokeError()}");
                    return 0;
                }
            }

            _classRegistered = true;
        }

        // Raised: a layered child (alpha 255) like DefView; WS_EX_TRANSPARENT keeps it out of mouse hit-testing.
        var exStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE
            | (layered ? Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT : 0);
        var hwnd = Native.CreateWindowEx(exStyle, WindowClass, "DesktopSpike", Native.WS_POPUP,
            0, 0, 1, 1, 0, 0, instance, 0);
        if (hwnd == 0)
        {
            Log.Error($"CreateWindowEx failed: error {Marshal.GetLastPInvokeError()}");
            return 0;
        }

        if (layered && !Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA))
        {
            Log.Warn($"SetLayeredWindowAttributes failed: error {Marshal.GetLastPInvokeError()}");
        }

        return hwnd;
    }

    private void CheckDpi()
    {
        var thread = Native.DescribeAwareness(Native.GetThreadDpiAwarenessContext());
        var window = Native.DescribeAwareness(Native.GetWindowDpiAwarenessContext(Window));
        var parent = Native.DescribeAwareness(Native.GetWindowDpiAwarenessContext(Parent));
        var message = $"DPI after attach: thread={thread}, our window={window} (dpi {Native.GetDpiForWindow(Window)}), " +
                      $"parent={parent} (dpi {Native.GetDpiForWindow(Parent)})";
        if (thread == "PerMonitorV2" && window == "PerMonitorV2")
        {
            Log.Info(message + " - OK");
        }
        else
        {
            Log.Warn(message + " - expected PerMonitorV2");
        }
    }

    private void HookShellWorkerW()
    {
        if (ShellWorkerW == 0)
        {
            return;
        }

        var thread = Native.GetWindowThreadProcessId(ShellWorkerW, out var process);
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_DESTROY, 0, &OnWinEvent,
            process, thread, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        Log.Info(_hook != 0
            ? $"watching WorkerW {Native.Hex(ShellWorkerW)} for destruction (explorer pid {process})"
            : "SetWinEventHook failed; WorkerW destruction will not be noticed");
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
                return 1; // DirectComposition owns our pixels
            case Native.WM_NCHITTEST:
                return Native.HTTRANSPARENT;
            case Native.WM_DESTROY when _current is { _detaching: false } attacher && hwnd == attacher.Window:
                Log.Warn($"our window {Native.Hex(hwnd)} was destroyed by someone else (its parent went away?)");
                attacher.Window = 0;
                attacher.Lost = true;
                break;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_current is { } attacher && idObject == Native.OBJID_WINDOW && hwnd == attacher.ShellWorkerW)
        {
            Log.Warn($"the shell destroyed WorkerW {Native.Hex(hwnd)}; re-attaching");
            attacher.Lost = true;
        }
    }
}
