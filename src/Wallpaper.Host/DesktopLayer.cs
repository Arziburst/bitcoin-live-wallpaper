using System.Runtime.InteropServices;
using BitcoinWallpaper.Chart.Core.Desktop;

namespace BitcoinWallpaper.Host;

/// <summary>
/// Puts our wallpaper window behind the desktop icons, on either layout, keeps it there and takes it away
/// again. Written from the Win32 docs and the research notes (coordinator/research/stack.md), ported from
/// the spike (tools/DesktopSpike); one instance per process.
/// </summary>
internal sealed unsafe class DesktopLayer : IDisposable
{
    /// <summary>Undocumented Progman message that makes the shell create the wallpaper WorkerW.</summary>
    private const uint SpawnWorkerWMessage = 0x052C;

    private const string WindowClass = "BitcoinWallpaper.Surface";

    private static DesktopLayer? _current;
    private static bool _classRegistered;

    private readonly Action<string> _onLost;
    private nint _hook;
    private bool _detaching;

    /// <param name="onLost">Called on our thread when the shell destroyed our window or its WorkerW.</param>
    public DesktopLayer(Action<string> onLost)
    {
        _onLost = onLost;
        _current = this;
    }

    /// <summary>What we are attached to; null while detached.</summary>
    public AttachedState? State { get; private set; }

    public nint Window => (nint)(State?.Window ?? 0);

    /// <summary>
    /// Detects the layout and attaches a new window of ours sized and placed for <paramref name="placement"/>
    /// on the monitor at <paramref name="monitorBounds"/> (screen coordinates). False when the shell is not
    /// ready (explorer restarting); the caller tries again later.
    /// </summary>
    public bool Attach(SurfacePlacement placement, ScreenRect monitorBounds)
    {
        Detach("re-attach");

        var probe = DesktopProbe.Read();
        var detection = DesktopLayoutDetector.Detect(probe.Snapshot);
        Log.Info($"desktop layout: {detection.Layout} ({detection.Reason}); SHELLDLL_DefView {Native.Hex(probe.DefView)} " +
                 $"in {probe.Snapshot.DefViewHost}");
        foreach (var warning in detection.Warnings)
        {
            Log.Warn($"detection: {warning}");
        }

        if (detection.Layout == DesktopLayout.Unknown)
        {
            return false;
        }

        var progman = probe.Progman;
        Native.GetWindowThreadProcessId(progman, out var explorerPid);

        // Ask the shell for its wallpaper WorkerW (both layouts). wParam 0xD / lParam 0x1 as in the research notes.
        var sent = Native.SendMessageTimeout(progman, SpawnWorkerWMessage, 0xD, 0x1, Native.SMTO_NORMAL, 1000, out _);
        Log.Info($"sent 0x052C(0xD, 0x1) to Progman {Native.Hex(progman)} (explorer pid {explorerPid}): " +
                 (sent != 0 ? "ok" : "timed out/failed"));

        nint parent;
        nint shellWorkerW;
        nint insertAfter;
        if (detection.Layout == DesktopLayout.Legacy)
        {
            var workerW = (nint)DesktopLayoutDetector.FindLegacyWorkerW(DesktopProbe.Children(0));
            if (workerW == 0)
            {
                Native.SendMessageTimeout(progman, SpawnWorkerWMessage, 0, 0, Native.SMTO_NORMAL, 1000, out _);
                Log.Info("legacy: no WorkerW after the DefView host yet; sent 0x052C(0, 0) as well");
                workerW = (nint)DesktopLayoutDetector.FindLegacyWorkerW(DesktopProbe.Children(0));
            }

            if (workerW == 0)
            {
                Log.Warn("legacy: no top-level WorkerW behind the SHELLDLL_DefView host yet");
                return false;
            }

            parent = workerW;
            shellWorkerW = workerW;
            insertAfter = 0; // HWND_TOP among the WorkerW's children
        }
        else
        {
            var targets = DesktopLayoutDetector.FindRaisedTargets(DesktopProbe.Children(progman));
            if (targets is null)
            {
                Log.Warn("raised: no SHELLDLL_DefView child of Progman yet");
                return false;
            }

            if (targets.WorkerW == 0)
            {
                Log.Warn("raised: Progman has no WorkerW child; attaching below DefView anyway");
            }

            parent = progman;
            shellWorkerW = (nint)targets.WorkerW;
            insertAfter = (nint)targets.DefView;
        }

        var window = CreateWindow(detection.Layout == DesktopLayout.Raised);
        if (window == 0)
        {
            return false;
        }

        // SetParent docs: clear WS_POPUP and set WS_CHILD before parenting a former top-level window.
        Native.SetWindowLongPtr(window, Native.GWL_STYLE, (nint)(Native.WS_CHILD | Native.WS_CLIPSIBLINGS));
        if (Native.SetParent(window, parent) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            Log.Error($"SetParent failed: error {Marshal.GetLastPInvokeError()}");
            Native.DestroyWindow(window);
            return false;
        }

        State = new AttachedState(detection.Layout, window, parent, shellWorkerW, explorerPid);
        if (!Place(placement, monitorBounds, insertAfter, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW))
        {
            Detach("placing failed");
            return false;
        }

        if (detection.Layout == DesktopLayout.Raised)
        {
            KeepShellWorkerWAtBottom();
        }

        Log.Info(detection.Layout == DesktopLayout.Raised
            ? $"attach path: raised - layered child {Native.Hex(window)} of Progman {Native.Hex(parent)}, " +
              $"below SHELLDLL_DefView {Native.Hex(insertAfter)}, above WorkerW {Native.Hex(shellWorkerW)}"
            : $"attach path: legacy - child {Native.Hex(window)} of top-level WorkerW {Native.Hex(parent)} " +
              "behind the SHELLDLL_DefView host");
        Log.Info($"our window {DesktopProbe.Describe(window)}");
        if (detection.Layout == DesktopLayout.Raised)
        {
            Log.Info("Progman children after attach (z-order, top first): " +
                     string.Join(", ", DesktopProbe.Children(progman).Select(c => $"{c.ClassName} {Native.Hex(c.Handle)}")));
        }

        CheckDpi(window, parent);
        HookShell(progman, explorerPid);
        return true;
    }

