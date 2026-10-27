namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 连续看图的三种滚动方式。
///
/// <para>**偏移量的约定（很重要）**：界面里所有滚动操作都使用「阅读偏移」而不是 ScrollViewer 的物理偏移 ——
/// 阅读偏移 0 = 第一张图的开头，越往后越大。这样三种方式共用同一套滚轮 / 拖拽 / 中键锚点逻辑：</para>
/// <list type="bullet">
///   <item><see cref="Vertical"/>：滚轮向下 = 阅读偏移变大（从上往下看）；</item>
///   <item><see cref="HorizontalLeftToRight"/>：点击滚轮向下 = 阅读偏移变大（画面往右推，从左往右看）；</item>
///   <item><see cref="HorizontalRightToLeft"/>：同上，只是**第一张图摆在最右边**，阅读方向从右往左（日式漫画）。</item>
/// </list>
///
/// <para>为什么「滚轮向下 = 阅读偏移变大」在左右两种模式里都要成立：横向滚动如果不改滚轮语义，
/// 用户得按 Shift 才能左右滚，体验很别扭 —— 见 <see cref="PictureScrollMath.WheelOffset"/>。</para>
/// </summary>
public enum PictureScrollMode
{
    /// <summary>纵向：从上到下（条漫 / 长图 / 普通图集，默认）。</summary>
    Vertical = 0,

    /// <summary>横向：从左到右（中式漫画阅读习惯；第一张在最左边）。</summary>
    HorizontalLeftToRight = 1,

    /// <summary>横向：从右到左（日式漫画阅读习惯；第一张在最右边，往左翻页）。</summary>
    HorizontalRightToLeft = 2,
}

/// <summary>滚动方式的纯逻辑小工具（界面 / 自检都用它，别在各处重复判断）。</summary>
public static class PictureScrollModes
{
    /// <summary>是不是横向滚动（决定用水平偏移、按高度统一缩放、指示器画左右箭头）。</summary>
    public static bool IsHorizontal(PictureScrollMode mode) => mode != PictureScrollMode.Vertical;

    /// <summary>
    /// 是不是「反向摆放」：图片按阅读顺序排在长带里，但**物理上从右往左**摆。
    /// 只有日式右→左用得到（第一张图片摆在最右边）。
    /// </summary>
    public static bool IsReverse(PictureScrollMode mode) => mode == PictureScrollMode.HorizontalRightToLeft;

    /// <summary>把任意枚举值（例如手改坏了的配置）归一化到合法值。</summary>
    public static PictureScrollMode Normalize(PictureScrollMode mode)
        => Enum.IsDefined(typeof(PictureScrollMode), mode) ? mode : PictureScrollMode.Vertical;

    /// <summary>给人看的名字（设置下拉、状态栏用）。</summary>
    public static string Describe(PictureScrollMode mode) => mode switch
    {
        PictureScrollMode.HorizontalLeftToRight => "横向 左 → 右",
        PictureScrollMode.HorizontalRightToLeft => "横向 右 → 左",
        _ => "纵向 上 → 下",
    };

    /// <summary>一句话解释这个方式适合看什么。</summary>
    public static string Hint(PictureScrollMode mode) => mode switch
    {
        PictureScrollMode.HorizontalLeftToRight => "中式漫画习惯：第一张在最左边，滚轮 / 拖拽往右看",
        PictureScrollMode.HorizontalRightToLeft => "日式漫画习惯：第一张在最右边，滚轮 / 拖拽往左看",
        _ => "条漫 / 长图习惯：图片上下相接，滚轮往下看",
    };
}
