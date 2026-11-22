namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 桌宠窗口的「取景框」换算（纯数学，自检可测）。
///
/// <para><b>为什么要有这一层</b>：WPF 顶层透明窗口的程序尺寸会被系统按「最大跟踪尺寸」悄悄夹住
/// （1080p 屏上大约 1100 高）。旧版把窗口高度直接按 <c>宽度 / 画布宽高比</c> 设成 2053，
/// 但系统实际只给 1100：模型被 FitCanvas 缩在 1100 高的窗口里，怎么滚都不再变大，
/// 而窗口 Top 又按 2053 的“名义高度”算，于是看起来只会往上跑。现在窗口尺寸先夹到工作区，
/// 模型大小改由 <see cref="Calculate"/> 的缩放倍率单独控制 —— 窗口是取景框，模型可以比它大。</para>
///
/// <para>坐标约定与渲染器一致：画布像素左上角 (0,0)、y 向下；窗口 DIP 的 (0,0) 在左上角。
/// <see cref="View.Scale"/> 是「画布像素 → 窗口 DIP」的缩放，<see cref="View.OffsetX"/> /
/// <see cref="View.OffsetY"/> 是画布左上角在窗口里的位置。</para>
/// </summary>
public static class PetViewport
{
    /// <summary>桌宠缩放范围（相对「整张画布适应窗口」的倍数）。</summary>
    public const double MinZoom = 0.5;
    public const double MaxZoom = 8.0;

    /// <summary>桌宠窗口宽度范围（DIP；实际还会再按屏幕工作区夹取）。</summary>
    public const double MinWindowWidth = 160;
    public const double MaxWindowWidth = 1000;

    /// <summary>窗口最矮高度（工作区异常小的时候兜底，避免算出 1 像素高的窗口）。</summary>
    public const double MinWindowHeight = 120;

    /// <summary>一次视口换算的结果。</summary>
    /// <param name="Scale">画布像素 → 窗口 DIP 的缩放。</param>
    /// <param name="OffsetX">画布左上角在窗口中的 x。</param>
    /// <param name="OffsetY">画布左上角在窗口中的 y。</param>
    /// <param name="FitScale">「整张画布适应窗口」时的基准缩放（未乘 Zoom）。</param>
    public readonly record struct View(double Scale, double OffsetX, double OffsetY, double FitScale);

    /// <summary>把缩放夹到允许范围；NaN / 无穷退回 1.0。</summary>
    public static double ClampZoom(double zoom)
        => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1.0;

    /// <summary>
    /// 由「想要的窗口宽度」算实际窗口尺寸：宽度夹到 160~1000 且不超过工作区宽；
    /// 高度按模型画布比例算，但不超过工作区高。返回的是窗口 DIP 尺寸（不保证等于模型尺寸，
    /// 模型在窗口里按 <see cref="Calculate"/> 的缩放显示 / 裁切）。
    /// </summary>
    public static (double Width, double Height) ComputeWindowSize(
        double requestedWidth, double canvasAspect, double workAreaWidth, double workAreaHeight)
    {
        var workWidth = double.IsFinite(workAreaWidth) && workAreaWidth > 0 ? workAreaWidth : MaxWindowWidth;
        var workHeight = double.IsFinite(workAreaHeight) && workAreaHeight > 0 ? workAreaHeight : 1080.0;
        var maxWidth = Math.Max(MinWindowWidth, Math.Min(MaxWindowWidth, workWidth));
        var width = double.IsFinite(requestedWidth)
            ? Math.Clamp(requestedWidth, MinWindowWidth, maxWidth)
            : Math.Min(420, maxWidth);

        var aspect = double.IsFinite(canvasAspect) && canvasAspect > 0.05 ? canvasAspect : 3749.0 / 7697.0;
        var height = width / aspect;
        var maxHeight = Math.Max(MinWindowHeight, workHeight);
        return (width, Math.Min(height, maxHeight));
    }

