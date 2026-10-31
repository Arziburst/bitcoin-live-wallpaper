namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>
/// Reads what is under the cursor for <see cref="CrosshairVisibility"/> (D-35): the window (WindowFromPoint and its
/// class) and, over the icons' list view, whether an icon is hit. The icon test asks the list view itself
/// (LVM_HITTEST): exact for icons and their labels at any size, layout or DPI, and it changes nothing. LVM_HITTEST
/// takes its LVHITTESTINFO in the list view's process (explorer), so one 24-byte buffer is allocated there once per
/// explorer process and reused; each test writes the point and sends the message (SendMessageTimeout, abort if
/// explorer hangs, 50 ms). If explorer's memory cannot be opened, the hot item (LVM_GETHOTITEM, no buffer; it lags by
/// one mouse move) is used instead. UI Automation / IAccessible were not taken: far slower per call (COM, a
/// cross-process tree walk) for every mouse move over the desktop. Host thread only.
/// </summary>
internal sealed unsafe class DesktopCursorProbe : IDisposable
{
    private const uint HitTestTimeoutMs = 50;
    private const string IconsClass = "SysListView32";

    private uint _explorerPid;
    private nint _process;
    private nint _buffer;
    private bool _fallbackLogged;

    /// <summary>The sample at (x, y), virtual-screen px.</summary>
    public CursorSample Sample(int x, int y)
    {
        var point = new Native.POINT { X = x, Y = y };
        var window = CrosshairNative.WindowFromPoint(point);
        if (window == 0)
        {
            return new CursorSample(x, y, null, false);
        }

        var cls = Native.ClassNameOf(window);
        var overIcon = cls == IconsClass && IsOverIcon(window, point);
        return new CursorSample(x, y, cls, overIcon);
    }

    public void Dispose() => Release();

    private bool IsOverIcon(nint listView, Native.POINT screen)
    {
        var client = screen;
        if (!CrosshairNative.ScreenToClient(listView, ref client))
        {
            return false;
        }

        if (Buffer(listView) is { } remote)
        {
            var info = new CrosshairNative.LVHITTESTINFO { pt = client, iItem = -1 };
            if (CrosshairNative.WriteProcessMemory(_process, remote, &info, (nuint)sizeof(CrosshairNative.LVHITTESTINFO), out _)
                && Native.SendMessageTimeout(listView, CrosshairNative.LVM_HITTEST, 0, remote,
                    CrosshairNative.SMTO_ABORTIFHUNG, HitTestTimeoutMs, out var item) != 0)
            {
                return (int)item >= 0;
            }
        }

        // Fallback: the item explorer highlights under the cursor.
        return Native.SendMessageTimeout(listView, CrosshairNative.LVM_GETHOTITEM, 0, 0,
                   CrosshairNative.SMTO_ABORTIFHUNG, HitTestTimeoutMs, out var hot) != 0
               && (int)hot >= 0;
    }

    // The LVHITTESTINFO buffer in the list view's process, allocated once per explorer process; null when it cannot be.
    private nint? Buffer(nint listView)
    {
        Native.GetWindowThreadProcessId(listView, out var pid);
        if (pid == 0)
        {
            return null;
        }

        if (pid == _explorerPid)
        {
            return _buffer != 0 ? _buffer : null;
        }

        Release();
        _explorerPid = pid;
        _process = CrosshairNative.OpenProcess(
            CrosshairNative.PROCESS_VM_OPERATION | CrosshairNative.PROCESS_VM_READ | CrosshairNative.PROCESS_VM_WRITE, false, pid);
        if (_process != 0)
        {
            _buffer = CrosshairNative.VirtualAllocEx(_process, 0, (nuint)sizeof(CrosshairNative.LVHITTESTINFO),
                CrosshairNative.MEM_COMMIT | CrosshairNative.MEM_RESERVE, CrosshairNative.PAGE_READWRITE);
        }

        if (_buffer == 0)
        {
            if (!_fallbackLogged)
            {
                Log.Warn($"crosshair: no hit-test buffer in explorer (pid {pid}, error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}); " +
                         "telling icons by the hot item instead");
                _fallbackLogged = true;
            }

            return null;
        }

        Log.Info($"crosshair: icon hit test through explorer pid {pid}");
        return _buffer;
    }

    private void Release()
    {
        if (_buffer != 0)
        {
            CrosshairNative.VirtualFreeEx(_process, _buffer, 0, CrosshairNative.MEM_RELEASE);
            _buffer = 0;
        }

        if (_process != 0)
        {
            Native.CloseHandle(_process);
            _process = 0;
        }

        _explorerPid = 0;
    }
}
