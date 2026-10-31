using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Tests;

// Contract #9, criterion D-17: starting the app while an instance runs does not start a second one - it tells the
// running instance to turn the wallpaper on (On stays On); starting it when none runs starts it with the wallpaper
// on, even if Off was persisted. The launch at sign-in (the Run value carries --autostart, AutostartTests.cs) keeps
// the persisted state.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Settings, src/Wallpaper.Host/Settings/.
// Uses WallpaperToggle / IWallpaperControl / ISettingsStore / WallpaperState (WallpaperToggleTests.cs) and
// Autostart.AutostartArgument (AutostartTests.cs).
//
//   public enum LaunchKind { User, Autostart }
//
//   public static class Launch
//   {
//       // Autostart when args contains Autostart.AutostartArgument ("--autostart"); anything else - no arguments,
//       // unknown arguments such as Velopack's - is User.
//       public static LaunchKind Parse(IReadOnlyList<string> args);
//   }
//
//   // One wallpaper per user session. Production: a per-session named mutex plus a way to reach the primary
//   // (named pipe or a window message); TurnOnRequested is raised so that the toggle runs on the host's UI thread.
//   public interface IInstanceChannel
//   {
//       bool TryAcquirePrimary();          // true: this process is the only instance and becomes the primary
//       void SendTurnOn();                 // from a second process to the primary
//       event Action? TurnOnRequested;     // in the primary, once per SendTurnOn
//   }
//
//   public sealed class SingleInstanceStartup
//   {
//       public SingleInstanceStartup(IInstanceChannel channel, WallpaperToggle toggle);
//       // Calls channel.TryAcquirePrimary() once.
//       // Primary: subscribes TurnOnRequested -> toggle.TurnOn(); then User -> toggle.StartOn(), Autostart ->
//       //          toggle.Start(); returns null (Program goes on to run the host and its tray icon).
//       // Not primary: never starts the toggle (no layer, no data client, no settings write) and returns 0 (the exit
//       //          code). User -> channel.SendTurnOn() once. Autostart -> no signal (a sign-in launch never changes
//       //          a running wallpaper).
//       public int? Run(LaunchKind kind);
//   }
public class SecondStartTests
{
    private const string Exe = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.exe";

    // ---- fakes: one "user session" shared by the processes of a test ----

    private sealed class FakeSession
    {
        public FakeChannel? Primary { get; set; }
        public int TurnOnSignals { get; set; }
    }

    private sealed class FakeChannel(FakeSession session) : IInstanceChannel
    {
        public int AcquireCalls { get; private set; }
        public event Action? TurnOnRequested;

        public bool TryAcquirePrimary()
        {
            AcquireCalls++;
            if (session.Primary is not null) return false;
            session.Primary = this;
            return true;
        }

        public void SendTurnOn()
        {
            session.TurnOnSignals++;
            session.Primary?.TurnOnRequested?.Invoke();
        }
    }

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
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? Read() { Reads++; return Text; }
        public void Write(string text) { Writes++; Text = text; }
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

    private sealed record Process(FakeChannel Channel, FakeControl Control, WallpaperToggle Toggle, int? ExitCode);

    /// <summary>Starts one process of the app in <paramref name="session"/>, with the settings in <paramref name="store"/>.</summary>
    private static Process Launch_(FakeSession session, FakeStore store, LaunchKind kind)
    {
        var channel = new FakeChannel(session);
        var control = new FakeControl();
        var toggle = new WallpaperToggle(store, control);
        var exit = new SingleInstanceStartup(channel, toggle).Run(kind);
        return new Process(channel, control, toggle, exit);
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

    // ---- (a) an instance is already running ----

    [Fact]
    public void Second_start_turns_the_running_wallpaper_on_and_exits_0()
    {
        var session = new FakeSession();
        var store = StoreWithOff();
        var running = Launch_(session, store, LaunchKind.Autostart);   // signed in with the wallpaper off
        Assert.Equal(WallpaperState.Off, running.Toggle.State);
        Assert.Empty(running.Control.Calls);

        var second = Launch_(session, store, LaunchKind.User);         // the owner clicks the shortcut

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, session.TurnOnSignals);
        Assert.Equal(WallpaperState.On, running.Toggle.State);
        Assert.Equal(1, running.Control.Count("attach"));
        Assert.Equal(1, running.Control.Count("start-data"));
        Assert.Equal(1, running.Control.Count("frame"));
        Assert.Equal(0, running.Control.Count("detach"));
    }

    [Fact]
    public void Second_start_does_not_start_a_second_wallpaper()
    {
        var session = new FakeSession();
        var store = new FakeStore();
        Launch_(session, store, LaunchKind.User);
        var writesBefore = store.Writes;

        var second = Launch_(session, store, LaunchKind.User);

        Assert.Equal(0, second.ExitCode);
        Assert.Empty(second.Control.Calls);   // no layer, no data client, no frame in the second process
        Assert.Equal(1, second.Channel.AcquireCalls);
        Assert.Equal(writesBefore, store.Writes);
    }

    [Fact]
    public void Second_start_while_on_stays_on_without_a_frame()
    {
        var session = new FakeSession();
        var store = new FakeStore();
        var running = Launch_(session, store, LaunchKind.User);
        Assert.Equal(WallpaperState.On, running.Toggle.State);
        running.Control.Calls.Clear();

        var second = Launch_(session, store, LaunchKind.User);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(WallpaperState.On, running.Toggle.State);
        Assert.Empty(running.Control.Calls);   // On stays On: no re-attach, no reload, no frame
    }

