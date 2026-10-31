using System.Drawing;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace DesktopSpike;

/// <summary>
/// D3D11 device, a flip-model swap chain for composition and a DirectComposition visual on our window;
/// Direct2D draws a deep-blue rectangle with a white bar moving across it.
/// </summary>
internal sealed class DCompPresenter : IDisposable
{
    private const float BarPeriodSeconds = 8f;

    private static readonly Color4 DeepBlue = new(0.02f, 0.10f, 0.42f, 1f);
    private static readonly Color4 White = new(1f, 1f, 1f, 1f);

    private readonly int _width;
    private readonly int _height;
    private readonly ID3D11Device _device;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionDevice _composition;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1Bitmap1 _backBuffer;
    private readonly ID2D1SolidColorBrush _white;

    public DCompPresenter(nint hwnd, int width, int height)
    {
        _width = width;
        _height = height;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device).CheckError();
        _device = device!;
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        Log.Info($"GPU: {adapter.Description.Description}, feature level {_device.FeatureLevel}");

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, description);

        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _composition.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _visual = _composition.CreateVisual();
        _visual.SetContent(_swapChain);
        _target.SetRoot(_visual);
        _composition.Commit().CheckError();
        Log.Info($"DirectComposition: flip-sequential swap chain {width}x{height} B8G8R8A8 on {Native.Hex(hwnd)}");

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        _backBuffer = _context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _context.Target = _backBuffer;
        _white = _context.CreateSolidColorBrush(White);
    }

    /// <summary>Draws one frame at <paramref name="seconds"/> since start; false when Present failed.</summary>
    public bool Render(double seconds)
    {
        var barWidth = Math.Max(8f, _width / 40f);
        var phase = (float)(seconds % BarPeriodSeconds / BarPeriodSeconds);
        var x = (phase * (_width + barWidth)) - barWidth;

        _context.BeginDraw();
        _context.Clear(DeepBlue);
        _context.FillRectangle(new RectangleF(x, 0, barWidth, _height), _white);
        var drawn = _context.EndDraw();
        if (drawn.Failure)
        {
            Log.Error($"Direct2D EndDraw failed: 0x{drawn.Code:X8}");
            return false;
        }

        var presented = _swapChain.Present(1, PresentFlags.None);
        if (presented.Failure)
        {
            Log.Error($"Present failed: 0x{presented.Code:X8}");
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _white.Dispose();
        _context.Target = null;
        _backBuffer.Dispose();
        _context.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _target.SetRoot(null);
        _composition.Commit();
        _visual.Dispose();
        _target.Dispose();
        _composition.Dispose();
        _swapChain.Dispose();
        _device.Dispose();
    }
}
