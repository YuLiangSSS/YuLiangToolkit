using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Rendering.Gpu;

/// <summary>
/// Live2D GPU 渲染器：把官方 Cubism Core 每帧输出的网格（顶点 / UV / 三角形 / 蒙版 / 三种混合 /
/// MultiplyColor·ScreenColor）交给 D3D11 画，出图走 D3DImage 零拷贝或读回通道。
/// <para>每帧 CPU 只做很轻的活：收集可见网格 + 顶点变换到输出像素（和 CPU 渲染器同一套换算公式），
/// 光栅化 / 蒙版覆盖度 / 混合全部在 GPU 上完成。语义与 CPU 软件渲染器（<see cref="CubismRenderer"/>）和
/// 官方 CubismRenderer_D3D11 一致：
/// 蒙版 R8 目标 R 通道累积 <c>Π(1 - 蒙版alpha)</c>（反转蒙版取 1 - 覆盖度）；
/// 纹理直采样（非预乘）→ 乘 MultiplyColor → ScreenColor → 预乘 alpha → (ONE, INV_SRC_ALPHA) 系混合。</para>
/// </summary>
internal sealed unsafe class CubismGpuRenderer : IDisposable
{
    private const float MinDrawableOpacity = 1f / 255f;

    private readonly TextureAtlasSet _textures;
    private readonly GpuRenderDevice _device;
    private readonly GpuPresenter _presenter;

    /// <summary>顶点缓冲数据（float4：屏幕像素 x,y + 图集 u,v；主 pass 与蒙版 pass 共用一块）。</summary>
    private float[] _vertices = new float[4096];

    private int _vertexFloatCount;

    /// <summary>索引缓冲数据（已加顶点基址偏移）。</summary>
    private ushort[] _indices = new ushort[8192];

    private int _indexCount;

    private readonly List<GpuDrawCommand> _commands = new(256);
    private readonly List<GpuMaskPass> _maskPasses = new();

    // 蒙版组合归类（同 CPU 渲染器的 BuildMaskContexts：排序后的蒙版下标组相同即同一组合）
    private readonly List<int[]> _contextMasks = new();
    private readonly List<int> _scratch = new(32);
    private readonly Dictionary<int, (int VertexBase, int IndexOffset, int IndexCount)> _maskBlocks = new();

    private bool _disposed;

    // 【诊断】渲染流程走到哪一步（GPU 失败回退时带回给调用方）
    internal static string? LastStage;

    public CubismGpuRenderer(TextureAtlasSet textures, GpuRenderDevice device, GpuPresenter presenter)
    {
        _textures = textures;
        _device = device;
        _presenter = presenter;
    }

    /// <summary>出图源（D3DImage 或读回位图）。</summary>
    public ImageSource? Surface => _presenter.SurfaceImage;

    /// <summary>出图是否零拷贝（D3DImage）。</summary>
    public bool IsZeroCopy => _presenter.IsZeroCopy;

    /// <summary>GPU 设备上下文（Capture 用）。</summary>
    public GpuRenderDevice Device => _device;

    /// <summary>最近一帧渲染统计（语义同 CPU 渲染器）。</summary>
    public RenderStats LastStats { get; private set; } = RenderStats.Empty;

    /// <summary>最近一帧 CPU 侧耗时（毫秒）：收集 + 顶点变换 + 提交。</summary>
    public double LastCpuMilliseconds { get; private set; }

    /// <summary>
    /// 渲染一帧。失败返回 false（目标创建失败等），调用方应回退 CPU 渲染器。
    /// 只读模型数据，不改参数；必须在 UI 线程调。
    /// </summary>
    public bool Render(CubismModel model, RenderOptions options)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var width = Math.Clamp(options.Width, 1, 8192);
        var height = Math.Clamp(options.Height, 1, 8192);
        LastStage = "EnsureTarget";
        if (!_presenter.EnsureTarget(width, height)) return false;

        LastStage = "EnsureAtlasTextures";
        _device.EnsureAtlasTextures(_textures);

        // ---------- 画布像素 → 输出像素 变换（与 CPU 渲染器同一公式） ----------
        var canvasWidth = Math.Max(model.CanvasWidthPixels, 1f);
        var canvasHeight = Math.Max(model.CanvasHeightPixels, 1f);
        double scale;
        double offsetX;
        double offsetY;
        if (options.FitCanvas)
        {
            scale = Math.Min(width / (double)canvasWidth, height / (double)canvasHeight);
            offsetX = (width - canvasWidth * scale) * 0.5;
            offsetY = (height - canvasHeight * scale) * 0.5;
        }
        else
        {
            scale = options.Scale;
            offsetX = options.OffsetX;
            offsetY = options.OffsetY;
        }

