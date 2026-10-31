using System.Text.Json;
using BitcoinWallpaper.Host.Screensaver;

namespace BitcoinWallpaper.Host.Tests;

// Contract 26.1 (D-21, issue #26), criterion 3: the HKCU model of "Use as screensaver", with a fake registry and a temp
// folder for the saved file. A test never touches the real registry.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Screensaver, src/Wallpaper.Host/Screensaver/
// (IScreensaverRegistry.cs, ScreensaverSetting.cs), public types. No Win32 in ScreensaverSetting.
//
//   // The string values of HKCU\Control Panel\Desktop (production: a thin Microsoft.Win32 adapter, which also tells
//   // Windows about the change, e.g. SPI_SETSCREENSAVEACTIVE with SPIF_SENDCHANGE; never HKLM). All three may throw.
//   public interface IScreensaverRegistry
//   {
//       string? GetValue(string name);              // null = the value is ABSENT; "" = present and empty (a different thing)
//       void SetValue(string name, string value);   // REG_SZ
//       void DeleteValue(string name);              // no-op when absent
//   }
//
//   public sealed class ScreensaverSetting
//   {
//       public const string ScrValueName = "SCRNSAVE.EXE";
//       public const string ActiveValueName = "ScreenSaveActive";
//       public const string TimeOutValueName = "ScreenSaveTimeOut";   // NEVER written, read or deleted by this class
//
//       // %LOCALAPPDATA%\BitcoinWallpaper\screensaver.json, i.e.
//       // Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "screensaver.json")
//       public static string DefaultSavedFilePath { get; }
//
//       // savedFilePath: where the previous values are kept (valid JSON, the format is the worker's; the folder is
//       //   created when missing). scrPath: our BitcoinWallpaper.scr. log: a failure line (never null).
//       public ScreensaverSetting(IScreensaverRegistry registry, string savedFilePath, string scrPath, Action<string> log);
//
//       // The check mark: SCRNSAVE.EXE equals scrPath, compared case-insensitively. Absent, empty, another path, or
//       // a registry that throws: false (a throw is logged).
//       public bool IsOn { get; }
//
//       // When SCRNSAVE.EXE is NOT already scrPath: save the previous SCRNSAVE.EXE and ScreenSaveActive (absent kept
//       // apart from empty) to savedFilePath, then SetValue(SCRNSAVE.EXE, scrPath) and SetValue(ScreenSaveActive, "1").
//       // Already scrPath: nothing is written anywhere (the first saved values stay). Never touches ScreenSaveTimeOut.
//       public void TurnOn();
//
//       // When SCRNSAVE.EXE is scrPath: restore the saved SCRNSAVE.EXE and ScreenSaveActive (a value saved as absent is
//       // DeleteValue'd, one saved as empty is set to ""), then delete savedFilePath. When SCRNSAVE.EXE is not scrPath
//       // (the user picked another screensaver in Windows): no registry write of any kind. Never touches
//       // ScreenSaveTimeOut. The saved file is read from disk each time (another process may have written it).
//       public void TurnOff();
//
//       public void Toggle();       // IsOn ? TurnOff() : TurnOn()
//       public void OnUninstall();  // does exactly what TurnOff does
//
//       // None of the five members above throws: a registry or file failure (IOException, UnauthorizedAccessException,
//       // SecurityException, a corrupt saved file, ...) is passed to log as one or more lines and swallowed.
//   }

/// <summary>An in-memory HKCU\Control Panel\Desktop: records every write and can be told to fail.</summary>
internal sealed class FakeScreensaverRegistry : IScreensaverRegistry
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"set:NAME=value" and "delete:NAME", in order, for every write attempted (also a failing one).</summary>
    public List<string> Writes { get; } = new();

    public bool ThrowOnGet { get; set; }
    public bool ThrowOnSet { get; set; }
    public bool ThrowOnDelete { get; set; }

    public string? GetValue(string name)
    {
        if (ThrowOnGet) throw new IOException("registry read denied");
        return Values.TryGetValue(name, out var v) ? v : null;
    }

    public void SetValue(string name, string value)
    {
        Writes.Add($"set:{name}={value}");
        if (ThrowOnSet) throw new UnauthorizedAccessException("registry write denied");
        Values[name] = value;
    }

    public void DeleteValue(string name)
    {
        Writes.Add($"delete:{name}");
        if (ThrowOnDelete) throw new UnauthorizedAccessException("registry delete denied");
        Values.Remove(name);
    }
}

