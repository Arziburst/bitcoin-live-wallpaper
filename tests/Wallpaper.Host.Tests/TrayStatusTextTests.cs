using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Settings;
using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Host.Tests;

// Contract #22.4 (D-26, issue #31), criteria 1 and 2: the tray tooltip text, a pure function; and its 127-character
// limit (NOTIFYICONDATAW.szTip is 128 with its terminator).
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Settings, src/Wallpaper.Host/Settings/
// (e.g. TrayStatusText.cs). Everything public. Uses WallpaperState (Settings), PauseReason (Chart.Core.Pause),
// FeedState (Market.Data) and MarketSource (Chart.Core.Candles) as they are today.
//
//   // What the tooltip shows, as the host reads it on its thread (the scene's 250 ms poll).
//   public sealed record TrayStatus(
//       WallpaperState Wallpaper,    // On / Off (the toggle)
//       PauseReason Pause,           // PauseReason.None = not paused; any other = paused for that reason
//       FeedState Feed,              // Connecting / Live / Stale
//       MarketSource Source,         // Binance / Kraken; shown as the enum name
//       decimal? LastPrice,          // last trade price in USD, null before the first data
//       DateTimeOffset? LastLiveAt); // when the feed was last live, null when it never was
//
//   public static class TrayStatusText
//   {
//       public const string Title = "Bitcoin Live Wallpaper";
//       public const int MaxLength = 127;
//
//       // Title + "\n" + line 2 (a single '\n', no '\r'), then Shorten(text, MaxLength). Line 2 by precedence
//       //   1. Wallpaper Off                      -> "Off"           (whatever the pause or the feed say)
//       //   2. Pause != None                      -> "Paused (<what>)":
//       //        Fullscreen, NotificationState, NotificationCorroborated, NotificationUncorroborated -> "fullscreen"
//       //        Maximized                        -> "maximized window"
//       //        ForegroundCovers, WindowsCover   -> "covered"
//       //        SessionLocked                    -> "locked"
//       //        SessionDisconnected              -> "disconnected"
//       //        DisplayOff                       -> "display off"
//       //   3. Feed Stale                         -> "offline since HH:mm" : LastLiveAt converted into `zone`
//       //        (TimeZoneInfo.ConvertTime), 24-hour "HH:mm", two digits each; LastLiveAt null -> just "offline"
//       //   4. Feed Connecting                    -> "Connecting · <Source>"   (LastPrice ignored)
//       //   5. Feed Live                          -> "Live · <Source> · <price>"; LastPrice null -> "Live · <Source>"
//       // The separator is " · " (space, U+00B7, space).
//       // <price>: LastPrice rounded to whole dollars with MidpointRounding.AwayFromZero (77182.5 -> 77183), then the
//       // digits in groups of three counted from the right, separated by one regular space U+0020
//       // (999 -> "999", 1000 -> "1 000", 105432.1 -> "105 432"). Independent of CultureInfo.CurrentCulture and
//       // CurrentUICulture (no NBSP, no U+202F, no '.' or ',' from the culture).
//       public static string Build(TrayStatus status, TimeZoneInfo zone);
//
//       // Text of at most `maxLength` UTF-16 chars. A text that is not longer than maxLength is returned unchanged.
//       // A longer one is cut and ends with the single char "…" (U+2026): the result is exactly maxLength chars
//       // long (maxLength - 1 chars of the text, then "…"); when the cut would split a surrogate pair the lone
//       // high surrogate is dropped too (the result is then one char shorter, never longer, never invalid).
//       public static string Shorten(string text, int maxLength = MaxLength);
//   }
public class TrayStatusTextTests
{
    private const string Title = "Bitcoin Live Wallpaper";
    private const string Sep = " · ";

