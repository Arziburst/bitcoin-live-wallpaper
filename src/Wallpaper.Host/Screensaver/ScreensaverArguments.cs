namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>How the process was started, as far as the screensaver is concerned (D-21).</summary>
public enum ScreensaverMode
{
    /// <summary>Not a screensaver start: the supervisor, the wallpaper child or a Velopack hook, as before.</summary>
    None,

    /// <summary>/s: the full-screen screensaver.</summary>
    Run,

    /// <summary>/p &lt;hwnd&gt;: the preview in the Windows dialog; a no-op that exits 0 at once.</summary>
    Preview,

    /// <summary>/c, /c:&lt;hwnd&gt;, or no arguments on the .scr: a short message box.</summary>
    Config,
}

/// <summary>
/// The Windows screensaver command line, decided before the supervisor, the wallpaper child and the single-instance
/// check. Only the first argument decides; its switch is '/' or '-' and the letter is case-insensitive.
/// </summary>
public static class ScreensaverArguments
{
    public static ScreensaverMode Parse(IReadOnlyList<string> args, string? processPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return processPath is not null && processPath.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)
                ? ScreensaverMode.Config
                : ScreensaverMode.None;
        }

        var first = args[0];
        if (first is null || first.Length < 2 || (first[0] != '/' && first[0] != '-'))
        {
            return ScreensaverMode.None;
        }

        var rest = first.AsSpan(2);
        var bareOrHandle = rest.Length == 0 || rest[0] == ':';
        return char.ToLowerInvariant(first[1]) switch
        {
            's' when rest.Length == 0 => ScreensaverMode.Run,
            'p' when bareOrHandle => ScreensaverMode.Preview,
            'c' when bareOrHandle => ScreensaverMode.Config,
            _ => ScreensaverMode.None,
        };
    }
}
