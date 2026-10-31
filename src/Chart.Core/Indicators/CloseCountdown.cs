using System.Globalization;

namespace BitcoinWallpaper.Chart.Core.Indicators;

public static class CloseCountdown
{
    /// <summary>"closes in mm:ss": the time from now to closeTime, rounded up to whole seconds, never negative.</summary>
    public static string Text(DateTimeOffset closeTime, DateTimeOffset now)
    {
        var ticks = Math.Max(0, (closeTime - now).Ticks);
        var seconds = (ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        return string.Create(CultureInfo.InvariantCulture, $"closes in {seconds / 60:00}:{seconds % 60:00}");
    }
}
