namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「阅读偏移」的纯数学：滚轮 / 中键锚点 / 拖拽 / 按比例定位都只调这里，界面不自己写公式。
///
/// <para><b>偏移量的含义</b>：一律是「阅读偏移」——0 = 第一张图的开头，最大值 = 最后一张图的末尾。
/// ScrollViewer 的物理偏移在日式右→左模式下是反的（物理 0 在最左边 = 阅读的最末尾），
/// 界面层负责 <c>阅读偏移 ↔ 物理偏移</c> 的换算，这里只算「阅读偏移该变成多少」。</para>
///
/// <para><b>约定</b>：<c>wheelDelta</c> 用 WPF 的 <c>MouseWheelEventArgs.Delta</c>（向下滚是负数，
/// 一格 120）；<paramref name="stepPixels"/> 是「滚一格前进多少像素」。滚轮**向下 = 阅读往前走**，
/// 三种模式一致（横向模式不用按 Shift 就能左右滚）。</para>
/// </summary>
public static class PictureScrollMath
{
    /// <summary>滚轮一格默认前进多少像素（约等于 2.5 行文字，看图时手感合适）。</summary>
    public const double DefaultWheelStepPixels = 120;

    /// <summary>中键锚点的默认灵敏度：鼠标离开锚点 100px，每帧（约 16ms）滚 50px。</summary>
    public const double DefaultAnchorSensitivity = 0.5;

    /// <summary>滚轮一格（像素）允许的范围。</summary>
    public const int MinWheelStep = 20;
    public const int MaxWheelStep = 600;

    /// <summary>「单点 / 按住」判定时长的默认值与允许范围（毫秒）。</summary>
    public const int DefaultHoldMilliseconds = 250;
    public const int MinHoldMilliseconds = 60;
    public const int MaxHoldMilliseconds = 2000;

    /// <summary>把偏移夹在 [0, maximum] 里；内容不够长（maximum ≤ 0）时永远是 0。</summary>
    public static double ClampOffset(double offset, double maximum)
    {
        if (maximum <= 0 || double.IsNaN(maximum)) return 0;
        if (double.IsNaN(offset)) return 0;
        return Math.Clamp(offset, 0, maximum);
    }

    /// <summary>滚轮一格之后新的阅读偏移（向下滚 = 前进；高速滚轮 delta 可能是 240 的倍数）。</summary>
    public static double WheelOffset(double currentOffset, int wheelDelta, double stepPixels, double maximum)
    {
        var step = Math.Clamp(stepPixels, MinWheelStep, MaxWheelStep);
        if (wheelDelta == 0) return ClampOffset(currentOffset, maximum);
        var steps = wheelDelta / 120.0;
        return ClampOffset(currentOffset - steps * step, maximum);
    }

    /// <summary>
    /// 中键锚点：指针在**阅读轴**上离锚点多远 → 这一帧阅读偏移该加多少。
    ///
    /// <para>方向 = 往阅读方向移动指针就是「往前翻」：纵向往下、横向左→右往右、横向右→左往左。
    /// 指针停在锚点上返回 0（不动）；偏离越远滚得越快（浏览器中键自动滚动的手感）。</para>
    /// </summary>
    public static double AnchorDelta(
        PictureScrollMode mode,
        double pointerX,
        double pointerY,
        double anchorX,
        double anchorY,
        double sensitivity = DefaultAnchorSensitivity)
    {
        var sens = double.IsNaN(sensitivity) ? DefaultAnchorSensitivity : Math.Clamp(sensitivity, 0.05, 3.0);
        return PictureScrollModes.Normalize(mode) switch
        {
            PictureScrollMode.HorizontalLeftToRight => (pointerX - anchorX) * sens,
            PictureScrollMode.HorizontalRightToLeft => (anchorX - pointerX) * sens,
            _ => (pointerY - anchorY) * sens,
        };
    }

    /// <summary>
    /// 左键拖拽：把「指针这一帧移动了多少」换成「阅读偏移该加多少」（内容跟着手走）。
    ///
    /// <para>纵向模式看 <paramref name="dy"/>，横向模式看 <paramref name="dx"/>：
    /// 指针往右拖 = 内容往右走 = 看到左边的部分 = 阅读偏移减小，所以是负号；
    /// 日式右→左的阅读偏移和物理偏移反向，符号再翻正。</para>
    /// </summary>
    public static double DragDelta(PictureScrollMode mode, double dx, double dy)
        => PictureScrollModes.Normalize(mode) switch
        {
            PictureScrollMode.HorizontalLeftToRight => -dx,
            PictureScrollMode.HorizontalRightToLeft => dx,
            _ => -dy,
        };

