using System.Runtime.InteropServices;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host;

/// <summary>The notification-area icon (the app icon, D-14) and its menu, drawn from <see cref="TrayMenuModel"/> (D-16).</summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    /// <summary>Menu item ids are the command + this, so 0 stays "nothing chosen".</summary>
    private const uint FirstCommandId = 1;

    /// <summary>The icon group the SDK writes for ApplicationIcon (IDI_APPLICATION); see LoadAppIcon.</summary>
    private const nint AppIconResource = 32512;

    private const uint MF_GRAYED = 0x1;
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
    private string _tip = TrayStatusText.Title;

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
        CopyTip(ref data);

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

    /// <summary>
    /// The tooltip (D-26): NIM_MODIFY only when the text changed; <see cref="Add"/> puts the current text on a new
    /// taskbar's icon.
    /// </summary>
    public void SetTip(string text)
    {
        text = TrayStatusText.Shorten(text);
        if (text == _tip)
        {
            return;
        }

        _tip = text;
        if (!_added)
        {
            return;
        }

        var data = Data();
        data.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP;
        CopyTip(ref data);
        if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, &data))
        {
            Log.Warn("tray icon: Shell_NotifyIcon(NIM_MODIFY) failed for the tooltip");
        }
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
        try
        {
            _menu.Invoke(command);
        }
        catch (Exception e) when (command == TrayCommand.OpenLogs)
        {
            // The logs folder could not be created: a tray click never ends the host.
            Log.Warn($"tray: Open logs failed: {e.Message}");
        }

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

                Native.AppendMenu(menu, Native.MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED),
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

    /// <summary>szTip is 128 chars with its terminator; <see cref="SetTip"/> keeps the text within 127.</summary>
    private void CopyTip(ref Native.NOTIFYICONDATAW data)
    {
        fixed (char* tip = data.szTip)
        {
            var target = new Span<char>(tip, 128);
            target.Clear();
            _tip.AsSpan().CopyTo(target);
        }
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
