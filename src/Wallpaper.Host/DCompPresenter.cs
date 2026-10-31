using BitcoinWallpaper.Chart.Core.Desktop;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using AlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace BitcoinWallpaper.Host;

/// <summary>
/// D3D11 + Direct2D devices and, per attached window, a flip-model composition swap chain shown through a
/// DirectComposition visual (the path the spike proved on the raised desktop). Draws only when asked:
/// every <see cref="Render"/> is one Present. Frames are paced by the swap chain's frame latency waitable object
/// (at most one frame queued): <see cref="Render"/> first waits until the previous frame is on screen, so a run of
/// renders gives one frame per display refresh (D-10). Without the waitable object, DwmFlush paces instead.
/// </summary>
internal sealed class DCompPresenter : IDisposable
{
    private const uint BufferCount = 2;

    /// <summary>Longer than any refresh: a missed signal (device trouble) never hangs the host.</summary>
    private const uint FrameWaitMs = 250;

    private readonly ID3D11Device _device;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly IDXGIFactory2 _factory;
    private readonly IDCompositionDevice _composition;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _context;

    private IDXGISwapChain1? _swapChain;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private ID2D1Bitmap1? _backBuffer;
    private SwapChainFlags _flags;
    private nint _frameWait;
    private bool _frameWaitTimeoutLogged;

    public DCompPresenter()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device).CheckError();
        _device = device!;
        _dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using (var adapter = _dxgiDevice.GetAdapter())
        {
            _factory = adapter.GetParent<IDXGIFactory2>();
            Log.Info($"GPU: {adapter.Description.Description}, feature level {_device.FeatureLevel}");
        }

        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
        _context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
    }

    public long Presents { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsBound => _swapChain is not null;

    /// <summary>The GPU was reset or removed (driver update, sleep, adapter change): build a new presenter.</summary>
    public bool DeviceLost => _device.DeviceRemovedReason.Failure;

    /// <summary>The DXGI reason, e.g. 0x887A0005 removed, 0x887A0007 reset; 0 while the device is fine.</summary>
    public int DeviceRemovedCode => _device.DeviceRemovedReason.Code;

    /// <summary>Creates the swap chain for <paramref name="hwnd"/> and shows it through a composition visual.</summary>
    public void Bind(nint hwnd, int width, int height)
    {
        Unbind();
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        try
        {
            _swapChain = _factory.CreateSwapChainForComposition(_device, description);
            _flags = description.Flags;
        }
        catch (Exception e)
        {
            Log.Warn($"swap chain with a frame latency waitable object failed ({e.Message}); pacing with DwmFlush");
            description.Flags = SwapChainFlags.None;
            _swapChain = _factory.CreateSwapChainForComposition(_device, description);
            _flags = SwapChainFlags.None;
        }

        if (_flags.HasFlag(SwapChainFlags.FrameLatencyWaitableObject))
        {
            using var chain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
            chain2.MaximumFrameLatency = 1;
            _frameWait = chain2.FrameLatencyWaitableObject;
        }

        _composition.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _visual = _composition.CreateVisual();
        _visual.SetContent(_swapChain);
        _target!.SetRoot(_visual);
        _composition.Commit().CheckError();
        Width = width;
        Height = height;
        CreateBackBuffer();
        Log.Info($"DirectComposition: flip-sequential swap chain {width}x{height} B8G8R8A8 on {Native.Hex(hwnd)}, " +
                 (_frameWait != 0 ? "paced by the frame latency waitable object (latency 1)" : "paced by DwmFlush"));
    }

    /// <summary>Resizes the swap chain buffers (a monitor switch or a resolution change).</summary>
    public void Resize(int width, int height)
    {
        if (_swapChain is null || (width == Width && height == Height))
        {
            return;
        }

        ReleaseBackBuffer();
        _swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, Format.Unknown, _flags).CheckError();
        Width = width;
        Height = height;
        CreateBackBuffer();
        Log.Info($"swap chain resized to {width}x{height}");
    }

    /// <summary>Draws one frame of <paramref name="scene"/> and presents it; false when drawing or Present failed.</summary>
    public bool Render(IWallpaperScene scene, SurfacePlacement placement)
    {
        if (_swapChain is null)
        {
            return false;
        }

        // Wait until the previous frame is shown (immediate when idle), then draw with the freshest state.
        if (_frameWait != 0 && Native.WaitForSingleObjectEx(_frameWait, FrameWaitMs, false) != Native.WAIT_OBJECT_0
            && !_frameWaitTimeoutLogged)
        {
            Log.Warn($"the frame latency waitable object was not signalled within {FrameWaitMs} ms; drawing anyway");
            _frameWaitTimeoutLogged = true;
        }

        _context.BeginDraw();
        scene.Draw(_context, placement);
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

        Presents++;
        if (_frameWait == 0)
        {
            Native.DwmFlush();
        }

        return true;
    }

    /// <summary>Drops the swap chain and the composition target (before our window is destroyed).</summary>
    public void Unbind()
    {
        ReleaseBackBuffer();
        if (_target is not null)
        {
            _target.SetRoot(null);
            _composition.Commit();
        }

        _visual?.Dispose();
        _visual = null;
        _target?.Dispose();
        _target = null;
        if (_frameWait != 0)
        {
            Native.CloseHandle(_frameWait);
            _frameWait = 0;
        }

        _swapChain?.Dispose();
        _swapChain = null;
        Width = 0;
        Height = 0;
    }

    public void Dispose()
    {
        Unbind();
        _context.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _composition.Dispose();
        _factory.Dispose();
        _dxgiDevice.Dispose();
        _device.Dispose();
    }

    private void CreateBackBuffer()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _backBuffer = _context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _context.Target = _backBuffer;
    }

    private void ReleaseBackBuffer()
    {
        _context.Target = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
    }
}
