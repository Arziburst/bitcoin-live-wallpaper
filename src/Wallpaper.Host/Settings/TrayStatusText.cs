using System.Globalization;
using System.Text;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.Settings;

/// <summary>What the tray tooltip shows, as the host reads it on its thread (the scene's 250 ms poll).</summary>
/// <param name="Pause"><see cref="PauseReason.None"/> = not paused.</param>
/// <param name="LastPrice">The last trade price in USD; null before the first data.</param>
/// <param name="LastLiveAt">When the host last saw the feed live; null when it never was.</param>
/// <param name="LockScreenError">The last lock screen failure (D-34); null when there is none.</param>
public sealed record TrayStatus(
    WallpaperState Wallpaper,
    PauseReason Pause,
    FeedState Feed,
    MarketSource Source,
    decimal? LastPrice,
    DateTimeOffset? LastLiveAt,
    string? LockScreenError = null);

/// <summary>
/// The tray tooltip (D-26): the title, then one line by precedence Off &gt; Paused &gt; offline &gt; connecting &gt;
/// live. Never longer than <see cref="MaxLength"/> (NOTIFYICONDATAW.szTip is 128 with its terminator).
/// </summary>
public static class TrayStatusText
{
    public const string Title = "Bitcoin Live Wallpaper";
    public const int MaxLength = 127;

    private const string Separator = " · ";

    public static string Build(TrayStatus status, TimeZoneInfo zone) =>
        Shorten(Title + "\n" + Line(status, zone) + LockScreenSuffix(status));

    // D-34: a lock screen failure ends the line, except when the wallpaper is Off.
    private static string LockScreenSuffix(TrayStatus status) =>
        status.LockScreenError is { } error && status.Wallpaper != WallpaperState.Off
            ? Separator + "lock screen: " + error
            : "";

    /// <summary>
    /// At most <paramref name="maxLength"/> chars: a longer text is cut and ends with "…"; a cut that would split a
    /// surrogate pair drops its high half too.
    /// </summary>
    public static string Shorten(string text, int maxLength = MaxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var keep = maxLength - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "…");
    }

    private static string Line(TrayStatus status, TimeZoneInfo zone)
    {
        if (status.Wallpaper == WallpaperState.Off)
        {
            return "Off";
        }

        if (status.Pause != PauseReason.None)
        {
            return $"Paused ({PauseText(status.Pause)})";
        }

        return status.Feed switch
        {
            FeedState.Stale => status.LastLiveAt is { } at
                ? "offline since " + TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture)
                : "offline",
            FeedState.Connecting => "Connecting" + Separator + status.Source,
            _ => status.LastPrice is { } price
                ? "Live" + Separator + status.Source + Separator + Price(price)
                : "Live" + Separator + status.Source,
        };
    }

    private static string PauseText(PauseReason reason) => reason switch
    {
        PauseReason.Maximized => "maximized window",
        PauseReason.ForegroundCovers or PauseReason.WindowsCover => "covered",
        PauseReason.SessionLocked => "locked",
        PauseReason.SessionDisconnected => "disconnected",
        PauseReason.DisplayOff => "display off",
        _ => "fullscreen", // Fullscreen and the Notification* reasons
    };

    /// <summary>Whole dollars, thousands grouped by a regular space (77 182), whatever the culture.</summary>
    private static string Price(decimal price)
    {
        var digits = Math.Round(price, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        var sign = digits.StartsWith('-') ? "-" : "";
        digits = digits[sign.Length..];
        var text = new StringBuilder(sign, digits.Length + digits.Length / 3 + 1);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                text.Append(' ');
            }

            text.Append(digits[i]);
        }

        return text.ToString();
    }
}
