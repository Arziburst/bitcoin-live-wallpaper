using Microsoft.Win32;

namespace BitcoinWallpaper.Host.Install;

/// <summary>The production <see cref="IRegistryRunKey"/>: Registry.CurrentUser + <see cref="Autostart.RunSubKey"/>, fixed.</summary>
public sealed class RegistryRunKey : IRegistryRunKey
{
    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Autostart.RunSubKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Autostart.RunSubKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Autostart.RunSubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
