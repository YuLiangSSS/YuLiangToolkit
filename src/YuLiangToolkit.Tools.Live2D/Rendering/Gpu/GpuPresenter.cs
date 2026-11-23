using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using D9Format = Vortice.Direct3D9.Format;
using D11Format = Vortice.DXGI.Format;
using D9Usage = Vortice.Direct3D9.Usage;
using D9DeviceType = Vortice.Direct3D9.DeviceType;
using D9SwapEffect = Vortice.Direct3D9.SwapEffect;
using D9PresentParameters = Vortice.Direct3D9.PresentParameters;

namespace YuLiangToolkit.Tools.Live2D.Rendering.Gpu;

/// <summary>
/// GPU 帧出图通道：把 D3D11 渲染进的目标纹理搬到 WPF。
/// <list type="number">
/// <item><b>D3DImage 零拷贝</b>（首选）：用 D3D9Ex 创建共享纹理（A8R8G8B8 渲染目标），
/// D3D11 侧 <c>OpenSharedResource</c> 直接往里画，D3DImage 把 D3D9 表面当 back buffer——
/// WPF 合成器直接读 GPU 表面，中间没有任何 CPU 拷贝。</item>
/// <item><b>读回</b>（D3D9Ex / 共享句柄创建失败时的兜底）：渲染到普通 D3D11 目标，
/// 每帧 <c>CopyResource</c> → staging → Map → <see cref="WriteableBitmap"/>（GPU 光栅化仍然在显卡上，
/// 只是出图多一次 GPU→CPU 拷贝）。</item>
/// </list>
/// <para><b>线程</b>：所有方法在 UI 线程调（和 CPU 渲染器同一约定）；D3DImage 的 Lock / Unlock 要求 UI 线程。</para>
/// </summary>
internal sealed unsafe class GpuPresenter : IDisposable
{
    /// <summary>蒙版 R8 目标不被硬件支持时的兜底格式（读 R 通道即可，语义一致）。</summary>
    internal const D11Format MaskFallbackFormat = D11Format.B8G8R8A8_UNorm;

    private readonly ID3D11Device _device;
    private readonly Mode _mode;

    private IDirect3DDevice9Ex? _device9;
    private D3DImage? _d3dImage;
    private IDirect3DTexture9? _d3d9Texture;
    private IDirect3DSurface9? _d3d9Surface;
    private bool _needSetBackBuffer;

    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _staging;
    private uint[]? _readback;
    private WriteableBitmap? _readbackBitmap;

    private int _width;
    private int _height;

    private GpuPresenter(ID3D11Device device, Mode mode)
    {
        _device = device;
        _mode = mode;
    }

    private enum Mode
    {
        D3DImage,
        Readback,
    }

    /// <summary>当前出图源（D3DImage 或读回位图；UI 绑定用）。</summary>
    public ImageSource? SurfaceImage => _mode == Mode.D3DImage ? _d3dImage : _readbackBitmap;

    /// <summary>是否零拷贝通道。</summary>
    public bool IsZeroCopy => _mode == Mode.D3DImage;

    /// <summary>当前渲染目标视图（EnsureTarget 成功后有效）。</summary>
    public ID3D11RenderTargetView? RenderTargetView => _rtv;

    public int Width => _width;
    public int Height => _height;

    /// <summary>创建出图通道：优先 D3DImage（D3D9Ex 共享纹理），失败退回读回模式。</summary>
    public static GpuPresenter Create(ID3D11Device device)
    {
        var presenter = new GpuPresenter(device, Mode.D3DImage);
        if (presenter.TryCreateD3D9Interop(device))
        {
            return presenter;
        }

        presenter.DisposeD3D9();
        return new GpuPresenter(device, Mode.Readback);
    }

