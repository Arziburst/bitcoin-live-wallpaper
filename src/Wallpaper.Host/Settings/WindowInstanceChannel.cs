namespace BitcoinWallpaper.Host.Settings;

/// <summary>
/// The production <see cref="IInstanceChannel"/>: a per-session named mutex marks the primary; a second process finds
/// the primary's hidden host window (same session, same desktop) and posts it a registered window message, so
/// <see cref="TurnOnRequested"/> is raised by the host's window procedure on its UI thread.
/// </summary>
internal sealed class WindowInstanceChannel : IInstanceChannel, IDisposable
{
    public const string MutexName = @"Local\BitcoinWallpaper.Host";

    /// <summary>The primary may still be creating its window: a second start looks for it this long.</summary>
    private static readonly TimeSpan FindWindowFor = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FindWindowEvery = TimeSpan.FromMilliseconds(100);

    private Mutex? _mutex;
    private bool _owned;

    public event Action? TurnOnRequested;

    /// <summary>"BitcoinWallpaper.TurnOn", registered once per session; the host routes it to <see cref="OnMessage"/>.</summary>
    public static uint TurnOnMessage { get; } = Native.RegisterWindowMessage("BitcoinWallpaper.TurnOn");

    public bool TryAcquirePrimary()
    {
        _mutex ??= new Mutex(initiallyOwned: true, MutexName, out _owned);
        return _owned;
    }

    public void SendTurnOn()
    {
        var window = FindPrimaryWindow(FindWindowFor);
        if (window == 0)
        {
            Log.Warn("second start: the running instance has no window yet; the turn-on request is dropped");
            return;
        }

        var posted = Native.PostMessage(window, TurnOnMessage, 0, 0);
        Log.Info(posted
            ? $"second start: asked the running instance ({Native.Hex(window)}) to turn the wallpaper on"
            : "second start: posting the turn-on request failed");
    }

    /// <summary>Called by the host window procedure for every message; true when it was ours.</summary>
    public bool OnMessage(uint message)
    {
        if (message == 0 || message != TurnOnMessage)
        {
            return false;
        }

        Log.Info("a second start asked to turn the wallpaper on");
        TurnOnRequested?.Invoke();
        return true;
    }

    /// <summary>
    /// Uninstall hook: asks a running instance to exit (WM_CLOSE; it detaches and the shell repaints the normal
    /// wallpaper) and waits up to <paramref name="timeout"/> for it to go. True when none runs any more.
    /// </summary>
    public static bool RequestExit(TimeSpan timeout)
    {
        var window = FindPrimaryWindow(TimeSpan.Zero);
        if (window == 0)
        {
            return !IsPrimaryRunning();
        }

        Native.PostMessage(window, Native.WM_CLOSE, 0, 0);
        var until = DateTime.UtcNow + timeout;
        while (IsPrimaryRunning() && DateTime.UtcNow < until)
        {
            Thread.Sleep(FindWindowEvery);
        }

        return !IsPrimaryRunning();
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex!.ReleaseMutex();
            _owned = false;
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private static bool IsPrimaryRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex))
        {
            return false;
        }

        mutex.Dispose();
        return true;
    }

    private static nint FindPrimaryWindow(TimeSpan waitFor)
    {
        var until = DateTime.UtcNow + waitFor;
        while (true)
        {
            var window = Native.FindWindow(WallpaperHost.WindowClass, null);
            if (window != 0 || DateTime.UtcNow >= until)
            {
                return window;
            }

            Thread.Sleep(FindWindowEvery);
        }
    }
}