    [Fact]
    public void The_running_instance_answers_every_second_start()
    {
        var session = new FakeSession();
        var store = new FakeStore();
        var running = Launch_(session, store, LaunchKind.User);

        running.Toggle.TurnOff();                        // tray: Turn wallpaper off
        Launch_(session, store, LaunchKind.User);        // shortcut: on again
        Assert.Equal(WallpaperState.On, running.Toggle.State);

        running.Toggle.TurnOff();
        running.Control.Calls.Clear();
        Launch_(session, store, LaunchKind.User);

        Assert.Equal(WallpaperState.On, running.Toggle.State);
        Assert.Equal(1, running.Control.Count("attach"));
        Assert.Equal(1, running.Control.Count("frame"));
        Assert.Equal(2, session.TurnOnSignals);
    }

    [Fact]
    public void Autostart_launch_while_running_exits_0_without_a_signal()
    {
        var session = new FakeSession();
        var store = StoreWithOff();
        var running = Launch_(session, store, LaunchKind.Autostart);

        var second = Launch_(session, store, LaunchKind.Autostart);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(0, session.TurnOnSignals);
        Assert.Equal(WallpaperState.Off, running.Toggle.State);
        Assert.Empty(running.Control.Calls);
        Assert.Empty(second.Control.Calls);
    }

    // ---- (b) no instance running: a start by the user turns the wallpaper on ----

    [Fact]
    public void User_start_with_off_persisted_starts_on()
    {
        var process = Launch_(new FakeSession(), StoreWithOff(), LaunchKind.User);

        Assert.Null(process.ExitCode);   // primary: Program goes on to run the host
        Assert.Equal(WallpaperState.On, process.Toggle.State);
        Assert.Equal(1, process.Control.Count("attach"));
        Assert.Equal(1, process.Control.Count("start-data"));
        Assert.Equal(1, process.Control.Count("frame"));
        Assert.True(process.Control.Calls.IndexOf("attach") < process.Control.Calls.IndexOf("frame"),
            string.Join(",", process.Control.Calls));
    }

    [Fact]
    public void User_start_with_nothing_persisted_starts_on()
    {
        var process = Launch_(new FakeSession(), new FakeStore(), LaunchKind.User);

        Assert.Null(process.ExitCode);
        Assert.Equal(WallpaperState.On, process.Toggle.State);
        Assert.Equal(1, process.Control.Count("attach"));
        Assert.Equal(1, process.Control.Count("frame"));
    }

    [Fact]
    public void User_start_saves_on_so_the_next_sign_in_starts_on()
    {
        var store = StoreWithOff();
        Launch_(new FakeSession(), store, LaunchKind.User);

        var nextSignIn = Launch_(new FakeSession(), store, LaunchKind.Autostart);   // a new session after a restart

        Assert.Equal(WallpaperState.On, nextSignIn.Toggle.State);
        Assert.Equal(1, nextSignIn.Control.Count("attach"));
    }

    [Fact]
    public void StartOn_ignores_a_persisted_off()
    {
        var control = new FakeControl();
        var toggle = new WallpaperToggle(StoreWithOff(), control);

        toggle.StartOn();

        Assert.Equal(WallpaperState.On, toggle.State);
        Assert.Equal(1, control.Count("attach"));
        Assert.Equal(1, control.Count("start-data"));
        Assert.Equal(1, control.Count("frame"));
        Assert.Equal(0, control.Count("detach"));
    }

    // ---- (c) the launch at sign-in keeps the persisted state ----

    [Fact]
    public void Autostart_launch_keeps_a_persisted_off()
    {
        var process = Launch_(new FakeSession(), StoreWithOff(), LaunchKind.Autostart);

        Assert.Null(process.ExitCode);   // still the primary: the tray icon is there to turn it on
        Assert.Equal(WallpaperState.Off, process.Toggle.State);
        Assert.Empty(process.Control.Calls);
    }

    [Fact]
    public void Autostart_launch_keeps_a_persisted_on()
    {
        var process = Launch_(new FakeSession(), new FakeStore(), LaunchKind.Autostart);

        Assert.Null(process.ExitCode);
        Assert.Equal(WallpaperState.On, process.Toggle.State);
        Assert.Equal(1, process.Control.Count("attach"));
        Assert.Equal(1, process.Control.Count("frame"));
    }

    // ---- telling the launches apart ----

    [Fact]
    public void No_arguments_is_a_user_start()
    {
        Assert.Equal(LaunchKind.User, Launch.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void The_autostart_argument_is_an_autostart_launch()
    {
        Assert.Equal(LaunchKind.Autostart, Launch.Parse(new[] { "--autostart" }));
    }

    [Theory]
    [InlineData("--veloapp-firstrun")]
    [InlineData("--something-else")]
    [InlineData("autostart")]
    public void Other_arguments_are_a_user_start(string argument)
    {
        Assert.Equal(LaunchKind.User, Launch.Parse(new[] { argument }));
    }

    [Fact]
    public void The_command_line_in_the_Run_value_is_an_autostart_launch()
    {
        var runKey = new FakeRunKey();
        new Autostart(runKey, new FakeMarker(), Exe).Enable();
        var value = runKey.GetValue(Autostart.ValueName)!;

        // What Windows passes to Main: everything after the quoted exe path, split on spaces.
        Assert.StartsWith("\"" + Exe + "\"", value);
        var args = value["\"".Length..][(Exe.Length + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(LaunchKind.Autostart, Launch.Parse(args));
    }
}