    /// <summary>确保渲染目标 / 出图源是这个尺寸（变化才重建）。</summary>
    public bool EnsureTarget(int width, int height)
    {
        if (_rtv is not null && _width == width && _height == height) return true;
        if (width < 1 || height < 1) return false;

        ReleaseTarget();

        try
        {
            if (_mode == Mode.D3DImage && _device9 is not null)
            {
                // D3D9Ex 建共享纹理（A8R8G8B8 = B8G8R8A8 内存序，预乘 BGRA 正好是 D3DImage 要的格式）
                var sharedHandle = IntPtr.Zero;
                _d3d9Texture = _device9.CreateTexture((uint)width, (uint)height, 1,
                    D9Usage.RenderTarget, D9Format.A8R8G8B8, Pool.Default, ref sharedHandle);
                if (_d3d9Texture is null || sharedHandle == IntPtr.Zero)
                {
                    return false;
                }

                _d3d9Surface = _d3d9Texture.GetSurfaceLevel(0);
                _target = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
            }
            else
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = D11Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _target = _device.CreateTexture2D(desc);
            }

            _rtv = _device.CreateRenderTargetView(_target);

            // 读回 staging：读回模式每帧用；D3DImage 模式截图（CaptureBitmap）用
            {
                var stagingDesc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = D11Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _staging = _device.CreateTexture2D(stagingDesc);
            }

            _width = width;
            _height = height;
            _readback = null;
            _needSetBackBuffer = true;
            return true;
        }
        catch (SharpGenException)
        {
            ReleaseTarget();
            return false;
        }
    }

    /// <summary>把刚画完的帧搬到 WPF（UI 线程；渲染完成后调）。</summary>
    public void Present(ID3D11DeviceContext context)
    {
        if (_rtv is null) return;

        if (_mode == Mode.D3DImage)
        {
            // 先 Flush：保证 D3D11 的绘制命令都进了驱动队列，WPF 合成器再读表面
            context.Flush();
            var image = _d3dImage!;
            image.Lock();
            try
            {
                if (_needSetBackBuffer)
                {
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface!.NativePointer, true);
                    _needSetBackBuffer = false;
                }

                image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                image.Unlock();
            }
        }
        else
        {
            ReadbackToBitmap(context, null);
        }
    }

    /// <summary>把当前 GPU 帧读回成 WriteableBitmap（截图 / 自检断言 / 调试截图用；UI 线程）。</summary>
    public WriteableBitmap Capture(ID3D11DeviceContext context)
    {
        if (_rtv is null) throw new InvalidOperationException("GPU 渲染目标还没准备好（先 Render 再 Capture）");
        if (_mode == Mode.Readback && _readbackBitmap is not null) return _readbackBitmap;
        return ReadbackToBitmap(context, null);
    }

    /// <summary>CopyResource → staging → Map → WriteableBitmap（可选复用传入的位图缓存）。</summary>
    private WriteableBitmap ReadbackToBitmap(ID3D11DeviceContext context, WriteableBitmap? reusable)
    {
        var width = _width;
        var height = _height;
        var pixels = _readback;
        if (pixels is null || pixels.Length != width * height)
        {
            pixels = new uint[width * height];
            _readback = pixels;
        }

        context.CopyResource(_staging, _target);
        var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowPitch = (int)mapped.RowPitch;
            var source = (uint*)mapped.DataPointer;
            fixed (uint* dest = pixels)
            {
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(source + (long)y * rowPitch / 4, dest + (long)y * width,
                        (long)width * 4, (long)width * 4);
                }
            }
        }
        finally
        {
            context.Unmap(_staging, 0);
        }

        var bitmap = reusable;
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            if (_mode == Mode.Readback) _readbackBitmap = bitmap;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return bitmap;
    }

    /// <summary>建 D3D9Ex 设备并选与 D3D11 设备同一块物理适配器（跨适配器共享句柄会失败）。</summary>
    private bool TryCreateD3D9Interop(ID3D11Device device)
    {
        try
        {
            var d3d9 = D3D9.Direct3DCreate9Ex();
            if (d3d9 is null) return false;

            var adapterIndex = PickD3D9Adapter(d3d9, device);

            var present = new D9PresentParameters
            {
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1,
                BackBufferFormat = D9Format.A8R8G8B8,
                SwapEffect = D9SwapEffect.Discard,
                Windowed = true,
                EnableAutoDepthStencil = false,
            };

            _device9 = d3d9.CreateDeviceEx((uint)adapterIndex, D9DeviceType.Hardware, IntPtr.Zero,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                present);

            _d3dImage = new D3DImage();
            return true;
        }
        catch (Exception)
        {
            DisposeD3D9();
            return false;
        }
    }

    /// <summary>按 LUID 找 D3D9 适配器里和 D3D11 设备同一块物理显卡的那个；找不到就回退适配器 0。</summary>
    private static int PickD3D9Adapter(IDirect3D9Ex d3d9, ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
            var dxgiLuid = adapter1.Description1.Luid;
            var count = d3d9.AdapterCount;
            for (uint i = 0; i < count; i++)
            {
                var luid = d3d9.GetAdapterLuid(i);
                if (luid.LowPart == dxgiLuid.LowPart && luid.HighPart == dxgiLuid.HighPart)
                {
                    return (int)i;
                }
            }
        }
        catch (Exception)
        {
            // LUID 拿不到就退回适配器 0（单显卡机器永远命中）
        }

        return 0;
    }

    private void ReleaseTarget()
    {
        _rtv?.Dispose();
        _rtv = null;
        _staging?.Dispose();
        _staging = null;
        _d3d9Surface?.Dispose();
        _d3d9Surface = null;
        _d3d9Texture?.Dispose();
        _d3d9Texture = null;
        _target?.Dispose();
        _target = null;
        _readback = null;
        _width = _height = 0;
    }

    private void DisposeD3D9()
    {
        _d3d9Surface?.Dispose();
        _d3d9Surface = null;
        _d3d9Texture?.Dispose();
        _d3d9Texture = null;
        _device9?.Dispose();
        _device9 = null;
        _d3dImage = null;
    }

    public void Dispose()
    {
        ReleaseTarget();
        DisposeD3D9();
    }
}