    /// <summary>Moves and resizes the attached window for a new placement, keeping its z-order.</summary>
    public bool Move(SurfacePlacement placement, ScreenRect monitorBounds) =>
        State is not null && Place(placement, monitorBounds, 0, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);

    /// <summary>Raised layout: put our window directly below DefView again and the shell's WorkerW at the bottom.</summary>
    public bool FixZOrder()
    {
        if (State is not { Layout: DesktopLayout.Raised } state)
        {
            return false;
        }

        var targets = DesktopLayoutDetector.FindRaisedTargets(DesktopProbe.Children((nint)state.Parent));
        if (targets is null)
        {
            return false;
        }

        var moved = Native.SetWindowPos(Window, (nint)targets.DefView, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        KeepShellWorkerWAtBottom();
        return moved;
    }

    /// <summary>Destroys our window and asks the shell to repaint what it covered. Never SPI_SETDESKWALLPAPER.</summary>
    public void Detach(string why)
    {
        if (State is not { } state)
        {
            return;
        }

        _detaching = true;
        try
        {
            Unhook();
            var window = (nint)state.Window;
            if (Native.IsWindow(window))
            {
                Native.DestroyWindow(window);
                Log.Info($"destroyed our window {Native.Hex(window)} ({why})");
            }

            State = null;

            // Let the shell repaint (no SPI_SETDESKWALLPAPER: on the raised layout it destroys the WorkerW).
            var repaint = state.ShellWorkerW != 0 && Native.IsWindow((nint)state.ShellWorkerW)
                ? (nint)state.ShellWorkerW
                : (nint)state.Parent;
            if (repaint != 0 && Native.IsWindow(repaint))
            {
                Native.RedrawWindow(repaint, 0, 0,
                    Native.RDW_INVALIDATE | Native.RDW_ERASE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
                Log.Info($"asked the shell to repaint {Native.Hex(repaint)}");
            }
        }
        finally
        {
            _detaching = false;
        }
    }

    public void Dispose()
    {
        Detach("exit");
        if (_current == this)
        {
            _current = null;
        }
    }

    private bool Place(SurfacePlacement placement, ScreenRect monitorBounds, nint insertAfter, uint flags)
    {
        var state = State!;

        // Our rectangle in the parent's client coordinates: the parent spans the virtual screen, so this should
        // equal the placement's (X, Y); the mapped value wins when the shell's origin differs.
        var origin = new Native.POINT { X = monitorBounds.Left, Y = monitorBounds.Top };
        Native.MapWindowPoints(0, (nint)state.Parent, &origin, 1);
        if (origin.X != placement.X || origin.Y != placement.Y)
        {
            Log.Warn($"parent client origin differs from the virtual screen: mapped ({origin.X},{origin.Y}), " +
                     $"expected ({placement.X},{placement.Y}); using the mapped position");
        }

        if (!Native.SetWindowPos((nint)state.Window, insertAfter, origin.X, origin.Y, placement.Width, placement.Height, flags))
        {
            Log.Error($"SetWindowPos failed: error {Marshal.GetLastPInvokeError()}");
            return false;
        }

        Log.Info($"our window at ({origin.X},{origin.Y}) in parent, {placement.Width}x{placement.Height} px " +
                 $"on {placement.DeviceName} ({placement.Dpi} dpi, scale {placement.Scale:0.##})");
        return true;
    }

    private void KeepShellWorkerWAtBottom()
    {
        if (State is { Layout: DesktopLayout.Raised, ShellWorkerW: not 0 } state)
        {
            Native.SetWindowPos((nint)state.ShellWorkerW, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private static nint CreateWindow(bool layered)
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
                    Log.Error($"RegisterClassEx({WindowClass}) failed: error {Marshal.GetLastPInvokeError()}");
                    return 0;
                }
            }

            _classRegistered = true;
        }

        // Raised: a layered child (alpha 255) like DefView; WS_EX_TRANSPARENT keeps it out of mouse hit-testing.
        var exStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE
            | (layered ? Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT : 0);
        var hwnd = Native.CreateWindowEx(exStyle, WindowClass, "Bitcoin Wallpaper", Native.WS_POPUP,
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

    private static void CheckDpi(nint window, nint parent)
    {
        var thread = Native.DescribeAwareness(Native.GetThreadDpiAwarenessContext());
        var ours = Native.DescribeAwareness(Native.GetWindowDpiAwarenessContext(window));
        var theirs = Native.DescribeAwareness(Native.GetWindowDpiAwarenessContext(parent));
        var message = $"DPI after attach: thread={thread}, our window={ours} (dpi {Native.GetDpiForWindow(window)}), " +
                      $"parent={theirs} (dpi {Native.GetDpiForWindow(parent)})";
        if (thread == "PerMonitorV2" && ours == "PerMonitorV2")
        {
            Log.Info(message + " - OK");
        }
        else
        {
            Log.Warn(message + " - expected PerMonitorV2");
        }
    }

    /// <summary>Watches the shell's desktop thread for the destruction of the WorkerW or our parent.</summary>
    private void HookShell(nint progman, uint explorerPid)
    {
        var thread = Native.GetWindowThreadProcessId(progman, out _);
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_DESTROY, 0, &OnWinEvent,
            explorerPid, thread, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == 0)
        {
            Log.Warn("SetWinEventHook failed; WorkerW destruction is noticed by the watchdog only");
        }
    }

    private void Unhook()
    {
        if (_hook != 0)
        {
            Native.UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case Native.WM_ERASEBKGND:
                    return 1; // DirectComposition owns our pixels
                case Native.WM_NCHITTEST:
                    return Native.HTTRANSPARENT;
                case Native.WM_DESTROY when _current is { _detaching: false } layer && hwnd == layer.Window:
                    layer._onLost($"our window {Native.Hex(hwnd)} was destroyed by someone else (its parent went away?)");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error($"surface window procedure: {e}");
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (_current is { State: { } state } layer && idObject == Native.OBJID_WINDOW
                && (hwnd == (nint)state.ShellWorkerW || hwnd == (nint)state.Parent))
            {
                layer._onLost($"the shell destroyed {(hwnd == (nint)state.Parent ? "our parent" : "its WorkerW")} {Native.Hex(hwnd)}");
            }
        }
        catch (Exception e)
        {
            Log.Error($"WinEvent callback: {e}");
        }
    }
}
