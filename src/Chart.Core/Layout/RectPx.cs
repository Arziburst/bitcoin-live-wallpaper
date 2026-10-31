namespace BitcoinWallpaper.Chart.Core.Layout;

/// <summary>A rectangle in physical pixels, origin top-left of the screen.</summary>
public readonly record struct RectPx(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}