        var axisScale = model.PixelsPerUnit * (float)scale;
        var baseX = model.CanvasOriginX * (float)scale + (float)offsetX;
        var baseY = model.CanvasOriginY * (float)scale + (float)offsetY;

        // ---------- 收集可见网格 + 顶点变换 ----------
        _vertexFloatCount = 0;
        _indexCount = 0;
        _commands.Clear();
        _maskPasses.Clear();
        _contextMasks.Clear();
        _maskBlocks.Clear();

        var pixelsFilled = 0L;
        var ordered = model.RenderOrderedDrawableIndices;
        for (var k = 0; k < ordered.Length; k++)
        {
            var drawable = model.Drawables[ordered[k]];

            // 官方 DrawDrawable / DrawMeshDX11：不可见 / 透明度 <= 0 / 网格退化直接跳过
            if (!drawable.IsVisible) continue;
            if (drawable.VertexCount < 3 || drawable.IndexCount < 3) continue;
            if (drawable.Positions is null || drawable.Uvs is null || drawable.Indices is null) continue;
            if (drawable.Opacity <= MinDrawableOpacity) continue;

            var textureIndex = drawable.TextureIndex;
            if (textureIndex < 0 || textureIndex >= _textures.Count) continue;

            var vertexBase = AppendVertices(drawable.Positions, drawable.Uvs, drawable.VertexCount,
                axisScale, baseX, baseY, width, height, out var boundsArea);
            if (vertexBase < 0) continue; // 整块在屏幕外（AppendVertices 内做包围盒剔除）

            var indexOffset = AppendIndices(drawable.Indices, drawable.IndexCount, vertexBase);

            // 蒙版组合归类（排序后相同的一组蒙版共用一份覆盖目标）
            var maskContext = -1;
            var maskIndices = drawable.MaskIndices;
            if (maskIndices is { Length: > 0 })
            {
                maskContext = FindOrAddMaskContext(maskIndices);
                while (_maskPasses.Count <= maskContext) _maskPasses.Add(new GpuMaskPass());
                var pass = _maskPasses[maskContext];
                foreach (var maskIndex in maskIndices)
                {
                    var block = GetMaskBlock(model, maskIndex, axisScale, baseX, baseY);
                    if (block.IndexCount > 0)
                    {
                        pass.Commands.Add(new GpuDrawCommand(
                            block.IndexOffset, block.IndexCount, model.Drawables[maskIndex].TextureIndex,
                            0, 1f, 0f, 0f, 0f, 0f, 0f, 0f, -1, false));
                    }
                }
            }

            _commands.Add(new GpuDrawCommand(
                indexOffset, drawable.IndexCount, textureIndex,
                drawable.IsAdditive ? 1 : drawable.IsMultiplicative ? 2 : 0,
                drawable.Opacity,
                drawable.MultiplyColor.X, drawable.MultiplyColor.Y, drawable.MultiplyColor.Z,
                drawable.ScreenColor.X, drawable.ScreenColor.Y, drawable.ScreenColor.Z,
                maskContext, drawable.IsInvertedMask));

            pixelsFilled += (long)boundsArea;
        }

        var cpuMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        // ---------- 提交 GPU ----------
        LastStage = "DrawFrame";
        _device.DrawFrame(_presenter.RenderTargetView!, width, height,
            _vertices, _vertexFloatCount, _indices, _indexCount,
            _commands, _maskPasses,
            options.TransparentBackground, options.BackgroundColor);

        LastStage = "Present";
        _presenter.Present(_device.Context);
        LastStage = null;

