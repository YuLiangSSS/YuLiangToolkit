using System.Buffers;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Rendering;

/// <summary>
/// Live2D 软件渲染器：把 <see cref="CubismModel"/> 当前帧的 715 个网格（三角形 + UV + 蒙版 + 混合模式）
/// 光栅化成一张 BGRA 位图，交给 WPF 的 <see cref="WriteableBitmap"/> 显示。
///
/// <para><b>为什么自己写渲染器</b>：本项目不引入第三方 NuGet（含 WebView2 / Skia / OpenGL 绑定），
/// 而 WPF 本身不能高效画「上千个带 UV 的三角形」；所以用 C# 自己写一个按目标位图分块并行的小光栅器。
/// 语义严格对照官方 Cubism Native Framework：
/// <list type="bullet">
/// <item>片元公式（<c>CubismEffect.fx</c>）：<c>tex.rgb *= MultiplyColor.rgb</c>；
/// <c>tex.rgb = tex.rgb + ScreenColor.rgb - tex.rgb*ScreenColor.rgb</c>；
/// <c>color = tex * (1,1,1,Opacity)</c>；<c>color.rgb *= color.a</c>（预乘）；有蒙版时整条 color 再乘蒙版覆盖度。</item>
/// <item>混合状态（<c>CubismRenderState_D3D11.cpp</c>）：Normal = (ONE, INV_SRC_ALPHA)（alpha 同样 src-over）；
/// Additive = (ONE, ONE) 且 alpha 不动；Multiplicative = (DEST_COLOR, INV_SRC_ALPHA) 且 alpha 不动。</item>
/// <item>蒙版（<c>PixelSetupMask</c> + Blend_Mask）：蒙版 pass 只取纹理 alpha（<b>不乘</b>蒙版网格自身不透明度），
/// 多个蒙版按「并集覆盖度」= <c>1 - Π(1-a_i)</c>；<c>IsInvertedMask</c> 时取 <c>1-覆盖度</c>。
/// 蒙版几何每帧随参数变化，所以覆盖缓冲每帧重新光栅化（只预热蒙版组合的索引，不缓存像素）。</item>
/// </list></para>
///
/// <para><b>线程</b>：<see cref="Render"/> 在 UI 线程调用（内部用 Parallel 分块并行；<see cref="Bitmap"/> 只在 UI 线程重建）。
/// 渲染器<b>无静态状态</b>，可以多个实例共享同一个 <see cref="TextureAtlasSet"/>（工具页和桌宠各一份、各自尺寸）。
/// 每次 <see cref="Render"/> 都从透明 / 背景色完整重画，不依赖上一帧内容。</para>
/// <para>实现范围：<c>Rendering/**</c>（渲染器 / 纹理 / 位图封装）。</para>
/// </summary>
public sealed unsafe partial class CubismRenderer : ILive2DRenderSurface, IDisposable
{
    /// <summary>不透明度低于 1/255 的网格视为不可见（和官方 <c>opacity &lt;= 0</c> 的判断接近，避免白跑光栅化）。</summary>
    private const float MinDrawableOpacity = 1f / 255f;

    private readonly TextureAtlasSet _textures;
    private readonly object _syncRoot = new();

    private WriteableBitmap? _bitmap;
    private uint[] _output = Array.Empty<uint>();
    private int _width;
    private int _height;
    private bool _disposed;

    // ---------- 每帧复用缓冲（避免每帧大分配） ----------

    /// <summary>可见网格（已按渲染顺序排列）。</summary>
    private VisibleDrawable[] _visible = new VisibleDrawable[128];

    private int _visibleCount;

    /// <summary>可见网格顶点在输出位图里的 x/y（2 个 float / 顶点，按 <see cref="VisibleDrawable.VertexBase"/> 定位）。</summary>
    private float[] _vertexXY = Array.Empty<float>();

    /// <summary>蒙版组合（每个组合 = 一组蒙版网格下标，排序后比较相同视为同一组合）。</summary>
    private int[][] _contextMasks = new int[32][];

    private int _contextCount;

    /// <summary>每个蒙版组合本帧的覆盖缓冲矩形（output 像素，半开区间）。</summary>
    private RectI[] _contextRects = new RectI[32];

    /// <summary>每个蒙版组合的覆盖度缓冲（byte / 像素，存 <c>Π(1-a_i)</c>；缓冲外恒为 255）。</summary>
    private byte[][] _maskBuffers = new byte[32][];

    /// <summary>上一帧往每个蒙版缓冲里写过像素的矩形（用于每帧只清理脏区）。</summary>
    private RectI[] _maskDirty = new RectI[32];

    private int[] _scratchMaskIndices = new int[32];

