using System.Numerics;
using BitcoinWallpaper.Chart.Core.Desktop;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace BitcoinWallpaper.Host;

/// <summary>
/// The design spec's background without the chart (coordinator/research/design-data.md): base #0E131A, a
/// vertical gradient #131A24 -> #0A0E13 and a radial vignette darkening the edges to rgba(0,0,0,0.35).
/// </summary>
internal sealed class PlaceholderScene : IWallpaperScene
{
    private static readonly Color4 Base = Rgb(0x0E, 0x13, 0x1A);
    private static readonly Color4 Top = Rgb(0x13, 0x1A, 0x24);
    private static readonly Color4 Bottom = Rgb(0x0A, 0x0E, 0x13);
    private static readonly Color4 Clear = new(0f, 0f, 0f, 0f);
    private static readonly Color4 Edge = new(0f, 0f, 0f, 0.35f);

    /// <summary>Where the vignette starts, as a fraction of the way from the centre to the corners.</summary>
    private const float VignetteStart = 0.55f;

    public void Draw(ID2D1DeviceContext context, SurfacePlacement placement)
    {
        float width = placement.Width;
        float height = placement.Height;
        var all = new Rect(0, 0, width, height);

        context.Clear(Base);

        using (var stops = context.CreateGradientStopCollection([new GradientStop(0f, Top), new GradientStop(1f, Bottom)]))
        using (var gradient = context.CreateLinearGradientBrush(
                   new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(0, height)), stops))
        {
            context.FillRectangle(all, gradient);
        }

        // An ellipse with the surface's aspect whose rim passes through the corners (radii x sqrt 2).
        var radiusX = width / 2 * MathF.Sqrt(2);
        var radiusY = height / 2 * MathF.Sqrt(2);
        using (var stops = context.CreateGradientStopCollection(
                   [new GradientStop(0f, Clear), new GradientStop(VignetteStart, Clear), new GradientStop(1f, Edge)]))
        using (var vignette = context.CreateRadialGradientBrush(
                   new RadialGradientBrushProperties(new Vector2(width / 2, height / 2), Vector2.Zero, radiusX, radiusY), stops))
        {
            context.FillRectangle(all, vignette);
        }
    }

    private static Color4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
