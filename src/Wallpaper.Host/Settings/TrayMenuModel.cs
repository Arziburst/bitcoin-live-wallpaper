using BitcoinWallpaper.Host.Install;

namespace BitcoinWallpaper.Host.Settings;

public enum TrayCommand
{
    ToggleWallpaper,
    StartWithWindows,
    Exit,
}

public sealed record TrayMenuItem(TrayCommand Command, string Text, bool Checked);

/// <summary>
/// The tray menu as data (D-16); TrayIcon draws <see cref="Items"/> and passes the chosen command to
/// <see cref="Invoke"/>. A new item is a new <see cref="TrayCommand"/>, one line in <see cref="Items"/> and one case in
/// <see cref="Invoke"/>.
/// </summary>
public sealed class TrayMenuModel(WallpaperToggle toggle, Autostart autostart, Action exit)
{
    /// <summary>Read afresh each time the menu opens.</summary>
    public IReadOnlyList<TrayMenuItem> Items =>
    [
        new(TrayCommand.ToggleWallpaper,
            toggle.State == WallpaperState.On ? "Turn wallpaper off" : "Turn wallpaper on", false),
        new(TrayCommand.StartWithWindows, "Start with Windows", autostart.IsEnabled),
        new(TrayCommand.Exit, "Exit", false),
    ];

    public void Invoke(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.ToggleWallpaper:
                toggle.Toggle();
                break;
            case TrayCommand.StartWithWindows:
                if (autostart.IsEnabled)
                {
                    autostart.Disable();
                }
                else
                {
                    autostart.Enable();
                }

                break;
            case TrayCommand.Exit:
                exit();
                break;
        }
    }
}