    /// <summary>本帧每个纹理图集选用的 mip 级别像素视图（缓存友好；按输出分辨率自动选层）。</summary>
    private AtlasView[] _atlasViews = Array.Empty<AtlasView>();

    /// <summary>一个纹理图集在本帧实际采样用的 mip 级别。</summary>
    private struct AtlasView
    {
        public uint[] Pixels;
        public int Width;
        public int Height;

        /// <summary>宽高都是 2 的幂时可以用位与做环绕采样（省掉 4 个分支）。</summary>
        public bool PowerOfTwo;
        public int WidthMask;
        public int HeightMask;
    }

    // 【性能诊断】帧内开关（从 RenderOptions 复制，避免每像素访问 options）
    private bool _debugSkipTextureSampling;
    private bool _debugSkipMask;

    /// <summary>用一套纹理图集创建渲染器（纹理只读，模型切换时复用）。</summary>
    /// <param name="textures">已加载的纹理图集（可被多个渲染器共享）。</param>
    public CubismRenderer(TextureAtlasSet textures)
    {
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        Textures = _textures;
    }

    /// <summary>当前使用的纹理图集（只读）。</summary>
    public TextureAtlasSet Textures { get; }

    /// <summary>当前输出位图（尺寸变化时重建；透明背景用 Pbgra32）。只在 UI 线程创建 / 重建。</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>最近一帧的渲染统计。</summary>
    public RenderStats LastStats { get; private set; } = RenderStats.Empty;

    // ---------- 性能分析用（调试面板 / 沙盒实测用；不参与渲染语义） ----------

    /// <summary>最近一帧「收集可见网格 + 变换顶点」耗时（毫秒）。</summary>
    public double LastBuildMilliseconds { get; private set; }

    /// <summary>最近一帧蒙版覆盖度光栅化耗时（毫秒）。</summary>
    public double LastMaskMilliseconds { get; private set; }

    /// <summary>最近一帧主光栅化（不含写回位图）耗时（毫秒）。</summary>
    public double LastTileMilliseconds { get; private set; }

    /// <summary>最近一帧写回 WriteableBitmap 耗时（毫秒）。</summary>
    public double LastWriteMilliseconds { get; private set; }

    /// <summary>最近一帧三角形初始化次数（所有 tile 累加，含被裁剪掉的）。</summary>
    public long LastTriangleSetups { get; private set; }

    /// <summary>最近一帧逐像素测试次数（所有 tile 累加，用于判断包围盒浪费）。</summary>
    public long LastPixelTests { get; private set; }

