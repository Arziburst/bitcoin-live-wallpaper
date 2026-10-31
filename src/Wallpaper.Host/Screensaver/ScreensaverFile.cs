namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// BitcoinWallpaper.scr: a copy of the running exe in its own folder (the app is self-contained but not single-file,
/// so the copy needs the DLLs next to it - Velopack's current\). The apphost finds BitcoinWallpaper.dll by the name
/// built into it, so the renamed copy runs the same app.
/// </summary>
internal static class ScreensaverFile
{
    public const string FileName = "BitcoinWallpaper.scr";

    /// <summary>The .scr next to <paramref name="exePath"/>.</summary>
    public static string PathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath) ?? "", FileName);

    /// <summary>The .scr next to the running exe.</summary>
    public static string Current => PathFor(Environment.ProcessPath!);

    /// <summary>Copies the running exe to the .scr (the Velopack install and update hooks: always).</summary>
    public static void Write() => Copy(overwrite: true);

    /// <summary>Copies it only when missing (turning the check mark on).</summary>
    public static void EnsureExists()
    {
        if (!File.Exists(Current))
        {
            Copy(overwrite: false);
        }
    }

    private static void Copy(bool overwrite)
    {
        var exe = Environment.ProcessPath!;
        var scr = PathFor(exe);
        if (string.Equals(exe, scr, StringComparison.OrdinalIgnoreCase))
        {
            return;   // running as the .scr itself
        }

        File.Copy(exe, scr, overwrite);
    }
}
