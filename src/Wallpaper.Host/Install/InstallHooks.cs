using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// The Velopack fast callbacks (they run in a short-lived process of the installed exe, then Velopack exits it).
/// After install: autostart on (the first run does the same; whichever comes first wins). Before uninstall: the
/// running instance exits (the normal wallpaper is back), the Run value, the settings, the first-run marker and the
/// logs are removed; Velopack then deletes the program folder and the shortcuts.
/// </summary>
internal static class InstallHooks
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    public static Autostart CurrentAutostart() =>
        new(new RegistryRunKey(), new FileFirstRunMarker(FileFirstRunMarker.DefaultPath), Environment.ProcessPath!);

    public static void AfterInstall()
    {
        Try(() => CurrentAutostart().OnStartup());
    }

    public static void BeforeUninstall()
    {
        Try(() => WindowInstanceChannel.RequestExit(ExitWait));
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
