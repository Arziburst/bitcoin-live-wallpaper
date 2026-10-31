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
public sealed class ChartRenderer : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IDWriteFactory5 dwrite;
    private readonly ChartFonts fonts;
    private readonly IDWriteTypography typography;
    private readonly IDWriteRenderingParams renderingParams;
    private readonly Dictionary<(string Family, FontWeight Weight, float Size), IDWriteTextFormat> formats = [];

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

        DrawBackground(target, frame.Layout);
        DrawGrid(target, paint, scene);
        DrawWatermark(target, paint, scene);
        DrawVolume(target, paint, scene);

        target.PushAxisAlignedClip(ToRect(frame.Layout.Plot), AntialiasMode.Aliased);
        DrawDayHighLow(target, paint, scene);
        DrawAverages(target, paint, scene);
        DrawCandles(target, paint, scene);
        DrawLastPriceLine(target, paint, scene);
        target.PopAxisAlignedClip();

        DrawHeadline(target, paint, scene);
        DrawPriceAxis(target, paint, scene);
        DrawTimeAxis(target, paint, scene);
    }

    public void Dispose()
    {
        foreach (var format in formats.Values)
        {
            format.Dispose();
        }

        formats.Clear();
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

    private void DrawWatermark(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var plot = s.Layout.Plot;
        var size = plot.Height * 0.36;
        using var text = TextLayout(s.Base, ChartFonts.Inter, FontWeight.Bold, size, plot.Width, TextAlignment.Center);
        var height = text.Metrics.Height;
        target.DrawTextLayout(
            new Vector2((float)plot.X, (float)(plot.Y + ((plot.Height - height) / 2))), text, paint.Watermark);
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
        for (var i = l.FirstDrawnIndex; i < candles.Count; i++)
        {
            var c = candles[i];
            var height = (float)Math.Round((double)(c.Volume / max) * pane.Height * 0.92);
            if (height < 1)
            {
                continue;
            }

            var x = SnapLeft(l.CandleCenterX(i), body);
            target.FillRectangle(new Rect(x, floor - height, body, height), IsUp(c) ? paint.VolumeUp : paint.VolumeDown);
        }
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
        var labelSize = l.Text.AxisLabelPx * 0.82;
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var (price, name, above) in new[] { (hl.High, "24h high", true), (hl.Low, "24h low", false) })
        {
            var y = s.Scale.Y(price);
            if (y < plot.Y || y > plot.Bottom)
            {
                continue;
            }

            var yy = Snap(y) + 0.5f;
            target.DrawLine(new Vector2((float)plot.X, yy), new Vector2((float)plot.Right, yy), paint.DayLine, width, dots);

            using var text = TextLayout(name, ChartFonts.Inter, FontWeight.Medium, labelSize, plot.Width, TextAlignment.Leading);
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
    }

    private static void DrawAverages(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        var width = LineWidth(s.Layout) * 1.5f;
        using var round = target.Factory.CreateStrokeStyle(
            new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        foreach (var (values, brush) in new[] { (s.Ma50, paint.Ma50), (s.Ma20, paint.Ma20) })
        {
            using var path = target.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                var open = false;
                for (var i = s.Layout.FirstDrawnIndex; i < values.Count; i++)
                {
                    if (values[i] is not { } v)
                    {
                        continue;
                    }

                    var point = new Vector2((float)s.Layout.CandleCenterX(i), (float)s.Scale.Y(v));
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

    private static void DrawCandles(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var l = s.Layout;
        target.AntialiasMode = AntialiasMode.Aliased;
        var body = (float)l.CandleBodyWidthPx;
        var wick = (float)l.WickWidthPx;
        for (var i = l.FirstDrawnIndex; i < s.Candles.Count; i++)
        {
            var c = s.Candles[i];
            var up = IsUp(c);
            var center = l.CandleCenterX(i);

            var high = Snap(s.Scale.Y(c.High));
            var low = Snap(s.Scale.Y(c.Low));
            target.FillRectangle(new Rect(SnapLeft(center, wick), high, wick, Math.Max(1, low - high)), up ? paint.WickUp : paint.WickDown);

            var top = Snap(s.Scale.Y(Math.Max(c.Open, c.Close)));
            var bottom = Snap(s.Scale.Y(Math.Min(c.Open, c.Close)));
            target.FillRectangle(new Rect(SnapLeft(center, body), top, body, Math.Max(1, bottom - top)), up ? paint.Up : paint.Down);
        }
    }

    private static void DrawLastPriceLine(ID2D1RenderTarget target, Paint paint, Scene s)
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
        target.DrawLine(
            new Vector2((float)plot.X, y), new Vector2((float)plot.Right, y), IsUp(last) ? paint.LastUp : paint.LastDown, width, dashed);
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
        var caption = $"{s.Frame.Pair}  ·  {s.Frame.Source}  ·  5m";
        if (s.Countdown is { } countdown)
        {
            caption += $"  ·  {countdown}";
        }

        if (s.Frame.IsStale)
        {
            caption += "  ·  delayed";
        }

        using (var text = TextLayout(caption, ChartFonts.Inter, FontWeight.Medium, t.PairSourcePx, width, align))
        {
            DrawWithOpacity(target, paint.Axis, opacity, b => target.DrawTextLayout(new Vector2(x, (float)y), text, b));
        }

        y += 1.5 * t.PairSourcePx;

        // 2. price
        // The chart's own last price, so the headline and the price tag never disagree.
        var price = s.Last?.Close ?? s.Frame.Ticker?.Last;
        if (price is { } p)
        {
            using var text = TextLayout(Price(p), ChartFonts.Inter, FontWeight.SemiBold, t.HeadlinePricePx, width, align);
            DrawWithOpacity(target, paint.Primary, opacity, b => target.DrawTextLayout(new Vector2(x, (float)(y - (0.08 * t.HeadlinePricePx))), text, b));
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
        using (var text = TextLayout(changeText, ChartFonts.Inter, FontWeight.Medium, t.ChangePx, width, align))
        {
            DrawWithOpacity(target, changeBrush, opacity, b => target.DrawTextLayout(new Vector2(x, (float)y), text, b));
        }

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
        var l = s.Layout;
        var axis = l.PriceAxis;
        var size = l.Text.AxisLabelPx;
        var pad = 0.45 * size;
        var tagHeight = 1.55 * size;

        // Tags first: the last price and the 24h high / low; tick labels give way to them.
        var tags = new List<(double Y, string Text, ID2D1Brush Fill, ID2D1Brush Ink)>();
        if (s.Last is { } last)
        {
            var up = IsUp(last);
            tags.Add((s.Scale.Y(last.Close), Price(last.Close), up ? paint.Up : paint.Down, paint.TagText));
        }

        if (s.DayHighLow is { } hl)
        {
            tags.Add((s.Scale.Y(hl.High), Price(hl.High), paint.DayTag, paint.Axis));
            tags.Add((s.Scale.Y(hl.Low), Price(hl.Low), paint.DayTag, paint.Axis));
        }

        tags.RemoveAll(tag => tag.Y < axis.Y || tag.Y > axis.Bottom);
        var lastTag = tags.Count > 0 && s.Last is not null ? tags[0] : default;
        if (tags.Count > 1 && lastTag.Text is not null)
        {
            // A 24h tag that would sit under the last-price tag is left out.
            tags.RemoveAll(tag => tag.Text != lastTag.Text && Math.Abs(tag.Y - lastTag.Y) < tagHeight && tag.Fill == paint.DayTag);
        }

        var format = s.PriceStep >= 1 ? "#,##0" : "#,##0.00";
        foreach (var level in s.PriceLevels)
        {
            var y = s.Scale.Y(level);
            if (y < axis.Y + (0.6 * size) || y > axis.Bottom - (0.6 * size) ||
                tags.Any(tag => Math.Abs(tag.Y - y) < tagHeight * 0.95))
            {
                continue;
            }

            using var text = TextLayout(level.ToString(format, Invariant), ChartFonts.Mono, FontWeight.Regular, size, axis.Width, TextAlignment.Leading);
            target.DrawTextLayout(new Vector2((float)(axis.X + pad), (float)(y - (text.Metrics.Height / 2))), text, paint.Axis);
        }

        target.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var tag in Enumerable.Reverse(tags))
        {
            using var text = TextLayout(tag.Text, ChartFonts.Mono, FontWeight.Medium, size, axis.Width, TextAlignment.Leading);
            var top = tag.Y - (tagHeight / 2);
            var rect = new System.Drawing.RectangleF((float)axis.X, (float)top, (float)Math.Min(axis.Width, text.Metrics.Width + (2 * pad)), (float)tagHeight);
            target.FillRoundedRectangle(new RoundedRectangle(rect, (float)(0.16 * size), (float)(0.16 * size)), tag.Fill);
            target.DrawTextLayout(new Vector2((float)(axis.X + pad), (float)(tag.Y - (text.Metrics.Height / 2))), text, tag.Ink);
        }
    }

    private void DrawTimeAxis(ID2D1RenderTarget target, Paint paint, Scene s)
    {
        var axis = s.Layout.TimeAxis;
        var size = s.Layout.Text.AxisLabelPx;
        foreach (var tick in s.TimeTicks)
        {
            var label = tick.IsDay
                ? tick.Local.ToString("d MMM", Invariant)
                : tick.Local.ToString("HH:mm", Invariant);
            using var text = TextLayout(label, ChartFonts.Mono, tick.IsDay ? FontWeight.Medium : FontWeight.Regular, size, axis.Width, TextAlignment.Leading);
            var w = text.Metrics.Width;
            var x = tick.X - (w / 2);
            if (x < axis.X || x + w > axis.Right)
            {
                continue;
            }

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

    private static void DrawWithOpacity(ID2D1RenderTarget target, ID2D1SolidColorBrush brush, float opacity, Action<ID2D1Brush> draw)
    {
        var before = brush.Opacity;
        brush.Opacity = opacity;
        draw(brush);
        brush.Opacity = before;
    }

    private static string Price(decimal price) => price.ToString("#,##0.00", Invariant);

    private static bool IsUp(Candle c) => c.Close >= c.Open;

    private static float LineWidth(ChartLayout l) => l.Profile == DisplayProfile.Tv ? 2 : 1;

    private static float Snap(double v) => (float)Math.Round(v);

    // The left edge of a span of whole-px width centred on x, on the pixel grid.
    private static float SnapLeft(double center, float width) => (float)Math.Floor(center - (width / 2) + 0.5);

    private static bool Intersects(RectPx a, RectPx b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static Rect ToRect(RectPx r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    /// <summary>What the frame derives to: computed once per draw from the frame alone.</summary>
    private sealed class Scene
    {
        public Scene(ChartFrame frame)
        {
            Frame = frame;
            Layout = frame.Layout;
            Candles = frame.Candles;
            Last = Candles.Count > 0 ? Candles[^1] : null;
            Ma20 = MovingAverage.Simple(Candles, 20);
            Ma50 = MovingAverage.Simple(Candles, 50);
            DayHighLow = frame.Ticker is { } ticker
                ? DayStats.HighLowFromTicker(ticker)
                : Candles.Count > 0 ? DayStats.HighLowFromCandles(Candles, frame.Now) : null;
            Base = frame.Pair.Split('/')[0];

            var optional = DayHighLow is { } hl ? new[] { hl.High, hl.Low } : [];
            Scale = PriceScale.For(Layout, Candles, [Ma20, Ma50], optional);
            (PriceStep, PriceLevels) = Ticks.Prices(Scale, Layout.Text.AxisLabelPx * 4.5);
            TimeTicks = TimeTicksFor(frame);

            if (Last is { } last)
            {
                var close = last.IsClosed ? NextBoundary(frame.Now) : last.OpenTime + Interval;
                Countdown = CloseCountdown.Text(close, frame.Now);
            }
        }

        public ChartFrame Frame { get; }

        public ChartLayout Layout { get; }

        public IReadOnlyList<Candle> Candles { get; }

        public Candle? Last { get; }

        public IReadOnlyList<decimal?> Ma20 { get; }

        public IReadOnlyList<decimal?> Ma50 { get; }

        public HighLow? DayHighLow { get; }

        public string Base { get; }

        public PriceScale Scale { get; }

        public double PriceStep { get; }

        public IReadOnlyList<double> PriceLevels { get; }

        public IReadOnlyList<TimeTick> TimeTicks { get; }

        public string? Countdown { get; }

        private static DateTimeOffset NextBoundary(DateTimeOffset now)
        {
            var ticks = now.UtcTicks - (now.UtcTicks % Interval.Ticks);
            return new DateTimeOffset(ticks, TimeSpan.Zero) + Interval;
        }

        // A tick on every candle (and every future slot right of the newest) whose local time is a whole interval.
        private static List<TimeTick> TimeTicksFor(ChartFrame frame)
        {
            var l = frame.Layout;
            var ticks = new List<TimeTick>();
            if (frame.Candles.Count == 0)
            {
                return ticks;
            }

            var minutes = Ticks.TimeIntervalMinutes(l.CandlePitchPx, 6.6 * l.Text.AxisLabelPx);
            var last = frame.Candles[^1].OpenTime;
            for (var i = l.FirstDrawnIndex; i < frame.Candles.Count + l.RightOffsetBars; i++)
            {
                var open = i < frame.Candles.Count ? frame.Candles[i].OpenTime : last + ((i - frame.Candles.Count + 1) * Interval);
                var local = TimeZoneInfo.ConvertTime(open, frame.TimeZone);
                var minuteOfDay = (local.Hour * 60) + local.Minute;
                if (minuteOfDay % minutes == 0)
                {
                    ticks.Add(new TimeTick(l.CandleCenterX(i), local, minuteOfDay == 0));
                }
            }

            return ticks;
        }
    }

    private readonly record struct TimeTick(double X, DateTimeOffset Local, bool IsDay);

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
            Watermark = Make(new Color4(1, 1, 1, 0.03f));
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

        public ID2D1SolidColorBrush Watermark { get; }

        public void Dispose()
        {
            foreach (var brush in all)
            {
                brush.Dispose();
            }
        }
    }
}
