namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// The per-user Run key and nothing else: implementations address exactly
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run; there is no way to give them another hive or key.
/// </summary>
public interface IRegistryRunKey
{
    /// <summary>The value's text; null when the value does not exist.</summary>
    string? GetValue(string name);

    /// <summary>REG_SZ; creates or overwrites.</summary>
    void SetValue(string name, string value);

    /// <summary>No-op when the value does not exist.</summary>
    void DeleteValue(string name);
}
