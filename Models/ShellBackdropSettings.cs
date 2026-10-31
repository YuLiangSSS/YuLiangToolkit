using YuLiangToolkit.App.Themes;
using YuLiangToolkit.Core.Effects;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 窗口背景图「模糊度」的统一参数（v0.43.1，主页设置 → 界面风格 → 选中某个半透明风格后出现的滑块）。
///
/// <para><b>为什么单独放一份</b>：设置页滑块、配置默认值、主窗口渲染层都要用同一组数字，
/// 别在 XAML / VM / 渲染层各写一套（做法同 <c>CursorEffectTuning</c>）。</para>
///
/// <para><b>按风格分别记</b>：液态玻璃 / 毛玻璃 / 亚克力各有一个模糊度，存在
/// <see cref="ShellConfig.LiquidGlassBlurRadius"/> / <c>FrostedGlassBlurRadius</c> / <c>AcrylicBlurRadius</c>；
/// 默认值 = v0.43.0 写死的那套外观（液态 0 = 清晰、毛玻璃 28、亚克力 20），老配置缺字段时外观不变。
/// 模糊只加在**静态背景图**上（不影响面板 / 文字），套在面板上的玻璃质感不依赖它。</para>
/// </summary>
public static class ShellBackdropSettings
{
    /// <summary>背景图模糊度的取值范围（0 = 清晰 … 60 = 很朦胧；1 px 一档，用 <see cref="CursorEffectRange"/> 复用滑块逻辑）。</summary>
    public static CursorEffectRange BlurRadiusRange { get; } = new(0.0, 60.0, 1.0);

    /// <summary>滑块数值的单位（拼在「28 px」这种显示文本后面）。</summary>
    public const string BlurUnit = " px";

    /// <summary>默认模糊度（与 v0.43.0 写死的外观一致）：液态玻璃保持清晰、毛玻璃重模糊、亚克力轻模糊。</summary>
    public static double DefaultBlurRadius(UiTheme theme) => theme switch
    {
        UiTheme.FrostedGlass => 28.0,
        UiTheme.Acrylic => 20.0,
        _ => 0.0,
    };

    /// <summary>把模糊度夹进合法范围（手改配置写了负数 / 超大值 / NaN 也不会让渲染层崩）。</summary>
    public static double ClampBlurRadius(double value) => BlurRadiusRange.Clamp(value);

    /// <summary>滑块下面那句说明（每套风格一句，讲清楚默认值和“只糊背景图”）。</summary>
    public static string DescribeBlur(UiTheme theme) => theme switch
    {
        UiTheme.FrostedGlass =>
            "背景图的高斯模糊半径：0 = 清晰，越大越朦胧；毛玻璃默认 28。只糊背景图，面板和文字永远清晰。",
        UiTheme.Acrylic =>
            "背景图的高斯模糊半径：0 = 清晰，越大越朦胧；亚克力默认 20。只糊背景图，面板和文字永远清晰。",
        _ =>
            "背景图的高斯模糊半径：0 = 清晰，越大越朦胧；液态玻璃默认 0（保持原图清晰）。只糊背景图，面板和文字永远清晰。",
    };
}
