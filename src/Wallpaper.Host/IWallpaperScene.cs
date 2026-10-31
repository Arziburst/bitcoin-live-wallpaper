using BitcoinWallpaper.Chart.Core.Desktop;
using Vortice.Direct2D1;

namespace BitcoinWallpaper.Host;

/// <summary>
/// What the host draws on its surface. The seam for #8: the chart renderer implements this and replaces
/// <see cref="PlaceholderScene"/> in Program.cs; when its data changes it calls
/// <see cref="WallpaperHost.RequestRedraw"/>, and the pause monitor calls <see cref="WallpaperHost.SetPaused"/>.
/// </summary>
internal interface IWallpaperScene
{
    /// <summary>
    /// Draws one full frame. The context targets the swap chain's back buffer at 96 dpi, so one unit is one
    /// physical pixel; the surface is placement.Width x placement.Height px and placement.Scale is the
    /// monitor's DPI scale for sizing text. Called on the host's thread, between BeginDraw and EndDraw.
    /// </summary>
    void Draw(ID2D1DeviceContext context, SurfacePlacement placement);
}
