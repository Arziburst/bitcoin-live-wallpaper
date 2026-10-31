using BitcoinWallpaper.Host.Install;

namespace BitcoinWallpaper.Host.Settings;

/// <summary>Raw settings text. Production: <see cref="FileSettingsStore"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Null when there is no file; may throw IOException.</summary>
    string? Read();

    /// <summary>Replaces the whole text; may throw IOException.</summary>
    void Write(string text);
}

/// <summary>The per-user settings file, %LOCALAPPDATA%\BitcoinWallpaper\settings.json by default.</summary>
public sealed class FileSettingsStore(string path) : ISettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(DataFolder.Path, "settings.json");

    public string? Read() => File.Exists(path) ? File.ReadAllText(path) : null;

    public void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