    /// <summary>
    /// 计算当前取景：整张画布先按窗口等比适配，再乘 <paramref name="zoom"/>，
    /// 并以窗口中心为基准加上归一化平移 <paramref name="panX"/> / <paramref name="panY"/>
    /// （1.0 = 平移一个窗口宽 / 高）。返回值可直接喂给渲染器（FitCanvas=false）。
    /// </summary>
    public static View Calculate(
        double windowWidth, double windowHeight,
        double canvasWidth, double canvasHeight,
        double zoom, double panX, double panY)
    {
        var w = Math.Max(1.0, windowWidth);
        var h = Math.Max(1.0, windowHeight);
        var cw = Math.Max(1.0, canvasWidth);
        var ch = Math.Max(1.0, canvasHeight);

        var fit = Math.Min(w / cw, h / ch);
        var zoomValue = ClampZoom(zoom);
        var scale = fit * zoomValue;
        var (clampedPanX, clampedPanY) = ClampPan(w, h, cw, ch, zoomValue, panX, panY);

        return new View(
            scale,
            (w - cw * scale) * 0.5 + clampedPanX * w,
            (h - ch * scale) * 0.5 + clampedPanY * h,
            fit);
    }

    /// <summary>
    /// 以窗口内某一点（DIP）为不动点缩放。返回新的缩放倍率与归一化平移量；
    /// 用于滚轮缩放 —— 鼠标指向脸就把脸放大在鼠标下面，而不是整只角色往上跑。
    /// </summary>
    public static (double Zoom, double PanX, double PanY) ZoomAt(
        double windowWidth, double windowHeight,
        double canvasWidth, double canvasHeight,
        double oldZoom, double oldPanX, double oldPanY,
        double factor, double cursorX, double cursorY)
    {
        var w = Math.Max(1.0, windowWidth);
        var h = Math.Max(1.0, windowHeight);
        var cw = Math.Max(1.0, canvasWidth);
        var ch = Math.Max(1.0, canvasHeight);

        var before = Calculate(w, h, cw, ch, oldZoom, oldPanX, oldPanY);
        var currentZoom = ClampZoom(oldZoom);
        var newZoom = ClampZoom(currentZoom * factor);
        if (Math.Abs(newZoom - currentZoom) < 0.0001)
        {
            var currentPan = ClampPan(w, h, cw, ch, currentZoom, oldPanX, oldPanY);
            return (currentZoom, currentPan.PanX, currentPan.PanY);
        }

        // 缩放前光标下的画布点
        var canvasX = (cursorX - before.OffsetX) / before.Scale;
        var canvasY = (cursorY - before.OffsetY) / before.Scale;

        // 新缩放下的「居中基准」偏移，再反推平移量，让同一个画布点仍然落在光标下
        var newScale = before.FitScale * newZoom;
        var baseX = (w - cw * newScale) * 0.5;
        var baseY = (h - ch * newScale) * 0.5;
        var panX = (cursorX - canvasX * newScale - baseX) / w;
        var panY = (cursorY - canvasY * newScale - baseY) / h;
        var clamped = ClampPan(w, h, cw, ch, newZoom, panX, panY);
        return (newZoom, clamped.PanX, clamped.PanY);
    }

    /// <summary>
    /// 把归一化平移夹在「还能看到模型」的范围：最多平移到模型边缘贴住窗口边缘。
    /// 缩放 ≤ 1 时模型本来就装得下，不允许平移。
    /// </summary>
    public static (double PanX, double PanY) ClampPan(
        double windowWidth, double windowHeight,
        double canvasWidth, double canvasHeight,
        double zoom, double panX, double panY)
    {
        var w = Math.Max(1.0, windowWidth);
        var h = Math.Max(1.0, windowHeight);
        var cw = Math.Max(1.0, canvasWidth);
        var ch = Math.Max(1.0, canvasHeight);

        var scale = Math.Min(w / cw, h / ch) * ClampZoom(zoom);
        var modelWidth = cw * scale;
        var modelHeight = ch * scale;

        // 模型比窗口大多少，就允许往两边各拖多少；最多拖到模型边缘正好贴住窗口边缘。
        var maxPanX = Math.Max(0.0, (modelWidth - w) * 0.5 / w);
        var maxPanY = Math.Max(0.0, (modelHeight - h) * 0.5 / h);

        var safePanX = double.IsFinite(panX) ? panX : 0.0;
        var safePanY = double.IsFinite(panY) ? panY : 0.0;
        return (Math.Clamp(safePanX, -maxPanX, maxPanX), Math.Clamp(safePanY, -maxPanY, maxPanY));
    }
}
