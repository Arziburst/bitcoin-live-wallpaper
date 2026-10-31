using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

/// <summary>How far two images are apart: per pixel the largest channel difference (0..255).</summary>
public sealed record ImageDiff(int MaxDelta, double MeanDelta, double ShareOverThreshold)
{
    // Text antialiasing may differ by a few levels between Windows builds; geometry must not move.
    public const int Threshold = 48;
    // 0.02 % of the pixels (about 1500 at 5120x1440); a last price changed by 37.50 moves about 0.18 %.
    public const double MaxShareOverThreshold = 0.0002;
    public const double MaxMeanDelta = 0.5;

    public bool WithinTolerance => ShareOverThreshold <= MaxShareOverThreshold && MeanDelta <= MaxMeanDelta;

    public static ImageDiff Compare(BgraImage expected, BgraImage actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        long sum = 0, over = 0;
        var max = 0;
        var a = expected.Pixels;
        var b = actual.Pixels;
        for (var i = 0; i < a.Length; i += 4)
        {
            var d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            sum += d;
            max = Math.Max(max, d);
            if (d > Threshold)
            {
                over++;
            }
        }

        var pixels = a.Length / 4.0;
        return new ImageDiff(max, sum / pixels, over / pixels);
    }

    /// <summary>The differing pixels in white on black, for looking at a failure.</summary>
    public static BgraImage Visualize(BgraImage expected, BgraImage actual)
    {
        var diff = new byte[expected.Pixels.Length];
        for (var i = 0; i < diff.Length; i += 4)
        {
            var d = 0;
            for (var c = 0; c < 3; c++)
            {
                d = Math.Max(d, Math.Abs(expected.Pixels[i + c] - actual.Pixels[i + c]));
            }

            var v = (byte)Math.Min(255, d * 4);
            diff[i] = diff[i + 1] = diff[i + 2] = v;
            diff[i + 3] = 255;
        }

        return new BgraImage(expected.Width, expected.Height, diff);
    }

    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"max delta {MaxDelta}, mean {MeanDelta:0.###}, {ShareOverThreshold:P3} of pixels over {Threshold} " +
        $"(allowed: mean <= {MaxMeanDelta}, <= {MaxShareOverThreshold:P2} over)");
}