        stopwatch.Stop();
        LastCpuMilliseconds = cpuMilliseconds;
        LastStats = new RenderStats(
            DrawablesDrawn: _commands.Count,
            TrianglesDrawn: _indexCount / 3,
            PixelsFilled: pixelsFilled,
            RenderMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            MaskPasses: _maskPasses.Count);
        return true;
    }

    /// <summary>把当前 GPU 帧读回成 WriteableBitmap（截图 / 自检 / 调试截图用）。</summary>
    public WriteableBitmap CaptureBitmap() => _presenter.Capture(_device.Context);

    /// <summary>
    /// 变换并追加一个网格的顶点（屏幕像素 + UV），顺带返回包围盒面积（统计用）。
    /// 整块在输出范围外时返回 -1（剔除）。
    /// </summary>
    private int AppendVertices(float* positions, float* uvs, int vertexCount,
        float axisScale, float baseX, float baseY, int width, int height, out float boundsArea)
    {
        // 先扫包围盒做整块剔除（半开区间，同 CPU 渲染器）；剔除的网格不写顶点
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        for (int i = 0, read = 0; i < vertexCount; i++, read += 2)
        {
            var x = positions[read] * axisScale + baseX;
            var y = -positions[read + 1] * axisScale + baseY;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (!(maxX > 0f && minX < width && maxY > 0f && minY < height))
        {
            boundsArea = 0f;
            return -1;
        }

        boundsArea = (maxX - minX) * (maxY - minY);

        var startVertex = _vertexFloatCount / 4;
        var needed = _vertexFloatCount + vertexCount * 4;
        if (_vertices.Length < needed) Array.Resize(ref _vertices, Math.Max(needed, _vertices.Length * 2));

        var write = _vertexFloatCount;
        for (int i = 0, read = 0; i < vertexCount; i++, read += 2)
        {
            _vertices[write] = positions[read] * axisScale + baseX;
            _vertices[write + 1] = -positions[read + 1] * axisScale + baseY;
            _vertices[write + 2] = uvs[read];
            _vertices[write + 3] = uvs[read + 1];
            write += 4;
        }

        _vertexFloatCount = write;
        return startVertex;
    }

    private int AppendIndices(ushort* indices, int indexCount, int vertexBase)
    {
        var needed = _indexCount + indexCount;
        if (_indices.Length < needed) Array.Resize(ref _indices, Math.Max(needed, _indices.Length * 2));

        var offset = _indexCount;
        for (var i = 0; i < indexCount; i++)
        {
            _indices[offset + i] = (ushort)(indices[i] + vertexBase);
        }

        _indexCount = needed;
        return offset;
    }

    /// <summary>追加一个蒙版网格的顶点 / 索引块（同一蒙版网格被多个组合引用时复用一块）。</summary>
    private (int VertexBase, int IndexOffset, int IndexCount) GetMaskBlock(
        CubismModel model, int maskDrawableIndex, float axisScale, float baseX, float baseY)
    {
        if (_maskBlocks.TryGetValue(maskDrawableIndex, out var cached)) return cached;

        var drawable = model.Drawables[maskDrawableIndex];
        if (drawable.VertexCount < 3 || drawable.IndexCount < 3
            || drawable.Positions is null || drawable.Uvs is null || drawable.Indices is null)
        {
            var empty = (0, 0, 0);
            _maskBlocks[maskDrawableIndex] = empty;
            return empty;
        }

        var vertexBase = AppendMaskVertices(drawable.Positions, drawable.Uvs, drawable.VertexCount, axisScale, baseX, baseY);
        var indexOffset = AppendIndices(drawable.Indices, drawable.IndexCount, vertexBase);
        var block = (vertexBase, indexOffset, drawable.IndexCount);
        _maskBlocks[maskDrawableIndex] = block;
        return block;
    }

    /// <summary>蒙版网格不做屏幕外剔除（只关心覆盖范围；缓冲外 GPU 自动裁掉）。</summary>
    private int AppendMaskVertices(float* positions, float* uvs, int vertexCount, float axisScale, float baseX, float baseY)
    {
        var startVertex = _vertexFloatCount / 4;
        var needed = _vertexFloatCount + vertexCount * 4;
        if (_vertices.Length < needed) Array.Resize(ref _vertices, Math.Max(needed, _vertices.Length * 2));

        var write = _vertexFloatCount;
        for (int i = 0, read = 0; i < vertexCount; i++, read += 2)
        {
            _vertices[write] = positions[read] * axisScale + baseX;
            _vertices[write + 1] = -positions[read + 1] * axisScale + baseY;
            _vertices[write + 2] = uvs[read];
            _vertices[write + 3] = uvs[read + 1];
            write += 4;
        }

        _vertexFloatCount = write;
        return startVertex;
    }

    private int FindOrAddMaskContext(int[] maskIndices)
    {
        _scratch.Clear();
        for (var i = 0; i < maskIndices.Length; i++) _scratch.Add(maskIndices[i]);
        _scratch.Sort();

        for (var c = 0; c < _contextMasks.Count; c++)
        {
            var existing = _contextMasks[c];
            if (existing.Length != _scratch.Count) continue;
            var same = true;
            for (var k = 0; k < existing.Length; k++)
            {
                if (existing[k] != _scratch[k])
                {
                    same = false;
                    break;
                }
            }

            if (same) return c;
        }

        var stored = _scratch.ToArray();
        _contextMasks.Add(stored);
        return _contextMasks.Count - 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _presenter.Dispose();
        _device.Dispose();
    }
}
