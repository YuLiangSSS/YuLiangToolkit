using System;
using System.Collections.Generic;
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
/// <para><b>双缓冲（D3DImage 模式）</b>：D3D9/D3D11 跨设备共享纹理没有同步原语（keyed mutex D3D9 不认），
/// 单缓冲时 WPF 合成可能读到 D3D11 画到一半的帧（清屏完、网格还没画完）→ 模型部件闪烁。
/// 所以建两块共享纹理交替「渲染 / 呈现」：呈现的那块永远是上上帧就画完的完整画面。</para>
/// <para><b>线程</b>：所有方法在 UI 线程调（和 CPU 渲染器同一约定）；D3DImage 的 Lock / Unlock 要求 UI 线程。</para>
/// </summary>
internal sealed unsafe class GpuPresenter : IDisposable
{
    /// <summary>蒙版 R8 目标不被硬件支持时的兜底格式（读 R 通道即可，语义一致）。</summary>
    internal const D11Format MaskFallbackFormat = D11Format.B8G8R8A8_UNorm;

    /// <summary>D3DImage 模式的共享纹理数（双缓冲）。</summary>
    private const int BufferCount = 2;

    private readonly ID3D11Device _device;
    private readonly Mode _mode;

    private IDirect3DDevice9Ex? _device9;
    private D3DImage? _d3dImage;

    // D3DImage 模式：双缓冲目标
    private readonly ID3D11Texture2D?[] _targets = new ID3D11Texture2D?[BufferCount];
    private readonly ID3D11RenderTargetView?[] _rtvs = new ID3D11RenderTargetView?[BufferCount];
    private readonly IDirect3DTexture9?[] _d3d9Textures = new IDirect3DTexture9?[BufferCount];
    private readonly IDirect3DSurface9?[] _d3d9Surfaces = new IDirect3DSurface9?[BufferCount];

    // 读回模式：单目标 + staging
    private ID3D11Texture2D? _readbackTarget;
    private ID3D11RenderTargetView? _readbackRtv;
    private ID3D11Texture2D? _staging;
    private uint[]? _readback;
    private WriteableBitmap? _readbackBitmap;

    /// <summary>下一次渲染写入的缓冲号（D3DImage 模式）。</summary>
    private int _writeIndex;

    /// <summary>最近一次呈现给 WPF 的缓冲号（Capture 从这里读，保证是完整画面）。</summary>
    private int _presentedIndex;

    /// <summary>换尺寸时退役的旧资源：必须等下一次 Present（Unlock 换好新 back buffer）之后才能 Dispose，
    /// 否则 D3DImage 里还指着已释放的 D3D9 表面，合成线程会画出一帧空内容 → 换尺寸 / 拖动时闪烁。</summary>
    private readonly List<IDisposable> _retired = new();

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

    /// <summary>本帧要渲染进的目标视图（每帧渲染前取；Present 之后自动切到下一块缓冲）。</summary>
    public ID3D11RenderTargetView? RenderTargetView => _mode == Mode.D3DImage
        ? _rtvs[_writeIndex]
        : _readbackRtv;

    public int Width => _width;
    public int Height => _height;

    /// <summary>创建出图通道：<paramref name="useD3DImage"/> 且 D3D9Ex 共享纹理可用时走 D3DImage 零拷贝
    /// （<b>只适合不透明窗口</b>——WPF 分层透明窗口软件合成 D3DImage 会闪）；否则读回模式。</summary>
    public static GpuPresenter Create(ID3D11Device device, bool useD3DImage = true)
    {
        if (useD3DImage)
        {
            var presenter = new GpuPresenter(device, Mode.D3DImage);
            if (presenter.TryCreateD3D9Interop(device))
            {
                return presenter;
            }

            presenter.DisposeD3D9();
        }

        return new GpuPresenter(device, Mode.Readback);
    }

