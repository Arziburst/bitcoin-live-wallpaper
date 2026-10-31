using System.Globalization;
using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Playback;
using BitcoinWallpaper.Host.Scene;
using BitcoinWallpaper.Host.Settings;
using BitcoinWallpaper.Market.Data;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;

namespace BitcoinWallpaper.Host;

/// <summary>
/// BitcoinWallpaper.exe: one instance per session, lives behind the desktop icons until Exit in the tray.
/// A start while it runs turns the running wallpaper on and exits (D-17); "--autostart" marks the sign-in launch.
/// Log: %LOCALAPPDATA%\BitcoinWallpaper\logs\wallpaper.log.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // First: Velopack's install/uninstall hooks run here and exit the process.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => InstallHooks.AfterInstall())
            .OnBeforeUninstallFastCallback(_ => InstallHooks.BeforeUninstall())
            .Run();

        var kind = Launch.Parse(args);
        var control = new WallpaperControl();
        var toggle = new WallpaperToggle(new FileSettingsStore(FileSettingsStore.DefaultPath), control);
        using var channel = new WindowInstanceChannel();

        // Only the running instance rolls the log; a second start writes its lines and exits.
        Log.Open(canRoll: false);
        var exit = new SingleInstanceStartup(channel, toggle).Run(kind);
        if (exit is { } secondStart)
        {
            Log.Info($"another BitcoinWallpaper is already running in this session ({kind} start); this one exits");
            Log.Close();
            return secondStart;
        }

        Log.Close();
        Log.Open(canRoll: true);
        var code = 1;
        MarketFeed? feed = null;
        try
        {
            Log.Info($"BitcoinWallpaper starting ({kind} start, wallpaper {toggle.State}); Windows {WindowsVersion()}, " +
                     $"process DPI awareness {Native.DescribeAwareness(Native.GetThreadDpiAwarenessContext())}; log {Log.FilePath}");

            var autostart = InstallHooks.CurrentAutostart();
            if (IsInstalledBySetup())
            {
                autostart.OnStartup();
                Log.Info($"start with Windows: {(autostart.IsEnabled ? "on" : "off")}");
            }
            else
            {
                Log.Info("not installed by Setup (a development build): the Run value is left as it is");
            }

            // The market data client runs on the thread pool while the wallpaper is on (D-16); the host reads it on
            // its own thread. The pause monitor starts once the wallpaper is first on.
            using var transport = new HttpMarketTransport();
            var client = new MarketDataClient(transport, TimeProvider.System, Random.Shared.NextDouble);
            feed = new MarketFeed(client);

            using var monitor = new PlaybackMonitor();
            using var scene = new LiveChartScene(client, monitor, TimeProvider.System);
            var menu = new TrayMenuModel(toggle, autostart, () => Native.PostQuitMessage(0));
            using (var host = new WallpaperHost(scene, menu, channel))
            {
                control.Bind(host, feed);
                code = host.Run();
            }

            monitor.Dispose();
            feed.Stop();
            feed.WaitStopped(TimeSpan.FromSeconds(3));
            return code;
        }
        catch (Exception e)
        {
            Log.Error($"failed: {e}");
            feed?.Stop();
            return code = 1;
        }
        finally
        {
            Log.Info(code == 0 ? "clean exit (code 0)" : $"exit with errors (code {code})");
            Log.Close();
        }
    }

    /// <summary>Installed by Setup.exe (Velopack), not a portable copy or a development build.</summary>
    private static bool IsInstalledBySetup()
    {
        try
        {
            return VelopackLocator.IsCurrentSet
                   && VelopackLocator.Current is { IsPortable: false, CurrentlyInstalledVersion: not null };
        }
        catch (Exception e)
        {
            Log.Warn($"Velopack locator: {e.Message}");
            return false;
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

    /// <summary>
    /// The toggle's <see cref="IWallpaperControl"/>. The toggle starts before the host exists (a second start must
    /// exit without building it), so its first calls wait here and run in order once the host and the feed are bound.
    /// Host thread only.
    /// </summary>
    private sealed class WallpaperControl : IWallpaperControl
    {
        private readonly List<Action> _pending = [];
        private WallpaperHost? _host;
        private MarketFeed? _feed;

        public void Bind(WallpaperHost host, MarketFeed feed)
        {
            _host = host;
            _feed = feed;
            foreach (var call in _pending)
            {
                call();
            }

            _pending.Clear();
        }

        public void AttachLayer() => Do(() => _host!.AttachLayer());

        public void DetachLayer() => Do(() => _host!.DetachLayer());

        public void StartMarketData() => Do(() => _feed!.Start());

        public void StopMarketData() => Do(() => _feed!.Stop());

        public void RequestFrame() => Do(() => _host!.RequestFrame());

        private void Do(Action call)
        {
            if (_host is null)
            {
                _pending.Add(call);
            }
            else
            {
                call();
            }
        }
    }

    /// <summary>
    /// The market data client's run (D-16): started when the wallpaper turns on (a new run reloads the history),
    /// cancelled when it turns off (the socket is closed, no REST polling). One run at a time: a run still closing
    /// finishes before the next starts.
    /// </summary>
    private sealed class MarketFeed(MarketDataClient client)
    {
        private CancellationTokenSource? _stop;
        private Task _run = Task.CompletedTask;

        public void Start()
        {
            if (_stop is not null)
            {
                return;
            }

            var stop = new CancellationTokenSource();
            _stop = stop;
            _run = _run
                .ContinueWith(_ => client.RunAsync(stop.Token), CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            _ = _run.ContinueWith(
                t => Log.Error($"the market data client stopped: {t.Exception?.GetBaseException()}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            Log.Info("market data client started");
        }

        public void Stop()
        {
            if (_stop is null)
            {
                return;
            }

            // Not disposed: the run may still read the token while it closes.
            _stop.Cancel();
            _stop = null;
            Log.Info("market data client stopped (socket closed, no REST polling)");
        }

        public void WaitStopped(TimeSpan timeout)
        {
            try
            {
                if (!_run.Wait(timeout))
                {
                    Log.Warn($"the market data client did not stop within {timeout.TotalSeconds:0} s");
                }
            }
            catch (AggregateException)
            {
                // Cancelled, as asked (a fault was logged when it happened).
            }
        }
    }
}
