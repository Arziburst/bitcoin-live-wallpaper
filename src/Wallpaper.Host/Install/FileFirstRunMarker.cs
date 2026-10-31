namespace BitcoinWallpaper.Host.Install;

/// <summary>The first-run marker as an empty file in the per-user data folder.</summary>
public sealed class FileFirstRunMarker(string path) : IFirstRunMarker
{
    /// <summary>%LOCALAPPDATA%\BitcoinWallpaper\first-run.done</summary>
    public static string DefaultPath { get; } = Path.Combine(DataFolder.Path, "first-run.done");

    public bool IsSet => File.Exists(path);

    public void Set()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
