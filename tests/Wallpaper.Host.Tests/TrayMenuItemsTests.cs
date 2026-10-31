using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Tests;

// Contract #22.4 (D-26, issue #31), criterion 3: the tray items "Open logs" and "Version x.y.z" in the menu model,
// testable with an injected opener. A test never opens Explorer: the opener is always a fake.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Settings, src/Wallpaper.Host/Settings/
// (TrayMenuModel.cs). Changes to what WallpaperToggleTests.cs sketches; those tests stay as they are and keep
// compiling and passing, so the old shapes stay valid.
//
//   public enum TrayCommand { ToggleWallpaper, StartWithWindows, Exit, OpenLogs, Version }   // two new members
//
//   // `Enabled` is new, last, default true: every existing construction/pattern keeps working.
//   public sealed record TrayMenuItem(TrayCommand Command, string Text, bool Checked, bool Enabled = true);
//
//   public sealed class TrayMenuModel
//   {
//       // The three-argument call of today stays valid (optional parameters) and gives exactly today's three items.
//       public TrayMenuModel(WallpaperToggle toggle, Autostart autostart, Action exit,
//                            Action<string>? openFolder = null,   // opens a folder in Explorer; production: a thin
//                                                                 // Process.Start("explorer.exe", path) wrapper
//                            string? logsDirectory = null,        // null -> Log.Directory
//                            string? version = null);             // e.g. "1.2.3": Velopack version, else the assembly's
//                                                                 // informational version; null -> no Version item
//
//       // Read afresh each time the menu opens, in this order (the tray puts its separator before Exit):
//       //   (ToggleWallpaper,   "Turn wallpaper off"/"Turn wallpaper on", false)             as today
//       //   (StartWithWindows,  "Start with Windows", autostart.IsEnabled)                     as today
//       //   (OpenLogs,          "Open logs", Checked false, Enabled true)    only when openFolder != null
//       //   (Version,           "Version " + version, Checked false, Enabled FALSE (greyed))  only when version != null
//       //   (Exit,              "Exit", false)                                                 as today, last
//       // Every item but Version has Enabled true.
//       public IReadOnlyList<TrayMenuItem> Items { get; }
//
//       // OpenLogs: Directory.CreateDirectory(dir) first (creates it, parents too, when missing), then
//       //   openFolder(dir) exactly once per invoke, where dir = logsDirectory ?? Log.Directory. Without an
//       //   openFolder it does nothing and does not throw.
//       // Version: does nothing at all (no opener call, no state change, no exit).
//       // The other commands are as in WallpaperToggleTests.cs.
//       public void Invoke(TrayCommand command);
//   }
public class TrayMenuItemsTests
{
    private const string Exe = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.exe";

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
        public required FakeStore Store { get; init; }
        public required FakeControl Control { get; init; }
        public required FakeRunKey RunKey { get; init; }
        public List<string> Opened { get; } = new();
        public int Exits { get; set; }
    }

    private static Menu Build(string? logsDirectory, string? version, bool withOpener = true, bool autostartEnabled = true,
        Action<string>? onOpen = null)
    {
        var store = new FakeStore();
        var control = new FakeControl();
        var toggle = new WallpaperToggle(store, control);
        toggle.Start();
        var runKey = new FakeRunKey();
        var autostart = new Autostart(runKey, new FakeMarker(), Exe);
        if (autostartEnabled) autostart.Enable();
        control.Calls.Clear();

        Menu? menu = null;
        TrayMenuModel model = withOpener
            ? new TrayMenuModel(toggle, autostart, () => menu!.Exits++,
                openFolder: path =>
                {
                    onOpen?.Invoke(path);
                    menu!.Opened.Add(path);
                },
                logsDirectory: logsDirectory, version: version)
            : new TrayMenuModel(toggle, autostart, () => menu!.Exits++, logsDirectory: logsDirectory, version: version);
        menu = new Menu { Model = model, Toggle = toggle, Store = store, Control = control, RunKey = runKey };
        return menu;
    }

    private static string FreshLogsPath() =>
        Path.Combine(Path.GetTempPath(), "bwp-tray-test-" + Guid.NewGuid().ToString("N"), "logs");

    private static void Cleanup(string logsPath)
    {
        var root = Path.GetDirectoryName(logsPath)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    // ---- the items ----

    [Fact]
    public void Menu_lists_open_logs_and_version_between_start_with_windows_and_exit()
    {
        var menu = Build(FreshLogsPath(), "1.2.3");

        var items = menu.Model.Items;

        Assert.Equal(
            new[]
            {
                TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.OpenLogs,
                TrayCommand.Version, TrayCommand.Exit,
            },
            items.Select(i => i.Command));
        Assert.Equal("Turn wallpaper off", items[0].Text);
        Assert.Equal("Start with Windows", items[1].Text);
        Assert.True(items[1].Checked);
        Assert.Equal("Open logs", items[2].Text);
        Assert.False(items[2].Checked);
        Assert.Equal("Version 1.2.3", items[3].Text);
        Assert.False(items[3].Checked);
        Assert.Equal("Exit", items[4].Text);
        Assert.False(items[4].Checked);
    }

    [Fact]
    public void Version_is_greyed_and_everything_else_is_enabled()
    {
        var menu = Build(FreshLogsPath(), "1.2.3");

        foreach (var item in menu.Model.Items)
        {
            Assert.Equal(item.Command != TrayCommand.Version, item.Enabled);
        }
    }

    [Theory]
    [InlineData("1.2.3", "Version 1.2.3")]
    [InlineData("0.1.0", "Version 0.1.0")]
    [InlineData("1.4.0-beta.2+abc1234", "Version 1.4.0-beta.2+abc1234")]
    public void Version_text_is_the_injected_version(string version, string expected)
    {
        var menu = Build(FreshLogsPath(), version);

        Assert.Equal(expected, menu.Model.Items.Single(i => i.Command == TrayCommand.Version).Text);
    }

    [Fact]
    public void Existing_items_keep_their_text_and_state_with_the_new_ones_present()
    {
        var menu = Build(FreshLogsPath(), "1.2.3", autostartEnabled: false);

        Assert.False(menu.Model.Items[1].Checked);
        menu.Model.Invoke(TrayCommand.StartWithWindows);
        Assert.True(menu.Model.Items[1].Checked);

        menu.Model.Invoke(TrayCommand.ToggleWallpaper);
        Assert.Equal(WallpaperState.Off, menu.Toggle.State);
        Assert.Equal("Turn wallpaper on", menu.Model.Items[0].Text);
        Assert.Equal(5, menu.Model.Items.Count);
        Assert.Equal(TrayCommand.Exit, menu.Model.Items[^1].Command);
    }

    [Fact]
    public void Exit_still_exits_once_and_does_nothing_else()
    {
        var menu = Build(FreshLogsPath(), "1.2.3");

        menu.Model.Invoke(TrayCommand.Exit);

        Assert.Equal(1, menu.Exits);
        Assert.Empty(menu.Opened);
        Assert.Empty(menu.Control.Calls);
    }

    [Fact]
    public void Without_the_injections_the_menu_is_todays_three_items()
    {
        var store = new FakeStore();
        var toggle = new WallpaperToggle(store, new FakeControl());
        toggle.Start();
        var autostart = new Autostart(new FakeRunKey(), new FakeMarker(), Exe);

        var items = new TrayMenuModel(toggle, autostart, () => { }).Items;

        Assert.Equal(
            new[] { TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.Exit },
            items.Select(i => i.Command));
        Assert.All(items, i => Assert.True(i.Enabled));
    }

    [Fact]
    public void Open_logs_is_left_out_without_an_opener_and_version_without_a_version()
    {
        var noOpener = Build(FreshLogsPath(), "1.2.3", withOpener: false);
        Assert.Equal(
            new[] { TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.Version, TrayCommand.Exit },
            noOpener.Model.Items.Select(i => i.Command));

        var noVersion = Build(FreshLogsPath(), version: null);
        Assert.Equal(
            new[] { TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.OpenLogs, TrayCommand.Exit },
            noVersion.Model.Items.Select(i => i.Command));
    }

    // ---- Open logs ----

    [Fact]
    public void Open_logs_opens_the_logs_directory_through_the_opener_once()
    {
        var logs = FreshLogsPath();
        try
        {
            var menu = Build(logs, "1.2.3");

            menu.Model.Invoke(TrayCommand.OpenLogs);

            Assert.Equal(new[] { logs }, menu.Opened);
        }
        finally
        {
            Cleanup(logs);
        }
    }

    [Fact]
    public void Open_logs_creates_the_directory_before_opening_it()
    {
        var logs = FreshLogsPath();
        try
        {
            Assert.False(Directory.Exists(logs));
            var existedWhenOpened = new List<bool>();
            var menu = Build(logs, "1.2.3", onOpen: path => existedWhenOpened.Add(Directory.Exists(path)));

            menu.Model.Invoke(TrayCommand.OpenLogs);

            Assert.Equal(new[] { true }, existedWhenOpened);
            Assert.True(Directory.Exists(logs));
        }
        finally
        {
            Cleanup(logs);
        }
    }

    [Fact]
    public void Open_logs_with_an_existing_directory_keeps_its_files()
    {
        var logs = FreshLogsPath();
        try
        {
            Directory.CreateDirectory(logs);
            var file = Path.Combine(logs, "wallpaper.log");
            File.WriteAllText(file, "a line");
            var menu = Build(logs, "1.2.3");

            menu.Model.Invoke(TrayCommand.OpenLogs);

            Assert.Equal("a line", File.ReadAllText(file));
            Assert.Equal(new[] { logs }, menu.Opened);
        }
        finally
        {
            Cleanup(logs);
        }
    }

    [Fact]
    public void Open_logs_opens_again_on_every_invoke()
    {
        var logs = FreshLogsPath();
        try
        {
            var menu = Build(logs, "1.2.3");

            menu.Model.Invoke(TrayCommand.OpenLogs);
            menu.Model.Invoke(TrayCommand.OpenLogs);

            Assert.Equal(new[] { logs, logs }, menu.Opened);
        }
        finally
        {
            Cleanup(logs);
        }
    }

    [Fact]
    public void Open_logs_changes_nothing_else()
    {
        var logs = FreshLogsPath();
        try
        {
            var menu = Build(logs, "1.2.3");
            var runKeyBefore = menu.RunKey.Values.ToDictionary(p => p.Key, p => p.Value);

            menu.Model.Invoke(TrayCommand.OpenLogs);

            Assert.Equal(WallpaperState.On, menu.Toggle.State);
            Assert.Empty(menu.Control.Calls);
            Assert.Equal(0, menu.Exits);
            Assert.Equal(runKeyBefore, menu.RunKey.Values);
        }
        finally
        {
            Cleanup(logs);
        }
    }

    [Fact]
    public void Showing_the_menu_opens_nothing_and_creates_nothing()
    {
        var logs = FreshLogsPath();
        try
        {
            var menu = Build(logs, "1.2.3");

            _ = menu.Model.Items;
            _ = menu.Model.Items;

            Assert.Empty(menu.Opened);
            Assert.False(Directory.Exists(logs));
        }
        finally
        {
            Cleanup(logs);
        }
    }

    [Fact]
    public void Open_logs_without_an_opener_does_nothing_and_does_not_throw()
    {
        var logs = FreshLogsPath();
        try
        {
            var menu = Build(logs, "1.2.3", withOpener: false);

            menu.Model.Invoke(TrayCommand.OpenLogs);

            Assert.Equal(0, menu.Exits);
            Assert.Empty(menu.Control.Calls);
        }
        finally
        {
            Cleanup(logs);
        }
    }

    // ---- Version ----

    [Fact]
    public void Version_does_nothing_when_invoked()
    {
        var logs = FreshLogsPath();
        try
        {
            var menu = Build(logs, "1.2.3");
            var runKeyBefore = menu.RunKey.Values.ToDictionary(p => p.Key, p => p.Value);
            var writesBefore = menu.Store.Writes;

            menu.Model.Invoke(TrayCommand.Version);

            Assert.Empty(menu.Opened);
            Assert.False(Directory.Exists(logs));
            Assert.Equal(0, menu.Exits);
            Assert.Empty(menu.Control.Calls);
            Assert.Equal(WallpaperState.On, menu.Toggle.State);
            Assert.Equal(writesBefore, menu.Store.Writes);
            Assert.Equal(runKeyBefore, menu.RunKey.Values);
            Assert.Equal("Version 1.2.3", menu.Model.Items.Single(i => i.Command == TrayCommand.Version).Text);
        }
        finally
        {
            Cleanup(logs);
        }
    }
}
