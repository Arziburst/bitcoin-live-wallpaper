using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.LockScreenChart;
using BitcoinWallpaper.Host.Settings;
using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.Tests;

// Contract 42, criterion 3 (D-34 step 2): the tray check mark "Show chart on lock screen" (default on, persisted
// without clobbering the other settings) and the tray tooltip that mentions a lock screen failure.
//
// API SKETCH (written before the code, #237).
//
//   // src/Wallpaper.Host/LockScreenChart/LockScreenSetting.cs, namespace BitcoinWallpaper.Host.LockScreenChart.
//   // The switch lives in its OWN file (like screensaver.json), because settings.json is rewritten whole by
//   // WallpaperToggle ({"wallpaper": ...}) and would lose the key; the default path is
//   // %LOCALAPPDATA%\BitcoinWallpaper\lockscreen.json. A missing, empty, corrupt or unreadable file means On.
//   // No member throws: a read or write failure is passed to `log` (the value then holds in memory only).
//   public sealed class LockScreenSetting(string filePath, Action<string> log, Action<bool>? changed = null)
//   {
//       public static string DefaultFilePath { get; }
//       public bool IsOn { get; }                      // read afresh from the file on each call (a second instance
//                                                      // over the same file sees the change)
//       public void Set(bool on);                      // writes the file (folder created), then calls `changed(on)`
//                                                      // only when the value actually changed
//       public void Toggle();                          // Set(!IsOn)
//   }
//
//   // src/Wallpaper.Host/Settings/TrayMenuModel.cs: TrayCommand gets a member ShowLockScreen, and the constructor a
//   // NEW optional parameter, last (after `screensaver`); earlier calls compile unchanged:
//   //     LockScreenSetting? lockScreen = null
//   // Items order: ToggleWallpaper, StartWithWindows, [UseAsScreensaver], [ShowLockScreen], [OpenLogs], [Version], Exit.
//   // ShowLockScreen: Text "Show chart on lock screen", Checked = lockScreen.IsOn, Enabled true; present only when
//   // lockScreen != null. Invoke(ShowLockScreen) = lockScreen.Toggle(), nothing else changes; null: does nothing.
//
//   // src/Wallpaper.Host/Settings/TrayStatusText.cs: TrayStatus gets a NEW optional last parameter
//   //     string? LockScreenError = null
//   // null: the text is exactly today's. Not null and the wallpaper is not Off: line 2 ends with
//   // " · lock screen: <error>" (the separator " · " as elsewhere), the whole text still cut to 127 chars by Shorten.
//   // Off: unchanged ("Off").
public sealed class LockScreenTrayTests : IDisposable
{
    private const string Exe = @"C:\Users\Owner\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.exe";
    private const string ItemText = "Show chart on lock screen";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bw-lockscreen-tray-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly List<bool> _changes = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SettingFile => Path.Combine(_root, "BitcoinWallpaper", "lockscreen.json");

    private LockScreenSetting NewSetting() => new(SettingFile, _log.Add, _changes.Add);

    private sealed class FakeControl : IWallpaperControl
    {
        public int Calls { get; private set; }
        public void AttachLayer() => Calls++;
        public void DetachLayer() => Calls++;
        public void StartMarketData() => Calls++;
        public void StopMarketData() => Calls++;
        public void RequestFrame() => Calls++;
    }

    private sealed class FakeStore : ISettingsStore
    {
        public string? Text { get; set; }
        public int Writes { get; private set; }
        public string? Read() => Text;
        public void Write(string text) { Writes++; Text = text; }
    }

