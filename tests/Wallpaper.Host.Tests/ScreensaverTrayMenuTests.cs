using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Screensaver;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Tests;

// Contract 26.1 (D-21, issue #26), criterion 6: the tray gets the check mark item "Use as screensaver", right after
// "Start with Windows", that toggles the HKCU model; the existing items, order and texts are otherwise unchanged
// (TrayMenuItemsTests and WallpaperToggleTests stay as they are and keep passing).
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Settings (TrayMenuModel.cs); the model type
// is ScreensaverSetting from ScreensaverSettingTests.cs (BitcoinWallpaper.Host.Screensaver).
//
//   public enum TrayCommand { ToggleWallpaper, StartWithWindows, Exit, OpenLogs, Version, UseAsScreensaver }   // one new member
//                                                                                           // (its place in the enum is free)
//   public sealed class TrayMenuModel
//   {
//       // A NEW optional parameter, last (after `version`); the earlier calls compile unchanged. Tests pass it by name.
//       public TrayMenuModel(WallpaperToggle toggle, Autostart autostart, Action exit,
//                            Action<string>? openFolder = null, string? logsDirectory = null, string? version = null,
//                            ScreensaverSetting? screensaver = null);
//
//       // Items, read afresh each time the menu opens, in this order:
//       //   ToggleWallpaper, StartWithWindows, [UseAsScreensaver], [OpenLogs], [Version], Exit
//       // UseAsScreensaver: Text "Use as screensaver", Checked = screensaver.IsOn, Enabled true;
//       // present only when screensaver != null (null: no item, exactly today's menu).
//
//       // Invoke(UseAsScreensaver): screensaver.Toggle(); nothing else changes (wallpaper state, autostart, exit, the
//       // wallpaper control). With screensaver == null: does nothing and does not throw. A failing registry is
//       // swallowed by ScreensaverSetting (logged), never thrown into the tray.
//       public void Invoke(TrayCommand command);
//   }
public sealed class ScreensaverTrayMenuTests : IDisposable
{
    private const string Exe = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.exe";
    private const string Scr = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.scr";
    private const string Ribbons = @"C:\Windows\System32\Ribbons.scr";
    private const string Bubbles = @"C:\Windows\System32\Bubbles.scr";
    private const string Scrn = ScreensaverSetting.ScrValueName;
    private const string Active = ScreensaverSetting.ActiveValueName;

    // ---- fakes ----

    private sealed class FakeControl : IWallpaperControl
    {
        public List<string> Calls { get; } = new();
        public void AttachLayer() => Calls.Add("attach");
        public void DetachLayer() => Calls.Add("detach");
        public void StartMarketData() => Calls.Add("start-data");
        public void StopMarketData() => Calls.Add("stop-data");
        public void RequestFrame() => Calls.Add("frame");
    }

    private sealed class FakeStore : ISettingsStore
    {
        public string? Text { get; set; }
        public int Writes { get; private set; }
        public string? Read() => Text;

        public void Write(string text)
        {
            Writes++;
            Text = text;
        }
    }