    /// <summary>确保渲染目标 / 出图源是这个尺寸（变化才重建；重建先建新、旧的延迟到下一次 Present 后释放）。</summary>
    public bool EnsureTarget(int width, int height)
    {
        if (_width == width && _height == height && (_mode == Mode.D3DImage ? _rtvs[0] is not null : _readbackRtv is not null))
        {
            return true;
        }

        if (width < 1 || height < 1) return false;

        ID3D11Texture2D?[]? newTargets = null;
        ID3D11RenderTargetView?[]? newRtvs = null;
        IDirect3DTexture9?[]? newD3d9Textures = null;
        IDirect3DSurface9?[]? newD3d9Surfaces = null;
        ID3D11Texture2D? newReadbackTarget = null;
        ID3D11RenderTargetView? newReadbackRtv = null;
        ID3D11Texture2D? newStaging = null;

        try
        {
            if (_mode == Mode.D3DImage && _device9 is not null)
            {
                newTargets = new ID3D11Texture2D?[BufferCount];
                newRtvs = new ID3D11RenderTargetView?[BufferCount];
                newD3d9Textures = new IDirect3DTexture9?[BufferCount];
                newD3d9Surfaces = new IDirect3DSurface9?[BufferCount];
                for (var i = 0; i < BufferCount; i++)
                {
                    // D3D9Ex 建共享纹理（A8R8G8B8 = B8G8R8A8 内存序，预乘 BGRA 正好是 D3DImage 要的格式）
                    var sharedHandle = IntPtr.Zero;
                    newD3d9Textures[i] = _device9.CreateTexture((uint)width, (uint)height, 1,
                        D9Usage.RenderTarget, D9Format.A8R8G8B8, Pool.Default, ref sharedHandle);
                    if (newD3d9Textures[i] is null || sharedHandle == IntPtr.Zero)
                    {
                        throw new SharpGenException(Result.Fail);
                    }

                    newD3d9Surfaces[i] = newD3d9Textures[i].GetSurfaceLevel(0);
                    newTargets[i] = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                    newRtvs[i] = _device.CreateRenderTargetView(newTargets[i]);
                }
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
                newReadbackTarget = _device.CreateTexture2D(desc);
                newReadbackRtv = _device.CreateRenderTargetView(newReadbackTarget);
            }

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
                newStaging = _device.CreateTexture2D(stagingDesc);
            }

            // 新资源全部就绪后才退役旧的（旧的等下一次 Present 之后再真正 Dispose）
            RetireCurrent();
            _staging?.Dispose();
            _staging = newStaging;
            newStaging = null;

            if (_mode == Mode.D3DImage)
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    _targets[i] = newTargets![i];
                    _rtvs[i] = newRtvs![i];
                    _d3d9Textures[i] = newD3d9Textures![i];
                    _d3d9Surfaces[i] = newD3d9Surfaces![i];
                    newTargets[i] = null;
                    newRtvs[i] = null;
                    newD3d9Textures[i] = null;
                    newD3d9Surfaces[i] = null;
                }
            }
            else
            {
                _readbackTarget = newReadbackTarget;
                _readbackRtv = newReadbackRtv;
                newReadbackTarget = null;
                newReadbackRtv = null;
            }

            _width = width;
            _height = height;
            _writeIndex = 0;
            _presentedIndex = 0;
            _readback = null;
            return true;
        }
        catch (SharpGenException)
        {
            // 失败：先把上一轮退役的资源真正释放（显存可能正好被它们占着），再立即重试一次。
            // 典型场景：纹理 4096 时显存吃紧，缩放重建瞬间 OOM——释放旧纹理后往往就能建成。
            newStaging?.Dispose();
            newReadbackRtv?.Dispose();
            newReadbackTarget?.Dispose();
            if (newD3d9Surfaces is not null) foreach (var s in newD3d9Surfaces) s?.Dispose();
            if (newD3d9Textures is not null) foreach (var t in newD3d9Textures) t?.Dispose();
            if (newRtvs is not null) foreach (var r in newRtvs) r?.Dispose();
            if (newTargets is not null) foreach (var t in newTargets) t?.Dispose();

            DisposeRetired();
            return EnsureTargetRetry(width, height);
        }
    }

    /// <summary>EnsureTarget 的第二次尝试（调用方已释放退役资源）。</summary>
    private bool EnsureTargetRetry(int width, int height)
    {
        ID3D11Texture2D?[]? newTargets = null;
        ID3D11RenderTargetView?[]? newRtvs = null;
        IDirect3DTexture9?[]? newD3d9Textures = null;
        IDirect3DSurface9?[]? newD3d9Surfaces = null;
        ID3D11Texture2D? newReadbackTarget = null;
        ID3D11RenderTargetView? newReadbackRtv = null;

        try
        {
            if (_mode == Mode.D3DImage && _device9 is not null)
            {
                newTargets = new ID3D11Texture2D?[BufferCount];
                newRtvs = new ID3D11RenderTargetView?[BufferCount];
                newD3d9Textures = new IDirect3DTexture9?[BufferCount];
                newD3d9Surfaces = new IDirect3DSurface9?[BufferCount];
                for (var i = 0; i < BufferCount; i++)
                {
                    var sharedHandle = IntPtr.Zero;
                    newD3d9Textures[i] = _device9.CreateTexture((uint)width, (uint)height, 1,
                        D9Usage.RenderTarget, D9Format.A8R8G8B8, Pool.Default, ref sharedHandle);
                    if (newD3d9Textures[i] is null || sharedHandle == IntPtr.Zero)
                    {
                        return false;
                    }

                    newD3d9Surfaces[i] = newD3d9Textures[i].GetSurfaceLevel(0);
                    newTargets[i] = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                    newRtvs[i] = _device.CreateRenderTargetView(newTargets[i]);
                }
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
                newReadbackTarget = _device.CreateTexture2D(desc);
                newReadbackRtv = _device.CreateRenderTargetView(newReadbackTarget);
            }

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
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(stagingDesc);

            // 新资源就绪后再退役旧目标（等下一次 Present 后释放；retry 路径也要走，否则旧目标泄漏）
            RetireCurrent();
            if (_mode == Mode.D3DImage)
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    _targets[i] = newTargets![i];
                    _rtvs[i] = newRtvs![i];
                    _d3d9Textures[i] = newD3d9Textures![i];
                    _d3d9Surfaces[i] = newD3d9Surfaces![i];
                    newTargets[i] = null;
                    newRtvs[i] = null;
                    newD3d9Textures[i] = null;
                    newD3d9Surfaces[i] = null;
                }
            }
            else
            {
                _readbackTarget = newReadbackTarget;
                _readbackRtv = newReadbackRtv;
                newReadbackTarget = null;
                newReadbackRtv = null;
            }

            _width = width;
            _height = height;
            _writeIndex = 0;
            _presentedIndex = 0;
            _readback = null;
            return true;
        }
        catch (SharpGenException)
        {
            newReadbackRtv?.Dispose();
            newReadbackTarget?.Dispose();
            if (newD3d9Surfaces is not null) foreach (var s in newD3d9Surfaces) s?.Dispose();
            if (newD3d9Textures is not null) foreach (var t in newD3d9Textures) t?.Dispose();
            if (newRtvs is not null) foreach (var r in newRtvs) r?.Dispose();
            if (newTargets is not null) foreach (var t in newTargets) t?.Dispose();
            return false;
        }
    }

    /// <summary>把刚画完的帧搬到 WPF（UI 线程；渲染完成后调）。</summary>
    public void Present(ID3D11DeviceContext context)
    {
        if (_mode == Mode.D3DImage)
        {
            var surface = _d3d9Surfaces[_writeIndex];
            if (surface is null || _d3dImage is null) return;

            // 先 Flush：保证这一帧的 D3D11 命令都提交了，WPF 再锁定呈现
            context.Flush();
            var image = _d3dImage;
            image.Lock();
            try
            {
                // 双缓冲每帧切换 back buffer（SetBackBuffer 在 Lock 内调用）
                image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer, true);
                image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                image.Unlock();
            }

            _presentedIndex = _writeIndex;
            _writeIndex = (_writeIndex + 1) % BufferCount;
            DisposeRetired();
        }
        else
        {
            ReadbackToBitmap(context, _readbackTarget, null);
            DisposeRetired();
        }
    }

    /// <summary>把当前在用的目标整体移入退役名单（等下一次 Present 后再 Dispose）。</summary>
    private void RetireCurrent()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (_rtvs[i] is not null) _retired.Add(_rtvs[i]!);
            if (_d3d9Surfaces[i] is not null) _retired.Add(_d3d9Surfaces[i]!);
            if (_d3d9Textures[i] is not null) _retired.Add(_d3d9Textures[i]!);
            if (_targets[i] is not null) _retired.Add(_targets[i]!);
            _rtvs[i] = null;
            _d3d9Surfaces[i] = null;
            _d3d9Textures[i] = null;
            _targets[i] = null;
        }

        if (_readbackRtv is not null) _retired.Add(_readbackRtv);
        if (_readbackTarget is not null) _retired.Add(_readbackTarget);
        _readbackRtv = null;
        _readbackTarget = null;
    }

    private void DisposeRetired()
    {
        if (_retired.Count == 0) return;
        foreach (var item in _retired) item.Dispose();
        _retired.Clear();
    }

    /// <summary>把当前 GPU 帧读回成 WriteableBitmap（截图 / 自检断言 / 调试截图用；UI 线程）。</summary>
    public WriteableBitmap Capture(ID3D11DeviceContext context)
    {
        if (_mode == Mode.D3DImage)
        {
            var source = _targets[_presentedIndex];
            if (source is null) throw new InvalidOperationException("GPU 渲染目标还没准备好（先 Render 再 Capture）");
            return ReadbackToBitmap(context, source, null);
        }

        if (_readbackRtv is null) throw new InvalidOperationException("GPU 渲染目标还没准备好（先 Render 再 Capture）");
        if (_readbackBitmap is not null) return _readbackBitmap;
        return ReadbackToBitmap(context, _readbackTarget, null);
    }

    /// <summary>CopyResource → staging → Map → WriteableBitmap（可选复用传入的位图缓存）。</summary>
    private WriteableBitmap ReadbackToBitmap(ID3D11DeviceContext context, ID3D11Texture2D? source, WriteableBitmap? reusable)
    {
        var width = _width;
        var height = _height;
        if (source is null || _staging is null)
        {
            throw new InvalidOperationException("GPU 读回目标还没准备好（先 Render 再 Capture）");
        }

        var pixels = _readback;
        if (pixels is null || pixels.Length != width * height)
        {
            pixels = new uint[width * height];
            _readback = pixels;
        }

        context.CopyResource(_staging, source);
        var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowPitch = (int)mapped.RowPitch;
            var row = (uint*)mapped.DataPointer;
            fixed (uint* dest = pixels)
            {
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(row + (long)y * rowPitch / 4, dest + (long)y * width,
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
        for (var i = 0; i < BufferCount; i++)
        {
            _rtvs[i]?.Dispose();
            _rtvs[i] = null;
            _d3d9Surfaces[i]?.Dispose();
            _d3d9Surfaces[i] = null;
            _d3d9Textures[i]?.Dispose();
            _d3d9Textures[i] = null;
            _targets[i]?.Dispose();
            _targets[i] = null;
        }

        _readbackRtv?.Dispose();
        _readbackRtv = null;
        _readbackTarget?.Dispose();
        _readbackTarget = null;
        _staging?.Dispose();
        _staging = null;
        _readback = null;
        _writeIndex = 0;
        _width = _height = 0;
    }

    private void DisposeD3D9()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            _d3d9Surfaces[i]?.Dispose();
            _d3d9Surfaces[i] = null;
            _d3d9Textures[i]?.Dispose();
            _d3d9Textures[i] = null;
        }

        _device9?.Dispose();
        _device9 = null;
        _d3dImage = null;
    }

    public void Dispose()
    {
        ReleaseTarget();
        DisposeRetired();
        DisposeD3D9();
    }
}
