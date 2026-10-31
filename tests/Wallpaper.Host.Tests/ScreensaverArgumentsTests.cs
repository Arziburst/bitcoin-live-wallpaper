using BitcoinWallpaper.Host.Screensaver;

namespace BitcoinWallpaper.Host.Tests;

// Contract 26.1 (D-21, issue #26), criterion 1: the exe decides its mode from the command line before the supervisor,
// the wallpaper child and the single-instance check. A pure parser, no Win32.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Screensaver, src/Wallpaper.Host/Screensaver/
// (ScreensaverArguments.cs), public types:
//
//   public enum ScreensaverMode
//   {
//       None,      // not a screensaver start: the current behaviour (supervisor / wallpaper child / Velopack hooks)
//       Run,       // /s : the full-screen screensaver
//       Preview,   // /p <hwnd> : a no-op that exits 0 at once
//       Config,    // /c, /c:<hwnd>, or no arguments when the process file is a .scr : a short message box
//   }
//
//   public static class ScreensaverArguments
//   {
//       // args: the command-line arguments as Main gets them (no program name). processPath: Environment.ProcessPath
//       // (may be null).
//       // Only the FIRST argument decides. Its switch character is '/' or '-', the letter is case-insensitive:
//       //   s            -> Run         (the whole argument is exactly "/s" or "-s")
//       //   p            -> Preview     ("/p", "-p", "/p:<hwnd>", "-p:<hwnd>"; with "/p <hwnd>" the hwnd is the second arg)
//       //   c            -> Config      ("/c", "-c", "/c:<hwnd>", "-c:<hwnd>")
//       // No arguments at all -> Config when processPath ends with ".scr" (case-insensitive), else None.
//       // Anything else (a longer word such as "/start" or "--autostart", "--wallpaper-child", "--crash-test",
//       // "--veloapp-install 1.2.3", an unknown switch, an empty first argument) -> None, whatever the process file is.
//       public static ScreensaverMode Parse(IReadOnlyList<string> args, string? processPath);
//   }
public class ScreensaverArgumentsTests
{
    private const string Exe = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.exe";
    private const string Scr = @"C:\Users\Owner Name\AppData\Local\BitcoinWallpaper\current\BitcoinWallpaper.scr";

    // ---- /s ----

    [Theory]
    [InlineData("/s")]
    [InlineData("/S")]
    [InlineData("-s")]
    [InlineData("-S")]
    public void S_runs_the_screensaver_from_the_exe_and_from_the_scr(string arg)
    {
        Assert.Equal(ScreensaverMode.Run, ScreensaverArguments.Parse(new[] { arg }, Exe));
        Assert.Equal(ScreensaverMode.Run, ScreensaverArguments.Parse(new[] { arg }, Scr));
    }

    [Fact]
    public void S_runs_whatever_the_process_path_is()
    {
        Assert.Equal(ScreensaverMode.Run, ScreensaverArguments.Parse(new[] { "/s" }, null));
        Assert.Equal(ScreensaverMode.Run, ScreensaverArguments.Parse(new[] { "/s" }, @"C:\x\Windows\System32\BitcoinWallpaper.SCR"));
    }

    // ---- /p ----

    [Theory]
    [InlineData("/p")]
    [InlineData("/P")]
    [InlineData("-p")]
    [InlineData("/p:197266")]
    [InlineData("/P:197266")]
    [InlineData("-p:197266")]
    public void P_is_the_preview(string arg)
    {
        Assert.Equal(ScreensaverMode.Preview, ScreensaverArguments.Parse(new[] { arg }, Scr));
        Assert.Equal(ScreensaverMode.Preview, ScreensaverArguments.Parse(new[] { arg }, Exe));
    }

    [Fact]
    public void P_with_the_handle_as_a_second_argument_is_the_preview()
    {
        Assert.Equal(ScreensaverMode.Preview, ScreensaverArguments.Parse(new[] { "/p", "197266" }, Scr));
        Assert.Equal(ScreensaverMode.Preview, ScreensaverArguments.Parse(new[] { "-P", "197266" }, Exe));
    }

    // ---- /c ----

    [Theory]
    [InlineData("/c")]
    [InlineData("/C")]
    [InlineData("-c")]
    [InlineData("/c:197266")]
    [InlineData("/C:197266")]
    [InlineData("-c:197266")]
    public void C_is_the_config(string arg)
    {
        Assert.Equal(ScreensaverMode.Config, ScreensaverArguments.Parse(new[] { arg }, Scr));
        Assert.Equal(ScreensaverMode.Config, ScreensaverArguments.Parse(new[] { arg }, Exe));
    }

    // ---- no arguments ----

    [Theory]
    [InlineData(@"C:\Windows\System32\BitcoinWallpaper.scr")]
    [InlineData(@"C:\Windows\System32\BitcoinWallpaper.SCR")]
    [InlineData(Scr)]
    public void No_arguments_on_a_scr_is_the_config(string processPath)
    {
        Assert.Equal(ScreensaverMode.Config, ScreensaverArguments.Parse(Array.Empty<string>(), processPath));
    }

    [Theory]
    [InlineData(Exe)]
    [InlineData(@"C:\x\BitcoinWallpaper.scr.exe")]
    [InlineData(@"C:\x\scr")]
    [InlineData(@"C:\x\BitcoinWallpaper")]
    [InlineData(null)]
    public void No_arguments_on_the_exe_keeps_the_current_behaviour(string? processPath)
    {
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(Array.Empty<string>(), processPath));
    }

    // ---- everything else is today's behaviour ----

    [Theory]
    [InlineData("--autostart")]
    [InlineData("--wallpaper-child")]
    [InlineData("--crash-test")]
    [InlineData("--veloapp-install")]
    [InlineData("--veloapp-updated")]
    [InlineData("--veloapp-obsolete")]
    [InlineData("--veloapp-uninstall")]
    public void Other_starts_keep_the_current_behaviour_on_the_exe_and_on_a_scr(string arg)
    {
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { arg }, Exe));
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { arg }, Scr));
    }

    [Fact]
    public void Velopack_hook_with_a_version_and_a_child_with_crash_test_keep_the_current_behaviour()
    {
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { "--veloapp-install", "1.2.3" }, Exe));
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { "--wallpaper-child", "--crash-test" }, Exe));
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { "--autostart", "--crash-test" }, Scr));
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/sx")]
    [InlineData("/ss")]
    [InlineData("--s")]
    [InlineData("--p")]
    [InlineData("--c")]
    [InlineData("s")]
    [InlineData("p")]
    [InlineData("c")]
    [InlineData("/x")]
    [InlineData("/")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("/cc")]
    [InlineData("/pp:1")]
    public void Lookalikes_and_unknown_arguments_keep_the_current_behaviour(string arg)
    {
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { arg }, Exe));
        Assert.Equal(ScreensaverMode.None, ScreensaverArguments.Parse(new[] { arg }, Scr));
    }
}
