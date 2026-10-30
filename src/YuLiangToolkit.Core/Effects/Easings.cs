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
    /// easeOutQuint：<c>t=0 → 0</c>、<c>t=1 → 1</c>，前段变化极快、越接近终点越慢
    /// （<c>1 - (1-t)^5</c>）。
    ///
    /// <para>点击反色圆环用它做「扩散」和「收缩」两个阶段：扩散时半径先暴涨后变慢，
    /// 收缩时反过来读同一条曲线（剩余半径 = <c>OutQuint(1-t)</c>），就得到「先缩得慢、最后缩得快」。
    /// 比 easeOutExpo 温和一点（Expo 在开头几乎是一瞬间到位），观感更像一圈涟漪而不是闪一下。</para>
    /// </summary>
    public static double OutQuint(double t)
    {
        if (double.IsNaN(t)) return 0;
        t = Math.Clamp(t, 0.0, 1.0);
        var inverse = 1.0 - t;
        return 1.0 - inverse * inverse * inverse * inverse * inverse;
    }
}
