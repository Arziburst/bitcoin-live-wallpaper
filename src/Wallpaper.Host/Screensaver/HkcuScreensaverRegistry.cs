using Microsoft.Win32;

namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// The real HKCU\Control Panel\Desktop (never HKLM, no admin rights). After each write Windows is told about the
/// screensaver state (SPI_SETSCREENSAVEACTIVE with SPIF_SENDCHANGE, without SPIF_UPDATEINIFILE: the registry value
/// written here is what persists). Never used by the tests.
/// </summary>
internal sealed class HkcuScreensaverRegistry : IScreensaverRegistry
{
    private const string KeyPath = @"Control Panel\Desktop";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            string text => text,
            var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public void SetValue(string name, string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true))
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }

        Notify();
    }

    public void DeleteValue(string name)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
        {
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        Notify();
    }

    private void Notify()
    {
        var active = GetValue(ScreensaverSetting.ActiveValueName) == "1";
        if (!Native.SystemParametersInfo(Native.SPI_SETSCREENSAVEACTIVE, active ? 1u : 0u, 0, Native.SPIF_SENDCHANGE))
        {
            Log.Warn($"screensaver: SystemParametersInfo(SPI_SETSCREENSAVEACTIVE) failed: error " +
                     $"{System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
    }
}