    /// <summary>
    /// 把模型当前帧画到位图上。<b>只读模型数据</b>（不调 <see cref="CubismModel.Update"/>、不改参数），
    /// 每次调用都从透明 / 背景色完整重画。必须在 UI 线程调用（<see cref="Bitmap"/> 的创建要求）。
    /// </summary>
    public void Render(CubismModel model, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new RenderOptions();

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RenderCore(model, options);
        }
    }

    private void RenderCore(CubismModel model, RenderOptions options)
    {
        var stopwatch = Stopwatch.StartNew();

        var width = Math.Clamp(options.Width, 1, 8192);
        var height = Math.Clamp(options.Height, 1, 8192);
        EnsureSurface(width, height);

        // ---------- 画布像素 → 输出像素 变换 ----------
        // 模型顶点是「模型单位」（画布宽 = 1.0、y 向上）；先换算成画布像素（y 向下），再缩放平移：
        //   canvasX = CanvasOriginX + x * PixelsPerUnit
        //   canvasY = CanvasOriginY - y * PixelsPerUnit
        //   outX = canvasX * scale + offsetX，outY = canvasY * scale + offsetY
        // 合并成：outX = x * axis + baseX；outY = -y * axis + baseY。
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

        // 选 mip 级别：输出分辨率越低，采样越小的 mip（大幅降低纹理内存访问）
        SelectAtlasViews(axisScale);

        // ---------- 收集可见网格 + 变换顶点 ----------
        var phaseWatch = Stopwatch.StartNew();
        var triangles = BuildVisibleDrawables(model, axisScale, baseX, baseY, width, height);
        BuildMaskContexts();
        LastBuildMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;

        // ---------- 蒙版组合 + 覆盖度缓冲（每帧按当前顶点重画） ----------
        phaseWatch.Restart();
        if (_contextCount > 0 && !options.DebugSkipMask)
        {
            RasterizeMaskContexts(model, axisScale, baseX, baseY, width, height, EffectiveDop(options.MaxDegreeOfParallelism));
        }

        LastMaskMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;

        // ---------- 背景色（预乘 float） ----------
        var transparent = options.TransparentBackground;
        var background = options.BackgroundColor;
        var bgA = (background >> 24) / 255f;
        var bgR = ((background >> 16) & 0xFF) / 255f * bgA;
        var bgG = ((background >> 8) & 0xFF) / 255f * bgA;
        var bgB = (background & 0xFF) / 255f * bgA;

        // ---------- 分块并行光栅化 ----------
        _debugSkipTextureSampling = options.DebugSkipTextureSampling;
        _debugSkipMask = options.DebugSkipMask;
        phaseWatch.Restart();
        var pixelsFilled = RenderTiles(options, width, height, transparent, bgR, bgG, bgB, bgA);
        LastTileMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;

        // ---------- 调试叠加（单线程，量很小） ----------
        DrawDebugOverlays(model, options, axisScale, baseX, baseY);

        // ---------- 写回 WriteableBitmap（UI 线程；一次拷贝） ----------
        phaseWatch.Restart();
        _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), _output, width * 4, 0);
        LastWriteMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;

        stopwatch.Stop();
        LastStats = new RenderStats(
            DrawablesDrawn: _visibleCount,
            TrianglesDrawn: triangles,
            PixelsFilled: pixelsFilled,
            RenderMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            MaskPasses: _contextCount);
    }

    /// <summary>按输出尺寸准备位图 / 输出缓冲（尺寸变化才重建）。</summary>
    private void EnsureSurface(int width, int height)
    {
        if (_bitmap is not null && _width == width && _height == height) return;

        _width = width;
        _height = height;
        _output = new uint[checked(width * height)];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);

        // 尺寸变了：蒙版缓冲全部作废（下次按新尺寸重建并填满 255）
        for (var i = 0; i < _maskBuffers.Length; i++)
        {
            _maskBuffers[i] = null!;
            _maskDirty[i] = default;
            _contextRects[i] = default;
        }
    }

    /// <summary>有效并行度（0 = 自动 = 逻辑核数）。</summary>
    private static int EffectiveDop(int requested)
    {
        if (requested > 0) return Math.Min(requested, 64);
        var processors = Environment.ProcessorCount;
        return processors <= 0 ? 1 : processors;
    }

    /// <summary>
    /// 给每个纹理图集选本帧要采样的 mip 级别：目标「1 个纹素 ≈ 1 个输出像素」。
    /// 纹理图集的分辨率远高于小窗口（420×860）所需，采样高级别 mip 能让工作集从 64 MB 降到几 MB。
    /// <para>axisScale = 1 个模型单位对应的输出像素数；画布宽 = 1 个模型单位，
    /// 所以「画布宽在输出里占多少像素」就是 desiredTexels。</para>
    /// </summary>
    private void SelectAtlasViews(float axisScale)
    {
        var count = _textures.Count;
        if (_atlasViews.Length < count) _atlasViews = new AtlasView[Math.Max(count, 8)];

        var desiredTexels = Math.Max(axisScale, 1f);
        for (var i = 0; i < count; i++)
        {
            var atlas = _textures[i];
            var mips = atlas.Mips;
            if (mips is null || mips.Length <= 1)
            {
                _atlasViews[i] = new AtlasView
                {
                    Pixels = atlas.Pixels,
                    Width = atlas.Width,
                    Height = atlas.Height,
                    PowerOfTwo = (atlas.Width & (atlas.Width - 1)) == 0 && (atlas.Height & (atlas.Height - 1)) == 0,
                    WidthMask = atlas.Width - 1,
                    HeightMask = atlas.Height - 1,
                };
                continue;
            }

            var ratio = atlas.Width / desiredTexels;
            var level = ratio > 1.05f ? (int)MathF.Floor(MathF.Log2(ratio)) : 0;
            if (level < 0) level = 0;
            else if (level >= mips.Length) level = mips.Length - 1;

            var mip = mips[level];
            _atlasViews[i] = new AtlasView
            {
                Pixels = mip.Pixels,
                Width = mip.Width,
                Height = mip.Height,
                PowerOfTwo = (mip.Width & (mip.Width - 1)) == 0 && (mip.Height & (mip.Height - 1)) == 0,
                WidthMask = mip.Width - 1,
                HeightMask = mip.Height - 1,
            };
        }
    }

    /// <summary>整型矩形（半开区间 [X0,X1) × [Y0,Y1)）。</summary>
    private readonly struct RectI
    {
        public RectI(int x0, int y0, int x1, int y1)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }

        public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _bitmap = null;
            _output = Array.Empty<uint>();
            _visible = new VisibleDrawable[0];
            _vertexXY = Array.Empty<float>();
            _contextMasks = new int[0][];
            _maskBuffers = new byte[0][];
            _contextCount = 0;
        }
    }
}
