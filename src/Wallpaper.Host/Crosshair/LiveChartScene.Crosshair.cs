using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Render.D2D;
using BitcoinWallpaper.Host.Crosshair;

namespace BitcoinWallpaper.Host.Scene;

/// <summary>The crosshair (D-35) of the live chart: the host's cursor goes into every frame drawn.</summary>
internal sealed partial class LiveChartScene : ICrosshairScene
{
    private CursorPx? _cursor;

    /// <summary>The last frame drawn, without the cursor: what the crosshair is computed against between frames.</summary>
    private ChartFrame? _lastFrame;

    public CrosshairState? SetCursor(CursorPx? cursor)
    {
        _cursor = cursor;
        return cursor is null || _lastFrame is null ? null : FrameView.Crosshair(_lastFrame with { Cursor = cursor });
    }
}