    private sealed class FakeRunKey : IRegistryRunKey
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }

    private sealed class FakeMarker : IFirstRunMarker
    {
        public bool IsSet { get; private set; }
        public void Set() => IsSet = true;
        public void Clear() => IsSet = false;
    }

    private (TrayMenuModel Model, WallpaperToggle Toggle, FakeStore Store, FakeControl Control) Build(
        LockScreenSetting? lockScreen, bool withOpener = true)
    {
        var store = new FakeStore();
        var control = new FakeControl();
        var toggle = new WallpaperToggle(store, control);
        toggle.Start();
        var autostart = new Autostart(new FakeRunKey(), new FakeMarker(), Exe);
        var model = new TrayMenuModel(toggle, autostart, () => { },
            openFolder: withOpener ? _ => { } : null, version: "1.2.3", lockScreen: lockScreen);
        return (model, toggle, store, control);
    }

    private static TrayMenuItem Item(TrayMenuModel model) => model.Items.Single(i => i.Command == TrayCommand.ShowLockScreen);

    // ---- the setting ----

    [Fact]
    public void Default_is_on_and_reading_writes_nothing()
    {
        var setting = NewSetting();

        Assert.True(setting.IsOn);
        Assert.False(Directory.Exists(_root));
        Assert.Empty(_changes);
    }

    [Fact]
    public void Off_persists_for_a_new_instance_and_on_again_too()
    {
        NewSetting().Set(false);

        Assert.False(NewSetting().IsOn);
        Assert.True(File.Exists(SettingFile));

        NewSetting().Set(true);
        Assert.True(NewSetting().IsOn);
    }

    [Fact]
    public void Changed_is_called_with_the_new_value_and_only_on_a_real_change()
    {
        var setting = NewSetting();

        setting.Set(true);    // already on
        setting.Set(false);
        setting.Set(false);   // already off
        setting.Toggle();

        Assert.Equal(new[] { false, true }, _changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"lockScreen\": 5}")]
    public void A_missing_empty_or_corrupt_file_means_on(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingFile)!);
        File.WriteAllText(SettingFile, text);

        Assert.True(NewSetting().IsOn);
    }

    [Fact]
    public void An_unwritable_location_does_not_throw_and_is_logged()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "BitcoinWallpaper");
        File.WriteAllText(blocker, "a file where the folder should be");
        var setting = NewSetting();

        var ex = Record.Exception(() => setting.Set(false));

        Assert.Null(ex);
        Assert.NotEmpty(_log);
    }

    // ---- the settings.json is not clobbered ----

    [Fact]
    public void The_wallpaper_toggle_keeps_the_lock_screen_switch_and_the_other_way_round()
    {
        var setting = NewSetting();
        setting.Set(false);
        var menu = Build(setting);

        menu.Model.Invoke(TrayCommand.ToggleWallpaper);   // Off: settings.json rewritten whole
        menu.Model.Invoke(TrayCommand.ToggleWallpaper);   // On again

        Assert.False(NewSetting().IsOn);

        menu.Model.Invoke(TrayCommand.ShowLockScreen);    // on
        var writes = menu.Store.Writes;
        menu.Model.Invoke(TrayCommand.ToggleWallpaper);

        Assert.True(NewSetting().IsOn);
        using var json = JsonDocument.Parse(menu.Store.Text!);
        Assert.Equal("off", json.RootElement.GetProperty("wallpaper").GetString());
        Assert.Equal(writes + 1, menu.Store.Writes);
    }

    [Fact]
    public void Choosing_the_item_does_not_write_the_settings_store()
    {
        var menu = Build(NewSetting());

        menu.Model.Invoke(TrayCommand.ShowLockScreen);
        menu.Model.Invoke(TrayCommand.ShowLockScreen);

        Assert.Equal(0, menu.Store.Writes);
        Assert.Equal(WallpaperState.On, menu.Toggle.State);
        Assert.Equal(0, menu.Control.Calls - 3);   // only the three calls of Start() (attach, start data, frame)
    }

    // ---- the item ----

    [Fact]
    public void Item_sits_after_start_with_windows_and_before_open_logs()
    {
        var menu = Build(NewSetting());

        Assert.Equal(
            new[]
            {
                TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.ShowLockScreen,
                TrayCommand.OpenLogs, TrayCommand.Version, TrayCommand.Exit,
            },
            menu.Model.Items.Select(i => i.Command));
        Assert.Equal(ItemText, Item(menu.Model).Text);
        Assert.True(Item(menu.Model).Enabled);
    }

    [Fact]
    public void Without_the_setting_there_is_no_item()
    {
        var menu = Build(null);

        Assert.DoesNotContain(menu.Model.Items, i => i.Command == TrayCommand.ShowLockScreen);
        Assert.DoesNotContain(menu.Model.Items, i => i.Text.Contains("lock screen", StringComparison.OrdinalIgnoreCase));
        Assert.Null(Record.Exception(() => menu.Model.Invoke(TrayCommand.ShowLockScreen)));
    }

    [Fact]
    public void The_check_mark_is_on_by_default_and_follows_choosing_the_item()
    {
        var menu = Build(NewSetting());
        Assert.True(Item(menu.Model).Checked);

        menu.Model.Invoke(TrayCommand.ShowLockScreen);
        Assert.False(Item(menu.Model).Checked);
        Assert.False(NewSetting().IsOn);
        Assert.Equal(new[] { false }, _changes);

        menu.Model.Invoke(TrayCommand.ShowLockScreen);
        Assert.True(Item(menu.Model).Checked);
        Assert.Equal(new[] { false, true }, _changes);
    }

    [Fact]
    public void Showing_the_menu_writes_nothing()
    {
        var menu = Build(NewSetting());

        _ = menu.Model.Items;

        Assert.False(Directory.Exists(_root));
    }

    // ---- the tooltip ----

    private static TrayStatus Status(WallpaperState wallpaper = WallpaperState.On, string? error = null) =>
        new(wallpaper, PauseReason.None, FeedState.Live, MarketSource.Binance, 77182m, null, error);

    [Fact]
    public void Tooltip_mentions_a_lock_screen_failure()
    {
        var text = TrayStatusText.Build(Status(error: "Spotlight is on"), TimeZoneInfo.Utc);

        Assert.Equal(
            "Bitcoin Live Wallpaper\nLive · Binance · 77 182 · lock screen: Spotlight is on", text);
    }

    [Fact]
    public void Tooltip_without_an_error_is_unchanged()
    {
        Assert.Equal(
            "Bitcoin Live Wallpaper\nLive · Binance · 77 182",
            TrayStatusText.Build(Status(), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Tooltip_is_still_cut_to_the_limit_with_a_long_error()
    {
        var text = TrayStatusText.Build(Status(error: new string('x', 400)), TimeZoneInfo.Utc);

        Assert.True(text.Length <= TrayStatusText.MaxLength);
        Assert.Contains("lock screen", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Tooltip_says_only_off_when_the_wallpaper_is_off()
    {
        Assert.Equal(
            "Bitcoin Live Wallpaper\nOff",
            TrayStatusText.Build(Status(WallpaperState.Off, "denied"), TimeZoneInfo.Utc));
    }
}