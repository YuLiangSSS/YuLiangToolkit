namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「阅读方式」——现在共 6 种，分两大类（以后还会加，所以界面上不再把每种都摆到工具条里，
/// 工具条只留一个「当前方式」按钮，切换统一放到设置面板，见 docs/dev/pitfalls.md）：
///
/// <para><b>连续滚动</b>（图片首尾相接成一条长带，滚轮连续滚）：</para>
/// <list type="bullet">
///   <item><see cref="Vertical"/>：纵向 上→下（条漫 / 长图，默认）；</item>
///   <item><see cref="HorizontalLeftToRight"/>：横向 左→右（中式漫画）；</item>
///   <item><see cref="HorizontalRightToLeft"/>：横向 右→左（日式漫画，第一张在最右）。</item>
/// </list>
///
/// <para><b>翻页 / 单张</b>（一次显示固定数量，靠翻页移动）：</para>
/// <list type="bullet">
///   <item><see cref="SpreadLeftToRight"/>：双页 左→右（中间两页：右边下一张，翻页时整页往左滑）；</item>
///   <item><see cref="SpreadRightToLeft"/>：双页 右→左（日式：左边下一张，翻页时整页往右滑）；</item>
///   <item><see cref="SinglePage"/>：单页（一次一张，左右按钮 / 滚轮翻页）。</item>
/// </list>
///
/// <para>枚举按 <b>C# 原名</b>存进配置（`"Vertical"` / `"SpreadRightToLeft"`…），所以老配置（0~2）
/// 不受影响；加新成员也不会让老配置错位。</para>
/// </summary>
public enum PictureScrollMode
{
    /// <summary>纵向：从上到下（条漫 / 长图 / 普通图集，默认）。</summary>
    Vertical = 0,

    /// <summary>横向：从左到右（中式漫画阅读习惯；第一张在最左边）。</summary>
    HorizontalLeftToRight = 1,

    /// <summary>横向：从右到左（日式漫画阅读习惯；第一张在最右边，往左翻页）。</summary>
    HorizontalRightToLeft = 2,

    /// <summary>双页翻页 · 左→右：中间并排两页，前进时整页往左滑（右边下一张移到左边、新的从右边进）。</summary>
    SpreadLeftToRight = 3,

    /// <summary>双页翻页 · 右→左：日式镜像版，前进时整页往右滑。</summary>
    SpreadRightToLeft = 4,

    /// <summary>单页浏览：一次一张，左右按钮 / 滚轮切换，可选用滑动动画。</summary>
    SinglePage = 5,
}

/// <summary>「阅读方式」的纯逻辑小工具（界面 / 自检都用它，别在各处重复判断）。</summary>
public static class PictureScrollModes
{
    /// <summary>是不是「连续滚动」类（长带 + 阅读偏移那一套）。</summary>
    public static bool IsContinuous(PictureScrollMode mode)
        => Normalize(mode) is PictureScrollMode.Vertical
            or PictureScrollMode.HorizontalLeftToRight
            or PictureScrollMode.HorizontalRightToLeft;

    /// <summary>是不是「翻页 / 单张」类（双页 / 单页；用 <see cref="PicturePaging"/> 那套排版）。</summary>
    public static bool IsPaged(PictureScrollMode mode) => !IsContinuous(mode);

    /// <summary>是不是双页翻页（左→右 / 右→左两种）。</summary>
    public static bool IsSpread(PictureScrollMode mode)
        => Normalize(mode) is PictureScrollMode.SpreadLeftToRight or PictureScrollMode.SpreadRightToLeft;

    /// <summary>是不是单页浏览。</summary>
    public static bool IsSinglePage(PictureScrollMode mode) => Normalize(mode) == PictureScrollMode.SinglePage;

    /// <summary>连续横向滚动（决定「阅读轴」用水平偏移、统一高度）。翻页模式不算横向。</summary>
    public static bool IsHorizontal(PictureScrollMode mode)
        => Normalize(mode) is PictureScrollMode.HorizontalLeftToRight
            or PictureScrollMode.HorizontalRightToLeft;

    /// <summary>
    /// 是不是「反向」：日式横向 / 日式双页（阅读方向从右往左；物理摆放镜像、翻页动画反向）。
    /// </summary>
    public static bool IsReverse(PictureScrollMode mode)
        => Normalize(mode) is PictureScrollMode.HorizontalRightToLeft
            or PictureScrollMode.SpreadRightToLeft;

    /// <summary>把任意枚举值（例如手改坏了的配置）归一化到合法值。</summary>
    public static PictureScrollMode Normalize(PictureScrollMode mode)
        => Enum.IsDefined(typeof(PictureScrollMode), mode) ? mode : PictureScrollMode.Vertical;

    /// <summary>给人看的名字（工具条 / 设置 / 状态栏用）。</summary>
    public static string Describe(PictureScrollMode mode) => mode switch
    {
        PictureScrollMode.HorizontalLeftToRight => "横向 左 → 右",
        PictureScrollMode.HorizontalRightToLeft => "横向 右 → 左",
        PictureScrollMode.SpreadLeftToRight => "双页 左 → 右",
        PictureScrollMode.SpreadRightToLeft => "双页 右 → 左",
        PictureScrollMode.SinglePage => "单页浏览",
        _ => "纵向 上 → 下",
    };

    /// <summary>分段按钮 / 小标签上的短名。</summary>
    public static string DescribeCompact(PictureScrollMode mode) => mode switch
    {
        PictureScrollMode.HorizontalLeftToRight => "左→右",
        PictureScrollMode.HorizontalRightToLeft => "右→左",
        PictureScrollMode.SpreadLeftToRight => "双页→",
        PictureScrollMode.SpreadRightToLeft => "双页←",
        PictureScrollMode.SinglePage => "单页",
        _ => "上→下",
    };

    /// <summary>一句话解释这个方式适合看什么 / 怎么翻。</summary>
    public static string Hint(PictureScrollMode mode) => mode switch
    {
        PictureScrollMode.HorizontalLeftToRight => "中式漫画习惯：第一张在最左边，滚轮 / 拖拽往右看",
        PictureScrollMode.HorizontalRightToLeft => "日式漫画习惯：第一张在最右边，滚轮 / 拖拽往左看",
        PictureScrollMode.SpreadLeftToRight => "双页翻页（中式）：右边是下一张；翻页时整页往左滑，新图从右边进来",
        PictureScrollMode.SpreadRightToLeft => "双页翻页（日式）：左边是下一张；翻页时整页往右滑，新图从左边进来",
        PictureScrollMode.SinglePage => "单页浏览：一次一张，用左右按钮 / 滚轮 / 方向键切换",
        _ => "条漫 / 长图习惯：图片上下相接，滚轮往下看",
    };
}

/// <summary>翻页模式下滚轮做什么。</summary>
public enum PictureWheelAction
{
    /// <summary>上一张 / 下一张（翻页，默认）。</summary>
    TurnPage = 0,

    /// <summary>缩放（和连续模式里 Ctrl+滚轮一样）。</summary>
    Zoom = 1,
}

/// <summary>翻页模式下滚轮动作的小工具。</summary>
public static class PictureWheelActions
{
    public static PictureWheelAction Normalize(PictureWheelAction action)
        => Enum.IsDefined(typeof(PictureWheelAction), action) ? action : PictureWheelAction.TurnPage;

    public static string Describe(PictureWheelAction action)
        => Normalize(action) == PictureWheelAction.Zoom ? "缩放" : "翻页（上一张 / 下一张）";
}
