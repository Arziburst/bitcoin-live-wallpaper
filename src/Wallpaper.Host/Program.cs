using System.Globalization;
using BitcoinWallpaper.Host.Playback;
using BitcoinWallpaper.Host.Scene;
using BitcoinWallpaper.Market.Data;
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

            // The market data client runs on the thread pool from the start (the socket stays open while paused);
            // the host reads it on its own thread. The pause monitor starts once the host window exists.
            using var transport = new HttpMarketTransport();
            var client = new MarketDataClient(transport, TimeProvider.System, Random.Shared.NextDouble);
            using var stop = new CancellationTokenSource();
            var feed = Task.Run(() => client.RunAsync(stop.Token));
            _ = feed.ContinueWith(
                t => Log.Error($"the market data client stopped: {t.Exception?.GetBaseException()}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            using var monitor = new PlaybackMonitor();
            using var scene = new LiveChartScene(client, monitor, TimeProvider.System);
            using (var host = new WallpaperHost(scene))
            {
                code = host.Run();
            }

            monitor.Dispose();
            stop.Cancel();
            try
            {
                if (!feed.Wait(TimeSpan.FromSeconds(3)))
                {
                    Log.Warn("the market data client did not stop within 3 s");
                }
            }
            catch (AggregateException)
            {
                // Cancelled, as asked (a fault was logged when it happened).
            }

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
