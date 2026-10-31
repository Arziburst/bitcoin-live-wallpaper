// Contract #47 (D-37), criterion 1: the background fills the whole monitor, the chart only the content (work area).
//
// API SKETCH (written before the code, #237). In src/Chart.Render.D2D/ChartFrame.cs:
//
//   public sealed record ChartFrame(...)
//   {
//       // content: the work area inside the screen (RectPx, physical px, relative to the screen); null = full screen.
//       // Create passes it to LayoutEngine.Compute (see tests/Chart.Core.Tests/WorkAreaLayoutTests.cs); the frame's
//       // Layout.Content is it. Everything else of the frame is as before.
//       public static ChartFrame Create(int widthPx, int heightPx, double dpiScale, IReadOnlyList<Candle> candles,
//           Ticker24h? ticker, DateTimeOffset now, TimeZoneInfo timeZone, string pair = "BTC/USDT",
//           string source = "Binance", bool isStale = false, double shiftPitches = 0, double pulse = 0,
//           bool pulseUp = false, TimeSpan? interval = null, RectPx? content = null);
//   }
//
// Rendering: the frame's pixel size is the screen's; the background (base colour, gradient, vignette) covers the whole
// screen; no chart element (text, grid, candles, bars) is drawn outside the layout's Content. With content = full
// screen (or null) the picture is pixel-identical to today's (the golden images stay as they are).

using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class WorkAreaRenderTests(RendererFixture fx)
{
    private const string Desk = "btc-5120x1440.png";
    private const string Tv = "btc-3840x2160.png";
    private const int Dim = 0x30;    // the background (gradient 0x0A..0x24, vignette darker) never exceeds this per channel
    private const int Bright = 0x60; // chart text and candles exceed this

    public static TheoryData<string, int, int, double, int> Cases => new()
    {
        { Desk, 5120, 1440, 1.75, 84 },
        { Tv, 3840, 2160, 1.0, 96 },
    };

    private ChartFrame Make(string file, int w, int h, double dpi, RectPx? content)
    {
        var f = fx.Frame(file);
        return ChartFrame.Create(w, h, dpi, f.Candles, f.Ticker, f.Now, f.TimeZone, interval: f.Interval, content: content);
    }

    private static int MaxChannel(BgraImage im, int x0, int y0, int x1, int y1)
    {
        var max = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * im.Width) + x) * 4;
                max = Math.Max(max, Math.Max(im.Pixels[i], Math.Max(im.Pixels[i + 1], im.Pixels[i + 2])));
            }
        }

        return max;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Content_equal_to_the_full_screen_renders_pixel_identical_to_no_content(string file, int w, int h, double dpi, int bar)
    {
        _ = bar;
        var plain = fx.Renderer.Render(Make(file, w, h, dpi, null));
        var full = fx.Renderer.Render(Make(file, w, h, dpi, new RectPx(0, 0, w, h)));
        Assert.True(plain.Pixels.AsSpan().SequenceEqual(full.Pixels));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_bottom_taskbar_leaves_its_rows_background_only_and_the_chart_above_it(string file, int w, int h, double dpi, int bar)
    {
        var frame = Make(file, w, h, dpi, new RectPx(0, 0, w, h - bar));
        Assert.Equal(w, frame.Layout.WidthPx);
        Assert.Equal(h, frame.Layout.HeightPx);
        var image = fx.Renderer.Render(frame);
        Assert.Equal(w, image.Width);
        Assert.Equal(h, image.Height);

        var strip = MaxChannel(image, 0, h - bar, w, h);
        Assert.True(strip <= Dim, $"chart pixels under the taskbar rows: max channel {strip:X2}");
        Assert.True(MaxChannel(image, 0, (int)frame.Layout.TimeAxis.Y, w, h - bar) >= Bright, "the time axis labels are drawn above the bar");
        Assert.True(frame.Layout.TimeAxis.Bottom <= h - bar);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_taskbar_on_any_side_leaves_that_side_background_only(string file, int w, int h, double dpi, int bar)
    {
        foreach (var (content, x0, y0, x1, y1) in new[]
        {
            (new RectPx(0, bar, w, h - bar), 0, 0, w, bar),         // top
            (new RectPx(bar, 0, w - bar, h), 0, 0, bar, h),         // left
            (new RectPx(0, 0, w - bar, h), w - bar, 0, w, h),       // right
        })
        {
            var image = fx.Renderer.Render(Make(file, w, h, dpi, content));
            var max = MaxChannel(image, x0, y0, x1, y1);
            Assert.True(max <= Dim, $"content {content}: chart pixels in the bar ({x0},{y0})-({x1},{y1}): max channel {max:X2}");
            Assert.True(MaxChannel(image, (int)content.X, (int)content.Y, (int)content.Right, (int)content.Bottom) >= Bright);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_background_under_the_bar_is_the_same_as_without_a_bar(string file, int w, int h, double dpi, int bar)
    {
        // The vignette and gradient depend on the screen, not on the content: the corner pixels of the bar rows match.
        var plain = fx.Renderer.Render(Make(file, w, h, dpi, null));
        var barred = fx.Renderer.Render(Make(file, w, h, dpi, new RectPx(0, 0, w, h - bar)));
        foreach (var (x, y) in new[] { (0, h - 1), (w - 1, h - 1), (0, h - bar), (w - 1, h - bar) })
        {
            var i = ((y * w) + x) * 4;
            Assert.Equal(plain.Pixels[i], barred.Pixels[i]);
            Assert.Equal(plain.Pixels[i + 1], barred.Pixels[i + 1]);
            Assert.Equal(plain.Pixels[i + 2], barred.Pixels[i + 2]);
        }
    }
}