    /// <summary>按比例（0 ~ 1）取阅读偏移，用来「换模式 / 换窗口大小时停在原来大概的位置」。</summary>
    public static double OffsetForRatio(double ratio, double maximum)
    {
        if (maximum <= 0 || double.IsNaN(ratio)) return 0;
        return ClampOffset(Math.Clamp(ratio, 0, 1) * maximum, maximum);
    }

    /// <summary>当前阅读偏移占总长的比例（0 ~ 1，进度条用）。</summary>
    public static double RatioOf(double offset, double maximum)
        => maximum <= 0 ? 0 : ClampOffset(offset, maximum) / maximum;

    /// <summary>
    /// 图片统一缩放后的「跨轴尺寸」：纵向模式 = 统一宽度，横向模式 = 统一高度。
    /// 取「所有图里最大的那一维」和视口尺寸的较小值 —— 小图不放大到发糊，大图缩到刚好一屏看全。
    /// </summary>
    public static double UniformCrossSize(double maxPixelCross, double viewportCross)
    {
        if (viewportCross <= 0 || double.IsNaN(viewportCross)) return 0;
        if (maxPixelCross <= 0 || double.IsNaN(maxPixelCross)) return viewportCross;
        return Math.Min(maxPixelCross, viewportCross);
    }

    /// <summary>
    /// 一张图在长带主轴上的长度：纵向模式是高度，横向模式是宽度。
    /// 尺寸信息不合法（像素宽高 ≤ 0）时退回统一尺寸，宁可显示成方形也不除以 0。
    /// </summary>
    public static double MainLengthForAspect(bool horizontal, int pixelWidth, int pixelHeight, double uniformCross)
    {
        if (uniformCross <= 0 || double.IsNaN(uniformCross)) return 0;
        if (pixelWidth <= 0 || pixelHeight <= 0) return uniformCross;
        return horizontal
            ? uniformCross * pixelWidth / pixelHeight
            : uniformCross * pixelHeight / pixelWidth;
    }

    /// <summary>滚轮步长的生效值（夹到 20 ~ 600，手改坏配置也不会滚飞）。</summary>
    public static int EffectiveWheelStep(int value) => Math.Clamp(value, MinWheelStep, MaxWheelStep);

    /// <summary>单点 / 按住判定时长的生效值（夹到 60 ~ 2000 毫秒）。</summary>
    public static int EffectiveHoldMilliseconds(int value)
        => Math.Clamp(value, MinHoldMilliseconds, MaxHoldMilliseconds);

    // ---------------------------------------------------------------- 阅读偏移 ↔ ScrollViewer 物理偏移
    // 这三个函数是「日式右→左」的关键：图片在物理上是反着摆的，ScrollViewer 的物理偏移从最左边算，
    // 而界面里的滚轮 / 拖拽 / 中键只认「阅读偏移」。放在这里是为了自检能直接断言（见 AGENTS 2.19）。

    /// <summary>某个滚动方式下的阅读总长度：横向看水平可滚范围，纵向看垂直可滚范围。</summary>
    public static double ReadingMaximum(
        PictureScrollMode mode, double scrollableWidth, double scrollableHeight)
    {
        var horizontal = PictureScrollModes.IsHorizontal(PictureScrollModes.Normalize(mode));
        var value = horizontal ? scrollableWidth : scrollableHeight;
        return value > 0 && !double.IsNaN(value) ? value : 0;
    }

    /// <summary>
    /// 物理偏移 → 阅读偏移。纵向 / 横向左→右直接取对应轴的偏移；
    /// **横向右→左 = 总长 − 物理水平偏移**（物理 0 在最左边 = 阅读的最末尾）。
    /// </summary>
    public static double ReadingOffset(
        PictureScrollMode mode,
        double horizontalOffset,
        double verticalOffset,
        double scrollableWidth,
        double scrollableHeight)
    {
        var normalized = PictureScrollModes.Normalize(mode);
        var max = ReadingMaximum(normalized, scrollableWidth, scrollableHeight);

        if (!PictureScrollModes.IsHorizontal(normalized))
            return ClampOffset(verticalOffset, max);

        return PictureScrollModes.IsReverse(normalized)
            ? ClampOffset(max - horizontalOffset, max)
            : ClampOffset(horizontalOffset, max);
    }

    /// <summary>
    /// 阅读偏移 → 物理水平偏移（只有横向模式用得到）。日式右→左要反过来，否则起点会跑到最左边。
    /// </summary>
    public static double PhysicalHorizontalOffset(
        PictureScrollMode mode, double readingOffset, double scrollableWidth)
    {
        var max = scrollableWidth > 0 && !double.IsNaN(scrollableWidth) ? scrollableWidth : 0;
        var reading = ClampOffset(readingOffset, max);
        return PictureScrollModes.IsReverse(PictureScrollModes.Normalize(mode)) ? max - reading : reading;
    }
}
