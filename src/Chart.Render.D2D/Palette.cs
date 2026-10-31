using Vortice.Mathematics;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>The colours of the design spec (coordinator/research/design-data.md, "Design spec").</summary>
internal static class Palette
{
    public static readonly Color4 Base = Hex(0x0E131A);
    public static readonly Color4 GradientTop = Hex(0x131A24);
    public static readonly Color4 GradientBottom = Hex(0x0A0E13);
    public static readonly Color4 Vignette = new(0, 0, 0, 0.35f);

    public static readonly Color4 Up = Hex(0x2EBD85);
    public static readonly Color4 Down = Hex(0xF0465A);
    public const float WickAlpha = 0.8f;
    public const float VolumeAlpha = 0.28f;

    public static readonly Color4 Ma20 = Hex(0x6EA8FF, 0.7f);
    public static readonly Color4 Ma50 = Hex(0xC084F5, 0.7f);

    public static readonly Color4 Grid = new(1, 1, 1, 0.06f);
    public static readonly Color4 GridDay = new(1, 1, 1, 0.12f);
    public static readonly Color4 AxisText = Hex(0x8A94A6);
    public static readonly Color4 PrimaryText = Hex(0xD8DEE6);
    public static readonly Color4 TagText = Hex(0x0E131A);

    // The crosshair (D-35, owner 2026-10-31): solid thin lines in a light grey-blue, lighter and bluer than the grid but
    // not white; its tags and info block are opaque dark boxes with a border of the same family.
    public static readonly Color4 Crosshair = Hex(0x8FA3B8, 0.55f);
    public static readonly Color4 CrosshairBorder = Hex(0x8FA3B8, 0.7f);
    public static readonly Color4 CrosshairTag = Hex(0x222B37);
    public static readonly Color4 CrosshairBlock = Hex(0x0E131A, 0.9f);

    /// <summary>Laid over a candle a crosshair line crosses: slightly brighter.</summary>
    public static readonly Color4 CandleLight = new(1, 1, 1, 0.2f);

    public static Color4 Hex(int rgb, float alpha = 1) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static Color4 WithAlpha(Color4 c, float alpha) => new(c.R, c.G, c.B, alpha);
}
