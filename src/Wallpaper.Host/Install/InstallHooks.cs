using BitcoinWallpaper.Host.LockScreenChart;
using BitcoinWallpaper.Host.Screensaver;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// The Velopack fast callbacks (they run in a short-lived process of the installed exe, then Velopack exits it).
/// After install: autostart on (the first run does the same; whichever comes first wins) and BitcoinWallpaper.scr is
/// written next to the exe; after an update the .scr is written again (D-21). Before uninstall: the running instance
/// exits (the normal wallpaper is back), the previous Windows screensaver is restored (D-21), the Run value, the
/// settings and the first-run marker are removed, the user's own lock screen picture is set back (D-34, a bounded
/// wait) and the logs are removed; Velopack then deletes the program folder and the shortcuts.
/// </summary>
internal static class InstallHooks
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockScreenRestoreWait = TimeSpan.FromSeconds(10);

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

        // The lock screen item is read before its file goes; the restore itself runs after the quick steps, so a
        // hanging Windows call cannot keep them from running (D-34).
        var lockScreenOn = true;
        Try(() => lockScreenOn = new LockScreenSetting(LockScreenSetting.DefaultFilePath, Log.Warn).IsOn);
        Try(() => CurrentAutostart().OnUninstall());
        Try(() => File.Delete(FileSettingsStore.DefaultPath));
        Try(() => File.Delete(FileSettingsStore.DefaultPath + ".tmp"));
        Try(() => File.Delete(LockScreenSetting.DefaultFilePath));
        Try(() => RestoreLockScreen(lockScreenOn));
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
    /// off it was restored when it was turned off, and a picture chosen since then must stay). The wait is bounded by
    /// <see cref="LockScreenRestoreWait"/>. Our files in lock\ are deleted here only to tidy up early: Velopack deletes
    /// %LOCALAPPDATA%\BitcoinWallpaper with everything in it after this hook anyway.
    /// </summary>
    private static void RestoreLockScreen(bool itemOn)
    {
        if (itemOn)
        {
            // On the pool (MTA), not this STA thread, as the wallpaper does it.
            var restore = Task.Run(() => CurrentLockScreenImages().RestoreAsync(DateTimeOffset.UtcNow));
            if (!restore.Wait(LockScreenRestoreWait))
            {
                Log.Warn($"lock screen: restoring the user's picture did not finish within {LockScreenRestoreWait.TotalSeconds:0} s; uninstall goes on");
                return;
            }

            if (!restore.Result.Ok)
            {
                return;   // logged by LockScreenImages; nothing more to do here
            }
        }

        var folder = LockScreenImages.DefaultFolder;
        if (!Directory.Exists(folder))
        {
            return;
        }

        // A restore_ copy is left for Velopack's cleanup with the rest of the folder.
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
