using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Screensaver;

namespace BitcoinWallpaper.Host.Settings;

public enum TrayCommand
{
    ToggleWallpaper,
    StartWithWindows,
    Exit,
    OpenLogs,
    Version,
    UseAsScreensaver,
}

public sealed record TrayMenuItem(TrayCommand Command, string Text, bool Checked, bool Enabled = true);

/// <summary>
/// The tray menu as data (D-16); TrayIcon draws <see cref="Items"/> and passes the chosen command to
/// <see cref="Invoke"/>. A new item is a new <see cref="TrayCommand"/>, one line in <see cref="Items"/> and one case in
/// <see cref="Invoke"/>.
/// </summary>
/// <param name="openFolder">Opens a folder in Explorer; without it there is no Open logs item (D-26).</param>
/// <param name="logsDirectory">What Open logs opens; null is <see cref="Log.Directory"/>.</param>
/// <param name="version">Shown greyed as "Version x.y.z"; null leaves the item out (D-26).</param>
/// <param name="screensaver">The "Use as screensaver" check mark (D-21); null leaves the item out.</param>
public sealed class TrayMenuModel(
    WallpaperToggle toggle,
    Autostart autostart,
    Action exit,
    Action<string>? openFolder = null,
    string? logsDirectory = null,
    string? version = null,
    ScreensaverSetting? screensaver = null)
{
    /// <summary>Read afresh each time the menu opens.</summary>
    public IReadOnlyList<TrayMenuItem> Items
    {
        get
        {
            var items = new List<TrayMenuItem>
            {
                new(TrayCommand.ToggleWallpaper,
                    toggle.State == WallpaperState.On ? "Turn wallpaper off" : "Turn wallpaper on", false),
                new(TrayCommand.StartWithWindows, "Start with Windows", autostart.IsEnabled),
            };
            if (screensaver is not null)
            {
                items.Add(new(TrayCommand.UseAsScreensaver, "Use as screensaver", screensaver.IsOn));
            }

            if (openFolder is not null)
            {
                items.Add(new(TrayCommand.OpenLogs, "Open logs", false));
            }

            if (version is not null)
            {
                items.Add(new(TrayCommand.Version, "Version " + version, false, Enabled: false));
            }

            items.Add(new(TrayCommand.Exit, "Exit", false));
            return items;
        }
    }

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
            case TrayCommand.UseAsScreensaver:
                screensaver?.Toggle();
                break;
            case TrayCommand.Exit:
                exit();
                break;
            case TrayCommand.OpenLogs when openFolder is not null:
                var directory = logsDirectory ?? Log.Directory;
                Directory.CreateDirectory(directory);
                openFolder(directory);
                break;
        }
    }
}
