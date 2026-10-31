namespace BitcoinWallpaper.Chart.Core.Pause;

/// <summary>
/// The display state of a power setting notification (D-15): POWERBROADCAST_SETTING.Data of
/// GUID_SESSION_DISPLAY_STATUS or GUID_CONSOLE_DISPLAY_STATE, a DWORD: 0 off, 1 on, 2 dimmed.
/// </summary>
public static class DisplayPower
{
    public const uint Off = 0;
    public const uint On = 1;
    public const uint Dimmed = 2;

    /// <summary>Only 0 is off: dimmed counts as on, and an unknown value never switches the picture off.</summary>
    public static bool IsOn(uint value) => value != Off;

    /// <summary>For the log: off, on, dimmed or the raw value.</summary>
    public static string Describe(uint value) => value switch
    {
        Off => "off",
        On => "on",
        Dimmed => "dimmed",
        _ => $"unknown ({value})",
    };
}
