namespace BitcoinWallpaper.Chart.Core.Layout;

/// <summary>The chart layout of the design spec (coordinator/research/design-data.md, "Design spec"), in physical px.
/// Pure: the same screen always gives the same layout.</summary>
public static class LayoutEngine
{
    private const double IconSafeShare = 0.08;
    private const double AxisRightShare = 0.97;
    private const double BodyShare = 0.62;
    private const int RightOffset = 8;

    // Aspect ratio at or above which the headline goes top-right (32:9, 21:9); below it top-left at x = 10 %.
    private const double WideAspect = 2.2;

    public static DisplayProfile ProfileFor(int widthPx, int heightPx, double dpiScale)
    {
        var aspect = (double)widthPx / heightPx;
        return Math.Abs(dpiScale - 1.0) <= 0.01 && widthPx >= 3840 && heightPx >= 2160 &&
               Math.Abs(aspect - (16.0 / 9.0)) <= 0.01
            ? DisplayProfile.Tv
            : DisplayProfile.Desk;
    }

    public static ChartLayout Compute(int widthPx, int heightPx, double dpiScale, int candleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPx);
        if (!(dpiScale > 0) || double.IsInfinity(dpiScale))
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale), dpiScale, "The DPI scale must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(candleCount);

        double w = widthPx, h = heightPx;
        var profile = ProfileFor(widthPx, heightPx, dpiScale);
        var tv = profile == DisplayProfile.Tv;
        var text = TextFor(h, tv);

        // Horizontal: icons | plot | price axis | margin.
        var plotX = Math.Ceiling(IconSafeShare * w);
        var axisRight = Math.Round(AxisRightShare * w);
        var axisWidth = Math.Ceiling(6.6 * text.AxisLabelPx); // "77,490.96" in mono plus padding
        var plotWidth = axisRight - axisWidth - plotX;

        // Vertical: margin | plot | gap | volume | time axis | margin.
        var top = Math.Round(0.03 * h);
        var bottom = Math.Round(0.015 * h);
        var timeAxisHeight = Math.Ceiling(2.0 * text.AxisLabelPx);
        var gap = Math.Round(0.006 * h);
        var chartBottom = h - bottom - timeAxisHeight;
        var chartHeight = chartBottom - top - gap;
        var volumeHeight = Math.Round(chartHeight * VolumeShare(w / h));
        var plotHeight = chartHeight - volumeHeight;

        var plot = new RectPx(plotX, top, plotWidth, plotHeight);
        var volume = new RectPx(plotX, plot.Bottom + gap, plotWidth, volumeHeight);
        var priceAxis = new RectPx(plot.Right, top, axisWidth, plotHeight);
        var timeAxis = new RectPx(plotX, chartBottom, plotWidth, timeAxisHeight);
        var headline = HeadlineRect(w, h, text, plot);

        var pitch = Math.Max(tv ? 24 : 15, Math.Round(0.011 * h));
        return new ChartLayout(
            widthPx,
            heightPx,
            dpiScale,
            profile,
            plot,
            priceAxis,
            timeAxis,
            volume,
            headline,
            new RectPx(0, 0, IconSafeShare * w, h),
            text,
            pitch,
            Math.Round(BodyShare * pitch),
            tv ? 2 : 1,
            candleCount,
            RightOffset);
    }

    private static TextSizes TextFor(double h, bool tv)
    {
        if (tv)
        {
            // Distance factor 1.3 on the secondary text, 34 px minimum for everything.
            const double distance = 1.3;
            return new TextSizes(
                Math.Max(0.08 * h, 34),
                Math.Max(0.016 * h * distance, 34),
                Math.Max(0.024 * h * distance, 34),
                Math.Max(0.015 * h * distance, 34),
                Math.Max(0.020 * h, 34));
        }

        // Secondary text never below 22 px, so 1080p stays readable.
        return new TextSizes(
            0.06 * h,
            Math.Max(0.016 * h, 22),
            Math.Max(0.024 * h, 22),
            Math.Max(0.015 * h, 22),
            Math.Clamp(0.017 * h, 22, 44));
    }

    // 16 % of the chart height at 16:9 (and narrower), 14 % at 32:9, linear in between.
    private static double VolumeShare(double aspect)
    {
        const double wide = 32.0 / 9.0, normal = 16.0 / 9.0;
        var t = Math.Clamp((aspect - normal) / (wide - normal), 0, 1);
        return 0.16 - (0.02 * t);
    }

    private static RectPx HeadlineRect(double w, double h, TextSizes text, RectPx plot)
    {
        var padding = 0.4 * text.PairSourcePx;
        var height = Math.Ceiling(
            (1.5 * text.PairSourcePx) + (1.2 * text.HeadlinePricePx) + (1.45 * text.ChangePx) +
            (1.5 * text.StatsRowPx) + (2 * padding));
        var gap = Math.Round(0.006 * w);

        if (w / h >= WideAspect)
        {
            // Top-right, next to the price axis.
            var width = Math.Ceiling(8 * text.HeadlinePricePx);
            var right = plot.Right - gap;
            return new RectPx(right - width, plot.Y, width, height);
        }

        // Top-left, right of the icon safe area.
        var x = Math.Round(0.10 * w);
        var fit = Math.Floor(plot.Right - gap - x);
        return new RectPx(x, plot.Y, Math.Min(Math.Ceiling(8 * text.HeadlinePricePx), fit), height);
    }
}
