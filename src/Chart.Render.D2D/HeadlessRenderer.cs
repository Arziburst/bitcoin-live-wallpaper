using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.WIC;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using IDWriteFactory5 = Vortice.DirectWrite.IDWriteFactory5;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>A frame's pixels: 32-bit BGRA, top-down, stride Width * 4, opaque.</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels);

/// <summary>Renders frames without a GPU or a window: Direct2D's software rasterizer into a WIC bitmap. For PNG
/// mockups and golden-image tests.</summary>
public sealed class HeadlessRenderer : IDisposable
{
    private readonly ID2D1Factory d2d;
    private readonly IDWriteFactory5 dwrite;
    private readonly IWICImagingFactory wic;
    private readonly ChartFonts fonts;
    private readonly ChartRenderer renderer;

    public HeadlessRenderer(string? fontDirectory = null)
    {
        d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded, DebugLevel.None);
        dwrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory5>(DWriteFactoryType.Isolated);
        wic = new IWICImagingFactory();
        fonts = ChartFonts.Load(dwrite, fontDirectory);
        renderer = new ChartRenderer(dwrite, fonts);
    }

    public BgraImage Render(ChartFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (w, h) = (frame.Layout.WidthPx, frame.Layout.HeightPx);
        using var bitmap = wic.CreateBitmap((uint)w, (uint)h, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        var properties = new RenderTargetProperties(
            RenderTargetType.Software,
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96,
            96,
            RenderTargetUsage.None,
            FeatureLevel.Default);
        using (var target = d2d.CreateWicBitmapRenderTarget(bitmap, properties))
        {
            target.BeginDraw();
            renderer.Draw(target, frame);
            target.EndDraw().CheckError();
        }

        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels((uint)(w * 4), pixels);
        return new BgraImage(w, h, pixels);
    }

    /// <summary>Writes the image as a 24-bit PNG (the image is opaque).</summary>
    public void SavePng(BgraImage image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var source = wic.CreateBitmapFromMemory((uint)image.Width, (uint)image.Height, PixelFormat.Format32bppBGRA, image.Pixels, (uint)(image.Width * 4));
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(source, PixelFormat.Format24bppBGR, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);

        using var stream = File.Create(path);
        using var encoder = wic.CreateEncoder(ContainerFormat.Png, stream, BitmapEncoderCacheOption.NoCache);
        using var frame = encoder.CreateNewFrame(out var options);
        options?.Dispose();
        frame.Initialize();
        frame.SetSize((uint)image.Width, (uint)image.Height);
        var format = PixelFormat.Format24bppBGR;
        frame.SetPixelFormat(ref format);
        frame.WriteSource(converter);
        frame.Commit();
        encoder.Commit();
    }

    /// <summary>Reads any PNG as 32-bit BGRA.</summary>
    public BgraImage LoadPng(string path)
    {
        using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var size = converter.Size;
        var pixels = new byte[size.Width * size.Height * 4];
        converter.CopyPixels((uint)(size.Width * 4), pixels);
        return new BgraImage(size.Width, size.Height, pixels);
    }

    public void Dispose()
    {
        renderer.Dispose();
        fonts.Dispose();
        wic.Dispose();
        dwrite.Dispose();
        d2d.Dispose();
    }
}
