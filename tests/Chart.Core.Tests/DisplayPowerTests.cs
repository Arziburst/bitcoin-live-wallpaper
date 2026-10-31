using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract 22.1 (D-15), criterion 1: the display power value of a PBT_POWERSETTINGCHANGE
// (GUID_SESSION_DISPLAY_STATUS / GUID_CONSOLE_DISPLAY_STATE, POWERBROADCAST_SETTING.Data, a DWORD) maps
// 0 -> off, 1 -> on, 2 (dimmed) -> on, any other value -> on (dimmed counts as on; an unknown value never
// switches the picture off).
//
// API SKETCH (written before the code, #237). The worker implements exactly this public type in
// src/Chart.Core/Pause/DisplayPower.cs, namespace BitcoinWallpaper.Chart.Core.Pause. Pure: no Win32 types.
//
//   public static class DisplayPower
//   {
//       /// The DWORD of a display power notification -> is the display on (true) or off (false).
//       public static bool IsOn(uint value);      // 0 -> false; every other value -> true
//   }
public class DisplayPowerTests
{
    [Fact]
    public void Zero_is_off()
    {
        Assert.False(DisplayPower.IsOn(0u));
    }

    [Fact]
    public void One_is_on()
    {
        Assert.True(DisplayPower.IsOn(1u));
    }

    [Fact]
    public void Two_dimmed_is_on()
    {
        Assert.True(DisplayPower.IsOn(2u));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(255u)]
    [InlineData(65536u)]
    [InlineData(2147483647u)]
    [InlineData(uint.MaxValue)]
    public void Any_other_value_is_on(uint value)
    {
        Assert.True(DisplayPower.IsOn(value));
    }
}
