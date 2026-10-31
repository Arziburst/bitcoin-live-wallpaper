namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// The per-user data folder, %LOCALAPPDATA%\BitcoinWallpaper: settings.json, first-run.done and logs\. It is also
/// the Velopack install root (packId BitcoinWallpaper: current\, Update.exe), so an uninstall leaves nothing.
/// </summary>
internal static class DataFolder
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper");
}
