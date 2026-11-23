using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace YuLiangToolkit.Tools.Live2D.Rendering.Gpu;

/// <summary>主 pass 的一次 indexed draw（一个可见网格；蒙版 pass 的每个蒙版网格也用同一条命令表示）。</summary>
internal readonly record struct GpuDrawCommand(
    int IndexOffset,
    int IndexCount,
    int TextureIndex,
    int Blend,
    float Opacity,
    float MultiplyR,
    float MultiplyG,
    float MultiplyB,
    float ScreenR,
    float ScreenG,
    float ScreenB,
    int MaskContext,
    bool InvertedMask);

/// <summary>一个蒙版组合的蒙版 pass：把每个蒙版网格的纹理 alpha 乘进 R8 目标（out = dst * (1 - alpha)）。</summary>
internal sealed class GpuMaskPass
{
    public List<GpuDrawCommand> Commands { get; } = new();
}

/// <summary>
/// D3D11 渲染管线封装：设备 / 着色器 / 混合状态 / 采样器 / 动态 VB·IB / 常量缓冲 / 图集纹理 / 蒙版目标池。
/// <para>混合状态严格对照官方 <c>CubismRenderState_D3D11.cpp</c>（预乘 alpha）：
/// Normal = (ONE, INV_SRC_ALPHA)（alpha 同样 src-over）；Additive = (ONE, ONE) 且 alpha 不动；
/// Multiplicative = (DEST_COLOR, INV_SRC_ALPHA) 且 alpha 不动；蒙版 pass = (ZERO, INV_SRC_ALPHA)。</para>
/// <para><b>线程</b>：所有方法在 UI 线程调（与 CPU 渲染器同一约定）。</para>
/// </summary>
internal sealed unsafe class GpuRenderDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShaderMain;
    private readonly ID3D11PixelShader _pixelShaderMask;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11RasterizerState _rasterizer;

    private readonly ID3D11BlendState _blendNormal;
    private readonly ID3D11BlendState _blendAdditive;
    private readonly ID3D11BlendState _blendMultiplicative;
    private readonly ID3D11BlendState _blendMask;

    private readonly ID3D11SamplerState _texSampler;
    private readonly ID3D11SamplerState _maskSampler;

    private readonly ID3D11Buffer _frameCbuffer;   // float4 OutputSize
    private readonly ID3D11Buffer _drawCbuffer;    // MulColor / ScrColor / Params
    private readonly ID3D11ShaderResourceView _whiteSrv;

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _vertexFloatCapacity;
    private int _indexCapacity;

    // 图集纹理缓存（按 TextureAtlasSet 实例；一个渲染器一套）
    private TextureAtlasSet? _atlasSet;
    private readonly List<ID3D11Texture2D> _atlasTextures = new();
    private readonly List<ID3D11ShaderResourceView> _atlasSrvs = new();

    // 蒙版目标池（每个蒙版组合一份 R8 目标，尺寸跟随输出）
    private Format _maskFormat = Format.R8_UNorm;
    private int _maskTargetWidth;
    private int _maskTargetHeight;
    private readonly List<ID3D11Texture2D> _maskTargets = new();
    private readonly List<ID3D11RenderTargetView> _maskRtvs = new();
    private readonly List<ID3D11ShaderResourceView> _maskSrvs = new();

    private bool _pipelineReady;
    private bool _disposed;

    // 【临时诊断】构造到哪一步了（TryCreate 失败时带回给调用方）
    internal static string? LastStage;

    private GpuRenderDevice(ID3D11Device device, ID3D11DeviceContext context,
        byte[] vertexCode, byte[] mainCode, byte[] maskCode)
    {
        _device = device;
        _context = context;
        LastStage = "CreateVertexShader";
        _vertexShader = device.CreateVertexShader(vertexCode, null);
        LastStage = "CreatePixelShaderMain";
        _pixelShaderMain = device.CreatePixelShader(mainCode, null);
        LastStage = "CreatePixelShaderMask";
        _pixelShaderMask = device.CreatePixelShader(maskCode, null);
        LastStage = "CreateInputLayout";
        _inputLayout = device.CreateInputLayout(Live2DGpuShaders.InputElements, vertexCode);
        LastStage = "CreateRasterizerState";
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,           // Live2D 网格双面，不剔背面（同 CPU 光栅器）
            DepthClipEnable = false,
            ScissorEnable = false,
        });

        LastStage = "CreateBlendStates";
        _blendNormal = MakeBlend(device, Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha);
        _blendAdditive = MakeBlend(device, Blend.One, Blend.One, Blend.Zero, Blend.One);
        _blendMultiplicative = MakeBlend(device, Blend.DestinationColor, Blend.InverseSourceAlpha, Blend.Zero, Blend.One);
        _blendMask = MakeBlend(device, Blend.Zero, Blend.InverseSourceAlpha, Blend.Zero, Blend.One);

        LastStage = "CreateSamplers";
        _texSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MaxAnisotropy = 16,
            MinLOD = 0f,
            MaxLOD = float.MaxValue, // mip 链比 atlas 自带的短就夹在最后一级（同 CPU 选层规则）
        });

        _maskSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = 0f,
        });

        LastStage = "CreateConstantBuffers";
        // 常量缓冲必须 Dynamic（CPU 每帧 Map WriteDiscard 写入）；Default 资源 Map 会 E_INVALIDARG
        _frameCbuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 16,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        _drawCbuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 48,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        LastStage = "CreateWhiteTexture";
        _whiteSrv = CreateWhiteTexture(device);

        var r8Support = device.CheckFormatSupport(Format.R8_UNorm);
        _maskFormat = (r8Support & FormatSupport.RenderTarget) != 0 ? Format.R8_UNorm : GpuPresenter.MaskFallbackFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerDrawData
    {
        public Vector4 MulColor;
        public Vector4 ScrColor;
        public Vector4 Params; // x = Opacity, y = MaskEnabled, z = Inverted, w = 未用
    }

    /// <summary>创建 D3D11 设备（硬件加速；失败返回 null，由调用方回退 CPU 渲染器）。</summary>
    public static GpuRenderDevice? TryCreate(out string? error)
    {
        error = null;
        try
        {
            // 逐级尝试 FL 11.0 / 10.1 / 10.0（IntPtr 重载一次只收一个级别）；
            // BGRA 标志是 D3DImage / B8G8R8A8 目标需要的
            SharpGen.Runtime.Result created = SharpGen.Runtime.Result.Fail;
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            foreach (var level in FeatureLevels)
            {
                created = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    level, out device, out context);
                if (created.Success && device is not null && context is not null) break;
            }

            if (created.Failure || device is null || context is null)
            {
                error = $"D3D11CreateDevice 失败：{created.Code}";
                return null;
            }

            try
            {
                var (vertex, main, mask) = Live2DGpuShaders.Compile();
                byte[][] codes = { vertex, main, mask };
                foreach (var code in codes)
                {
                    if (code is null || code.Length == 0)
                    {
                        error = "[Compile] 着色器字节码为空";
                        return null;
                    }
                }

                return new GpuRenderDevice(device, context, vertex, main, mask);
            }
            catch (Exception ex)
            {
                device.Dispose();
                context.Dispose();
                error = $"{GpuRenderDevice.LastStage} [{ex.GetType().Name}] {ex.Message}";
                return null;
            }
        }
        catch (Exception ex)
        {
            error = $"[DeviceCreate] [{ex.GetType().Name}] {ex.Message}";
            return null;
        }
    }

    public ID3D11DeviceContext Context => _context;
    public Format MaskFormat => _maskFormat;

    /// <summary>底层 D3D11 设备（GpuPresenter 建 D3D9 共享纹理 / 读回 staging 用）。</summary>
    public ID3D11Device Device => _device;

    /// <summary>上传纹理图集（每个 <see cref="TextureAtlasSet"/> 实例一次；mip 链原样上传，GPU 自动选层）。</summary>
    public void EnsureAtlasTextures(TextureAtlasSet textures)
    {
        if (ReferenceEquals(_atlasSet, textures) && _atlasSrvs.Count == textures.Count) return;

        foreach (var texture in _atlasTextures) texture.Dispose();
        _atlasTextures.Clear();
        foreach (var srv in _atlasSrvs) srv.Dispose();
        _atlasSrvs.Clear();
        _atlasSet = textures;

        for (var i = 0; i < textures.Count; i++)
        {
            var atlas = textures[i];
            var mips = atlas.Mips;
            var mipLevels = mips is { Length: > 0 } ? mips.Length : 1;

            var subresources = new SubresourceData[mipLevels];
            var handles = new GCHandle[mipLevels];
            try
            {
                for (var level = 0; level < mipLevels; level++)
                {
                    var mip = mips is { Length: > 0 } ? mips[level] : new TextureMipLevel(atlas.Width, atlas.Height, atlas.Pixels);
                    handles[level] = GCHandle.Alloc(mip.Pixels, GCHandleType.Pinned);
                    subresources[level] = new SubresourceData
                    {
                        DataPointer = handles[level].AddrOfPinnedObject(),
                        RowPitch = (uint)(mip.Width * 4),
                                            };
                }

                var desc = new Texture2DDescription
                {
                    Width = (uint)atlas.Width,
                    Height = (uint)atlas.Height,
                    MipLevels = (uint)mipLevels,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, // 像素数组就是 B,G,R,A 小端序（0xAARRGGBB），直传
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };
                var texture = _device.CreateTexture2D(desc, subresources);
                _atlasTextures.Add(texture);
                _atlasSrvs.Add(_device.CreateShaderResourceView(texture));
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.IsAllocated) handle.Free();
                }
            }
        }
    }

    /// <summary>渲染一帧（蒙版 pass → 主 pass）。顶点为 float4（屏幕像素 x,y + 图集 u,v）。</summary>
    public void DrawFrame(ID3D11RenderTargetView rtv, int width, int height,
        float[] vertices, int vertexFloatCount, uint[] indices, int indexCount,
        IReadOnlyList<GpuDrawCommand> commands, IReadOnlyList<GpuMaskPass> maskPasses,
        bool transparentBackground, uint backgroundColor)
    {
        EnsurePipeline();
        EnsureVertexBuffer(vertexFloatCount);
        LastStage = "EnsureIndexCapacity";
        EnsureIndexBuffer(indexCount);

        // ---------- 上传顶点 / 索引 ----------
        var mapped = _context.Map(_vertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            vertices.AsSpan(0, vertexFloatCount).CopyTo(new Span<float>((float*)mapped.DataPointer, vertexFloatCount));
        }
        finally
        {
            _context.Unmap(_vertexBuffer, 0);
        }

        LastStage = "UploadIndexBuffer";
        mapped = _context.Map(_indexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            indices.AsSpan(0, indexCount).CopyTo(new Span<uint>((uint*)mapped.DataPointer, indexCount));
        }
        finally
        {
            _context.Unmap(_indexBuffer, 0);
        }

        // ---------- 管线公共状态 ----------
        LastStage = "SetPipelineState";
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffers(0, 1, new[] { _vertexBuffer }, stackalloc uint[] { 16 }, stackalloc uint[] { 0 });
        _context.IASetIndexBuffer(_indexBuffer, Format.R32_UInt, 0);
        _context.RSSetViewport(0f, 0f, width, height, 0f, 1f);
        _context.RSSetState(_rasterizer);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _frameCbuffer);
        _context.PSSetConstantBuffer(0, _frameCbuffer);
        _context.PSSetConstantBuffer(1, _drawCbuffer);
        _context.PSSetSampler(0, _texSampler);
        _context.PSSetSampler(1, _maskSampler);

        // ---------- PerFrame 常量 ----------
        LastStage = "WriteFrameConstants";
        mapped = _context.Map(_frameCbuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(Vector4*)mapped.DataPointer = new Vector4(width, height, 1f / width, 1f / height);
        }
        finally
        {
            _context.Unmap(_frameCbuffer, 0);
        }

        // ---------- 蒙版 pass：每个组合一份 R8 目标，R 通道累积 Π(1 - 蒙版alpha) ----------
        LastStage = "EnsureMaskTargets";
        EnsureMaskTargets(maskPasses.Count, width, height);
        if (maskPasses.Count > 0)
        {
            // 防御：先占住 SRV 槽，清掉上一帧主 pass 对蒙版纹理的绑定
            // （同一资源同时作为 RT 和 SRV 时 D3D11 行为未定义，写进蒙版目标的内容可能错乱 → 部件闪烁）
            _context.PSSetShaderResources(0, new[] { _whiteSrv });
            _context.PSSetShaderResources(1, new[] { _whiteSrv });
            _context.PSSetShader(_pixelShaderMask);
            _context.OMSetBlendState(_blendMask);
            for (var c = 0; c < maskPasses.Count; c++)
            {
                _context.ClearRenderTargetView(_maskRtvs[c], new Color4(1f, 1f, 1f, 1f));
                _context.OMSetRenderTargets(_maskRtvs[c], null);
                foreach (var command in maskPasses[c].Commands)
                {
                    _context.PSSetShaderResources(0, new[] { _atlasSrvs[command.TextureIndex] });
                    _context.DrawIndexed((uint)command.IndexCount, (uint)command.IndexOffset, 0);
                }
            }
        }

        // ---------- 主 pass ----------
        LastStage = "MainPass";
        var bg = backgroundColor;
        var bgA = (bg >> 24) / 255f;
        var bgR = (bg >> 16 & 0xFF) / 255f * bgA;
        var bgG = (bg >> 8 & 0xFF) / 255f * bgA;
        var bgB = (bg & 0xFF) / 255f * bgA;
        var clearColor = transparentBackground
            ? new Color4(0f, 0f, 0f, 0f)
            : new Color4(bgR, bgG, bgB, bgA);

        _context.PSSetShader(_pixelShaderMain);
        _context.OMSetRenderTargets(rtv, null);
        _context.ClearRenderTargetView(rtv, clearColor);

        foreach (var command in commands)
        {
            var data = new PerDrawData
            {
                MulColor = new Vector4(command.MultiplyR, command.MultiplyG, command.MultiplyB, 1f),
                ScrColor = new Vector4(command.ScreenR, command.ScreenG, command.ScreenB, 1f),
                Params = new Vector4(command.Opacity,
                    command.MaskContext >= 0 ? 1f : 0f,
                    command.InvertedMask ? 1f : 0f, 0f),
            };

            mapped = _context.Map(_drawCbuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                *(PerDrawData*)mapped.DataPointer = data;
            }
            finally
            {
                _context.Unmap(_drawCbuffer, 0);
            }

            _context.PSSetShaderResources(0, new[] { _atlasSrvs[command.TextureIndex] });
            _context.PSSetShaderResources(1, new[] { command.MaskContext >= 0 ? _maskSrvs[command.MaskContext] : _whiteSrv });

            _context.OMSetBlendState(command.Blend switch
            {
                1 => _blendAdditive,
                2 => _blendMultiplicative,
                _ => _blendNormal,
            });

            _context.DrawIndexed((uint)command.IndexCount, (uint)command.IndexOffset, 0);
        }
    }

    private void EnsurePipeline()
    {
        if (_pipelineReady) return;
        _pipelineReady = true;
    }

    /// <summary>蒙版目标池（超出就再建；数量 = 本帧蒙版组合数，通常个位数）。</summary>
    private void EnsureMaskTargets(int count, int width, int height)
    {
        if (_maskSrvs.Count >= count && _maskTargetWidth == width && _maskTargetHeight == height) return;

        if (_maskTargetWidth != width || _maskTargetHeight != height)
        {
            foreach (var rtv in _maskRtvs) rtv.Dispose();
            _maskRtvs.Clear();
            foreach (var srv in _maskSrvs) srv.Dispose();
            _maskSrvs.Clear();
            foreach (var texture in _maskTargets) texture.Dispose();
            _maskTargets.Clear();
            _maskTargetWidth = width;
            _maskTargetHeight = height;
        }

        while (_maskSrvs.Count < count)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = _maskFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            var texture = _device.CreateTexture2D(desc);
            _maskTargets.Add(texture);
            _maskRtvs.Add(_device.CreateRenderTargetView(texture));
            _maskSrvs.Add(_device.CreateShaderResourceView(texture));
        }
    }

    private void EnsureVertexBuffer(int floatCount)
    {
        if (_vertexBuffer is not null && _vertexFloatCapacity >= floatCount) return;

        var newCapacity = Math.Max(floatCount, Math.Max(_vertexFloatCapacity * 2, 4096));
        _vertexBuffer?.Dispose();
        _vertexBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(newCapacity * 4),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });
        _vertexFloatCapacity = newCapacity;
    }

    private void EnsureIndexBuffer(int indexCount)
    {
        if (_indexBuffer is not null && _indexCapacity >= indexCount) return;

        var newCapacity = Math.Max(indexCount, Math.Max(_indexCapacity * 2, 8192));
        _indexBuffer?.Dispose();
        _indexBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(newCapacity * sizeof(uint)),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.IndexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });
        _indexCapacity = newCapacity;
    }

    private static ID3D11BlendState MakeBlend(ID3D11Device device, Blend source, Blend destination,
        Blend sourceAlpha, Blend destinationAlpha)
    {
        var desc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        desc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = source,
            DestinationBlend = destination,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = sourceAlpha,
            DestinationBlendAlpha = destinationAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return device.CreateBlendState(desc);
    }

    private static ID3D11ShaderResourceView CreateWhiteTexture(ID3D11Device device)
    {
        var desc = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        var white = 0xFFFFFFFFu;
        var subresource = new SubresourceData { DataPointer = (IntPtr)(&white), RowPitch = 4 };
        var texture = device.CreateTexture2D(desc, subresource);
        return device.CreateShaderResourceView(texture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        foreach (var texture in _atlasTextures) texture.Dispose();
        _atlasTextures.Clear();
        foreach (var srv in _atlasSrvs) srv.Dispose();
        _atlasSrvs.Clear();
        foreach (var rtv in _maskRtvs) rtv.Dispose();
        _maskRtvs.Clear();
        foreach (var srv in _maskSrvs) srv.Dispose();
        _maskSrvs.Clear();
        foreach (var texture in _maskTargets) texture.Dispose();
        _maskTargets.Clear();
        _whiteSrv.Dispose();
        _frameCbuffer.Dispose();
        _drawCbuffer.Dispose();
        _texSampler.Dispose();
        _maskSampler.Dispose();
        _blendNormal.Dispose();
        _blendAdditive.Dispose();
        _blendMultiplicative.Dispose();
        _blendMask.Dispose();
        _rasterizer.Dispose();
        _inputLayout.Dispose();
        _pixelShaderMask.Dispose();
        _pixelShaderMain.Dispose();
        _vertexShader.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
