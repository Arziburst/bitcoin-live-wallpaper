// Contract #4, criteria 4 and 7: headless rendering at the exact screen sizes; drawing is a pure function of the
// frame (layout and data), with no clock of its own.

using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class RendererTests(RendererFixture fx)
{
    private const string Desk = "btc-5120x1440.png";
    private const string Tv = "btc-3840x2160.png";

    [Theory]
    [InlineData(Desk, 5120, 1440, DisplayProfile.Desk)]
    [InlineData(Tv, 3840, 2160, DisplayProfile.Tv)]
    public void Renders_at_exactly_the_screen_size(string fileName, int w, int h, DisplayProfile profile)
    {
        var frame = fx.Frame(fileName);
        var image = fx.Renderer.Render(frame);
        Assert.Equal((w, h), (image.Width, image.Height));
        Assert.Equal(w * h * 4, image.Pixels.Length);
        Assert.Equal(profile, frame.Layout.Profile);
    }

    [Fact]
    public void The_same_frame_gives_the_same_pixels()
    {
        var frame = fx.Frame(Tv);
        var first = fx.Renderer.Render(frame);
        var second = fx.Renderer.Render(frame);
        Assert.Equal(first.Pixels, second.Pixels);
    }

    [Fact]
    public void Time_comes_from_the_frame_only()
    {
        // Ten seconds later the countdown text changes; nothing else does.
        var frame = fx.Frame(Tv);
        var now = fx.Renderer.Render(frame);
        var later = fx.Renderer.Render(frame with { Now = frame.Now.AddSeconds(10) });
        var diff = ImageDiff.Compare(now, later);
        Assert.True(diff.MaxDelta > 0, "the countdown did not follow the frame's time");

        var box = frame.Layout.Headline;
        var differing = Differing(now, later, (x, y) => !(x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom));
        Assert.True(differing is null, $"pixel {differing} outside the headline {box} changed with the time");
    }

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void Nothing_but_the_background_is_drawn_in_the_icon_safe_area(string fileName)
    {
        var frame = fx.Frame(fileName);
        var full = fx.Renderer.Render(frame);
        var empty = fx.Renderer.Render(ChartFrame.Create(
            frame.Layout.WidthPx, frame.Layout.HeightPx, frame.Layout.DpiScale, [], null, frame.Now, frame.TimeZone));

        var safe = frame.Layout.IconSafeArea;
        var differing = Differing(full, empty, (x, _) => x < Math.Floor(safe.Right));
        Assert.True(differing is null, $"pixel {differing} in the icon safe area {safe} differs from the bare background");
    }

    [Fact]
    public void A_layout_for_another_candle_count_is_refused()
    {
        var frame = fx.Frame(Desk);
        var wrong = frame with { Candles = frame.Candles.Take(10).ToArray() };
        Assert.Throws<ArgumentException>(() => fx.Renderer.Render(wrong));
    }

    [Fact]
    public void An_empty_frame_renders()
    {
        var frame = fx.Frame(Desk);
        var image = fx.Renderer.Render(ChartFrame.Create(1920, 1080, 1.0, [], null, frame.Now, TimeZoneInfo.Utc));
        Assert.Equal((1920, 1080), (image.Width, image.Height));
    }

    // The first pixel (x, y) inside the region where the two images differ, or null.
    private static (int X, int Y)? Differing(BgraImage a, BgraImage b, Func<int, int, bool> region)
    {
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var i = ((y * a.Width) + x) * 4;
                if (region(x, y) &&
                    (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2]))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }
}
