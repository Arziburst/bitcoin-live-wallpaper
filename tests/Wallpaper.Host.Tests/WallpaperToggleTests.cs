using System.Text.Json;
using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Tests;

// Contract #9, criterion "D-16 tray menu": "Turn wallpaper off" / "Turn wallpaper on", "Start with Windows" (check
// mark), "Exit"; off = our layer detached, no frames, the market data client stopped, the process stays in the tray;
// on = re-attach, data reloaded, one frame; the off/on state persists across restarts in a per-user settings file
// under %LOCALAPPDATA%\BitcoinWallpaper.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Settings, src/Wallpaper.Host/Settings/.
// Uses Autostart / IRegistryRunKey / IFirstRunMarker from AutostartTests.cs (BitcoinWallpaper.Host.Install).
//
//   public enum WallpaperState { On, Off }
//
//   // What the toggle asks of the host. Production: WallpaperHost / the market data client / the scene.
//   public interface IWallpaperControl
//   {
//       void AttachLayer();       // put our layer behind the icons again
//       void DetachLayer();       // remove our layer: the normal Windows wallpaper shows
//       void StartMarketData();   // (re)connect the market data client and reload the history
//       void StopMarketData();    // stop the client: socket closed, no REST polling
//       void RequestFrame();      // draw one frame
//   }
//
//   // Raw settings text. Production: FileSettingsStore.
//   public interface ISettingsStore
//   {
//       string? Read();             // null when there is no file; may throw IOException
//       void Write(string text);    // may throw IOException
//   }
//
//   public sealed class FileSettingsStore : ISettingsStore
//   {
//       public FileSettingsStore(string path);
//       // %LOCALAPPDATA%\BitcoinWallpaper\settings.json, i.e.
//       // Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "settings.json")
//       public static string DefaultPath { get; }
//       // Read: the file's text, null when the file does not exist. Write: creates the folder when missing,
//       // replaces the whole file.
//   }
//
//   public sealed class WallpaperToggle
//   {
//       public WallpaperToggle(ISettingsStore store, IWallpaperControl control);
//       public WallpaperState State { get; }   // On before Start()
//       // App start. Reads the store: a persisted Off -> State Off and no calls on control at all (the host still
//       // shows its tray icon). Anything else (no file, empty, corrupt JSON, JSON without the setting, Read throws)
//       // -> State On and the TurnOn effects: AttachLayer, StartMarketData, exactly one RequestFrame (after AttachLayer).
//       public void Start();
//       // D-17 explicit start by the user (SecondStartTests.cs): like Start() but a persisted Off is ignored: State On,
//       // the TurnOn effects (AttachLayer, StartMarketData, exactly one RequestFrame after AttachLayer), and On is saved.
//       public void StartOn();
//       // On -> Off: DetachLayer once, StopMarketData once, no RequestFrame; saves Off. Already Off: no calls, no save.
//       public void TurnOff();
//       // Off -> On: AttachLayer once, StartMarketData once, exactly one RequestFrame (after AttachLayer); saves On.
//       // Already On: no calls, no save.
//       public void TurnOn();
//       public void Toggle();   // On -> TurnOff(), Off -> TurnOn()
//       // The text written to the store is valid JSON. A failing Write (IOException) does not escape and does not
//       // undo the switch (the state and the control calls stay as above).
//   }
//
//   public enum TrayCommand { ToggleWallpaper, StartWithWindows, Exit }
//   public sealed record TrayMenuItem(TrayCommand Command, string Text, bool Checked);
//
//   // The tray menu as data; TrayIcon draws it and passes the chosen command back to Invoke.
//   public sealed class TrayMenuModel
//   {
//       public TrayMenuModel(WallpaperToggle toggle, Autostart autostart, Action exit);
//       // Read afresh each time the menu opens, in this order:
//       //   (ToggleWallpaper, "Turn wallpaper off" when On / "Turn wallpaper on" when Off, Checked false)
//       //   (StartWithWindows, "Start with Windows", Checked = autostart.IsEnabled)
//       //   (Exit, "Exit", Checked false)
//       public IReadOnlyList<TrayMenuItem> Items { get; }
//       // ToggleWallpaper -> toggle.Toggle(); StartWithWindows -> autostart.Disable() when enabled, else Enable();
//       // Exit -> exit() once, nothing else.
//       public void Invoke(TrayCommand command);
//   }
public class WallpaperToggleTests
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
        public int Count(string call) => Calls.Count(c => c == call);
    }

    private sealed class FakeStore : ISettingsStore
    {
        public string? Text { get; set; }
        public int Writes { get; private set; }
        public bool ThrowOnRead { get; set; }
        public bool ThrowOnWrite { get; set; }

        public string? Read() => ThrowOnRead ? throw new IOException("settings.json is locked") : Text;

        public void Write(string text)
        {
            if (ThrowOnWrite) throw new IOException("disk full");
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

    /// <summary>A store holding a persisted Off, written by the toggle itself (the format is the worker's).</summary>
    private static FakeStore StoreWithOff()
    {
        var store = new FakeStore();
        var toggle = new WallpaperToggle(store, new FakeControl());
        toggle.Start();
        toggle.TurnOff();
        Assert.Equal(WallpaperState.Off, toggle.State);
        return store;
    }

    private static WallpaperToggle Started(FakeStore store, FakeControl control)
    {
        var toggle = new WallpaperToggle(store, control);
        toggle.Start();
        return toggle;
    }

    // ---- start ----

    [Fact]
    public void Missing_settings_starts_on_with_attach_data_and_one_frame()
    {
        var control = new FakeControl();
        var toggle = Started(new FakeStore(), control);

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(1, control.Count("frame"));
        Assert.Equal(0, control.Count("detach"));
        Assert.Equal(0, control.Count("stop-data"));
        Assert.True(control.Calls.IndexOf("attach") < control.Calls.IndexOf("frame"), string.Join(",", control.Calls));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"somethingElse\":1}")]
    [InlineData("\u0000\u0001\u0002")]
    public void Corrupt_or_empty_settings_mean_on(string text)
    {
        var control = new FakeControl();
        var toggle = Started(new FakeStore { Text = text }, control);

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(1, control.Count("frame"));
    }

    [Fact]
    public void Unreadable_settings_mean_on()
    {
        var control = new FakeControl();
        var toggle = Started(new FakeStore { ThrowOnRead = true }, control);

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("frame"));
    }

    [Fact]
    public void Persisted_off_starts_off_without_touching_the_layer_or_the_data_client()
    {
        var store = StoreWithOff();
        var control = new FakeControl();

        var toggle = Started(store, control);

        Assert.Equal(WallpaperState.Off, toggle.State);
        Assert.Empty(control.Calls);   // no attach, no data client, no frame
    }

    [Fact]
    public void Persisted_on_starts_on()
    {
        var store = StoreWithOff();
        var second = Started(store, new FakeControl());
        second.TurnOn();

        var control = new FakeControl();
        var third = Started(store, control);

        Assert.Equal(WallpaperState.On, third.State);
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(1, control.Count("frame"));
    }

    // ---- turn off / turn on ----

    [Fact]
    public void TurnOff_detaches_stops_the_data_client_and_draws_nothing()
    {
        var control = new FakeControl();
        var toggle = Started(new FakeStore(), control);
        control.Calls.Clear();

        toggle.TurnOff();

        Assert.Equal(WallpaperState.Off, toggle.State);
        Assert.Equal(1, control.Count("detach"));
        Assert.Equal(1, control.Count("stop-data"));
        Assert.Equal(0, control.Count("frame"));
        Assert.Equal(0, control.Count("attach"));
        Assert.Equal(0, control.Count("start-data"));
    }

    [Fact]
    public void TurnOn_reattaches_reloads_data_and_requests_exactly_one_frame()
    {
        var control = new FakeControl();
        var toggle = Started(new FakeStore(), control);
        toggle.TurnOff();
        control.Calls.Clear();

        toggle.TurnOn();

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(1, control.Count("frame"));
        Assert.Equal(0, control.Count("detach"));
        Assert.Equal(0, control.Count("stop-data"));
        Assert.True(control.Calls.IndexOf("attach") < control.Calls.IndexOf("frame"), string.Join(",", control.Calls));
    }

    [Fact]
    public void TurnOn_from_a_persisted_off_start()
    {
        var store = StoreWithOff();
        var control = new FakeControl();
        var toggle = Started(store, control);

        toggle.TurnOn();

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Equal(new[] { "attach", "frame" }, control.Calls.Where(c => c is "attach" or "frame"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(0, control.Count("detach"));
        Assert.Equal(0, control.Count("stop-data"));
    }

    [Fact]
    public void Repeated_TurnOff_is_idempotent()
    {
        var store = new FakeStore();
        var control = new FakeControl();
        var toggle = Started(store, control);
        toggle.TurnOff();
        var writes = store.Writes;
        control.Calls.Clear();

        toggle.TurnOff();
        toggle.TurnOff();

        Assert.Equal(WallpaperState.Off, toggle.State);
        Assert.Empty(control.Calls);
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public void Repeated_TurnOn_is_idempotent()
    {
        var store = new FakeStore();
        var control = new FakeControl();
        var toggle = Started(store, control);   // On
        var writes = store.Writes;
        control.Calls.Clear();

        toggle.TurnOn();
        toggle.TurnOn();

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Empty(control.Calls);
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public void Toggle_alternates_and_each_switch_does_its_work_once()
    {
        var control = new FakeControl();
        var toggle = Started(new FakeStore(), control);
        control.Calls.Clear();

        toggle.Toggle();   // off
        Assert.Equal(WallpaperState.Off, toggle.State);
        toggle.Toggle();   // on
        Assert.Equal(WallpaperState.On, toggle.State);
        toggle.Toggle();   // off
        Assert.Equal(WallpaperState.Off, toggle.State);

        Assert.Equal(2, control.Count("detach"));
        Assert.Equal(2, control.Count("stop-data"));
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(1, control.Count("frame"));
    }

    // ---- persistence ----

    [Fact]
    public void Each_switch_is_saved_as_json()
    {
        var store = new FakeStore();
        var toggle = Started(store, new FakeControl());

        toggle.TurnOff();
        Assert.True(store.Writes >= 1);
        var off = store.Text!;
        using (JsonDocument.Parse(off)) { }

        toggle.TurnOn();
        var on = store.Text!;
        using (JsonDocument.Parse(on)) { }

        Assert.NotEqual(off, on);
    }

    [Fact]
    public void Off_survives_several_restarts()
    {
        var store = StoreWithOff();

        for (var restart = 0; restart < 3; restart++)
        {
            var control = new FakeControl();
            var toggle = Started(store, control);
            Assert.Equal(WallpaperState.Off, toggle.State);
            Assert.Empty(control.Calls);
        }
    }

    [Fact]
    public void A_failing_save_does_not_escape_or_undo_the_switch()
    {
        var store = new FakeStore();
        var control = new FakeControl();
        var toggle = Started(store, control);
        store.ThrowOnWrite = true;
        control.Calls.Clear();

        var ex = Record.Exception(() => toggle.TurnOff());

        Assert.Null(ex);
        Assert.Equal(WallpaperState.Off, toggle.State);
        Assert.Equal(1, control.Count("detach"));
        Assert.Equal(1, control.Count("stop-data"));
    }

    [Fact]
    public void Settings_file_default_path_is_per_user_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "settings.json");

        Assert.Equal(expected, FileSettingsStore.DefaultPath);
    }

    [Fact]
    public void File_store_round_trips_and_creates_its_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "bw-toggle-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "BitcoinWallpaper", "settings.json");
            var store = new FileSettingsStore(path);

            Assert.Null(store.Read());   // no file yet

            store.Write("{\"a\":1}");
            Assert.True(File.Exists(path));
            Assert.Equal("{\"a\":1}", store.Read());

            store.Write("{}");
            Assert.Equal("{}", store.Read());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void File_store_persists_off_across_restarts_and_a_corrupt_file_means_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "bw-toggle-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "BitcoinWallpaper", "settings.json");

            var first = Started(new FileSettingsStore(path), new FakeControl());
            first.TurnOff();

            var control = new FakeControl();
            var second = Started(new FileSettingsStore(path), control);
            Assert.Equal(WallpaperState.Off, second.State);
            Assert.Empty(control.Calls);

            File.WriteAllText(path, "{ this is not json");
            var afterCorruption = new FakeControl();
            var third = Started(new FileSettingsStore(path), afterCorruption);
            Assert.Equal(WallpaperState.On, third.State);
            Assert.Equal(1, afterCorruption.Count("attach"));
            Assert.Equal(1, afterCorruption.Count("frame"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WallpaperToggle Started(ISettingsStore store, FakeControl control)
    {
        var toggle = new WallpaperToggle(store, control);
        toggle.Start();
        return toggle;
    }

    // ---- tray menu model ----

    private sealed record Menu(TrayMenuModel Model, WallpaperToggle Toggle, Autostart Autostart, FakeRunKey RunKey,
        FakeControl Control, Func<int> Exits);

    private static Menu MenuWith(FakeStore store, bool autostartEnabled)
    {
        var control = new FakeControl();
        var toggle = Started(store, control);
        var runKey = new FakeRunKey();
        var autostart = new Autostart(runKey, new FakeMarker(), Exe);
        if (autostartEnabled) autostart.Enable();
        var exits = 0;
        var model = new TrayMenuModel(toggle, autostart, () => exits++);
        return new Menu(model, toggle, autostart, runKey, control, () => exits);
    }

    [Fact]
    public void Menu_when_on_offers_turn_off_start_with_windows_and_exit()
    {
        var menu = MenuWith(new FakeStore(), autostartEnabled: true);

        var items = menu.Model.Items;

        Assert.Equal(
            new[] { TrayCommand.ToggleWallpaper, TrayCommand.StartWithWindows, TrayCommand.Exit },
            items.Select(i => i.Command));
        Assert.Equal("Turn wallpaper off", items[0].Text);
        Assert.False(items[0].Checked);
        Assert.Equal("Start with Windows", items[1].Text);
        Assert.True(items[1].Checked);
        Assert.Equal("Exit", items[2].Text);
        Assert.False(items[2].Checked);
    }

    [Fact]
    public void Menu_label_follows_the_state()
    {
        var menu = MenuWith(new FakeStore(), autostartEnabled: false);

        menu.Model.Invoke(TrayCommand.ToggleWallpaper);

        Assert.Equal(WallpaperState.Off, menu.Toggle.State);
        Assert.Equal("Turn wallpaper on", menu.Model.Items[0].Text);
        Assert.Equal(1, menu.Control.Count("detach"));
        Assert.Equal(1, menu.Control.Count("stop-data"));

        menu.Model.Invoke(TrayCommand.ToggleWallpaper);

        Assert.Equal(WallpaperState.On, menu.Toggle.State);
        Assert.Equal("Turn wallpaper off", menu.Model.Items[0].Text);
    }

    [Fact]
    public void Menu_after_a_persisted_off_start_offers_turn_on()
    {
        var menu = MenuWith(StoreWithOff(), autostartEnabled: true);

        Assert.Equal(WallpaperState.Off, menu.Toggle.State);
        Assert.Empty(menu.Control.Calls);
        Assert.Equal("Turn wallpaper on", menu.Model.Items[0].Text);
        Assert.Equal(3, menu.Model.Items.Count);
    }

    [Fact]
    public void Start_with_Windows_check_mark_reads_autostart()
    {
        var menu = MenuWith(new FakeStore(), autostartEnabled: false);
        Assert.False(menu.Model.Items[1].Checked);

        menu.Autostart.Enable();   // changed outside the menu
        Assert.True(menu.Model.Items[1].Checked);
    }

    [Fact]
    public void Start_with_Windows_toggles_autostart()
    {
        var menu = MenuWith(new FakeStore(), autostartEnabled: true);

        menu.Model.Invoke(TrayCommand.StartWithWindows);

        Assert.False(menu.Autostart.IsEnabled);
        Assert.Null(menu.RunKey.GetValue(Autostart.ValueName));
        Assert.False(menu.Model.Items[1].Checked);

        menu.Model.Invoke(TrayCommand.StartWithWindows);

        Assert.True(menu.Autostart.IsEnabled);
        Assert.Equal("\"" + Exe + "\" --autostart", menu.RunKey.GetValue(Autostart.ValueName));
        Assert.True(menu.Model.Items[1].Checked);
        Assert.Equal(WallpaperState.On, menu.Toggle.State);   // the wallpaper is not affected
    }

    [Fact]
    public void Exit_calls_exit_once_and_changes_nothing_else()
    {
        var menu = MenuWith(new FakeStore(), autostartEnabled: true);
        menu.Control.Calls.Clear();

        menu.Model.Invoke(TrayCommand.Exit);

        Assert.Equal(1, menu.Exits());
        Assert.Empty(menu.Control.Calls);
        Assert.Equal(WallpaperState.On, menu.Toggle.State);
        Assert.True(menu.Autostart.IsEnabled);
    }
}
