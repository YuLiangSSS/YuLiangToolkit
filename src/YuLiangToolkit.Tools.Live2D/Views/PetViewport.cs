namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 桌宠窗口尺寸换算（纯数学，自检可测）。
///
/// <para><b>设计：窗口 = 模型 + 左右摆动余量，没有取景框裁切</b>。窗口比「模型显示宽度」宽
/// <see cref="SwayMarginScale"/>（左右各 7.5%）：尾巴 / 头发被物理甩出画布、整身轻倾旋转时，
/// 画面有地方去，不会被窗口边裁掉（用户反馈：晃动到最右边时鱼尾被截掉一块）。画布在窗口里
/// 仍等比居中，模型大小只由 <c>PetWidth</c>（模型显示宽度）决定；多出来的两侧是透明区域，
/// 点击穿透命中测试按模型网格算，行为不变。</para>
///
/// <para>Windows 默认按 WM_GETMINMAXINFO 的系统默认值（约「虚屏宽+20 × 屏高+20」）夹所有顶层窗口的尺寸，
/// 桌宠窗口会被夹到约 1100 高。<see cref="YuLiangToolkit.Tools.Live2D.Views.PetWindow"/> 用
/// <c>SetWindowLongPtr</c> 子类化窗口过程，在链路最前面拦截 <c>WM_GETMINMAXINFO</c> 把上限放宽
/// （实测纯 Win32 与 WPF 窗口都能突破，1000 宽窗口实际建出 2053 高），所以窗口可以比屏幕高 ——
/// 超出屏幕的部分拖动窗口查看。</para>
///
/// <para><b>历史坑</b>：旧版窗口被系统悄悄夹到 1100，模型最大只有约 500 宽；
/// 「HwndSource.AddHook」方案在 WPF 里收不到这条消息（WPF 内部先消费），必须子类化。</para>
/// </summary>
public static class PetViewport
{
    /// <summary>横向摆动余量系数：窗口宽 = 模型显示宽 × 1.15（左右各 7.5% 的透明边）。
    /// 覆盖日常轻晃（0.6°）与音乐跟唱的整身轻倾、以及尾巴 / 头发物理甩出画布的常见幅度；
    /// 「摇一摇」演示（±12°）这类大幅摆动模型本身就甩出窗口很远，不追求完全包住。</summary>
    public const double SwayMarginScale = 1.15;

    /// <summary>「模型显示宽度」范围（DIP）。
    /// 上限 3840（4K 宽度档）：GPU 渲染按 D3D9 纹理上限 8192px 自动钳渲染倍率，
    /// 宽 3840 时高 7883 &lt; 8000 上限，位图最大 3840×7883 在 GPU 纹理范围内。</summary>
    public const double MinWindowWidth = 160;
    public const double MaxWindowWidth = 3840;

    /// <summary>窗口高度上限（DIP）：软件渲染器的位图上限是 8192 像素，留余量。</summary>
    public const double MaxWindowHeight = 8000;

    /// <summary>窗口最矮高度（兜底，避免算出 1 像素高的窗口）。</summary>
    public const double MinWindowHeight = 120;

    /// <summary>默认模型显示宽度（「缩放重置」回到这个值）。</summary>
    public const double DefaultWindowWidth = 420;

    /// <summary>
    /// 由「想要的模型显示宽度」算窗口尺寸：模型宽夹到 160~3840，模型高 = 宽 / 画布宽高比；
    /// 窗口宽 = 模型宽 × <see cref="SwayMarginScale"/>（左右摆动余量，被 3840 上限夹住），
    /// 窗口高 = 模型高（不按屏幕夹！子类化已解除系统上限，窗口允许比屏幕高，
    /// 超出部分拖动窗口查看），整体不超过渲染位图上限。
    /// <para>窗口里的画布仍等比居中（fit 取宽高比里较小的那个），所以模型在窗口里的
    /// 显示大小只由「模型显示宽度」决定，两侧余量是透明区域。</para>
    /// </summary>
    public static (double Width, double Height) ComputeWindowSize(double requestedWidth, double canvasAspect)
    {
        // requestedWidth 语义 = 模型显示宽度（滑杆 / 配置 petWidth），不是窗口宽
        var modelWidth = double.IsFinite(requestedWidth)
            ? Math.Clamp(requestedWidth, MinWindowWidth, MaxWindowWidth)
            : DefaultWindowWidth;

        var aspect = double.IsFinite(canvasAspect) && canvasAspect > 0.05 ? canvasAspect : 3749.0 / 7697.0;
        var modelHeight = modelWidth / aspect;

        // 渲染器位图上限 8192 像素：极端细长的模型按高度上限反夹模型宽度
        if (modelHeight > MaxWindowHeight)
        {
            modelHeight = MaxWindowHeight;
            modelWidth = Math.Min(modelWidth, modelHeight * aspect);
        }

        // 窗口比模型宽一圈摆动余量（被窗口宽度上限夹住：超大尺寸时余量变小但不越界）
        var windowWidth = Math.Min(modelWidth * SwayMarginScale, MaxWindowWidth);

        return (windowWidth, Math.Max(MinWindowHeight, modelHeight));
    }

    /// <summary>
    /// 滚轮锚定缩放：窗口尺寸变化前后，「光标下的模型点」保持在屏幕同一位置
    /// （鼠标指着脸放大，脸不动；窗口尺寸和位置一起变）。返回新的窗口 Left / Top。
    /// </summary>
    public static (double Left, double Top) AnchoredZoom(
        double oldLeft, double oldTop, double oldWidth, double oldHeight,
        double canvasWidth, double canvasHeight,
        double cursorLocalX, double cursorLocalY,
        double newWidth, double newHeight)
    {
        var cw = Math.Max(1.0, canvasWidth);
        var ch = Math.Max(1.0, canvasHeight);

        // 缩放前：光标下的画布像素坐标（FitCanvas：整张画布等比居中进窗口）
        var oldW = Math.Max(1.0, oldWidth);
        var oldH = Math.Max(1.0, oldHeight);
        var oldFit = Math.Min(oldW / cw, oldH / ch);
        if (oldFit <= 0.0001 || !double.IsFinite(oldFit)) return (oldLeft, oldTop);
        var oldOffsetX = (oldW - cw * oldFit) * 0.5;
        var oldOffsetY = (oldH - ch * oldFit) * 0.5;
        var canvasX = (cursorLocalX - oldOffsetX) / oldFit;
        var canvasY = (cursorLocalY - oldOffsetY) / oldFit;

        // 缩放后：让同一个画布点仍然落在屏幕上的同一个位置（窗口 Left/Top 反推）
        var newW = Math.Max(1.0, newWidth);
        var newH = Math.Max(1.0, newHeight);
        var newFit = Math.Min(newW / cw, newH / ch);
        if (newFit <= 0.0001 || !double.IsFinite(newFit)) return (oldLeft, oldTop);
        var newOffsetX = (newW - cw * newFit) * 0.5;
        var newOffsetY = (newH - ch * newFit) * 0.5;

        var screenX = oldLeft + cursorLocalX;
        var screenY = oldTop + cursorLocalY;
        return (screenX - newOffsetX - canvasX * newFit, screenY - newOffsetY - canvasY * newFit);
    }
}
