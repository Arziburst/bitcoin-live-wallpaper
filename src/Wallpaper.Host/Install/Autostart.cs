namespace BitcoinWallpaper.Host.Install;

/// <summary>
/// "Start with Windows" (D-1): the per-user Run value BitcoinWallpaper = "&lt;exe&gt;" --autostart. On by default at the
/// first run, then only the tray changes it; an update that moves the exe gets the value rewritten at the next
/// start; the uninstall hook removes it. Touches no other value of the Run key.
/// </summary>
public sealed class Autostart(IRegistryRunKey runKey, IFirstRunMarker marker, string exePath)
{
    public const string ValueName = "BitcoinWallpaper";
    public const string RunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>D-17: marks the launch at sign-in, which keeps the persisted on/off state.</summary>
    public const string AutostartArgument = "--autostart";

    /// <summary>The value exists, whatever path it holds.</summary>
    public bool IsEnabled => runKey.GetValue(ValueName) is not null;

    private string RunValue => "\"" + exePath + "\" " + AutostartArgument;

    /// <summary>Tray toggle on.</summary>
    public void Enable() => runKey.SetValue(ValueName, RunValue);

    /// <summary>Tray toggle off; no-op when missing.</summary>
    public void Disable()
    {
        if (IsEnabled)
        {
            runKey.DeleteValue(ValueName);
        }
    }

    /// <summary>A present value that is not exactly the current one is rewritten; an absent one stays absent.</summary>
    public void EnsureCurrentPath()
    {
        var value = runKey.GetValue(ValueName);
        if (value is not null && value != RunValue)
        {
            runKey.SetValue(ValueName, RunValue);
        }
    }

    /// <summary>Every app start: the first one enables autostart; later ones only keep the path current.</summary>
    public void OnStartup()
    {
        if (marker.IsSet)
        {
            EnsureCurrentPath();
            return;
        }

        Enable();
        marker.Set();
    }

    /// <summary>The Velopack uninstall hook.</summary>
    public void OnUninstall()
    {
        Disable();
        marker.Clear();
    }
}