public sealed class ScreensaverSettingTests : IDisposable
{
    private const string Scr = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.scr";
    private const string Ribbons = @"C:\Windows\System32\Ribbons.scr";
    private const string Bubbles = @"C:\Windows\System32\Bubbles.scr";
    private const string Scrn = ScreensaverSetting.ScrValueName;
    private const string Active = ScreensaverSetting.ActiveValueName;
    private const string TimeOut = ScreensaverSetting.TimeOutValueName;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bw-screensaver-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScreensaverRegistry _registry = new();
    private readonly List<string> _log = new();

    // The folder does not exist yet: the model creates it.
    private string SavedPath => Path.Combine(_root, "BitcoinWallpaper", "screensaver.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ScreensaverSetting Model() => new(_registry, SavedPath, Scr, _log.Add);

    private void Previous(string? scr, string? active)
    {
        if (scr is not null) _registry.Values[Scrn] = scr;
        if (active is not null) _registry.Values[Active] = active;
    }

    // ---- the saved file's place ----

    [Fact]
    public void The_saved_file_default_path_is_per_user_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "screensaver.json");

        Assert.Equal(expected, ScreensaverSetting.DefaultSavedFilePath);
    }

    [Fact]
    public void The_value_names_are_the_windows_ones()
    {
        Assert.Equal("SCRNSAVE.EXE", ScreensaverSetting.ScrValueName);
        Assert.Equal("ScreenSaveActive", ScreensaverSetting.ActiveValueName);
        Assert.Equal("ScreenSaveTimeOut", ScreensaverSetting.TimeOutValueName);
    }

    // ---- the check mark ----

    [Fact]
    public void Check_mark_is_off_when_the_value_is_absent_empty_or_another_screensaver()
    {
        var model = Model();
        Assert.False(model.IsOn);   // absent

        _registry.Values[Scrn] = "";
        Assert.False(model.IsOn);   // empty

        _registry.Values[Scrn] = Ribbons;
        Assert.False(model.IsOn);   // another screensaver

        _registry.Values[Scrn] = Scr + "x";
        Assert.False(model.IsOn);   // a different path that starts like ours
    }

    [Theory]
    [InlineData(Scr)]
    [InlineData(@"c:\users\owner name\appdata\local\bitcoinwallpaper\current\bitcoinwallpaper.scr")]
    [InlineData(@"C:\USERS\OWNER NAME\APPDATA\LOCAL\BITCOINWALLPAPER\CURRENT\BITCOINWALLPAPER.SCR")]
    public void Check_mark_is_on_exactly_while_the_value_is_our_scr_ignoring_case(string value)
    {
        _registry.Values[Scrn] = value;

        Assert.True(Model().IsOn);
    }

    [Fact]
    public void Check_mark_follows_changes_made_outside_the_model()
    {
        var model = Model();
        _registry.Values[Scrn] = Scr;
        Assert.True(model.IsOn);

        _registry.Values[Scrn] = Bubbles;   // the user picked another one in Windows
        Assert.False(model.IsOn);

        _registry.Values.Remove(Scrn);
        Assert.False(model.IsOn);
    }

    [Fact]
    public void Reading_the_check_mark_writes_nothing_and_never_throws()
    {
        var model = Model();
        _ = model.IsOn;
        Assert.Empty(_registry.Writes);
        Assert.False(File.Exists(SavedPath));

        _registry.ThrowOnGet = true;
        var ex = Record.Exception(() => model.IsOn);

        Assert.Null(ex);
        Assert.False(model.IsOn);
    }

    // ---- turning on ----

    [Fact]
    public void Turning_on_sets_our_scr_and_activates_and_the_check_mark_follows()
    {
        Previous(Ribbons, "0");
        var model = Model();

        model.TurnOn();

        Assert.Equal(Scr, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
        Assert.True(model.IsOn);
    }

    [Fact]
    public void Turning_on_saves_the_previous_values_to_the_json_file_creating_its_folder()
    {
        Previous(Ribbons, "1");
        Assert.False(Directory.Exists(Path.GetDirectoryName(SavedPath)));

        Model().TurnOn();

        Assert.True(File.Exists(SavedPath));
        var text = File.ReadAllText(SavedPath);
        using (JsonDocument.Parse(text)) { }   // valid JSON; the layout is the worker's
        Assert.Contains("Ribbons.scr", text);  // the previous value is in it (backslashes are escaped in JSON)
    }

    [Fact]
    public void Turning_on_from_nothing_set_at_all_works()
    {
        var model = Model();

        model.TurnOn();

        Assert.Equal(Scr, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
        Assert.True(File.Exists(SavedPath));
        Assert.Empty(_log);
    }

    [Fact]
    public void Turning_on_never_touches_the_timeout()
    {
        Previous(Ribbons, "1");
        _registry.Values[TimeOut] = "600";
        var model = Model();

        model.TurnOn();
        model.TurnOn();
        model.TurnOff();

        Assert.DoesNotContain(_registry.Writes, w => w.Contains(TimeOut, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("600", _registry.GetValue(TimeOut));
    }

    [Fact]
    public void Turning_on_when_the_timeout_is_absent_does_not_create_it()
    {
        Model().TurnOn();

        Assert.Null(_registry.GetValue(TimeOut));
        Assert.DoesNotContain(_registry.Writes, w => w.Contains(TimeOut, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Turning_on_writes_only_the_two_values()
    {
        Previous(Ribbons, "0");

        Model().TurnOn();

        Assert.All(_registry.Writes, w => Assert.True(
            w.StartsWith($"set:{Scrn}=", StringComparison.Ordinal) || w.StartsWith($"set:{Active}=", StringComparison.Ordinal), w));
        Assert.Equal(2, _registry.Writes.Count);
    }

    // ---- turning on again ----

    [Fact]
    public void Turning_on_again_changes_nothing()
    {
        Previous(Ribbons, "1");
        var model = Model();
        model.TurnOn();
        var writes = _registry.Writes.ToList();
        var saved = File.ReadAllText(SavedPath);
        var registry = _registry.Values.ToDictionary(p => p.Key, p => p.Value);

        model.TurnOn();
        Model().TurnOn();   // also from another instance (another process)

        Assert.Equal(writes, _registry.Writes);
        Assert.Equal(registry, _registry.Values);
        Assert.Equal(saved, File.ReadAllText(SavedPath));
    }

    [Fact]
    public void Turning_on_again_keeps_the_first_saved_values_for_the_restore()
    {
        Previous(Ribbons, "1");
        var model = Model();
        model.TurnOn();
        model.TurnOn();
        model.TurnOn();

        model.TurnOff();

        Assert.Equal(Ribbons, _registry.GetValue(Scrn));   // not our scr: the first values, not the ones set by us
        Assert.Equal("1", _registry.GetValue(Active));
    }

    [Fact]
    public void Turning_on_when_the_value_is_already_ours_writes_nothing()
    {
        _registry.Values[Scrn] = Scr.ToUpperInvariant();   // set by hand, in another case

        Model().TurnOn();

        Assert.Empty(_registry.Writes);
        Assert.False(File.Exists(SavedPath));
    }

    // ---- turning off ----

    [Theory]
    [InlineData(Ribbons, "1")]
    [InlineData(Ribbons, "0")]
    [InlineData(null, null)]      // both absent: both are deleted again
    [InlineData("", "")]          // both empty: both stay empty, not deleted
    [InlineData("", "0")]
    [InlineData(null, "1")]       // only ScreenSaveActive present
    [InlineData(null, "0")]
    [InlineData(Ribbons, null)]   // only SCRNSAVE.EXE present
    [InlineData("", null)]
    [InlineData(null, "")]
    public void Turning_off_restores_the_saved_values_absent_ones_deleted_empty_ones_kept(string? scr, string? active)
    {
        Previous(scr, active);
        var model = Model();
        model.TurnOn();
        Assert.True(model.IsOn);

        model.TurnOff();

        Assert.Equal(scr, _registry.GetValue(Scrn));
        Assert.Equal(active, _registry.GetValue(Active));
        Assert.Equal(scr is null, !_registry.Values.ContainsKey(Scrn));
        Assert.Equal(active is null, !_registry.Values.ContainsKey(Active));
        Assert.False(model.IsOn);
        Assert.False(File.Exists(SavedPath));
        Assert.Empty(_log);
    }

    [Fact]
    public void Turning_off_works_from_another_instance_the_values_are_on_disk()
    {
        Previous(Ribbons, "0");
        Model().TurnOn();

        Model().TurnOff();   // e.g. the uninstall hook, a different process

        Assert.Equal(Ribbons, _registry.GetValue(Scrn));
        Assert.Equal("0", _registry.GetValue(Active));
        Assert.False(File.Exists(SavedPath));
    }

    [Fact]
    public void Turning_off_leaves_every_other_value_alone()
    {
        Previous(Ribbons, "1");
        _registry.Values[TimeOut] = "300";
        _registry.Values["Wallpaper"] = @"C:\x\wall.jpg";
        var model = Model();
        model.TurnOn();

        model.TurnOff();

        Assert.Equal("300", _registry.GetValue(TimeOut));
        Assert.Equal(@"C:\x\wall.jpg", _registry.GetValue("Wallpaper"));
        Assert.Equal(4, _registry.Values.Count);
    }

    [Fact]
    public void Turning_off_then_on_then_off_saves_and_restores_each_time()
    {
        Previous(Ribbons, "1");
        var model = Model();

        model.TurnOn();
        model.TurnOff();
        Assert.Equal(Ribbons, _registry.GetValue(Scrn));

        model.TurnOn();
        Assert.True(File.Exists(SavedPath));
        Assert.True(model.IsOn);
        model.TurnOff();

        Assert.Equal(Ribbons, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
        Assert.False(File.Exists(SavedPath));
    }

    // ---- turning off when not ours ----

    [Fact]
    public void Turning_off_when_the_user_picked_another_screensaver_writes_nothing()
    {
        Previous(Ribbons, "1");
        var model = Model();
        model.TurnOn();
        _registry.Values[Scrn] = Bubbles;   // the user picks another one in Windows
        _registry.Writes.Clear();

        var ex = Record.Exception(() => model.TurnOff());

        Assert.Null(ex);
        Assert.Empty(_registry.Writes);
        Assert.Equal(Bubbles, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
    }

    [Fact]
    public void Turning_off_when_nothing_was_ever_turned_on_writes_nothing()
    {
        Previous(Ribbons, "1");

        Model().TurnOff();

        Assert.Empty(_registry.Writes);
        Assert.Equal(Ribbons, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
    }

    [Fact]
    public void Turning_off_with_no_screensaver_set_at_all_writes_nothing()
    {
        Model().TurnOff();

        Assert.Empty(_registry.Writes);
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void Turning_on_after_the_user_picked_another_saves_that_one_as_the_previous()
    {
        Previous(Ribbons, "1");
        var model = Model();
        model.TurnOn();
        _registry.Values[Scrn] = Bubbles;
        _registry.Values[Active] = "0";
        model.TurnOff();   // not ours: nothing happens

        model.TurnOn();    // not already ours: saves Bubbles / "0" now
        Assert.Equal(Scr, _registry.GetValue(Scrn));
        Assert.Equal("1", _registry.GetValue(Active));
        model.TurnOff();

        Assert.Equal(Bubbles, _registry.GetValue(Scrn));
        Assert.Equal("0", _registry.GetValue(Active));
    }

    // ---- toggle ----

    [Fact]
    public void Toggle_alternates_on_and_off()
    {
        Previous(Ribbons, "1");
        var model = Model();

        model.Toggle();
        Assert.True(model.IsOn);
        Assert.Equal(Scr, _registry.GetValue(Scrn));

        model.Toggle();
        Assert.False(model.IsOn);
        Assert.Equal(Ribbons, _registry.GetValue(Scrn));

        model.Toggle();
        Assert.True(model.IsOn);
    }

    [Fact]
    public void Toggle_with_another_screensaver_chosen_turns_ours_on()
    {
        Previous(Bubbles, "1");

        var model = Model();
        model.Toggle();

        Assert.True(model.IsOn);
        model.Toggle();
        Assert.Equal(Bubbles, _registry.GetValue(Scrn));
    }

    // ---- uninstall ----

    [Fact]
    public void Uninstall_restores_the_saved_values_and_removes_the_file()
    {
        Previous(null, "0");
        Model().TurnOn();

        Model().OnUninstall();

        Assert.Null(_registry.GetValue(Scrn));
        Assert.Equal("0", _registry.GetValue(Active));
        Assert.False(_registry.Values.ContainsKey(Scrn));
        Assert.False(File.Exists(SavedPath));
        Assert.Empty(_log);
    }

    [Fact]
    public void Uninstall_when_the_screensaver_is_not_ours_writes_nothing_to_the_registry()
    {
        Previous(Ribbons, "1");
        Model().TurnOn();
        _registry.Values[Scrn] = Bubbles;
        _registry.Writes.Clear();

        var ex = Record.Exception(() => Model().OnUninstall());

        Assert.Null(ex);
        Assert.Empty(_registry.Writes);
        Assert.Equal(Bubbles, _registry.GetValue(Scrn));
    }

    [Fact]
    public void Uninstall_when_never_turned_on_writes_nothing_and_does_not_throw()
    {
        Previous(Ribbons, "1");

        var ex = Record.Exception(() => Model().OnUninstall());

        Assert.Null(ex);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void Uninstall_never_touches_the_timeout()
    {
        Previous(Ribbons, "1");
        _registry.Values[TimeOut] = "900";
        Model().TurnOn();

        Model().OnUninstall();

        Assert.DoesNotContain(_registry.Writes, w => w.Contains(TimeOut, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("900", _registry.GetValue(TimeOut));
    }

    // ---- failures are logged, never thrown ----

    [Fact]
    public void A_registry_that_cannot_be_read_does_not_throw_from_turn_on_and_is_logged()
    {
        _registry.ThrowOnGet = true;

        var ex = Record.Exception(() => Model().TurnOn());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
        Assert.All(_log, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void A_registry_that_cannot_be_written_does_not_throw_from_turn_on_and_is_logged()
    {
        Previous(Ribbons, "1");
        _registry.ThrowOnSet = true;

        var ex = Record.Exception(() => Model().TurnOn());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
        Assert.Equal(Ribbons, _registry.GetValue(Scrn));   // nothing was written
    }

    [Fact]
    public void A_registry_that_cannot_be_written_does_not_throw_from_turn_off_and_is_logged()
    {
        Previous(Ribbons, "1");
        var model = Model();
        model.TurnOn();
        _registry.ThrowOnSet = true;

        var ex = Record.Exception(() => model.TurnOff());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
    }

    [Fact]
    public void A_registry_that_cannot_delete_does_not_throw_from_turn_off_and_is_logged()
    {
        var model = Model();   // nothing set before: turning off has to delete both values again
        model.TurnOn();
        _registry.ThrowOnDelete = true;

        var ex = Record.Exception(() => model.TurnOff());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
    }

    [Fact]
    public void A_registry_that_cannot_be_read_does_not_throw_from_turn_off_toggle_and_uninstall()
    {
        Previous(Ribbons, "1");
        var model = Model();
        model.TurnOn();
        _registry.ThrowOnGet = true;

        Assert.Null(Record.Exception(() => model.TurnOff()));
        Assert.Null(Record.Exception(() => model.Toggle()));
        Assert.Null(Record.Exception(() => model.OnUninstall()));
        Assert.NotEmpty(_log);
    }

    [Fact]
    public void A_failing_registry_does_not_throw_into_the_uninstall()
    {
        Previous(Ribbons, "1");
        Model().TurnOn();
        _registry.ThrowOnSet = true;
        _registry.ThrowOnDelete = true;

        var ex = Record.Exception(() => Model().OnUninstall());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
    }

    [Fact]
    public void A_saved_file_that_cannot_be_written_does_not_throw_and_is_logged()
    {
        Previous(Ribbons, "1");
        Directory.CreateDirectory(SavedPath);   // a folder where the file should be: writing it fails

        var ex = Record.Exception(() => Model().TurnOn());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
    }

    [Fact]
    public void A_saved_file_in_an_impossible_place_does_not_throw_and_is_logged()
    {
        Previous(Ribbons, "1");
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");
        var model = new ScreensaverSetting(_registry, Path.Combine(blocker, "screensaver.json"), Scr, _log.Add);

        var ex = Record.Exception(() => model.TurnOn());

        Assert.Null(ex);
        Assert.NotEmpty(_log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"unrelated\":1}")]
    public void A_corrupt_saved_file_does_not_throw_from_turn_off_or_uninstall(string text)
    {
        _registry.Values[Scrn] = Scr;   // ours, so the restore is tried
        Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
        File.WriteAllText(SavedPath, text);

        Assert.Null(Record.Exception(() => Model().TurnOff()));
        Assert.Null(Record.Exception(() => Model().OnUninstall()));
    }

    [Fact]
    public void A_missing_saved_file_does_not_throw_from_turn_off()
    {
        _registry.Values[Scrn] = Scr;   // ours, but the saved file is gone

        Assert.Null(Record.Exception(() => Model().TurnOff()));
        Assert.Null(Record.Exception(() => Model().OnUninstall()));
    }
}
