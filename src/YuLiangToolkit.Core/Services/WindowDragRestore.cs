using System.Windows;

namespace YuLiangToolkit.Core.Services;

/// <summary>
/// 最大化窗口「按住标题栏拖出来」的还原位置计算（纯逻辑，自检直接断言，见 AGENTS.md）。
///
/// <para>手感和普通 Windows 窗口一致：拖动开始那一刻窗口先按**最大化之前的尺寸**还原，
/// 光标抓住标题栏上哪个点、还原后还落在哪个点 —— 水平方向按「抓点在标题栏宽度里的比例」换算，
/// 竖直方向保持抓点在标题栏里的偏移不变；最后把窗口夹回工作区，保证一出现就能看见 / 能拖。</para>
/// </summary>
public static class WindowDragRestore
{
    /// <summary>还原后的窗口位置与尺寸（DIP）。</summary>
    public readonly record struct Placement(double Left, double Top, double Width, double Height);

    /// <summary>算「最大化窗口被拖动」时窗口该还原到哪、多大（DIP）。</summary>
    /// <param name="cursorX">光标当前屏幕坐标 X（DIP）。</param>
    /// <param name="cursorY">光标当前屏幕坐标 Y（DIP）。</param>
    /// <param name="grabOffsetX">按下时光标在窗口内的 X（DIP，相对窗口左边）。</param>
    /// <param name="grabOffsetY">按下时光标在窗口内的 Y（DIP，相对窗口上边）。</param>
    /// <param name="currentWidth">当前（最大化时）窗口宽度（DIP），用来算水平抓取比例。</param>
    /// <param name="restoreWidth">最大化前的窗口宽度（DIP）；拿不到就传 0，会退化成工作区宽度。</param>
    /// <param name="restoreHeight">最大化前的窗口高度（DIP）；拿不到就传 0，会退化成工作区高度。</param>
    /// <param name="workArea">工作区（DIP，已扣掉任务栏）。</param>
    /// <param name="minWidth">窗口最小宽度（DIP，来自 <c>Window.MinWidth</c>）。</param>
    /// <param name="minHeight">窗口最小高度（DIP，来自 <c>Window.MinHeight</c>）。</param>
    public static Placement Compute(
        double cursorX,
        double cursorY,
        double grabOffsetX,
        double grabOffsetY,
        double currentWidth,
        double restoreWidth,
        double restoreHeight,
        Rect workArea,
        double minWidth = 0,
        double minHeight = 0)
    {
        var areaWidth = Math.Max(1, workArea.Width);
        var areaHeight = Math.Max(1, workArea.Height);

        var width = ClampSize(restoreWidth, minWidth, areaWidth);
        var height = ClampSize(restoreHeight, minHeight, areaHeight);

        // 水平按比例：抓在标题栏正中间 → 还原后还在正中间；抓在靠右 → 还原后也靠右
        var ratio = currentWidth > 0 ? grabOffsetX / currentWidth : 0.5;
        ratio = Math.Clamp(ratio, 0, 1);

        var left = cursorX - width * ratio;
        var top = cursorY - grabOffsetY;

        // 夹回工作区（宽度 / 高度都已经不超过工作区，所以右下边界一定成立）
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

        return new Placement(left, top, width, height);
    }

    /// <summary>还原尺寸：别小于窗口最小尺寸、也别大过工作区（否则一还原就有大半在屏幕外）。</summary>
    private static double ClampSize(double size, double min, double max)
    {
        if (double.IsNaN(size) || size <= 0) return max;   // 拿不到还原尺寸 → 用工作区兜底

        if (double.IsNaN(min) || min <= 0) min = 1;
        return Math.Clamp(size, Math.Min(min, max), max);
    }
}
