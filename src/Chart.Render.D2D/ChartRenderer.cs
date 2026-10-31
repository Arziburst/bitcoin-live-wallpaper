using System.Globalization;
using System.Numerics;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Indicators;
using BitcoinWallpaper.Chart.Core.Layout;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>Draws one <see cref="ChartFrame"/> into any Direct2D render target (a WIC bitmap, a swap chain's
/// device context). A pure function of the frame: no clock, timer or animation; the same frame on the same target
/// type gives the same pixels. The caller owns BeginDraw / EndDraw. Not thread-safe: one renderer per thread.</summary>
public sealed partial class ChartRenderer : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>D2D1_DEFAULT_FLATTENING_TOLERANCE.</summary>
    private const float DefaultFlatteningTolerance = 0.25f;

    private readonly IDWriteFactory5 dwrite;
    private readonly ChartFonts fonts;
    private readonly IDWriteTypography typography;
    private readonly IDWriteRenderingParams renderingParams;
    private readonly Dictionary<(string Family, FontWeight Weight, float Size), IDWriteTextFormat> formats = [];
    private Dictionary<LayoutKey, IDWriteTextLayout> layouts = [];
    private Dictionary<LayoutKey, IDWriteTextLayout> previousLayouts = [];

    // On any target that is an ID2D1DeviceContext1 (D-6) - the swap chain's context, and on current Windows also the
    // headless WIC target - long dotted/dashed lines and the settled part of the averages are tessellated once and
    // kept as geometry realizations (device-dependent) instead of 120 times a second. Realized lines differ slightly
    // in antialiasing from direct drawing: the golden images still pass within their tolerance (max delta about 100
    // on the dotted 24h and MA lines, mean delta about 0.02). A target without DeviceContext1 draws directly.
    private ID2D1DeviceContext1? realizedOn; // held, so a new context never reuses its address
    private readonly Dictionary<(string Kind, float Length, float Width), ID2D1GeometryRealization> realizedLines = [];
    private readonly RealizedAverage[] realizedAverages = [new(), new()];

    public ChartRenderer(IDWriteFactory5 dwrite, ChartFonts fonts)
    {
        this.dwrite = dwrite ?? throw new ArgumentNullException(nameof(dwrite));
        this.fonts = fonts ?? throw new ArgumentNullException(nameof(fonts));

        // Tabular, slashed-zero figures, so a changing price never shifts its neighbours.
        typography = dwrite.CreateTypography();
        typography.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.TabularFigures, Parameter = 1 });
        typography.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.SlashedZero, Parameter = 1 });

        // Fixed text rendering, independent of the machine's ClearType tuning: grayscale, symmetric smoothing.
        renderingParams = dwrite.CreateCustomRenderingParams(1.8f, 0.5f, 0f, PixelGeometry.Flat, RenderingMode.NaturalSymmetric);
    }

    public void Draw(ID2D1RenderTarget target, ChartFrame frame)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Layout.CandleCount != frame.Candles.Count)
        {
            throw new ArgumentException(
                $"The layout is for {frame.Layout.CandleCount} candles, the frame has {frame.Candles.Count}.", nameof(frame));
        }

        target.Transform = Matrix3x2.Identity;
        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        target.TextRenderingParams = renderingParams;

        using var paint = new Paint(target);
        var scene = new Scene(frame);
        AgeLayouts();

        DrawBackground(target, frame.Layout);
        DrawGrid(target, paint, scene);
        DrawVolume(target, paint, scene);

        target.PushAxisAlignedClip(ToRect(frame.Layout.Plot), AntialiasMode.Aliased);
        DrawDayHighLow(target, paint, scene);
        DrawAverages(target, paint, scene);
        DrawCandles(target, paint, scene);
        DrawLitCandles(target, scene);
        DrawLastPriceLine(target, paint, scene);
        DrawDayLabels(target, paint, scene);
        target.PopAxisAlignedClip();

        DrawHeadline(target, paint, scene);
        DrawPriceAxis(target, paint, scene);
        DrawTimeAxis(target, paint, scene);
        DrawCrosshair(target, scene);
    }

    public void Dispose()
    {
        foreach (var format in formats.Values)
        {
            format.Dispose();
        }

        formats.Clear();
        foreach (var layout in layouts.Values.Concat(previousLayouts.Values))
        {
            layout.Dispose();
        }

        layouts.Clear();
        previousLayouts.Clear();
        DropRealizations();
        renderingParams.Dispose();
        typography.Dispose();
    }

    // ---- background ---------------------------------------------------------------------------

    private static void DrawBackground(ID2D1RenderTarget target, ChartLayout layout)
    {
        float w = layout.WidthPx, h = layout.HeightPx;
        target.Clear(Palette.Base);

        using (var stops = target.CreateGradientStopCollection(
                   [new GradientStop(0, Palette.GradientTop), new GradientStop(1, Palette.GradientBottom)]))
        using (var gradient = target.CreateLinearGradientBrush(
                   new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(0, h)), stops))
        {
            target.FillRectangle(new Rect(0, 0, w, h), gradient);
        }

        // Radial vignette: clear in the middle, rgba(0,0,0,0.35) at the edges.
        var clear = Palette.WithAlpha(Palette.Vignette, 0);
        using (var stops = target.CreateGradientStopCollection(
                   [new GradientStop(0, clear), new GradientStop(0.5f, clear), new GradientStop(1, Palette.Vignette)]))
        using (var vignette = target.CreateRadialGradientBrush(
                   new RadialGradientBrushProperties(new Vector2(w / 2, h / 2), Vector2.Zero, w * 0.62f, h * 0.9f), stops))
        {
            target.FillRectangle(new Rect(0, 0, w, h), vignette);
        }
    }

    // ---- grid, watermark, volume --------------------------------------------------------------

    private static void DrawGrid(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var plot = s.Layout.Plot;
        target.AntialiasMode = AntialiasMode.Aliased;
        foreach (var level in s.PriceLevels)
        {
            var y = Snap(s.Scale.Y(level));
            if (y > plot.Y + 1 && y < plot.Bottom - 1)
            {
                target.FillRectangle(new Rect((float)plot.X, y, (float)plot.Width, 1), paint.Grid);
            }
        }

        var top = (float)plot.Y;
        var bottom = (float)s.Layout.VolumePane.Bottom;
        foreach (var tick in s.TimeTicks)
        {
            var x = (float)Math.Floor(tick.X);
            target.FillRectangle(new Rect(x, top, 1, bottom - top), tick.IsDay ? paint.GridDay : paint.Grid);
        }
    }

    private static void DrawVolume(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var l = s.Layout;
        var pane = l.VolumePane;
        var candles = s.Candles;
        var max = 0m;
        for (var i = l.FirstDrawnIndex; i < candles.Count; i++)
        {
            max = Math.Max(max, candles[i].Volume);
        }

        if (max <= 0)
        {
            return;
        }

        target.AntialiasMode = AntialiasMode.Aliased;
        var body = (float)l.CandleBodyWidthPx;
        var floor = (float)Math.Round(pane.Bottom);

        // The bar of the candle cut by the left edge, and of the one sliding out while shifting, end at the pane.
        target.PushAxisAlignedClip(ToRect(pane), AntialiasMode.Aliased);

        for (var i = s.FirstIndex; i < candles.Count; i++)
        {
            var c = candles[i];
            var height = (float)Math.Round((double)(c.Volume / max) * pane.Height * 0.92);
            if (height < 1)
            {
                continue;
            }

            var x = SnapLeft(s.CenterX(i), body);
            target.FillRectangle(new Rect(x, floor - height, body, height), IsUp(c) ? paint.VolumeUp : paint.VolumeDown);
        }

        target.PopAxisAlignedClip();
    }

    // ---- plot content -------------------------------------------------------------------------

    private void DrawDayHighLow(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        if (s.DayHighLow is not { } hl)
        {
            return;
        }

        var l = s.Layout;
        var plot = l.Plot;
        var width = LineWidth(l) * 1.5f;
        using var dots = target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties
            {
                DashStyle = DashStyle.Custom,
                DashCap = CapStyle.Round,
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Round,
            },
            [0.01f, 2.6f]);
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        using var gpu = Gpu(target);
        foreach (var price in new[] { hl.High, hl.Low })
        {
            if (!s.OnScale(price))
            {
                continue;
            }

            var yy = Snap(s.Scale.Y(price)) + 0.5f;
            if (gpu is not null)
            {
                var before = target.Transform;
                target.Transform = Matrix3x2.CreateTranslation((float)plot.X, yy) * before;
                gpu.DrawGeometryRealization(RealizedLine(gpu, "dots", (float)plot.Width, width, dots), paint.DayLine);
                target.Transform = before;
            }
            else
            {
                target.DrawLine(new Vector2((float)plot.X, yy), new Vector2((float)plot.Right, yy), paint.DayLine, width, dots);
            }
        }
    }

    // The labels of the 24h lines and the edge markers, drawn over the candles, so the candles at the left edge never
    // hide them.
    private void DrawDayLabels(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        if (s.DayHighLow is not { } hl)
        {
            return;
        }

        var l = s.Layout;
        var plot = l.Plot;
        var labelSize = l.Text.AxisLabelPx * 0.82;
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var (price, name, above) in new[] { (hl.High, "24h high", true), (hl.Low, "24h low", false) })
        {
            if (!s.OnScale(price))
            {
                continue;
            }

            var y = s.Scale.Y(price);
            var text = CachedLayout(name, ChartFonts.Inter, FontWeight.Medium, labelSize, plot.Width, TextAlignment.Leading);
            var tw = text.Metrics.Width;
            var th = text.Metrics.Height;
            var pad = l.Text.AxisLabelPx * 0.6;
            var ty = above ? y - th - (labelSize * 0.15) : y + (labelSize * 0.15);
            var tx = plot.X + pad;
            var box = new RectPx(tx, ty, tw, th);
            if (Intersects(box, l.Headline))
            {
                tx = plot.Right - pad - tw - (l.RightOffsetBars * l.CandlePitchPx);
            }

            target.DrawTextLayout(new Vector2((float)tx, (float)ty), text, paint.DayLabel);
        }

        // Off the scale (D-11): a marker at the plot's edge, in the axis text style, in the dotted line's colour.
        foreach (var marker in s.EdgeMarkers)
        {
            var text = CachedLayout(marker.Text, ChartFonts.Mono, FontWeight.Regular, s.MarkerSize, marker.Bounds.Width + s.MarkerSize, TextAlignment.Leading);
            target.DrawTextLayout(new Vector2((float)marker.Bounds.X, (float)marker.Bounds.Y), text, paint.DayLine);
        }
    }

    private void DrawAverages(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        var width = LineWidth(s.Layout) * 1.5f;
        using var round = target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        using var gpu = Gpu(target);
        var series = 0;
        foreach (var (values, brush) in new[] { (s.Ma50, paint.Ma50), (s.Ma20, paint.Ma20) })
        {
            if (gpu is not null)
            {
                DrawRealizedAverage(target, gpu, realizedAverages[series++], values, brush, width, round, s);
                continue;
            }

            using var path = target.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                var open = false;
                for (var i = s.FirstIndex; i < values.Count; i++)
                {
                    if (values[i] is not { } v)
                    {
                        continue;
                    }

                    var point = new Vector2((float)s.CenterX(i), (float)s.Scale.Y(v));
                    if (!open)
                    {
                        sink.BeginFigure(point, FigureBegin.Hollow);
                        open = true;
                    }
                    else
                    {
                        sink.AddLine(point);
                    }
                }

                if (open)
                {
                    sink.EndFigure(FigureEnd.Open);
                }

                sink.Close();
            }

            target.DrawGeometry(path, brush, width, round);
        }
    }

    // DeviceContext1 targets: everything up to the second-to-last point depends on closed candles only, so it is realized once
    // and translated by the shift; the segment to the forming candle's value is drawn each frame (the round cap
    // joins it like the round line join would).
    private static void DrawRealizedAverage(
        ID2D1RenderTarget target, ID2D1DeviceContext1 gpu, RealizedAverage cache, IReadOnlyList<decimal?> values,
        ID2D1Brush brush, float width, ID2D1StrokeStyle round, Scene s)
    {
        var settled = values.Count - 1;
        var l = s.Layout;
        if (!cache.Matches(s.Scale, s.FirstIndex, l.CandleCenterX(s.FirstIndex), l.CandlePitchPx, width, values, settled))
        {
            cache.Clear();
            using var path = gpu.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                var open = false;
                for (var i = s.FirstIndex; i < settled; i++)
                {
                    if (values[i] is not { } v)
                    {
                        continue;
                    }

                    var point = new Vector2((float)l.CandleCenterX(i), (float)s.Scale.Y(v));
                    if (!open)
                    {
                        sink.BeginFigure(point, FigureBegin.Hollow);
                        open = true;
                    }
                    else
                    {
                        sink.AddLine(point);
                    }
                }

                if (open)
                {
                    sink.EndFigure(FigureEnd.Open);
                }

                sink.Close();
            }

            cache.Set(s.Scale, s.FirstIndex, l.CandleCenterX(s.FirstIndex), l.CandlePitchPx, width, values, settled,
                gpu.CreateStrokedGeometryRealization(path, DefaultFlatteningTolerance, width, round));
        }

        var before = target.Transform;
        target.Transform = Matrix3x2.CreateTranslation((float)s.ShiftPx, 0) * before;
        gpu.DrawGeometryRealization(cache.Realization!, brush);
        target.Transform = before;

        if (settled >= 1 && settled - 1 >= s.FirstIndex && values[settled - 1] is { } a && values[settled] is { } b)
        {
            target.DrawLine(
                new Vector2((float)s.CenterX(settled - 1), (float)s.Scale.Y(a)),
                new Vector2((float)s.CenterX(settled), (float)s.Scale.Y(b)),
                brush, width, round);
        }
    }

    private static void DrawCandles(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var l = s.Layout;
        target.AntialiasMode = AntialiasMode.Aliased;
        var body = (float)l.CandleBodyWidthPx;
        var wick = (float)l.WickWidthPx;
        for (var i = s.FirstIndex; i < s.Candles.Count; i++)
        {
            var c = s.Candles[i];
            var up = IsUp(c);
            var center = s.CenterX(i);

            var high = Snap(s.Scale.Y(c.High));
            var low = Snap(s.Scale.Y(c.Low));
            target.FillRectangle(new Rect(SnapLeft(center, wick), high, wick, Math.Max(1, low - high)), up ? paint.WickUp : paint.WickDown);

            var top = Snap(s.Scale.Y(Math.Max(c.Open, c.Close)));
            var bottom = Snap(s.Scale.Y(Math.Min(c.Open, c.Close)));
            target.FillRectangle(new Rect(SnapLeft(center, body), top, body, Math.Max(1, bottom - top)), up ? paint.Up : paint.Down);
        }
    }

    private void DrawLastPriceLine(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        if (s.Last is not { } last)
        {
            return;
        }

        var plot = s.Layout.Plot;
        var width = LineWidth(s.Layout);
        var dash = 4 * width;
        using var dashed = target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties { DashStyle = DashStyle.Custom }, [dash / width, dash / width]);
        var y = Snap(s.Scale.Y(last.Close)) + (width / 2);
        target.AntialiasMode = AntialiasMode.Aliased;
        var brush = IsUp(last) ? paint.LastUp : paint.LastDown;
        using var gpu = Gpu(target);
        if (gpu is not null)
        {
            var before = target.Transform;
            target.Transform = Matrix3x2.CreateTranslation((float)plot.X, y) * before;
            gpu.DrawGeometryRealization(RealizedLine(gpu, "dashes", (float)plot.Width, width, dashed), brush);
            target.Transform = before;
            return;
        }

        target.DrawLine(new Vector2((float)plot.X, y), new Vector2((float)plot.Right, y), brush, width, dashed);
    }

    // ---- headline -----------------------------------------------------------------------------

    private void DrawHeadline(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var l = s.Layout;
        var box = l.Headline;
        var t = l.Text;
        var align = box.X >= l.WidthPx / 2.0 ? TextAlignment.Trailing : TextAlignment.Leading;

        // A soft scrim, so grid lines and averages pass quietly behind the numbers.
        var cx = (float)(box.X + (box.Width / 2));
        var cy = (float)(box.Y + (box.Height / 2));
        var scrimColor = Palette.WithAlpha(Palette.Base, 0);
        using (var stops = target.CreateGradientStopCollection(
                   [
                       new GradientStop(0, Palette.WithAlpha(Palette.Base, 0.8f)),
                       new GradientStop(0.6f, Palette.WithAlpha(Palette.Base, 0.6f)),
                       new GradientStop(1, scrimColor),
                   ]))
        using (var scrim = target.CreateRadialGradientBrush(
                   new RadialGradientBrushProperties(new Vector2(cx, cy), Vector2.Zero, (float)box.Width * 0.72f, (float)box.Height * 0.95f),
                   stops))
        {
            target.AntialiasMode = AntialiasMode.PerPrimitive;
            target.FillRectangle(
                new Rect(cx - ((float)box.Width * 0.72f), cy - ((float)box.Height * 0.95f), (float)box.Width * 1.44f, (float)box.Height * 1.9f),
                scrim);
        }

        var opacity = s.Frame.IsStale ? 0.7f : 1f;
        var x = (float)box.X;
        var width = box.Width;
        var y = box.Y + (0.4 * t.PairSourcePx);

        // 1. pair, source, interval, countdown
        var captionText = CachedLayout(s.Caption, ChartFonts.Inter, FontWeight.Medium, t.PairSourcePx, width, align);
        DrawWithOpacity(target, paint.Axis, opacity, b => target.DrawTextLayout(new Vector2(x, (float)y), captionText, b));

        y += 1.5 * t.PairSourcePx;

        // 2. price
        // The chart's own last price, so the headline and the price tag never disagree.
        var price = s.Last?.Close ?? s.Frame.Ticker?.Last;
        if (price is { } p)
        {
            var text = CachedLayout(Price(p), ChartFonts.Inter, FontWeight.SemiBold, t.HeadlinePricePx, width, align);
            using var pulsed = s.Frame.Pulse > 0
                ? target.CreateSolidColorBrush(Mix(Palette.PrimaryText, s.Frame.PulseUp ? Palette.Up : Palette.Down, (float)Math.Min(1, s.Frame.Pulse)))
                : null;
            DrawWithOpacity(target, pulsed ?? paint.Primary, opacity, b => target.DrawTextLayout(new Vector2(x, (float)(y - (0.08 * t.HeadlinePricePx))), text, b));
        }

        y += 1.2 * t.HeadlinePricePx;

        if (s.Frame.Ticker is not { } ticker)
        {
            return;
        }

        // 3. 24h change, absolute and percent
        var change = DayStats.ChangeFromTicker(ticker);
        var changeText = string.Create(
            Invariant, $"{change.Absolute:+#,##0.00;−#,##0.00;0.00}   {change.Percent:+0.00;−0.00;0.00}%");
        var changeBrush = change.Absolute > 0 ? paint.Up : change.Absolute < 0 ? paint.Down : paint.Primary;
        var changeLayout = CachedLayout(changeText, ChartFonts.Inter, FontWeight.Medium, t.ChangePx, width, align);
        DrawWithOpacity(target, changeBrush, opacity, b => target.DrawTextLayout(new Vector2(x, (float)y), changeLayout, b));

        y += 1.45 * t.ChangePx;

        // 4. 24h high / low / volume
        var hl = DayStats.HighLowFromTicker(ticker);
        var asset = s.Frame.Pair.Split('/')[0];
        var parts = new (string Text, bool Label)[]
        {
            ("24h   H ", true), (Price(hl.High), false), ("     L ", true), (Price(hl.Low), false),
            ("     Vol ", true), (string.Create(Invariant, $"{ticker.Volume:#,##0}"), false), ($" {asset}", true),
        };
        var stats = string.Concat(parts.Select(part => part.Text));
        using (var text = TextLayout(stats, ChartFonts.Inter, FontWeight.Medium, t.StatsRowPx, width, align))
        {
            var at = 0;
            foreach (var (part, label) in parts)
            {
                text.SetDrawingEffect(label ? paint.Axis : paint.Primary, new TextRange((uint)at, (uint)part.Length));
                at += part.Length;
            }

            DrawWithOpacity(target, paint.Primary, opacity, b => target.DrawTextLayout(new Vector2(x, (float)y), text, b));
        }
    }

    // ---- axes ---------------------------------------------------------------------------------

    private void DrawPriceAxis(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var axis = s.Layout.PriceAxis;
        var size = s.Layout.Text.AxisLabelPx;

        // Each label and tag centred in the column (Scene.AxisItems): the labels first, the tags over them.
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var item in s.AxisItems)
        {
            var b = item.Bounds;
            var y = b.Y + (b.Height / 2);
            if (item.Kind == PriceAxisItemKind.Level)
            {
                var label = CachedLayout(item.Text, ChartFonts.Mono, FontWeight.Regular, size, axis.Width, TextAlignment.Leading);
                target.DrawTextLayout(
                    new Vector2((float)(b.X + ((b.Width - label.Metrics.Width) / 2)), (float)(y - (label.Metrics.Height / 2))), label, paint.Axis);
                continue;
            }

            ID2D1Brush fill = paint.DayTag, ink = paint.Axis;
            if (item.Kind == PriceAxisItemKind.LastPrice)
            {
                (fill, ink) = (s.Last is { } last && IsUp(last) ? paint.Up : paint.Down, paint.TagText);
            }

            var text = CachedLayout(item.Text, ChartFonts.Mono, FontWeight.Medium, size, axis.Width, TextAlignment.Leading);
            var rect = new System.Drawing.RectangleF((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
            target.FillRoundedRectangle(new RoundedRectangle(rect, (float)(0.16 * size), (float)(0.16 * size)), fill);
            target.DrawTextLayout(
                new Vector2((float)(b.X + ((b.Width - text.Metrics.Width) / 2)), (float)(y - (text.Metrics.Height / 2))), text, ink);
        }
    }

    private void DrawTimeAxis(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var axis = s.Layout.TimeAxis;
        var size = s.Layout.Text.AxisLabelPx;
        foreach (var tick in s.TimeLabels)
        {
            var text = CachedLayout(tick.Text, ChartFonts.Mono, tick.IsDay ? FontWeight.Medium : FontWeight.Regular, size, axis.Width, TextAlignment.Leading);
            var x = tick.X - (text.Metrics.Width / 2);
            target.DrawTextLayout(
                new Vector2((float)x, (float)(axis.Y + ((axis.Height - text.Metrics.Height) / 2))), text, tick.IsDay ? paint.Primary : paint.Axis);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private IDWriteTextLayout TextLayout(string text, string family, FontWeight weight, double size, double maxWidth, TextAlignment align)
    {
        var key = (family, weight, (float)size);
        if (!formats.TryGetValue(key, out var format))
        {
            format = dwrite.CreateTextFormat(family, fonts.Collection, weight, FontStyle.Normal, FontStretch.Normal, (float)size, "en-us");
            format.WordWrapping = WordWrapping.NoWrap;
            formats[key] = format;
        }

        var layout = dwrite.CreateTextLayout(text, format, (float)Math.Max(1, maxWidth), (float)(size * 4));
        layout.TextAlignment = align;
        layout.SetTypography(typography, new TextRange(0, (uint)text.Length));
        return layout;
    }

    // A layout owned by the cache (the caller does not dispose it): labels repeat from frame to frame, and making
    // them anew 120 times a second is most of a frame's CPU (D-6). Layouts unused for a whole frame are dropped.
    private IDWriteTextLayout CachedLayout(string text, string family, FontWeight weight, double size, double maxWidth, TextAlignment align)
    {
        var key = new LayoutKey(text, family, weight, (float)size, (float)Math.Max(1, maxWidth), align);
        if (layouts.TryGetValue(key, out var layout))
        {
            return layout;
        }

        if (previousLayouts.Remove(key, out layout))
        {
            layouts[key] = layout;
            return layout;
        }

        layout = TextLayout(text, family, weight, size, maxWidth, align);
        layouts[key] = layout;
        return layout;
    }

    // The target as an ID2D1DeviceContext1, or null when it is not one. A new context drops what was realized.
    private ID2D1DeviceContext1? Gpu(ID2D1RenderTarget target)
    {
        var gpu = target.QueryInterfaceOrNull<ID2D1DeviceContext1>();
        if (gpu is not null && realizedOn?.NativePointer != gpu.NativePointer)
        {
            DropRealizations();
            realizedOn = gpu.QueryInterface<ID2D1DeviceContext1>();
        }

        return gpu;
    }

    private void DropRealizations()
    {
        foreach (var line in realizedLines.Values)
        {
            line.Dispose();
        }

        realizedLines.Clear();
        foreach (var average in realizedAverages)
        {
            average.Clear();
        }

        realizedOn?.Dispose();
        realizedOn = null;
    }

    // A horizontal line from (0, 0) to (length, 0) in the given stroke, realized once.
    private ID2D1GeometryRealization RealizedLine(ID2D1DeviceContext1 gpu, string kind, float length, float width, ID2D1StrokeStyle style)
    {
        if (realizedLines.TryGetValue((kind, length, width), out var realized))
        {
            return realized;
        }

        using var line = gpu.Factory.CreatePathGeometry();
        using (var sink = line.Open())
        {
            sink.BeginFigure(Vector2.Zero, FigureBegin.Hollow);
            sink.AddLine(new Vector2(length, 0));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        realized = gpu.CreateStrokedGeometryRealization(line, DefaultFlatteningTolerance, width, style);
        realizedLines[(kind, length, width)] = realized;
        return realized;
    }

    // Called at the start of each frame: what the last frame did not use goes.
    private void AgeLayouts()
    {
        foreach (var unused in previousLayouts.Values)
        {
            unused.Dispose();
        }

        previousLayouts.Clear();
        (previousLayouts, layouts) = (layouts, previousLayouts);
    }

    private static void DrawWithOpacity(ID2D1RenderTarget target, ID2D1SolidColorBrush brush, float opacity, Action<ID2D1Brush> draw)
    {
        var before = brush.Opacity;
        brush.Opacity = opacity;
        draw(brush);
        brush.Opacity = before;
    }

    private static string Price(decimal price) => price.ToString("#,##0.00", Invariant);

    private static Color4 Mix(Color4 from, Color4 to, float amount) => new(
        from.R + ((to.R - from.R) * amount),
        from.G + ((to.G - from.G) * amount),
        from.B + ((to.B - from.B) * amount),
        from.A + ((to.A - from.A) * amount));

    private static bool IsUp(Candle c) => c.Close >= c.Open;

    private static float LineWidth(ChartLayout l) => l.Profile == DisplayProfile.Tv ? 2 : 1;

    private static float Snap(double v) => (float)Math.Round(v);

    // The left edge of a span of whole-px width centred on x, on the pixel grid.
    private static float SnapLeft(double center, float width) => (float)Math.Floor(center - (width / 2) + 0.5);

    private static bool Intersects(RectPx a, RectPx b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static Rect ToRect(RectPx r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    /// <summary>One average's settled part as a geometry realization, and what it was built from.</summary>
    private sealed class RealizedAverage
    {
        private PriceScale scale;
        private int first;
        private double firstX;
        private double pitch;
        private float width;
        private decimal?[] values = [];

        public ID2D1GeometryRealization? Realization { get; private set; }

        public bool Matches(PriceScale scale, int first, double firstX, double pitch, float width, IReadOnlyList<decimal?> all, int settled)
        {
            if (Realization is null || scale != this.scale || first != this.first || firstX != this.firstX
                || pitch != this.pitch || width != this.width || values.Length != Math.Max(0, settled - first))
            {
                return false;
            }

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] != all[first + i])
                {
                    return false;
                }
            }

            return true;
        }

        public void Set(PriceScale scale, int first, double firstX, double pitch, float width, IReadOnlyList<decimal?> all, int settled,
            ID2D1GeometryRealization realization)
        {
            (this.scale, this.first, this.firstX, this.pitch, this.width) = (scale, first, firstX, pitch, width);
            values = new decimal?[Math.Max(0, settled - first)];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = all[first + i];
            }

            Realization = realization;
        }

        public void Clear()
        {
            Realization?.Dispose();
            Realization = null;
        }
    }

    private readonly record struct LayoutKey(string Text, string Family, FontWeight Weight, float Size, float MaxWidth, TextAlignment Align);

    /// <summary>The brushes of one draw, made on (and only valid for) that target.</summary>
    private sealed class Paint : IDisposable
    {
        private readonly List<ID2D1Brush> all = [];

        public Paint(ID2D1RenderTarget target)
        {
            ID2D1SolidColorBrush Make(Color4 color)
            {
                var brush = target.CreateSolidColorBrush(color);
                all.Add(brush);
                return brush;
            }

            Up = Make(Palette.Up);
            Down = Make(Palette.Down);
            WickUp = Make(Palette.WithAlpha(Palette.Up, Palette.WickAlpha));
            WickDown = Make(Palette.WithAlpha(Palette.Down, Palette.WickAlpha));
            VolumeUp = Make(Palette.WithAlpha(Palette.Up, Palette.VolumeAlpha));
            VolumeDown = Make(Palette.WithAlpha(Palette.Down, Palette.VolumeAlpha));
            LastUp = Make(Palette.WithAlpha(Palette.Up, 0.6f));
            LastDown = Make(Palette.WithAlpha(Palette.Down, 0.6f));
            Ma20 = Make(Palette.Ma20);
            Ma50 = Make(Palette.Ma50);
            Grid = Make(Palette.Grid);
            GridDay = Make(Palette.GridDay);
            Axis = Make(Palette.AxisText);
            Primary = Make(Palette.PrimaryText);
            TagText = Make(Palette.TagText);
            DayLine = Make(Palette.WithAlpha(Palette.AxisText, 0.55f));
            DayLabel = Make(Palette.WithAlpha(Palette.AxisText, 0.8f));
            DayTag = Make(Palette.WithAlpha(Palette.AxisText, 0.16f));
        }

        public ID2D1SolidColorBrush Up { get; }

        public ID2D1SolidColorBrush Down { get; }

        public ID2D1SolidColorBrush WickUp { get; }

        public ID2D1SolidColorBrush WickDown { get; }

        public ID2D1SolidColorBrush VolumeUp { get; }

        public ID2D1SolidColorBrush VolumeDown { get; }

        public ID2D1SolidColorBrush LastUp { get; }

        public ID2D1SolidColorBrush LastDown { get; }

        public ID2D1SolidColorBrush Ma20 { get; }

        public ID2D1SolidColorBrush Ma50 { get; }

        public ID2D1SolidColorBrush Grid { get; }

        public ID2D1SolidColorBrush GridDay { get; }

        public ID2D1SolidColorBrush Axis { get; }

        public ID2D1SolidColorBrush Primary { get; }

        public ID2D1SolidColorBrush TagText { get; }

        public ID2D1SolidColorBrush DayLine { get; }

        public ID2D1SolidColorBrush DayLabel { get; }

        public ID2D1SolidColorBrush DayTag { get; }

        public void Dispose()
        {
            foreach (var brush in all)
            {
                brush.Dispose();
            }
        }
    }
}
