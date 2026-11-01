using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Live2D.Rendering;

/// <summary>
/// 渲染一帧的参数：目标尺寸、画布变换、背景、调试叠加。
/// <para>坐标约定：模型画布是「画布像素」坐标系（左上角 0,0，y 向下，尺寸 = Cubism 画布宽高）。
/// 渲染器把画布像素按 <see cref="Scale"/> 缩放后平移到输出位图（<see cref="OffsetX"/> / <see cref="OffsetY"/>）。
/// 自动适配（<see cref="FitCanvas"/> = true）时由渲染器按 <see cref="Width"/>/<see cref="Height"/> 算 Scale/Offset，
/// 忽略调用方传的 Scale/Offset。</para>
/// </summary>
public sealed class RenderOptions
{
    /// <summary>输出位图宽度（像素）。</summary>
    public int Width { get; set; } = 512;

    /// <summary>输出位图高度（像素）。</summary>
    public int Height { get; set; } = 1024;

    /// <summary>画布像素 → 输出像素的缩放倍率（<see cref="FitCanvas"/> = false 时生效）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>画布左上角在输出位图里的 x 偏移（<see cref="FitCanvas"/> = false 时生效）。</summary>
    public double OffsetX { get; set; }

    /// <summary>画布左上角在输出位图里的 y 偏移（<see cref="FitCanvas"/> = false 时生效）。</summary>
    public double OffsetY { get; set; }

    /// <summary>是否自动把整张画布等比缩放进输出位图（桌宠 / 测试页默认开）。</summary>
    public bool FitCanvas { get; set; } = true;

    /// <summary>背景是否透明（桌宠必须 true；测试页可选棋盘格 / 纯色）。</summary>
    public bool TransparentBackground { get; set; } = true;

    /// <summary>不透明背景色（0xAARRGGBB；<see cref="TransparentBackground"/> = true 时忽略）。</summary>
    public uint BackgroundColor { get; set; } = 0xFF1B1E28;

    /// <summary>调试：画出所有网格的包围盒（线框）。</summary>
    public bool ShowDrawableBounds { get; set; }

    /// <summary>调试：把所有带蒙版的网格整体染红（看蒙版范围）。</summary>
    public bool ShowMaskRanges { get; set; }

    /// <summary>调试：高亮某个网格下标（-1 = 不高亮）。</summary>
    public int HighlightDrawableIndex { get; set; } = -1;

    /// <summary>调试：把某个网格的蒙版缓冲区直接可视化（-1 = 不显示）。</summary>
    public int ShowMaskBufferForDrawableIndex { get; set; } = -1;

    /// <summary>渲染并行度（0 = 自动 = 逻辑核数；调试 / 性能对照时可以设 1 做单线程基线）。</summary>
    public int MaxDegreeOfParallelism { get; set; }

    /// <summary>光栅化分块边长（像素）。0 = 自动（小窗口 64 / 大窗口 128）；一般不用改。</summary>
    public int TileSize { get; set; }

    /// <summary>【性能诊断】跳过纹理采样（用固定颜色），用于定位采样开销（默认 false）。</summary>
    public bool DebugSkipTextureSampling { get; set; }

    /// <summary>【性能诊断】跳过蒙版覆盖度查询，用于定位蒙版开销（默认 false）。</summary>
    public bool DebugSkipMask { get; set; }

    public RenderOptions Clone() => (RenderOptions)MemberwiseClone();
}

/// <summary>一帧的渲染统计（性能面板用）。</summary>
public readonly record struct RenderStats(
    int DrawablesDrawn,
    int TrianglesDrawn,
    long PixelsFilled,
    double RenderMilliseconds,
    int MaskPasses)
{
    public static RenderStats Empty => new(0, 0, 0, 0, 0);
}

/// <summary>
/// 渲染器对外的最小契约：把模型当前帧画到自己的 <see cref="Bitmap"/> 上。
/// 由渲染实现填充（见 <see cref="CubismRenderer"/>）。
/// </summary>
public interface ILive2DRenderSurface
{
    /// <summary>当前位图（尺寸随 <see cref="RenderOptions"/> 变化时重建；UI 绑定这个）。</summary>
    WriteableBitmap? Bitmap { get; }

    /// <summary>最近一帧的统计。</summary>
    RenderStats LastStats { get; }
}

/// <summary>背景色 / 调试色工具。</summary>
internal static class RenderColor
{
    public static Color ToColor(uint argb)
        => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
