using System.Text.Json;

namespace BitcoinWallpaper.Host.LockScreenChart;

/// <summary>
/// The tray check mark "Show chart on lock screen" (D-34), default on. It lives in its own file
/// (lockscreen.json, <c>{"lockScreen": false}</c>) because settings.json is rewritten whole by WallpaperToggle. A
/// missing, empty, corrupt or unreadable file means on. No member throws: a failure is logged, and a value that could
/// not be written holds in memory for this instance.
/// </summary>
/// <param name="changed">Called after <see cref="Set"/> changed the value, with the new value.</param>
public sealed class LockScreenSetting(string filePath, Action<string> log, Action<bool>? changed = null)
{
    private const string Property = "lockScreen";

    /// <summary>A value whose write failed; it wins over the file for this instance.</summary>
    private bool? _unsaved;

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "lockscreen.json");

    /// <summary>Read afresh from the file on each call.</summary>
    public bool IsOn => _unsaved ?? Read();

    public void Set(bool on)
    {
        if (IsOn == on)
        {
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(new Dictionary<string, bool> { [Property] = on }));
            _unsaved = null;
        }
        catch (Exception e)
        {
            log($"lock screen: cannot save the setting to {filePath} (kept until exit): {e.Message}");
            _unsaved = on;
        }

        changed?.Invoke(on);
    }

    public void Toggle() => Set(!IsOn);

    private bool Read()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return true;
            }

            var text = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind != JsonValueKind.Object
                   || !document.RootElement.TryGetProperty(Property, out var value)
                   || value.ValueKind != JsonValueKind.False;
        }
        catch (Exception e)
        {
            log($"lock screen: cannot read the setting {filePath}, taken as on: {e.Message}");
            return true;
        }
    }
}
