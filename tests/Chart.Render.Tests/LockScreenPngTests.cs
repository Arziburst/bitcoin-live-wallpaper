using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

// Contract 42, criterion 4 (D-34 step 2): the lock screen snapshot is the wallpaper's own frame rendered offscreen
// at the monitor's resolution and written as a PNG file.
//
// API SKETCH (written before the code, #237). src/Chart.Render.D2D/HeadlessRenderer.cs gets one method:
//
//   // Renders the frame (as Render does) and writes it as a PNG at `path` (folder created when missing; an
//   // existing file is replaced). The PNG has exactly the frame's WidthPx x HeightPx. Nothing else is written
//   // next to it when it returns (no leftover temp file).
//   public void RenderToPng(ChartFrame frame, string path);
[Collection(nameof(RendererCollection))]
public sealed class LockScreenPngTests(RendererFixture fx) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bw-lockpng-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("btc-5120x1440.png", 5120, 1440)]
    [InlineData("btc-3840x2160.png", 3840, 2160)]
    public void A_frame_is_written_as_a_png_of_the_monitor_size_and_not_empty(string fileName, int w, int h)
    {
        var path = Path.Combine(_folder, "lock", "lock_20261031T120000Z.png");

        fx.Renderer.RenderToPng(fx.Frame(fileName), path);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 10_000, $"the PNG is only {bytes.Length} bytes");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        var image = fx.Renderer.LoadPng(path);
        Assert.Equal((w, h), (image.Width, image.Height));
        var first = BitConverter.ToUInt32(image.Pixels, 0);
        var different = false;
        for (var i = 4; i < image.Pixels.Length && !different; i += 4)
        {
            different = BitConverter.ToUInt32(image.Pixels, i) != first;
        }

        Assert.True(different, "the PNG is one flat colour");
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void The_png_shows_the_same_picture_as_the_frame_renders()
    {
        var frame = fx.Frame("btc-3840x2160.png");
        var path = Path.Combine(_folder, "same.png");

        fx.Renderer.RenderToPng(frame, path);

        var expected = fx.Renderer.Render(frame);
        var actual = fx.Renderer.LoadPng(path);
        Assert.Equal(expected.Width, actual.Width);
        Assert.True(ImageDiff.Compare(expected, actual).MaxDelta == 0, "the PNG differs from the rendered frame");
    }

    [Fact]
    public void An_existing_file_is_replaced()
    {
        var path = Path.Combine(_folder, "again.png");
        fx.Renderer.RenderToPng(fx.Frame("btc-3840x2160.png"), path);
        var first = File.ReadAllBytes(path);

        fx.Renderer.RenderToPng(fx.Frame("btc-5120x1440.png"), path);

        Assert.Equal(5120, fx.Renderer.LoadPng(path).Width);
        Assert.NotEqual(first.Length, File.ReadAllBytes(path).Length);
    }
}