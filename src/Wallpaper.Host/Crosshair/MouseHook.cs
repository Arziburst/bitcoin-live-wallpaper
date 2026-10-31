using System.Runtime.InteropServices;

namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>
/// The crosshair's cursor source (D-35): a low-level mouse hook (WH_MOUSE_LL) on a thread of its own. Why a hook: our
/// layer lies behind the desktop icons and never gets mouse messages (the icons' list view does), and polling the
/// cursor would wake the process while the mouse is still; the hook runs only when the mouse moves. Why its own
/// thread: Windows calls a low-level hook on the installing thread's message loop, and every mouse event in the
/// session waits for it; the host thread can be busy drawing a frame (or waiting for v-sync), which would make the
/// whole system's mouse lag. This thread does nothing else: the callback stores the point, posts one message to the
/// host window unless one is already pending (moves coalesce) and returns. It never captures the mouse or swallows
/// an event (CallNextHookEx always), so clicks and icons work as before. The host runs it only while the crosshair
/// can show (on, attached, not paused): a fullscreen game never runs through our hook.
/// </summary>
internal sealed unsafe class MouseHook : IDisposable
{
    private const int StartTimeoutMs = 2000;
    private const int StopTimeoutMs = 2000;

    // The callback is static (UnmanagedCallersOnly); one hook per process.
    private static MouseHook? _current;

    private readonly nint _target;
    private readonly uint _message;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private nint _hook;
    private int _installError;
    private long _point;
    private int _pending;

    private MouseHook(nint target, uint message)
    {
        _target = target;
        _message = message;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "crosshair mouse hook",
            Priority = ThreadPriority.AboveNormal, // the session's mouse waits for the callback: answer it at once
        };
    }

    /// <summary>Installs the hook on its thread; each mouse move posts <paramref name="message"/> to
    /// <paramref name="target"/> (at most one pending). Null when the hook could not be installed (logged).</summary>
    public static MouseHook? Start(nint target, uint message)
    {
        if (_current is not null)
        {
            Log.Warn("crosshair: a mouse hook is already running");
            return null;
        }

        var hook = new MouseHook(target, message);
        _current = hook;
        hook._thread.Start();
        if (!hook._started.Wait(StartTimeoutMs) || hook._hook == 0)
        {
            Log.Warn($"crosshair: the mouse hook could not be installed (error {hook._installError}); no crosshair");
            hook.Dispose();
            return null;
        }

        return hook;
    }

    /// <summary>Host thread: the last cursor point the hook saw (virtual-screen px, per-monitor aware) and clears the
    /// pending flag, so the next move posts again. False when no move came since the last call.</summary>
    public bool TryTake(out int x, out int y)
    {
        var pending = Interlocked.Exchange(ref _pending, 0) != 0;
        var point = Interlocked.Read(ref _point);
        x = (int)(point >> 32);
        y = (int)point;
        return pending;
    }

    /// <summary>Unhooks and ends the thread. Idempotent.</summary>
    public void Dispose()
    {
        if (_threadId != 0)
        {
            CrosshairNative.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        }

        if (_thread.IsAlive && !_thread.Join(StopTimeoutMs))
        {
            Log.Warn("crosshair: the mouse hook thread did not end in time");
        }

        _threadId = 0;
        if (_current == this)
        {
            _current = null;
        }
    }

    private void Run()
    {
        // A message queue exists before the id is published, so WM_QUIT can be posted to it.
        Native.PeekMessage(out _, 0, 0, 0, 0);
        _threadId = CrosshairNative.GetCurrentThreadId();
        _hook = CrosshairNative.SetWindowsHookEx(CrosshairNative.WH_MOUSE_LL, &Callback, Native.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            _installError = Marshal.GetLastPInvokeError();
            _started.Set();
            return;
        }

        _started.Set();
        try
        {
            while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(msg);
                Native.DispatchMessage(msg);
            }
        }
        finally
        {
            CrosshairNative.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint Callback(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code == CrosshairNative.HC_ACTION && wParam == Native.WM_MOUSEMOVE && _current is { } hook && lParam != 0)
            {
                var info = (CrosshairNative.MSLLHOOKSTRUCT*)lParam;
                Interlocked.Exchange(ref hook._point, ((long)info->pt.X << 32) | (uint)info->pt.Y);
                if (Interlocked.Exchange(ref hook._pending, 1) == 0)
                {
                    Native.PostMessage(hook._target, hook._message, 0, 0);
                }
            }
        }
        catch
        {
            // Never let an exception cross into user32; the event passes on regardless.
        }

        return CrosshairNative.CallNextHookEx(0, code, wParam, lParam);
    }
}
