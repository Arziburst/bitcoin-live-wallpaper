using System.Reflection;
using BitcoinWallpaper.Host.Install;

namespace BitcoinWallpaper.Host.Tests;

// Contract #9, criterion 2: autostart logic (pure, tested against a fake registry). The app writes
// HKCU\Software\Microsoft\Windows\CurrentVersion\Run "BitcoinWallpaper" = the quoted current exe path on first
// run and on a tray toggle, removes it on toggle-off and in the uninstall hook, updates a stale path after an
// update, and never touches HKLM.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Install, src/Wallpaper.Host/Install/.
//
//   // The per-user Run key and nothing else. Implementations address exactly
//   // HKCU\Software\Microsoft\Windows\CurrentVersion\Run; there is no way to give them another hive or key.
//   public interface IRegistryRunKey
//   {
//       string? GetValue(string name);              // null when the value does not exist
//       void SetValue(string name, string value);   // REG_SZ, creates or overwrites
//       void DeleteValue(string name);              // no-op when the value does not exist
//   }
//
//   // Production IRegistryRunKey over Registry.CurrentUser.OpenSubKey(Autostart.RunSubKey, writable).
//   // Exactly one public constructor and it takes no parameters (no hive, no key path can be passed in).
//   // The tests only inspect it by reflection: they never construct it or touch the real registry.
//   public sealed class RegistryRunKey : IRegistryRunKey { public RegistryRunKey(); ... }
//
//   // "First run done" marker (production: a file in the app's per-user data folder). Survives restarts.
//   public interface IFirstRunMarker
//   {
//       bool IsSet { get; }
//       void Set();
//       void Clear();     // no-op when not set
//   }
//
//   public sealed class Autostart
//   {
//       public const string ValueName  = "BitcoinWallpaper";
//       public const string RunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
//       public const string RunSubKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";   // under HKCU
//
//       public Autostart(IRegistryRunKey runKey, IFirstRunMarker marker, string exePath);
//
//       public bool IsEnabled { get; }    // the ValueName value exists in the Run key (whatever path it holds)
//       public void Enable();             // tray toggle on: ValueName = "\"" + exePath + "\""
//       public void Disable();            // tray toggle off: deletes ValueName; no-op when missing
//       public void EnsureCurrentPath();  // value present but not the quoted current exe path -> rewrite it;
//                                         // value present and already current -> no write; absent -> stays absent
//       public void OnStartup();          // every app start. Marker not set (first run): Enable() then marker.Set().
//                                         // Marker set: EnsureCurrentPath() only (a user's Disable is respected).
//       public void OnUninstall();        // Velopack uninstall hook: deletes ValueName and clears the marker.
//   }
//
// Rules the tests rely on:
// - Autostart touches only the ValueName value of the Run key: other programs' values are never written or removed.
// - The value is the exe path wrapped in double quotes, nothing else (no arguments).
// - Nothing under src/Wallpaper.Host/Install/ mentions HKLM / HKEY_LOCAL_MACHINE / LocalMachine.
public class AutostartTests
{
    private const string ExeV1 = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.exe";
    private const string ExeV2 = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\app-1.2.0\BitcoinWallpaper.exe";
    private const string OtherName = "OneDrive";
    private const string OtherValue = "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background";

    private static string Quoted(string path) => "\"" + path + "\"";

    private sealed class FakeRunKey : IRegistryRunKey
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Writes { get; } = new();
        public List<string> Deletes { get; } = new();

        public string? GetValue(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public void SetValue(string name, string value)
        {
            Writes.Add(name);
            Values[name] = value;
        }

        public void DeleteValue(string name)
        {
            Deletes.Add(name);
            Values.Remove(name);
        }
    }

    private sealed class FakeMarker : IFirstRunMarker
    {
        public bool IsSet { get; private set; }
        public void Set() => IsSet = true;
        public void Clear() => IsSet = false;
    }

    private static FakeRunKey KeyWithNeighbour()
    {
        var key = new FakeRunKey();
        key.Values[OtherName] = OtherValue;
        return key;
    }

    // ---- Enable / IsEnabled / Disable (the tray toggle) ----

    [Fact]
    public void Enable_writes_the_quoted_exe_path_under_BitcoinWallpaper()
    {
        var key = KeyWithNeighbour();
        var autostart = new Autostart(key, new FakeMarker(), ExeV1);

        autostart.Enable();

        Assert.Equal(Quoted(ExeV1), key.GetValue("BitcoinWallpaper"));
        Assert.True(autostart.IsEnabled);
        Assert.Equal(OtherValue, key.GetValue(OtherName));
    }

    [Fact]
    public void ValueName_is_BitcoinWallpaper()
    {
        Assert.Equal("BitcoinWallpaper", Autostart.ValueName);
    }

