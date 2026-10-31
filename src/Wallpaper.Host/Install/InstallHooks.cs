using BitcoinWallpaper.Host.LockScreenChart;
using BitcoinWallpaper.Host.Screensaver;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// The Velopack fast callbacks (they run in a short-lived process of the installed exe, then Velopack exits it).
/// After install: autostart on (the first run does the same; whichever comes first wins) and BitcoinWallpaper.scr is
/// written next to the exe; after an update the .scr is written again (D-21). Before uninstall: the running instance
/// exits (the normal wallpaper is back), the previous Windows screensaver is restored (D-21), the user's own lock screen picture is set back (D-34), the Run value, the
/// settings, the first-run marker and the logs are removed; Velopack then deletes the program folder and the shortcuts.
/// </summary>
internal static class InstallHooks
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    public static Autostart CurrentAutostart() =>
        new(new RegistryRunKey(), new FileFirstRunMarker(FileFirstRunMarker.DefaultPath), Environment.ProcessPath!);

    /// <summary>"Use as screensaver" for the current user: the .scr next to the running exe, written when missing on turn-on.</summary>
    public static ScreensaverSetting CurrentScreensaver() =>
        new(new HkcuScreensaverRegistry(), ScreensaverSetting.DefaultSavedFilePath, ScreensaverFile.Current, Log.Warn,
            ScreensaverFile.EnsureExists);

    /// <summary>The chart on the lock screen (D-34): the real lock screen and %LOCALAPPDATA%\BitcoinWallpaper\lock.</summary>
    public static LockScreenImages CurrentLockScreenImages() =>
        new(new WindowsLockScreenApi(), new SystemLockScreenFiles(), LockScreenImages.DefaultFolder, Log.Warn);

    public static void AfterInstall()
    {
        Try(() => CurrentAutostart().OnStartup());
        Try(ScreensaverFile.Write);
    }

    public static void AfterUpdate()
    {
        Try(ScreensaverFile.Write);
    }

    public static void BeforeUninstall()
    {
        Try(() => WindowInstanceChannel.RequestExit(ExitWait));
        Try(() => CurrentScreensaver().OnUninstall());
        Try(() => File.Delete(ScreensaverSetting.DefaultSavedFilePath));
        Try(RestoreLockScreen);
        Try(() => File.Delete(LockScreenSetting.DefaultFilePath));
        Try(() => CurrentAutostart().OnUninstall());
        Try(() => File.Delete(FileSettingsStore.DefaultPath));
        Try(() => File.Delete(FileSettingsStore.DefaultPath + ".tmp"));
        Try(() =>
        {
            if (Directory.Exists(Log.Directory))
            {
                Directory.Delete(Log.Directory, recursive: true);
            }
        });
    }

    /// <summary>
    /// D-34: with the item on, our snapshot is on the lock screen: the user's own picture is set back (with the item
    /// off it was restored when it was turned off, and a picture chosen since then must stay). Then our files go,
    /// except a restore_ copy that may be the picture Windows now shows.
    /// </summary>
    private static void RestoreLockScreen()
    {
        if (new LockScreenSetting(LockScreenSetting.DefaultFilePath, Log.Warn).IsOn)
        {
            // On the pool (MTA), not this STA thread, as the wallpaper does it.
            var result = Task.Run(() => CurrentLockScreenImages().RestoreAsync(DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
            if (!result.Ok)
            {
                return;   // keep everything: the backup is the user's only copy of the picture
            }
        }

        var folder = LockScreenImages.DefaultFolder;
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(folder))
        {
            if (!Path.GetFileName(file).StartsWith(LockScreenImages.RestorePrefix, StringComparison.OrdinalIgnoreCase))
            {
                Try(() => File.Delete(file));
            }
        }
    }

    // A failing step must not stop the others (and an uninstall must never fail because of us).
    private static void Try(Action step)
    {
        try
        {
            step();
        }
        catch (Exception)
        {
        }
    }
}
