// Contract #4, criterion 5: the fixture mockups compared with golden PNGs within a small tolerance.
//
// The golden PNGs are the offline mockups of the recorded snapshot in Fixtures/ (time axis in UTC). After an
// intended change of the look, regenerate them and review the new PNGs in the PR:
//   dotnet run --project tools/RenderMockup -- --offline --out tests/Chart.Render.Tests/Golden
// A failing comparison writes the actual image and a diff to bin/.../golden-failures/ (uploaded by CI).

using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Tools.RenderMockup;
using Xunit.Abstractions;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class GoldenImageTests(RendererFixture fx, ITestOutputHelper output)
{
    public static TheoryData<string> Screens => new(MockupScreens.All.Select(s => s.FileName));

    [Theory]
    [MemberData(nameof(Screens))]
    public void Fixture_mockup_matches_its_golden_png(string fileName)
    {
        var frame = fx.Frame(fileName);
        var actual = fx.Renderer.Render(frame);
        var expected = fx.Renderer.LoadPng(Path.Combine(RendererFixture.GoldenDirectory, fileName));

        var diff = ImageDiff.Compare(expected, actual);
        output.WriteLine($"{fileName}: {diff}");
        if (!diff.WithinTolerance)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "golden-failures");
            fx.Renderer.SavePng(actual, Path.Combine(dir, "actual-" + fileName));
            fx.Renderer.SavePng(ImageDiff.Visualize(expected, actual), Path.Combine(dir, "diff-" + fileName));
        }

        Assert.True(diff.WithinTolerance, $"{fileName}: {diff}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Negative_control_a_changed_last_price_fails_the_comparison(string fileName)
    {
        // The newest candle closes 37.50 higher: the headline price, the price tag and one candle change.
        var frame = fx.Frame(fileName);
        var candles = frame.Candles.ToArray();
        var last = candles[^1];
        candles[^1] = last with { Close = last.Close + 37.5m, High = Math.Max(last.High, last.Close + 37.5m) };
        var changed = fx.Renderer.Render(frame with { Candles = candles });
        var expected = fx.Renderer.LoadPng(Path.Combine(RendererFixture.GoldenDirectory, fileName));

        var diff = ImageDiff.Compare(expected, changed);
        output.WriteLine($"{fileName} with a changed price: {diff}");
        Assert.False(diff.WithinTolerance, $"a changed price passed as equal: {diff}");
    }

    [Fact]
    public void Golden_pngs_have_the_owner_screen_sizes()
    {
        foreach (var screen in MockupScreens.All)
        {
            var golden = fx.Renderer.LoadPng(Path.Combine(RendererFixture.GoldenDirectory, screen.FileName));
            Assert.Equal((screen.WidthPx, screen.HeightPx), (golden.Width, golden.Height));
        }

        Assert.Equal(
            [(5120, 1440, 1.75), (3840, 2160, 1.0)],
            MockupScreens.All.Select(s => (s.WidthPx, s.HeightPx, s.DpiScale)).ToArray());
    }
}