    [Fact]
    public void IsEnabled_reads_the_run_key()
    {
        var key = KeyWithNeighbour();
        var autostart = new Autostart(key, new FakeMarker(), ExeV1);

        Assert.False(autostart.IsEnabled);

        key.Values["BitcoinWallpaper"] = Quoted(ExeV1);   // written by someone else (an earlier run)
        Assert.True(autostart.IsEnabled);

        key.Values.Remove("BitcoinWallpaper");
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void IsEnabled_is_not_fooled_by_other_programs_values()
    {
        var key = KeyWithNeighbour();
        var autostart = new Autostart(key, new FakeMarker(), ExeV1);

        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void Disable_removes_the_value_and_leaves_the_neighbours()
    {
        var key = KeyWithNeighbour();
        var autostart = new Autostart(key, new FakeMarker(), ExeV1);
        autostart.Enable();

        autostart.Disable();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.False(autostart.IsEnabled);
        Assert.Equal(OtherValue, key.GetValue(OtherName));
        Assert.DoesNotContain(OtherName, key.Deletes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disable_when_missing_is_a_no_op()
    {
        var key = KeyWithNeighbour();
        var autostart = new Autostart(key, new FakeMarker(), ExeV1);

        var ex = Record.Exception(() => autostart.Disable());

        Assert.Null(ex);
        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.Empty(key.Writes);
        Assert.Equal(OtherValue, key.GetValue(OtherName));
        Assert.Single(key.Values);
    }

    [Fact]
    public void Toggle_on_off_on_ends_enabled_with_the_current_path()
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, new FakeMarker(), ExeV1);

        autostart.Enable();
        autostart.Disable();
        autostart.Enable();

        Assert.True(autostart.IsEnabled);
        Assert.Equal(Quoted(ExeV1), key.GetValue("BitcoinWallpaper"));
    }

    // ---- EnsureCurrentPath (after an update the exe path changes) ----

    [Fact]
    public void EnsureCurrentPath_rewrites_a_stale_path()
    {
        var key = KeyWithNeighbour();
        key.Values["BitcoinWallpaper"] = Quoted(ExeV1);
        var autostart = new Autostart(key, new FakeMarker(), ExeV2);

        autostart.EnsureCurrentPath();

        Assert.Equal(Quoted(ExeV2), key.GetValue("BitcoinWallpaper"));
        Assert.Equal(OtherValue, key.GetValue(OtherName));
    }

    [Fact]
    public void EnsureCurrentPath_fixes_an_unquoted_value()
    {
        var key = new FakeRunKey();
        key.Values["BitcoinWallpaper"] = ExeV2;   // right path but not quoted: breaks on the space in the user name
        var autostart = new Autostart(key, new FakeMarker(), ExeV2);

        autostart.EnsureCurrentPath();

        Assert.Equal(Quoted(ExeV2), key.GetValue("BitcoinWallpaper"));
    }

    [Fact]
    public void EnsureCurrentPath_leaves_a_disabled_state_disabled()
    {
        var key = KeyWithNeighbour();
        var autostart = new Autostart(key, new FakeMarker(), ExeV2);

        autostart.EnsureCurrentPath();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.False(autostart.IsEnabled);
        Assert.Empty(key.Writes);
    }

    [Fact]
    public void EnsureCurrentPath_does_not_write_when_the_path_is_current()
    {
        var key = new FakeRunKey();
        key.Values["BitcoinWallpaper"] = Quoted(ExeV2);
        var autostart = new Autostart(key, new FakeMarker(), ExeV2);

        autostart.EnsureCurrentPath();

        Assert.Equal(Quoted(ExeV2), key.GetValue("BitcoinWallpaper"));
        Assert.Empty(key.Writes);
    }

    // ---- OnStartup: first-run default is enabled, once ----

    [Fact]
    public void First_run_enables_autostart_and_sets_the_marker()
    {
        var key = KeyWithNeighbour();
        var marker = new FakeMarker();
        var autostart = new Autostart(key, marker, ExeV1);

        autostart.OnStartup();

        Assert.Equal(Quoted(ExeV1), key.GetValue("BitcoinWallpaper"));
        Assert.True(autostart.IsEnabled);
        Assert.True(marker.IsSet);
        Assert.Equal(OtherValue, key.GetValue(OtherName));
    }

    [Fact]
    public void A_users_Disable_is_respected_across_restarts()
    {
        var key = new FakeRunKey();
        var marker = new FakeMarker();

        var firstSession = new Autostart(key, marker, ExeV1);
        firstSession.OnStartup();
        firstSession.Disable();              // the owner unticks "Start with Windows" in the tray

        var secondSession = new Autostart(key, marker, ExeV1);   // next sign-in / restart
        secondSession.OnStartup();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.False(secondSession.IsEnabled);

        var thirdSession = new Autostart(key, marker, ExeV2);    // and after an update
        thirdSession.OnStartup();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.False(thirdSession.IsEnabled);
    }

    [Fact]
    public void Startup_after_an_update_rewrites_the_stale_path()
    {
        var key = new FakeRunKey();
        var marker = new FakeMarker();
        new Autostart(key, marker, ExeV1).OnStartup();
        Assert.Equal(Quoted(ExeV1), key.GetValue("BitcoinWallpaper"));

        var updated = new Autostart(key, marker, ExeV2);
        updated.OnStartup();

        Assert.Equal(Quoted(ExeV2), key.GetValue("BitcoinWallpaper"));
        Assert.True(updated.IsEnabled);
        Assert.True(marker.IsSet);
    }

    [Fact]
    public void Later_startups_do_not_rewrite_a_current_value()
    {
        var key = new FakeRunKey();
        var marker = new FakeMarker();
        new Autostart(key, marker, ExeV1).OnStartup();
        key.Writes.Clear();

        new Autostart(key, marker, ExeV1).OnStartup();

        Assert.Empty(key.Writes);
        Assert.Equal(Quoted(ExeV1), key.GetValue("BitcoinWallpaper"));
    }

    [Fact]
    public void Startup_with_the_marker_set_does_not_enable()
    {
        var key = new FakeRunKey();
        var marker = new FakeMarker();
        marker.Set();

        var autostart = new Autostart(key, marker, ExeV1);
        autostart.OnStartup();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.Empty(key.Writes);
    }

    // ---- Uninstall hook ----

    [Fact]
    public void Uninstall_hook_removes_the_value_and_clears_the_marker()
    {
        var key = KeyWithNeighbour();
        var marker = new FakeMarker();
        var autostart = new Autostart(key, marker, ExeV1);
        autostart.OnStartup();

        autostart.OnUninstall();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
        Assert.False(autostart.IsEnabled);
        Assert.False(marker.IsSet);
        Assert.Equal(OtherValue, key.GetValue(OtherName));
        Assert.DoesNotContain(OtherName, key.Deletes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstall_hook_removes_a_stale_value_too()
    {
        var key = new FakeRunKey();
        key.Values["BitcoinWallpaper"] = Quoted(ExeV1);
        var marker = new FakeMarker();
        marker.Set();

        new Autostart(key, marker, ExeV2).OnUninstall();

        Assert.Null(key.GetValue("BitcoinWallpaper"));
    }

    [Fact]
    public void Uninstall_hook_when_already_disabled_is_a_no_op_on_the_registry()
    {
        var key = KeyWithNeighbour();
        var marker = new FakeMarker();
        marker.Set();

        var ex = Record.Exception(() => new Autostart(key, marker, ExeV1).OnUninstall());

        Assert.Null(ex);
        Assert.Empty(key.Writes);
        Assert.Equal(OtherValue, key.GetValue(OtherName));
        Assert.Single(key.Values);
    }

    // ---- HKCU only, never HKLM ----

    [Fact]
    public void Run_key_path_is_exactly_the_per_user_Run_key()
    {
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", Autostart.RunKeyPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.RunSubKey);
        Assert.Equal(@"HKCU\" + Autostart.RunSubKey, Autostart.RunKeyPath);
    }

    [Fact]
    public void Run_key_constants_never_name_HKLM()
    {
        var constants = typeof(Autostart)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Contains(Autostart.RunKeyPath, constants);
        foreach (var value in constants)
        {
            Assert.DoesNotContain("HKLM", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HKEY_LOCAL_MACHINE", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LocalMachine", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Production_run_key_cannot_be_pointed_at_another_hive_or_key()
    {
        // Inspected by reflection only: never constructed, so the real registry is not touched.
        var type = typeof(RegistryRunKey);

        Assert.True(typeof(IRegistryRunKey).IsAssignableFrom(type));
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = Assert.Single(ctors);
        Assert.Empty(ctor.GetParameters());
    }

    [Fact]
    public void Install_sources_never_mention_HKLM()
    {
        var installDir = Path.Combine(RepoRoot(), "src", "Wallpaper.Host", "Install");
        Assert.True(Directory.Exists(installDir), $"missing {installDir}");

        var files = Directory.GetFiles(installDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var forbidden = new[] { "HKLM", "HKEY_LOCAL_MACHINE", "LocalMachine" };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(file)} mentions {word}");
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BitcoinWallpaper.sln")))
                return dir.FullName;
        throw new InvalidOperationException("BitcoinWallpaper.sln not found above " + AppContext.BaseDirectory);
    }
}
