using System.Globalization;
using Microsoft.Win32;

namespace BitcoinWallpaper.Host;

/// <summary>
/// BitcoinWallpaper.exe: one instance per session, lives behind the desktop icons until Exit in the tray.
/// Log: %LOCALAPPDATA%\BitcoinWallpaper\logs\wallpaper.log.
/// </summary>
internal static class Program
{
    private const string SingleInstanceMutex = @"Local\BitcoinWallpaper.Host";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var firstInstance);
        Log.Open(canRoll: firstInstance);
        if (!firstInstance)
        {
            Log.Info("another BitcoinWallpaper is already running in this session; this one exits");
            Log.Close();
            return 0;
        }

        var code = 1;
        try
        {
            Log.Info($"BitcoinWallpaper starting; Windows {WindowsVersion()}, process DPI awareness " +
                     $"{Native.DescribeAwareness(Native.GetThreadDpiAwarenessContext())}; log {Log.FilePath}");

            // The seam for #8: the chart scene replaces the placeholder here.
            using var host = new WallpaperHost(new PlaceholderScene());
            code = host.Run();
            return code;
        }
        catch (Exception e)
        {
            Log.Error($"failed: {e}");
            return code = 1;
        }
        finally
        {
            Log.Info(code == 0 ? "clean exit (code 0)" : $"exit with errors (code {code})");
            mutex.ReleaseMutex();
            Log.Close();
        }
    }

    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var display = key?.GetValue("DisplayVersion") as string ?? "?";
        var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        var revision = key?.GetValue("UBR") is int ubr ? ubr.ToString(CultureInfo.InvariantCulture) : "?";
        return $"{display} build {build}.{revision}";
    }
}
