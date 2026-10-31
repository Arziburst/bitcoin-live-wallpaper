using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// The screensaver starts (D-21), run from Program.Main before the supervisor, the wallpaper child and the
/// single-instance check: no instance channel, no supervisor, no wallpaper, its own log (screensaver.log).
/// </summary>
internal static class ScreensaverApp
{
    private const string Caption = "Bitcoin Wallpaper";

    private const string ConfigText =
        "Bitcoin Wallpaper screensaver\n\n" +
        "There are no settings: the live chart is shown on the monitor the wallpaper uses.\n\n" +
        "Switch the screensaver on or off with \"Use as screensaver\" in the Bitcoin Wallpaper tray menu.";

    public static int Run(ScreensaverMode mode) => mode switch
    {
        ScreensaverMode.Preview => 0,   // no preview drawing (a no-op is the decision)
        ScreensaverMode.Config => ShowConfig(),
        ScreensaverMode.Run => RunScreensaver(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a screensaver start"),
    };

    private static int ShowConfig()
    {
        Native.MessageBox(0, ConfigText, Caption, Native.MB_OK | Native.MB_ICONINFORMATION);
        return 0;
    }

    private static int RunScreensaver()
    {
        ScreensaverLog.Open();
        var code = 1;
        try
        {
            ScreensaverLog.Info($"screensaver starting (/s) from {Environment.ProcessPath}");
            using var transport = new HttpMarketTransport();
            var client = new MarketDataClient(transport, TimeProvider.System, Random.Shared.NextDouble);
            using var stop = new CancellationTokenSource();
            var feed = Task.Run(() => client.RunAsync(stop.Token));
            try
            {
                using var window = new ScreensaverWindow(client);
                code = window.Run();
            }
            finally
            {
                stop.Cancel();
                try
                {
                    feed.Wait(TimeSpan.FromSeconds(1));
                }
                catch (AggregateException)
                {
                    // Cancelled, as asked.
                }
            }
        }
        catch (Exception e)
        {
            ScreensaverLog.Error($"screensaver failed: {e}");
            code = 1;
        }
        finally
        {
            ScreensaverLog.Info($"screensaver exits (code {code})");
            ScreensaverLog.Close();
        }

        return code;
    }
}
