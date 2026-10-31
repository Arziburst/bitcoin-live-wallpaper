using System.Runtime.InteropServices;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host;

/// <summary>The notification-area icon (the app icon, D-14) and its menu, drawn from <see cref="TrayMenuModel"/> (D-16).</summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const string Tip = "Bitcoin Live Wallpaper";

    /// <summary>Menu item ids are the command + this, so 0 stays "nothing chosen".</summary>
    private const uint FirstCommandId = 1;

    /// <summary>The icon group the SDK writes for ApplicationIcon (IDI_APPLICATION); see LoadAppIcon.</summary>
    private const nint AppIconResource = 32512;

    private const uint MF_CHECKED = 0x8;
    private const uint MF_SEPARATOR = 0x800;
    private const uint IMAGE_ICON = 1;
    private const uint LR_DEFAULTCOLOR = 0;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private readonly nint _window;
    private readonly uint _callbackMessage;
    private readonly TrayMenuModel _menu;
    private nint _icon;
    private bool _added;

    /// <param name="window">Receives <paramref name="callbackMessage"/> for mouse events and WM_COMMAND for the menu.</param>
    public TrayIcon(nint window, uint callbackMessage, TrayMenuModel menu)
    {
        _window = window;
        _callbackMessage = callbackMessage;
        _menu = menu;
    }

    /// <summary>Adds the icon; called again after TaskbarCreated (a new taskbar has no icons).</summary>
    public void Add()
    {
        if (_icon == 0)
        {
            _icon = LoadAppIcon();
        }

        var data = Data();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        data.uCallbackMessage = _callbackMessage;
        data.hIcon = _icon != 0 ? _icon : Native.LoadIcon(0, Native.IDI_APPLICATION);
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

    /// <summary>WM_COMMAND from the menu: runs the chosen command; false when the id is not one of ours.</summary>
    public bool OnCommand(nuint wParam)
    {
        var id = (uint)(wParam & 0xFFFF);
        if (id < FirstCommandId || !Enum.IsDefined((TrayCommand)(id - FirstCommandId)))
        {
            return false;
        }

        var command = (TrayCommand)(id - FirstCommandId);
        Log.Info($"tray: {command} chosen");
        _menu.Invoke(command);
        return true;
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

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
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
            foreach (var item in _menu.Items)
            {
                if (item.Command == TrayCommand.Exit)
                {
                    Native.AppendMenu(menu, MF_SEPARATOR, 0, null!);
                }

                Native.AppendMenu(menu, Native.MF_STRING | (item.Checked ? MF_CHECKED : 0),
                    (uint)item.Command + FirstCommandId, item.Text);
            }

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

    /// <summary>The app icon at the small-icon size from the exe's own resources; 0 when it cannot be loaded.</summary>
    private static nint LoadAppIcon()
    {
        var icon = LoadImage(Native.GetModuleHandle(null), AppIconResource, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_DEFAULTCOLOR);
        if (icon == 0)
        {
            Log.Warn($"tray icon: the app icon could not be loaded (error {Marshal.GetLastPInvokeError()}); using the default");
        }

        return icon;
    }

    private Native.NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
        hWnd = _window,
        uID = IconId,
    };

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    private static partial nint LoadImage(nint instance, nint name, uint type, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
