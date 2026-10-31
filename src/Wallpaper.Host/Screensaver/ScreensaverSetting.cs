using System.Text.Json;

namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// "Use as screensaver" (D-21): on makes our .scr the current user's Windows screensaver (HKCU, the timeout stays the
/// Windows setting), off and uninstall put the previous one back. The previous SCRNSAVE.EXE and ScreenSaveActive go to
/// their own file (settings.json is rewritten whole by WallpaperToggle); in it a JSON null is an absent value, kept
/// apart from an empty string. No member throws: a registry or file failure is logged and swallowed.
/// </summary>
/// <param name="savedFilePath">Where the previous values are kept; the folder is created when missing.</param>
/// <param name="scrPath">Our BitcoinWallpaper.scr.</param>
/// <param name="log">One line per failure.</param>
/// <param name="ensureScr">Runs before turning on writes anything (production: copies the .scr when missing); a
/// throw stops the turn-on and is logged.</param>
public sealed class ScreensaverSetting(
    IScreensaverRegistry registry,
    string savedFilePath,
    string scrPath,
    Action<string> log,
    Action? ensureScr = null)
{
    public const string ScrValueName = "SCRNSAVE.EXE";
    public const string ActiveValueName = "ScreenSaveActive";
    public const string TimeOutValueName = "ScreenSaveTimeOut";

    private const string VersionProperty = "version";

    public static string DefaultSavedFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "screensaver.json");

    /// <summary>The check mark: SCRNSAVE.EXE is our .scr (case-insensitive).</summary>
    public bool IsOn
    {
        get
        {
            try
            {
                return IsOurs(registry.GetValue(ScrValueName));
            }
            catch (Exception e)
            {
                log($"screensaver: cannot read {ScrValueName}: {e.Message}");
                return false;
            }
        }
    }

    public void TurnOn()
    {
        try
        {
            var current = registry.GetValue(ScrValueName);
            if (IsOurs(current))
            {
                return;
            }

            var active = registry.GetValue(ActiveValueName);
            ensureScr?.Invoke();
            Save(current, active);
            registry.SetValue(ScrValueName, scrPath);
            registry.SetValue(ActiveValueName, "1");
        }
        catch (Exception e)
        {
            log($"screensaver: turning on failed: {e.Message}");
        }
    }

    public void TurnOff()
    {
        try
        {
            if (!IsOurs(registry.GetValue(ScrValueName)))
            {
                return;   // another screensaver (or none) was picked in Windows: it stays
            }

            if (Load() is { } saved)
            {
                Restore(ScrValueName, saved.Scr);
                Restore(ActiveValueName, saved.Active);
            }
            else
            {
                // No usable saved values: never leave Windows pointing at our .scr.
                log($"screensaver: no usable saved values in {savedFilePath}; {ScrValueName} is removed");
                registry.DeleteValue(ScrValueName);
            }

            if (File.Exists(savedFilePath))
            {
                File.Delete(savedFilePath);
            }
        }
        catch (Exception e)
        {
            log($"screensaver: turning off failed: {e.Message}");
        }
    }

    public void Toggle()
    {
        if (IsOn)
        {
            TurnOff();
        }
        else
        {
            TurnOn();
        }
    }

    public void OnUninstall() => TurnOff();

    private bool IsOurs(string? value) => string.Equals(value, scrPath, StringComparison.OrdinalIgnoreCase);

    private void Restore(string name, string? value)
    {
        if (value is null)
        {
            registry.DeleteValue(name);
        }
        else
        {
            registry.SetValue(name, value);
        }
    }

    private void Save(string? scr, string? active)
    {
        var folder = Path.GetDirectoryName(savedFilePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber(VersionProperty, 1);
            json.WriteString(ScrValueName, scr);
            json.WriteString(ActiveValueName, active);
            json.WriteEndObject();
        }

        File.WriteAllBytes(savedFilePath, stream.ToArray());
    }

    /// <summary>The saved values; null when the file is missing or not ours (a failure is logged).</summary>
    private (string? Scr, string? Active)? Load()
    {
        try
        {
            if (!File.Exists(savedFilePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(savedFilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(VersionProperty, out _)
                || !TryRead(root, ScrValueName, out var scr)
                || !TryRead(root, ActiveValueName, out var active))
            {
                log($"screensaver: {savedFilePath} does not hold the saved values");
                return null;
            }

            return (scr, active);
        }
        catch (Exception e)
        {
            log($"screensaver: cannot read {savedFilePath}: {e.Message}");
            return null;
        }
    }

    private static bool TryRead(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            default:
                return false;
        }
    }
}