    // UTC+3 without daylight saving: the same answer on every machine.
    private static readonly TimeZoneInfo Plus3 =
        TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "test+3", "test+3");

    private static string Line2(string text)
    {
        Assert.StartsWith(Title + "\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        return text[(Title.Length + 1)..];
    }

    private static TrayStatus Status(
        WallpaperState wallpaper = WallpaperState.On,
        PauseReason pause = PauseReason.None,
        FeedState feed = FeedState.Live,
        MarketSource source = MarketSource.Binance,
        decimal? price = 77182m,
        DateTimeOffset? lastLiveAt = null) =>
        new(wallpaper, pause, feed, source, price, lastLiveAt);

    private static string Second(TrayStatus status) => Line2(TrayStatusText.Build(status, Plus3));

    private static void WithCulture(string name, Action action)
    {
        var thread = Thread.CurrentThread;
        var culture = thread.CurrentCulture;
        var ui = thread.CurrentUICulture;
        try
        {
            thread.CurrentCulture = CultureInfo.GetCultureInfo(name);
            thread.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            action();
        }
        finally
        {
            thread.CurrentCulture = culture;
            thread.CurrentUICulture = ui;
        }
    }

    // ---- criterion 1: the lines ----

    [Fact]
    public void Live_shows_source_and_price_with_a_space_grouped_price()
    {
        var text = TrayStatusText.Build(Status(feed: FeedState.Live, source: MarketSource.Binance, price: 77182m), Plus3);

        Assert.Equal("Bitcoin Live Wallpaper\nLive · Binance · 77 182", text);
    }

    [Fact]
    public void Live_names_the_source_it_reads_from()
    {
        Assert.Equal("Live" + Sep + "Kraken" + Sep + "77 182", Second(Status(source: MarketSource.Kraken)));
    }

    [Theory]
    [InlineData("77182.49", "77 182")]
    [InlineData("77182.5", "77 183")] // midpoint rounds away from zero, not to even
    [InlineData("77183.5", "77 184")]
    [InlineData("77182.51", "77 183")]
    [InlineData("999", "999")]
    [InlineData("999.4", "999")]
    [InlineData("1000", "1 000")]
    [InlineData("105432.1", "105 432")]
    [InlineData("1234567.89", "1 234 568")]
    [InlineData("100000", "100 000")]
    [InlineData("0.4", "0")]
    public void Price_is_rounded_to_whole_dollars_and_grouped_by_a_regular_space(string price, string expected)
    {
        var line = Second(Status(price: decimal.Parse(price, CultureInfo.InvariantCulture)));

        Assert.Equal("Live" + Sep + "Binance" + Sep + expected, line);
        Assert.DoesNotContain(' ', line); // no no-break space
        Assert.DoesNotContain(' ', line); // no narrow no-break space (fr-FR groups with it)
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("")] // invariant
    public void Text_does_not_depend_on_the_culture(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.Equal("Live" + Sep + "Binance" + Sep + "105 432", Second(Status(price: 105432.1m)));
            Assert.Equal("Live" + Sep + "Binance" + Sep + "1 000", Second(Status(price: 1000m)));
            Assert.Equal("offline since 14:20", Second(Status(feed: FeedState.Stale,
                lastLiveAt: new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero))));
        });
    }

    [Fact]
    public void Live_without_a_price_shows_only_the_source()
    {
        Assert.Equal("Live" + Sep + "Binance", Second(Status(feed: FeedState.Live, price: null)));
    }

    [Theory]
    [InlineData(MarketSource.Binance)]
    [InlineData(MarketSource.Kraken)]
    public void Connecting_shows_the_source_and_ignores_the_price(MarketSource source)
    {
        Assert.Equal("Connecting" + Sep + source, Second(Status(feed: FeedState.Connecting, source: source, price: null)));
        Assert.Equal("Connecting" + Sep + source, Second(Status(feed: FeedState.Connecting, source: source, price: 77182m)));
    }

    [Fact]
    public void Stale_shows_offline_since_the_local_time_it_was_last_live()
    {
        // 11:20 UTC is 14:20 in UTC+3.
        var line = Second(Status(feed: FeedState.Stale, lastLiveAt: new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero)));

        Assert.Equal("offline since 14:20", line);
    }

    [Fact]
    public void Stale_time_is_converted_from_whatever_offset_the_instant_carries()
    {
        // The same instant written with another offset gives the same answer.
        var line = Second(Status(feed: FeedState.Stale,
            lastLiveAt: new DateTimeOffset(2026, 10, 31, 5, 20, 0, TimeSpan.FromHours(-6))));

        Assert.Equal("offline since 14:20", line);
    }

    [Theory]
    [InlineData(23, 50, "02:50")] // crosses midnight in the zone
    [InlineData(0, 5, "03:05")]   // two digits for hour and minute
    [InlineData(10, 59, "13:59")]
    [InlineData(21, 0, "00:00")]  // 24-hour clock, midnight is 00
    [InlineData(9, 7, "12:07")]
    public void Stale_time_is_a_two_digit_24_hour_HH_mm_in_the_given_zone(int utcHour, int utcMinute, string expected)
    {
        var line = Second(Status(feed: FeedState.Stale,
            lastLiveAt: new DateTimeOffset(2026, 10, 31, utcHour, utcMinute, 0, TimeSpan.Zero)));

        Assert.Equal("offline since " + expected, line);
    }

    [Fact]
    public void Stale_time_follows_the_zone_it_is_given()
    {
        var utc = new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero);
        var status = Status(feed: FeedState.Stale, lastLiveAt: utc);

        Assert.Equal("offline since 11:20", Line2(TrayStatusText.Build(status, TimeZoneInfo.Utc)));
        Assert.Equal("offline since 14:20", Line2(TrayStatusText.Build(status, Plus3)));
    }

    [Fact]
    public void Stale_with_a_price_still_says_offline()
    {
        var line = Second(Status(feed: FeedState.Stale, price: 77182m,
            lastLiveAt: new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero)));

        Assert.Equal("offline since 14:20", line);
    }

    [Fact]
    public void Stale_that_was_never_live_says_just_offline()
    {
        Assert.Equal("offline", Second(Status(feed: FeedState.Stale, price: null, lastLiveAt: null)));
    }

    [Fact]
    public void Off_shows_Off_whatever_the_pause_and_the_feed_say()
    {
        var at = new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero);
        foreach (var feed in Enum.GetValues<FeedState>())
        {
            foreach (var pause in Enum.GetValues<PauseReason>())
            {
                Assert.Equal("Off", Second(Status(WallpaperState.Off, pause, feed, lastLiveAt: at)));
            }
        }
    }

    [Theory]
    [InlineData(PauseReason.Fullscreen, "Paused (fullscreen)")]
    [InlineData(PauseReason.NotificationState, "Paused (fullscreen)")]
    [InlineData(PauseReason.NotificationCorroborated, "Paused (fullscreen)")]
    [InlineData(PauseReason.NotificationUncorroborated, "Paused (fullscreen)")]
    [InlineData(PauseReason.Maximized, "Paused (maximized window)")]
    [InlineData(PauseReason.ForegroundCovers, "Paused (covered)")]
    [InlineData(PauseReason.WindowsCover, "Paused (covered)")]
    [InlineData(PauseReason.SessionLocked, "Paused (locked)")]
    [InlineData(PauseReason.SessionDisconnected, "Paused (disconnected)")]
    [InlineData(PauseReason.DisplayOff, "Paused (display off)")]
    public void Paused_names_the_reason(PauseReason reason, string expected)
    {
        Assert.Equal(expected, Second(Status(pause: reason)));
    }

    [Fact]
    public void Every_pause_reason_but_None_gives_a_Paused_line()
    {
        foreach (var reason in Enum.GetValues<PauseReason>().Where(r => r != PauseReason.None))
        {
            var line = Second(Status(pause: reason));
            Assert.StartsWith("Paused (", line, StringComparison.Ordinal);
            Assert.EndsWith(")", line, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(FeedState.Live)]
    [InlineData(FeedState.Stale)]
    [InlineData(FeedState.Connecting)]
    public void Paused_wins_over_every_feed_state(FeedState feed)
    {
        var at = new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero);

        Assert.Equal("Paused (locked)", Second(Status(pause: PauseReason.SessionLocked, feed: feed, lastLiveAt: at)));
    }

    [Fact]
    public void PauseReason_None_is_not_paused()
    {
        Assert.Equal("Live" + Sep + "Binance" + Sep + "77 182", Second(Status(pause: PauseReason.None)));
    }

    [Fact]
    public void Text_is_the_title_a_single_newline_and_one_more_line()
    {
        var text = TrayStatusText.Build(Status(), Plus3);

        var lines = text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(Title, lines[0]);
        Assert.Equal(TrayStatusText.Title, lines[0]);
        Assert.NotEmpty(lines[1]);
    }

    [Fact]
    public void The_separator_is_a_middle_dot_between_spaces()
    {
        var line = Second(Status());

        Assert.Equal(2, line.Count(c => c == '·'));
        Assert.Contains(" · ", line, StringComparison.Ordinal);
    }

    // ---- criterion 2: never longer than 127 characters ----

    [Fact]
    public void MaxLength_is_127()
    {
        Assert.Equal(127, TrayStatusText.MaxLength);
    }

    [Fact]
    public void Every_normal_text_fits()
    {
        var at = new DateTimeOffset(2026, 10, 31, 11, 20, 0, TimeSpan.Zero);
        foreach (var wallpaper in Enum.GetValues<WallpaperState>())
        foreach (var pause in Enum.GetValues<PauseReason>())
        foreach (var feed in Enum.GetValues<FeedState>())
        foreach (var source in Enum.GetValues<MarketSource>())
        {
            var text = TrayStatusText.Build(new TrayStatus(wallpaper, pause, feed, source, 77182m, at), Plus3);
            Assert.True(text.Length <= 127, text);
        }
    }

    [Fact]
    public void An_absurd_price_never_makes_the_text_longer_than_127()
    {
        var text = TrayStatusText.Build(Status(price: decimal.MaxValue), Plus3);

        Assert.True(text.Length <= TrayStatusText.MaxLength, $"{text.Length}: {text}");
        Assert.StartsWith(Title + "\n", text, StringComparison.Ordinal);
        Assert.Contains("79 228 162 514 264 337 593 543 950 335", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unnamed_source_value_never_makes_the_text_longer_than_127()
    {
        var text = TrayStatusText.Build(Status(source: (MarketSource)12345, price: decimal.MaxValue), Plus3);

        Assert.True(text.Length <= TrayStatusText.MaxLength, $"{text.Length}: {text}");
    }

    [Fact]
    public void Shorten_leaves_a_text_up_to_the_limit_unchanged()
    {
        var exactly = new string('x', 127);

        Assert.Equal(exactly, TrayStatusText.Shorten(exactly));
        Assert.Equal("Bitcoin Live Wallpaper\nOff", TrayStatusText.Shorten("Bitcoin Live Wallpaper\nOff"));
        Assert.Equal("", TrayStatusText.Shorten(""));
    }

    [Fact]
    public void Shorten_cuts_a_longer_text_to_the_limit_and_ends_with_an_ellipsis()
    {
        var shortened = TrayStatusText.Shorten(new string('x', 128));

        Assert.Equal(127, shortened.Length);
        Assert.Equal(new string('x', 126) + "…", shortened);
    }

    [Fact]
    public void Shorten_cuts_a_very_long_text()
    {
        var shortened = TrayStatusText.Shorten(new string('y', 100_000));

        Assert.Equal(127, shortened.Length);
        Assert.EndsWith("…", shortened, StringComparison.Ordinal);
        Assert.StartsWith(new string('y', 126), shortened, StringComparison.Ordinal);
    }

    [Fact]
    public void Shorten_takes_another_limit()
    {
        Assert.Equal("abcd…", TrayStatusText.Shorten("abcdefgh", 5));
        Assert.Equal("abcde", TrayStatusText.Shorten("abcde", 5));
    }

    [Fact]
    public void Shorten_never_leaves_half_a_surrogate_pair()
    {
        // 125 chars + two emoji (4 chars): 129 chars; the cut after 126 chars would split the first emoji.
        var text = new string('a', 125) + "😀😀";

        var shortened = TrayStatusText.Shorten(text);

        Assert.True(shortened.Length <= 127);
        Assert.Equal(new string('a', 125) + "…", shortened);
        // Valid UTF-16 all through.
        var strict = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);
        strict.GetBytes(shortened);
    }
}
