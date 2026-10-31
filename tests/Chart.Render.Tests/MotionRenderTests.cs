// Contract #8: the renderer draws the motion of a LiveFrame (D-10) - the new-bar shift moves the series and
// nothing else, the price pulse tints the headline and nothing else; a still frame is unchanged (golden images).

using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class MotionRenderTests(RendererFixture fx)
{
    private const string Tv = "btc-3840x2160.png";

    [Fact]
    public void A_shift_moves_the_series_but_not_the_price_axis_nor_into_the_icon_area()
    {
        var frame = fx.Frame(Tv);
        var still = fx.Renderer.Render(frame);
        var shifted = fx.Renderer.Render(frame with { ShiftPitches = 1.0 });

        var plot = frame.Layout.Plot;
        var inPlot = Differing(still, shifted, (x, y) => x >= plot.X && x < plot.Right && y >= plot.Y && y < plot.Bottom);
        Assert.True(inPlot is not null, "a full-pitch shift left the plot unchanged");

        var axis = frame.Layout.PriceAxis;
        var onAxis = Differing(still, shifted, (x, y) => x >= Math.Ceiling(axis.X) && x < axis.Right && y >= axis.Y && y < axis.Bottom);
        Assert.True(onAxis is null, $"pixel {onAxis} of the price axis {axis} moved with the shift");

        var empty = fx.Renderer.Render(ChartFrame.Create(
            frame.Layout.WidthPx, frame.Layout.HeightPx, frame.Layout.DpiScale, [], null, frame.Now, frame.TimeZone));
        var safe = frame.Layout.IconSafeArea;
        var inSafe = Differing(shifted, empty, (x, _) => x < Math.Floor(safe.Right));
        Assert.True(inSafe is null, $"pixel {inSafe} in the icon safe area {safe} differs from the bare background while shifting");
    }

    [Fact]
    public void A_pulse_tints_the_headline_only_and_its_direction_shows()
    {
        var frame = fx.Frame(Tv);
        var still = fx.Renderer.Render(frame);
        var up = fx.Renderer.Render(frame with { Pulse = 1.0, PulseUp = true });
        var down = fx.Renderer.Render(frame with { Pulse = 1.0, PulseUp = false });

        var box = frame.Layout.Headline;
        bool Outside(int x, int y) => !(x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom);
        Assert.True(Differing(still, up, (_, _) => true) is not null, "a full pulse left the frame unchanged");
        var outside = Differing(still, up, Outside);
        Assert.True(outside is null, $"pixel {outside} outside the headline {box} changed with the pulse");
        Assert.True(Differing(up, down, (_, _) => true) is not null, "an up pulse and a down pulse look the same");
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
