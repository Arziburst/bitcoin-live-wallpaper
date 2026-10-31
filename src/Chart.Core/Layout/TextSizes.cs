namespace BitcoinWallpaper.Chart.Core.Layout;

/// <summary>Text sizes (font em size) in physical px, derived from the screen height, never scaled down by
/// the Windows DPI scale.</summary>
public sealed record TextSizes(
    double HeadlinePricePx,
    double PairSourcePx,
    double ChangePx,
    double StatsRowPx,
    double AxisLabelPx);
