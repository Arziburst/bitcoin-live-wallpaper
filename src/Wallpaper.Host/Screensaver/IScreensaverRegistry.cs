namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>The string values of HKCU\Control Panel\Desktop (never HKLM). Every member may throw.</summary>
public interface IScreensaverRegistry
{
    /// <summary>null: the value is absent; "": present and empty.</summary>
    string? GetValue(string name);

    /// <summary>Writes a REG_SZ value.</summary>
    void SetValue(string name, string value);

    /// <summary>No-op when the value is absent.</summary>
    void DeleteValue(string name);
}
