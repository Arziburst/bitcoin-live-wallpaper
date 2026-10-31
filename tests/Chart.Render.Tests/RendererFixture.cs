using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Tools.RenderMockup;

namespace BitcoinWallpaper.Chart.Render.Tests;

/// <summary>One headless renderer and the recorded snapshot, shared by the rendering tests.</summary>
public sealed class RendererFixture : IDisposable
{
    public static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static readonly string GoldenDirectory = Path.Combine(AppContext.BaseDirectory, "Golden");

    private readonly Dictionary<string, ChartFrame> frames;

    public RendererFixture()
    {
        Renderer = new HeadlessRenderer();
        Snapshot = Snapshot.Load(FixtureDirectory);
        frames = Snapshot.Frames(TimeZoneInfo.Utc).ToDictionary(f => f.Screen.FileName, f => f.Frame);
    }

    public HeadlessRenderer Renderer { get; }

    public Snapshot Snapshot { get; }

    public ChartFrame Frame(string fileName) => frames[fileName];

    public void Dispose() => Renderer.Dispose();
}

[CollectionDefinition(nameof(RendererCollection))]
public sealed class RendererCollection : ICollectionFixture<RendererFixture>;
