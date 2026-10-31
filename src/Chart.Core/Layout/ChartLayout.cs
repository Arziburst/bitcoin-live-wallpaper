namespace BitcoinWallpaper.Chart.Core.Layout;

/// <summary>Where everything goes on one screen, in physical px. Built by <see cref="LayoutEngine.Compute"/>.</summary>
public sealed class ChartLayout
{
    internal ChartLayout(
        int widthPx,
        int heightPx,
        double dpiScale,
        DisplayProfile profile,
        RectPx plot,
        RectPx priceAxis,
        RectPx timeAxis,
        RectPx volumePane,
        RectPx headline,
        RectPx iconSafeArea,
        TextSizes text,
        double candlePitchPx,
        double candleBodyWidthPx,
        double wickWidthPx,
        int candleCount,
        int rightOffsetBars)
    {
        WidthPx = widthPx;
        HeightPx = heightPx;
        DpiScale = dpiScale;
        Profile = profile;
        Plot = plot;
        PriceAxis = priceAxis;
        TimeAxis = timeAxis;
        VolumePane = volumePane;
        Headline = headline;
        IconSafeArea = iconSafeArea;
        Text = text;
        CandlePitchPx = candlePitchPx;
        CandleBodyWidthPx = candleBodyWidthPx;
        WickWidthPx = wickWidthPx;
        CandleCount = candleCount;
        RightOffsetBars = rightOffsetBars;
        CandleSlots = (int)Math.Floor((plot.Width / candlePitchPx) + 1e-9);
        DrawnCandleCount = Math.Min(candleCount, Math.Max(0, CandleSlots - rightOffsetBars));
        FirstDrawnIndex = candleCount - DrawnCandleCount;
    }

    public int WidthPx { get; }

    public int HeightPx { get; }

    public double DpiScale { get; }

    public DisplayProfile Profile { get; }

    /// <summary>The candle (price) pane.</summary>
    public RectPx Plot { get; }

    /// <summary>Price labels, right of the plot, spanning its height.</summary>
    public RectPx PriceAxis { get; }

    /// <summary>Time labels, under the volume pane, same x-range as the plot.</summary>
    public RectPx TimeAxis { get; }

    /// <summary>Volume bars, directly under the plot, same x-range as the plot.</summary>
    public RectPx VolumePane { get; }

    /// <summary>Price, 24h change, H/L/volume row, pair and source. Lies over the plot (with a scrim).</summary>
    public RectPx Headline { get; }

    /// <summary>The left 8 % of the screen, full height: desktop icons; no text here.</summary>
    public RectPx IconSafeArea { get; }

    public TextSizes Text { get; }

    /// <summary>Slot width per candle.</summary>
    public double CandlePitchPx { get; }

    public double CandleBodyWidthPx { get; }

    public double WickWidthPx { get; }

    /// <summary>The number of candles the layout was computed for (index 0 = oldest).</summary>
    public int CandleCount { get; }

    /// <summary>floor(Plot.Width / CandlePitchPx).</summary>
    public int CandleSlots { get; }

    /// <summary>Empty slots right of the newest candle.</summary>
    public int RightOffsetBars { get; }

    /// <summary>min(CandleCount, CandleSlots - RightOffsetBars): the newest candles that fit.</summary>
    public int DrawnCandleCount { get; }

    /// <summary>CandleCount - DrawnCandleCount: the index of the oldest drawn candle.</summary>
    public int FirstDrawnIndex { get; }

    /// <summary>Horizontal centre of the candle at <paramref name="index"/> (0 = oldest); the newest candle sits
    /// RightOffsetBars slots left of the plot's right edge.</summary>
    public double CandleCenterX(int index) =>
        Plot.X + Plot.Width - ((RightOffsetBars + (CandleCount - 1 - index) + 0.5) * CandlePitchPx);
}
