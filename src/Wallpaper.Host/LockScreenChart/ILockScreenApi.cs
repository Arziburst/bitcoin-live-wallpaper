namespace BitcoinWallpaper.Host.LockScreenChart;

/// <summary>The Windows lock screen picture (production: <see cref="WindowsLockScreenApi"/>; tests: a fake).</summary>
public interface ILockScreenApi
{
    /// <summary>Full path of the picture Windows reports; null when there is none (Spotlight, no access). May throw.</summary>
    string? OriginalImageFile { get; }

    /// <summary>Sets the picture; throws when Windows refuses.</summary>
    Task SetImageFileAsync(string path);
}

/// <summary>The few file operations <see cref="LockScreenImages"/> needs. Paths are full paths, case-insensitive.</summary>
public interface ILockScreenFiles
{
    bool Exists(string path);

    void CreateDirectory(string folder);

    /// <summary>Overwrites <paramref name="destination"/>.</summary>
    void Copy(string source, string destination);

    /// <summary>No-op when absent.</summary>
    void Delete(string path);

    /// <summary>Full paths of the files directly in <paramref name="folder"/>; empty when it does not exist.</summary>
    IReadOnlyList<string> List(string folder);

    string ReadAllText(string path);

    void WriteAllText(string path, string text);
}
