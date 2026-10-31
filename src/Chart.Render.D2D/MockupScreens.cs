namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>One mockup: a screen and the PNG it is written to.</summary>
public sealed record MockupScreen(string FileName, int WidthPx, int HeightPx, double DpiScale);

/// <summary>The owner's two screens (D-3, D-9): the desk monitor at 175 % and the TV at 100 % (TV profile).</summary>
public static class MockupScreens
{
    public static IReadOnlyList<MockupScreen> All { get; } =
    [
        new("btc-5120x1440.png", 5120, 1440, 1.75),
        new("btc-3840x2160.png", 3840, 2160, 1.0),
    ];
}
