namespace YuLiangToolkit.Core.Effects;

/// <summary>
/// 缓动函数（手写实现，数学定义对齐 <see href="https://github.com/ai/easings.net">easings.net</see>）。
///
/// <para>只放「点击特效」真正用到的曲线，避免引入第三方库；新增曲线时先在这里加静态方法 +
/// 自检断言（<c>ShellEffectsChecks</c>），再让动画去用。</para>
///
/// <para>约定：输入 <c>t</c> 会被夹到 <c>[0,1]</c>，<c>NaN</c> 按 0 处理 —— 动画里时间来自帧步长，
/// 偶发的异常值不应该把半径算成 NaN 画崩界面。</para>
/// </summary>
public static class Easings
{
    /// <summary>
    /// easeOutCubic：<c>t=0 → 0</c>、<c>t=1 → 1</c>，前段快、后段慢（<c>1 - (1-t)^3</c>）。
    ///
    /// <para>点击反色圆环用它做「扩散」和「收缩」两个阶段：扩散时半径先快后慢，
    /// 收缩时反过来读同一条曲线（剩余半径 = <c>OutCubic(1-t)</c>），就得到「先缩得慢、最后缩得快」。</para>
    ///
    /// <para>为什么不是更容易「弹」的 <c>easeOutQuint</c> / <c>easeOutExpo</c>：v0.36.0 实测五次方曲线
    /// 开头半径暴涨、后面几乎爬行，视觉上「起步太冲、减速太突然」。三次方曲线起点速度只有五次方的
    /// 五分之三、结尾速度按二次曲线收敛到 0，还是明确的「先快后慢」，但过渡平顺得多。</para>
    /// </summary>
    public static double OutCubic(double t)
    {
        if (double.IsNaN(t)) return 0;
        t = Math.Clamp(t, 0.0, 1.0);
        var inverse = 1.0 - t;
        return 1.0 - inverse * inverse * inverse;
    }
}
