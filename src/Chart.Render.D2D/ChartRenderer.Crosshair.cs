using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>The crosshair (D-35): drawn only when the frame's crosshair is shown, so a frame without it is
/// pixel-identical to one before the feature. Its brushes are made only then. Each part is one step: another part
/// (a column band behind the candle, say) is one more step in <see cref="DrawCrosshair"/>.</summary>
public sealed partial class ChartRenderer
{
    // Inside the plot's clip, right after the candles: the candles a line crosses, slightly brighter.
    private static void DrawLitCandles(ID2D1RenderTarget target, Scene s)
    {
        if (s.Crosshair.LitCandles.Count == 0)
        {
            return;
        }

        var l = s.Layout;
        var body = (float)l.CandleBodyWidthPx;
        var wick = (float)l.WickWidthPx;
        using var light = target.CreateSolidColorBrush(Palette.CandleLight);
        target.AntialiasMode = AntialiasMode.Aliased;
        foreach (var i in s.Crosshair.LitCandles)
        {
            var c = s.Candles[i];
            var center = s.CenterX(i);
            var high = Snap(s.Scale.Y(c.High));
            var low = Snap(s.Scale.Y(c.Low));
            var top = Snap(s.Scale.Y(Math.Max(c.Open, c.Close)));
            var bottom = Snap(s.Scale.Y(Math.Min(c.Open, c.Close)));

            // The wick above and below the body, then the body: no pixel is lit twice.
            var wickLeft = SnapLeft(center, wick);
            if (top > high)
            {
                target.FillRectangle(new Rect(wickLeft, high, wick, top - high), light);
            }

            var bodyHeight = Math.Max(1, bottom - top);
            if (low > top + bodyHeight)
            {
                target.FillRectangle(new Rect(wickLeft, top + bodyHeight, wick, low - (top + bodyHeight)), light);
            }

            target.FillRectangle(new Rect(SnapLeft(center, body), top, body, bodyHeight), light);
        }
    }

    // After everything else: the lines, the tags (over the price-axis tags) and the info block.
    private void DrawCrosshair(ID2D1RenderTarget target, Scene s)
    {
        if (s.Crosshair.State is not { } state)
        {
            return;
        }

        using var line = target.CreateSolidColorBrush(Palette.Crosshair);
        using var border = target.CreateSolidColorBrush(Palette.CrosshairBorder);
        using var tag = target.CreateSolidColorBrush(Palette.CrosshairTag);
        using var block = target.CreateSolidColorBrush(Palette.CrosshairBlock);
        using var primary = target.CreateSolidColorBrush(Palette.PrimaryText);
        using var axis = target.CreateSolidColorBrush(Palette.AxisText);
        using var up = target.CreateSolidColorBrush(Palette.Up);
        using var down = target.CreateSolidColorBrush(Palette.Down);

        DrawCrosshairLines(target, s, state.X, state.Y, line);
        foreach (var item in s.Crosshair.Items)
        {
            if (item.Kind == CrosshairItemKind.InfoBlock)
            {
                DrawInfoBlock(target, s, item, block, border, primary, axis, state.Candle is { } c && IsUp(c) ? up : down);
            }
            else
            {
                DrawCrosshairTag(target, s, item, tag, border, primary);
            }
        }
    }

    // A whole-monitor locator: solid thin lines on the pixel grid, edge to edge through the cursor wherever it is; the
    // tags are drawn over them.
    private static void DrawCrosshairLines(ID2D1RenderTarget target, Scene s, double x, double y, ID2D1Brush brush)
    {
        var l = s.Layout;
        var width = LineWidth(l);
        target.AntialiasMode = AntialiasMode.Aliased;
        target.FillRectangle(new Rect((float)Math.Floor(x), 0, width, l.HeightPx), brush);
        target.FillRectangle(new Rect(0, (float)Math.Floor(y), l.WidthPx, width), brush);
    }

    // A tag like the D-32 tags (rounded box, mono text centred), opaque, so it covers what lies under it.
    private void DrawCrosshairTag(ID2D1RenderTarget target, Scene s, CrosshairItem item, ID2D1Brush fill, ID2D1Brush border, ID2D1Brush ink)
    {
        var size = s.Layout.Text.AxisLabelPx;
        var b = item.Bounds;
        DrawBox(target, b, (float)(0.16 * size), fill, border);
        var text = CachedLayout(item.Text, ChartFonts.Mono, FontWeight.Medium, size, b.Width + size, TextAlignment.Leading);
        target.DrawTextLayout(
            new Vector2((float)(b.X + ((b.Width - text.Metrics.Width) / 2)), (float)(b.Y + ((b.Height - text.Metrics.Height) / 2))), text, ink);
    }

    // The candle's time, O/H/L/C and volume: labels in the axis colour, values in the candle's colour.
    private void DrawInfoBlock(
        ID2D1RenderTarget target, Scene s, CrosshairItem item, ID2D1Brush fill, ID2D1Brush border,
        ID2D1SolidColorBrush primary, ID2D1SolidColorBrush axis, ID2D1SolidColorBrush value)
    {
        var size = s.Layout.Text.AxisLabelPx;
        var b = item.Bounds;
        var pad = CrosshairView.BlockPadding * size;
        DrawBox(target, b, (float)(0.3 * size), fill, border);

        using var text = TextLayout(item.Text, ChartFonts.Mono, FontWeight.Regular, size, b.Width, TextAlignment.Leading);
        var lines = item.Text.Split('\n');
        text.SetDrawingEffect(primary, new TextRange(0, (uint)lines[0].Length));
        var at = lines[0].Length + 1;
        foreach (var line in lines.Skip(1))
        {
            // "O 1.00  H 2.00 ...": each letter a label, each number a value.
            var words = line.Split(' ');
            var position = at;
            for (var w = 0; w < words.Length; w++)
            {
                if (words[w].Length > 0)
                {
                    var isValue = w > 0 && words[w - 1].Length > 0;
                    text.SetDrawingEffect(isValue ? value : axis, new TextRange((uint)position, (uint)words[w].Length));
                }

                position += words[w].Length + 1;
            }

            at += line.Length + 1;
        }

        target.AntialiasMode = AntialiasMode.PerPrimitive;
        target.DrawTextLayout(new Vector2((float)(b.X + pad), (float)(b.Y + pad)), text, primary);
    }

    // A rounded box with a 1 px border inside its bounds.
    private static void DrawBox(ID2D1RenderTarget target, Chart.Core.Layout.RectPx b, float radius, ID2D1Brush fill, ID2D1Brush border)
    {
        target.AntialiasMode = AntialiasMode.PerPrimitive;
        var rect = new System.Drawing.RectangleF((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
        target.FillRoundedRectangle(new RoundedRectangle(rect, radius, radius), fill);
        var inner = new System.Drawing.RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1);
        target.DrawRoundedRectangle(new RoundedRectangle(inner, radius, radius), border, 1);
    }
}
