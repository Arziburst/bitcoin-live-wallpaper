using BitcoinWallpaper.Host.Screensaver;

namespace BitcoinWallpaper.Host.Tests;

// Contract 26.1 (D-21, issue #26), criterion 2: the close-on-input rules of the screensaver window, a pure type with
// a plain time parameter as the fake clock (no TimeProvider, no Win32).
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Screensaver, src/Wallpaper.Host/Screensaver/
// (CloseOnInput.cs), public:
//
//   public sealed class CloseOnInput
//   {
//       public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);   // a mouse move never closes before this
//       public const int MovePixels = 4;                                   // a move closes only when MORE than this far
//
//       // startedAt: the clock reading when the window appeared (production: Stopwatch time since an arbitrary
//       // origin as a TimeSpan; the tests use plain TimeSpans). Every `now` below is a reading of the same clock.
//       public CloseOnInput(TimeSpan startedAt);
//
//       // true = close the window now.
//       public bool OnKey();                                // a key press: always true, at any time (also inside the grace)
//       public bool OnMouseButton();                        // any mouse button: always true, at any time
//       // A mouse move at screen pixel (x, y). The FIRST move seen (even inside the grace, even at the start) only
//       // records the first position and returns false. A later move returns true only when now - startedAt >= Grace
//       // (a move inside the grace never closes) AND |x - firstX| > MovePixels OR |y - firstY| > MovePixels (either
//       // axis; exactly MovePixels does not close). The first position is never replaced by later moves.
//       public bool OnMouseMove(int x, int y, TimeSpan now);
//   }
public class ScreensaverCloseOnInputTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(1000);   // not zero: the clock origin is arbitrary

    private static TimeSpan At(double secondsAfterStart) => Start + TimeSpan.FromSeconds(secondsAfterStart);

    private static CloseOnInput Rules() => new(Start);

    [Fact]
    public void The_grace_is_one_second_and_the_distance_is_four_pixels()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CloseOnInput.Grace);
        Assert.Equal(4, CloseOnInput.MovePixels);
    }

    // ---- key and button ----

    [Fact]
    public void A_key_press_closes_at_once()
    {
        Assert.True(Rules().OnKey());   // a key has no time: the rule is the same inside and after the grace
    }

    [Fact]
    public void A_key_press_closes_after_a_move_too()
    {
        var rules = Rules();
        rules.OnMouseMove(10, 10, At(0.1));

        Assert.True(rules.OnKey());
    }

    [Fact]
    public void A_mouse_button_closes_at_once()
    {
        Assert.True(Rules().OnMouseButton());
    }

    [Fact]
    public void A_mouse_button_closes_after_moves_inside_the_grace_too()
    {
        var rules = Rules();
        rules.OnMouseMove(10, 10, At(0.1));
        rules.OnMouseMove(11, 10, At(0.2));

        Assert.True(rules.OnMouseButton());
    }

    // ---- the grace ----

    [Fact]
    public void A_big_move_inside_the_grace_never_closes()
    {
        var rules = Rules();

        Assert.False(rules.OnMouseMove(100, 100, At(0.0)));    // the first position
        Assert.False(rules.OnMouseMove(900, 700, At(0.2)));
        Assert.False(rules.OnMouseMove(0, 0, At(0.5)));
        Assert.False(rules.OnMouseMove(2000, 50, At(0.999)));
    }

    [Fact]
    public void A_big_move_after_the_grace_closes()
    {
        var rules = Rules();
        rules.OnMouseMove(100, 100, At(0.1));

        Assert.True(rules.OnMouseMove(200, 100, At(1.001)));
    }

    [Fact]
    public void The_grace_counts_from_the_start_not_from_the_first_move()
    {
        var rules = Rules();
        rules.OnMouseMove(100, 100, At(0.9));   // the first position, late inside the grace

        Assert.True(rules.OnMouseMove(300, 100, At(1.1)));   // 0.2 s after the first move, 1.1 s after the start
    }

    [Fact]
    public void A_first_move_after_the_grace_only_records_the_position()
    {
        var rules = Rules();

        Assert.False(rules.OnMouseMove(100, 100, At(2.0)));
        Assert.False(rules.OnMouseMove(103, 100, At(2.1)));
        Assert.True(rules.OnMouseMove(200, 100, At(2.2)));
    }

    // ---- the distance ----

    [Theory]
    [InlineData(100, 100)]   // the same place
    [InlineData(101, 100)]
    [InlineData(100, 99)]
    [InlineData(104, 100)]   // exactly 4 on x
    [InlineData(96, 100)]    // exactly 4 on x, to the left
    [InlineData(100, 104)]   // exactly 4 on y
    [InlineData(100, 96)]
    [InlineData(104, 104)]   // 4 on both axes: neither is more than 4
    [InlineData(96, 96)]
    [InlineData(103, 103)]
    public void A_move_within_four_pixels_never_closes(int x, int y)
    {
        var rules = Rules();
        rules.OnMouseMove(100, 100, At(0.1));

        Assert.False(rules.OnMouseMove(x, y, At(5.0)));
    }

    [Theory]
    [InlineData(105, 100)]   // 5 on x
    [InlineData(95, 100)]    // 5 on x, to the left
    [InlineData(100, 105)]   // 5 on y
    [InlineData(100, 95)]    // 5 on y, up
    [InlineData(105, 105)]
    [InlineData(95, 95)]
    [InlineData(100, 400)]
    [InlineData(1000, 100)]
    [InlineData(0, 0)]
    public void A_move_more_than_four_pixels_on_either_axis_closes_after_the_grace(int x, int y)
    {
        var rules = Rules();
        rules.OnMouseMove(100, 100, At(0.1));

        Assert.True(rules.OnMouseMove(x, y, At(1.5)));
    }

    [Fact]
    public void The_distance_is_from_the_first_position_not_from_the_last_move()
    {
        var rules = Rules();
        rules.OnMouseMove(100, 100, At(0.1));

        // Small steps that drift away: each is 3 px from the previous one, so only the first position counts.
        Assert.False(rules.OnMouseMove(103, 100, At(1.5)));
        Assert.False(rules.OnMouseMove(104, 100, At(1.6)));
        Assert.True(rules.OnMouseMove(107, 100, At(1.7)));   // 7 from (100, 100), only 3 from the last move
    }

    [Fact]
    public void Moves_inside_the_grace_do_not_move_the_first_position()
    {
        var rules = Rules();
        rules.OnMouseMove(100, 100, At(0.1));
        rules.OnMouseMove(500, 500, At(0.5));   // inside the grace: ignored, not a new first position

        Assert.False(rules.OnMouseMove(102, 101, At(1.5)));   // near the FIRST position
        Assert.True(rules.OnMouseMove(500, 500, At(1.6)));    // far from the first position
    }

    [Fact]
    public void Negative_virtual_desktop_coordinates_work()
    {
        var rules = Rules();
        rules.OnMouseMove(-1920, -200, At(0.1));

        Assert.False(rules.OnMouseMove(-1917, -203, At(2.0)));
        Assert.True(rules.OnMouseMove(-1900, -200, At(2.1)));
    }

    [Fact]
    public void A_small_move_does_not_close_even_long_after_the_grace()
    {
        var rules = Rules();
        rules.OnMouseMove(640, 360, At(0.0));

        Assert.False(rules.OnMouseMove(642, 361, At(3600)));
    }

    [Fact]
    public void Two_instances_do_not_share_a_first_position()
    {
        var a = Rules();
        var b = Rules();
        a.OnMouseMove(100, 100, At(0.1));
        b.OnMouseMove(900, 900, At(0.1));

        Assert.True(a.OnMouseMove(900, 900, At(2.0)));
        Assert.False(b.OnMouseMove(900, 902, At(2.0)));
    }
}
