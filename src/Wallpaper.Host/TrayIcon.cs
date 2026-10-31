namespace BitcoinWallpaper.Host;

/// <summary>The notification-area icon with one menu item, "Exit".</summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint ExitCommand = 1;
    private const uint IconId = 1;
    private const string Tip = "Bitcoin Wallpaper";

    private readonly nint _window;
    private readonly uint _callbackMessage;
    private bool _added;

    /// <param name="window">Receives <paramref name="callbackMessage"/> for mouse events and WM_COMMAND for the menu.</param>
    public TrayIcon(nint window, uint callbackMessage)
    {
        _window = window;
        _callbackMessage = callbackMessage;
    }

    /// <summary>Adds the icon; called again after TaskbarCreated (a new taskbar has no icons).</summary>
    public void Add()
    {
        var data = Data();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        data.uCallbackMessage = _callbackMessage;
        data.hIcon = Native.LoadIcon(0, Native.IDI_APPLICATION);
        var tip = Tip.AsSpan();
        tip.CopyTo(new Span<char>(data.szTip, 127));

        Native.Shell_NotifyIcon(Native.NIM_DELETE, &data); // harmless when absent; avoids a duplicate
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, &data);
        if (!_added)
        {
            Log.Warn("tray icon: Shell_NotifyIcon(NIM_ADD) failed; it is added again when the taskbar announces itself");
            return;
        }

        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, &data);
        Log.Info("tray icon added");
    }

    /// <summary>Handles the callback message; with NOTIFYICON_VERSION_4 the event is in LOWORD(lParam).</summary>
    public void OnCallback(nint lParam)
    {
        var mouseEvent = (uint)(lParam & 0xFFFF);
        if (mouseEvent is Native.WM_CONTEXTMENU or Native.WM_RBUTTONUP or Native.NIN_SELECT)
        {
            ShowMenu();
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Native.Shell_NotifyIcon(Native.NIM_DELETE, &data);
            _added = false;
            Log.Info("tray icon removed");
        }
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            Native.AppendMenu(menu, Native.MF_STRING, ExitCommand, "Exit");
            Native.GetCursorPos(out var cursor);

            // TrackPopupMenu docs: the owner must be foreground, or the menu does not close when clicking elsewhere.
            Native.SetForegroundWindow(_window);
            var command = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY,
                cursor.X, cursor.Y, 0, _window, 0);
            Native.PostMessage(_window, Native.WM_NULL, 0, 0);
            if (command != 0)
            {
                Native.PostMessage(_window, Native.WM_COMMAND, (nuint)command, 0);
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private Native.NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
        hWnd = _window,
        uID = IconId,
    };
}
