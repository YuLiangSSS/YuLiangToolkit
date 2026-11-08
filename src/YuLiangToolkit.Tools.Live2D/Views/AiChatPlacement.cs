namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// AI 聊天窗贴桌宠的定位算法（<b>纯静态函数</b>，不碰窗口 / 不依赖 WPF 运行时，方便自检直接断言）。
///
/// <para><b>规则（按优先级）</b>：</para>
/// <list type="number">
///   <item><b>桌宠正下方</b>：水平居中，和桌宠之间留 <see cref="Gap"/> 像素；放得下就优先用它（桌宠不挡聊天窗、聊天窗也不挡桌宠脸）。</item>
///   <item><b>桌宠左侧</b>：下方放不下（桌宠靠近屏幕底边 / 任务栏）就放到左边，右边贴着桌宠左边 -<see cref="Gap"/>，
///         底边和桌宠底边对齐，向上不超出工作区。</item>
///   <item><b>桌宠右侧</b>：左边也放不下（桌宠贴着屏幕左边）就放右边，其余同上。</item>
///   <item><b>兜底</b>：实在都没地方（窗口比工作区还大 / 桌宠位置离谱），就把「下方」那个位置夹进工作区，
///         四周各留 <see cref="EdgeMargin"/> 像素。</item>
/// </list>
///
/// <para><b>单位</b>：调用方保证左右上下都是同一种单位（<see cref="System.Windows.Window"/>.Left / Top /
/// ActualWidth / ActualHeight 和 <see cref="System.Windows.SystemParameters.WorkArea"/> 都是 DIP，一致）。</para>
/// </summary>
public static class AiChatPlacement
{
    /// <summary>聊天窗和桌宠之间的间隙（DIP）。</summary>
    public const double Gap = 8;

    /// <summary>聊天窗离屏幕工作区边缘的最小距离（DIP）。</summary>
    public const double EdgeMargin = 4;

    /// <summary>聊天窗默认宽（参数异常时兜底，和 AiChatWindow 的 Width 保持一致）。</summary>
    private const double DefaultChatWidth = 372;

    /// <summary>聊天窗默认高（参数异常时兜底，和 AiChatWindow 的 Height 保持一致）。</summary>
    private const double DefaultChatHeight = 524;

    /// <summary>
    /// 计算聊天窗左上角坐标。
    /// </summary>
    /// <param name="petLeft">桌宠窗口左边（DIP）。</param>
    /// <param name="petTop">桌宠窗口上边（DIP）。</param>
    /// <param name="petWidth">桌宠窗口宽（DIP）。</param>
    /// <param name="petHeight">桌宠窗口高（DIP）。</param>
    /// <param name="windowWidth">聊天窗宽（DIP）。</param>
    /// <param name="windowHeight">聊天窗高（DIP）。</param>
    /// <param name="workLeft">屏幕工作区左边（DIP；多屏时是桌宠所在屏的工作区）。</param>
    /// <param name="workTop">屏幕工作区上边（DIP）。</param>
    /// <param name="workWidth">屏幕工作区宽（DIP）。</param>
    /// <param name="workHeight">屏幕工作区高（DIP）。</param>
    /// <returns>聊天窗左上角（Left, Top），已保证落在工作区内（除非工作区比窗口还小，那会贴左上）。</returns>
    public static (double Left, double Top) Calculate(
        double petLeft,
        double petTop,
        double petWidth,
        double petHeight,
        double windowWidth,
        double windowHeight,
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight)
    {
        // ---------- 脏参数兜底：尺寸 / 坐标是 NaN、无穷、0 时换成默认值，别算出 NaN 把窗口扔到屏幕外 ----------
        if (!IsUsableSize(windowWidth)) windowWidth = DefaultChatWidth;
        if (!IsUsableSize(windowHeight)) windowHeight = DefaultChatHeight;
        if (!IsUsableSize(workWidth)) workWidth = 1920;
        if (!IsUsableSize(workHeight)) workHeight = 1080;
        if (!IsFinite(workLeft)) workLeft = 0;
        if (!IsFinite(workTop)) workTop = 0;
        if (!IsFinite(petLeft)) petLeft = 0;
        if (!IsFinite(petTop)) petTop = 0;
        if (!IsFinite(petWidth) || petWidth < 0) petWidth = 0;
        if (!IsFinite(petHeight) || petHeight < 0) petHeight = 0;

        // 允许摆放的范围（四周各留 4px，别贴着屏幕边）
        var minLeft = workLeft + EdgeMargin;
        var maxLeft = workLeft + workWidth - windowWidth - EdgeMargin;
        var minTop = workTop + EdgeMargin;
        var maxTop = workTop + workHeight - windowHeight - EdgeMargin;

        // ---------- ① 优先：桌宠正下方（水平居中 + 间隙 Gap） ----------
        var belowLeft = petLeft + ((petWidth - windowWidth) / 2.0);
        var belowTop = petTop + petHeight + Gap;
        if (belowTop <= maxTop)
        {
            // 下方竖直方向放得下：左右夹进工作区就完事
            return (ClampInto(belowLeft, minLeft, maxLeft), ClampInto(belowTop, minTop, maxTop));
        }

        // ---------- ② 下方放不下：放桌宠左侧（右边贴桌宠左边 -Gap，底边与桌宠底边对齐） ----------
        var sideTop = petTop + petHeight - windowHeight;
        var leftLeft = petLeft - windowWidth - Gap;
        if (leftLeft >= minLeft)
        {
            return (leftLeft, ClampInto(sideTop, minTop, maxTop));
        }

        // ---------- ③ 左边也放不下：放桌宠右侧（其余规则同上） ----------
        var rightLeft = petLeft + petWidth + Gap;
        if (rightLeft <= maxLeft)
        {
            return (rightLeft, ClampInto(sideTop, minTop, maxTop));
        }

        // ---------- ④ 全兜底：夹进工作区（各留 4px） ----------
        return (ClampInto(belowLeft, minLeft, maxLeft), ClampInto(belowTop, minTop, maxTop));
    }

    /// <summary>把坐标夹进 [min, max]；工作区比窗口还小时贴 min（左上角），不会返回 NaN。</summary>
    private static double ClampInto(double value, double min, double max)
    {
        if (!IsFinite(value)) return min;
        return max < min ? min : Math.Clamp(value, min, max);
    }

    /// <summary>是否有限数（排除 NaN / 正负无穷）。</summary>
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>是否是可用的尺寸（有限且大于 1px；太小的值当没传）。</summary>
    private static bool IsUsableSize(double value) => IsFinite(value) && value > 1;
}