    private sealed class FakeRunKey : IRegistryRunKey
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => Values.TryGetValue(name, out var v) ? v : null;
        public void SetValue(string name, string value) => Values[name] = value;
        public void DeleteValue(string name) => Values.Remove(name);
    }

    private sealed class FakeMarker : IFirstRunMarker
    {
        public bool IsSet { get; private set; }
        public void Set() => IsSet = true;
        public void Clear() => IsSet = false;
    }

    private sealed class Menu
    {
        public required TrayMenuModel Model { get; init; }
        public required WallpaperToggle Toggle { get; init; }
        public required Autostart Autostart { get; init; }
        public required FakeStore Store { get; init; }
        public required FakeControl Control { get; init; }
        public int Exits { get; set; }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bw-screensaver-tray-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScreensaverRegistry _registry = new();
    private readonly List<string> _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ScreensaverSetting NewSetting() =>
        new(_registry, Path.Combine(_root, "BitcoinWallpaper", "screensaver.json"), Scr, _log.Add);

    private Menu Build(bool withScreensaver = true, bool withOpener = true, string? version = "1.2.3")
    {
        var store = new FakeStore();
        var control = new FakeControl();
        var toggle = new WallpaperToggle(store, control);
        toggle.Start();
        var autostart = new Autostart(new FakeRunKey(), new FakeMarker(), Exe);
        autostart.Enable();
        control.Calls.Clear();

        Menu? menu = null;
        var screensaver = withScreensaver ? NewSetting() : null;
        var model = new TrayMenuModel(toggle, autostart, () => menu!.Exits++,
            openFolder: withOpener ? _ => { } : null,
            logsDirectory: Path.Combine(_root, "logs"),
            version: version,
            screensaver: screensaver);
        menu = new Menu { Model = model, Toggle = toggle, Autostart = autostart, Store = store, Control = control };
        return menu;
    }

    private static TrayMenuItem Item(Menu menu) => menu.Model.Items.Single(i => i.Command == TrayCommand.UseAsScreensaver);

    // ---- the items ----

    [Fact]
    public void Menu_lists_use_as_screensaver_right_after_start_with_windows()
    {
        var menu = Build();

        var items = menu.Model.Items;

        Assert.Equal(
            new[]
            {
                TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.UseAsScreensaver,
                TrayCommand.OpenLogs, TrayCommand.Version, TrayCommand.Exit,
            },
            items.Select(i => i.Command));
        Assert.Equal("Use as screensaver", items[2].Text);
        Assert.True(items[2].Enabled);
    }

    [Fact]
    public void Existing_items_keep_their_text_and_state()
    {
        var menu = Build();

        var items = menu.Model.Items;

        Assert.Equal("Turn wallpaper off", items[0].Text);
        Assert.False(items[0].Checked);
        Assert.Equal("Start with Windows", items[1].Text);
        Assert.True(items[1].Checked);
        Assert.Equal("Open logs", items[3].Text);
        Assert.False(items[3].Checked);
        Assert.Equal("Version 1.2.3", items[4].Text);
        Assert.False(items[4].Enabled);
        Assert.Equal("Exit", items[5].Text);
        Assert.False(items[5].Checked);
    }

    [Fact]
    public void Item_sits_between_start_with_windows_and_exit_without_open_logs_and_version()
    {
        var menu = Build(withOpener: false, version: null);

        Assert.Equal(
            new[] { TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.UseAsScreensaver, TrayCommand.Exit },
            menu.Model.Items.Select(i => i.Command));
    }

    [Fact]
    public void Without_the_model_there_is_no_item_and_the_menu_is_todays()
    {
        var menu = Build(withScreensaver: false);

        Assert.Equal(
            new[]
            {
                TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.OpenLogs, TrayCommand.Version,
                TrayCommand.Exit,
            },
            menu.Model.Items.Select(i => i.Command));
        Assert.DoesNotContain(menu.Model.Items, i => i.Text.Contains("screensaver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_old_three_argument_constructor_gives_todays_three_items()
    {
        var toggle = new WallpaperToggle(new FakeStore(), new FakeControl());
        toggle.Start();
        var autostart = new Autostart(new FakeRunKey(), new FakeMarker(), Exe);

        var items = new TrayMenuModel(toggle, autostart, () => { }).Items;

        Assert.Equal(
            new[] { TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.Exit },
            items.Select(i => i.Command));
    }

    // ---- the check mark ----

    [Fact]
    public void Check_mark_is_on_exactly_while_the_registry_holds_our_scr()
    {
        var menu = Build();
        Assert.False(Item(menu).Checked);   // absent

        _registry.Values[Scrn] = Ribbons;
        Assert.False(Item(menu).Checked);   // another screensaver

        _registry.Values[Scrn] = Scr;
        Assert.True(Item(menu).Checked);

        _registry.Values[Scrn] = Scr.ToUpperInvariant();
        Assert.True(Item(menu).Checked);    // case-insensitive

        _registry.Values[Scrn] = Bubbles;   // the user picked another one in Windows
        Assert.False(Item(menu).Checked);
    }

    [Fact]
    public void Showing_the_menu_writes_nothing()
    {
        var menu = Build();

        _ = menu.Model.Items;
        _ = menu.Model.Items;

        Assert.Empty(_registry.Writes);
        Assert.False(Directory.Exists(_root));
    }

    // ---- invoke ----

    [Fact]
    public void Choosing_the_item_turns_it_on_then_off_restoring_the_previous_screensaver()
    {
        _registry.Values[Scrn] = Ribbons;
        _registry.Values[Active] = "0";
        var menu = Build();

        menu.Model.Invoke(TrayCommand.UseAsScreensaver);

        Assert.Equal(Scr, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
        Assert.True(Item(menu).Checked);

        menu.Model.Invoke(TrayCommand.UseAsScreensaver);

        Assert.Equal(Ribbons, _registry.GetValue(Scrn));
        Assert.Equal("0", _registry.GetValue(Active));
        Assert.False(Item(menu).Checked);
    }

    [Fact]
    public void Choosing_the_item_with_another_screensaver_picked_turns_ours_on()
    {
        _registry.Values[Scrn] = Bubbles;
        var menu = Build();
        Assert.False(Item(menu).Checked);

        menu.Model.Invoke(TrayCommand.UseAsScreensaver);

        Assert.Equal(Scr, _registry.GetValue(Scrn));
        Assert.True(Item(menu).Checked);
    }

    [Fact]
    public void Choosing_the_item_changes_nothing_but_the_screensaver()
    {
        var menu = Build();
        var writesBefore = menu.Store.Writes;

        menu.Model.Invoke(TrayCommand.UseAsScreensaver);
        menu.Model.Invoke(TrayCommand.UseAsScreensaver);

        Assert.Equal(WallpaperState.On, menu.Toggle.State);
        Assert.True(menu.Autostart.IsEnabled);
        Assert.Equal(0, menu.Exits);
        Assert.Empty(menu.Control.Calls);
        Assert.Equal(writesBefore, menu.Store.Writes);
        Assert.Equal("Turn wallpaper off", menu.Model.Items[0].Text);
    }

    [Fact]
    public void The_other_items_still_work_with_the_screensaver_item_present()
    {
        var menu = Build();

        menu.Model.Invoke(TrayCommand.ToggleWallpaper);
        Assert.Equal(WallpaperState.Off, menu.Toggle.State);
        Assert.Equal("Turn wallpaper on", menu.Model.Items[0].Text);

        menu.Model.Invoke(TrayCommand.StartWithWindows);
        Assert.False(menu.Autostart.IsEnabled);
        Assert.False(menu.Model.Items[1].Checked);

        menu.Model.Invoke(TrayCommand.Exit);
        Assert.Equal(1, menu.Exits);
        Assert.Empty(_registry.Writes);   // none of them touches the screensaver
    }

    [Fact]
    public void Choosing_the_item_without_the_model_does_nothing_and_does_not_throw()
    {
        var menu = Build(withScreensaver: false);

        var ex = Record.Exception(() => menu.Model.Invoke(TrayCommand.UseAsScreensaver));

        Assert.Null(ex);
        Assert.Empty(_registry.Writes);
        Assert.Equal(0, menu.Exits);
        Assert.Empty(menu.Control.Calls);
    }

    // ---- a failing registry never reaches the tray ----

    [Fact]
    public void A_failing_registry_does_not_throw_into_the_tray_and_is_logged()
    {
        _registry.Values[Scrn] = Ribbons;
        var menu = Build();
        _registry.ThrowOnSet = true;

        var ex = Record.Exception(() => menu.Model.Invoke(TrayCommand.UseAsScreensaver));

        Assert.Null(ex);
        Assert.NotEmpty(_log);
        Assert.False(Item(menu).Checked);   // nothing was written: the check mark stays off
        Assert.Equal(0, menu.Exits);
    }

    [Fact]
    public void An_unreadable_registry_does_not_throw_from_the_menu_or_the_invoke()
    {
        var menu = Build();
        _registry.ThrowOnGet = true;

        Assert.Null(Record.Exception(() => menu.Model.Items));
        Assert.Null(Record.Exception(() => menu.Model.Invoke(TrayCommand.UseAsScreensaver)));
        Assert.False(Item(menu).Checked);
    }
}
