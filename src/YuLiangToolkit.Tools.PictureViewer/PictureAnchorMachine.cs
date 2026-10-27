namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>中键锚点的三种状态。</summary>
public enum PictureAnchorPhase
{
    /// <summary>没有锚点。</summary>
    None = 0,

    /// <summary>中键正按着（还不知道是「单点」还是「按住」）。</summary>
    Pressed = 1,

    /// <summary>单点中键留下的锚点（松手后仍然在，要再按任意键才消失）。</summary>
    Latched = 2,
}

/// <summary>
/// 中键锚点的**判定**：把「按下 / 松开 / 按了别的键」翻译成锚点该开还是该关。
///
/// <para>需求来自 PictureView：原来只支持「单点中键 → 锚点留下，移动鼠标快速滚动，再点任意键消失」。
/// 现在多了一种「**按住**中键 → 锚点出现，移动鼠标快速滚动，**一松手锚点就消失**」。两者靠按下时长区分：</para>
/// <list type="bullet">
///   <item>按下中键 → <see cref="PictureAnchorPhase.Pressed"/>（锚点立刻出现，按着就能滚）；</item>
///   <item>在判定时长内松手（单点）→ <see cref="PictureAnchorPhase.Latched"/>（锚点留下）；</item>
///   <item>超过判定时长才松手（按住）→ <see cref="PictureAnchorPhase.None"/>（锚点消失）；</item>
///   <item>锚点留着时按下**任意键** → 消失（与 PictureView 原来的行为一致）。</item>
/// </list>
///
/// <para>纯逻辑、不碰 WPF、时间由调用方传进来（毫秒），所以自检可以直接断言状态机。</para>
/// </summary>
public sealed class PictureAnchorMachine
{
    private double _pressStartedAtMilliseconds;

    /// <summary>
    /// 「单点」和「按住」的分界（毫秒）。按下到松开**小于**这个值算单点。
    /// 写进来时会夹到 <see cref="PictureScrollMath.MinHoldMilliseconds"/> ~
    /// <see cref="PictureScrollMath.MaxHoldMilliseconds"/>。
    /// </summary>
    public int HoldThresholdMilliseconds { get; set; } = PictureScrollMath.DefaultHoldMilliseconds;

    /// <summary>当前状态。</summary>
    public PictureAnchorPhase Phase { get; private set; } = PictureAnchorPhase.None;

    /// <summary>锚点是否可见 / 是否正在自动滚动（Pressed 与 Latched 都算）。</summary>
    public bool IsActive => Phase != PictureAnchorPhase.None;

    /// <summary>是不是「单点留下」的锚点（此时任意键都能关掉）。</summary>
    public bool IsLatched => Phase == PictureAnchorPhase.Latched;

    /// <summary>中键是否还按着（按住模式）。</summary>
    public bool IsPressed => Phase == PictureAnchorPhase.Pressed;

    /// <summary>生效的判定时长（夹过范围）。</summary>
    public int HoldMillisecondsEffective => PictureScrollMath.EffectiveHoldMilliseconds(HoldThresholdMilliseconds);

    /// <summary>
    /// 中键按下。已经处于锚点状态时 = 再按一次中键把它关掉（与「任意键关闭」一致），返回 <c>None</c>。
    /// </summary>
    public PictureAnchorPhase MiddleDown(double nowMilliseconds)
    {
        if (Phase != PictureAnchorPhase.None)
        {
            Phase = PictureAnchorPhase.None;
            return Phase;
        }

        _pressStartedAtMilliseconds = nowMilliseconds;
        Phase = PictureAnchorPhase.Pressed;
        return Phase;
    }

    /// <summary>
    /// 中键松开：按得短 = 单点，锚点留下（<c>Latched</c>）；按得久 = 按住，锚点消失（<c>None</c>）。
    /// 没有「正按着」的中键时什么都不做（比如锚点留着时按下别的键已经被关掉了）。
    /// </summary>
    public PictureAnchorPhase MiddleUp(double nowMilliseconds)
    {
        if (Phase != PictureAnchorPhase.Pressed) return Phase;

        var held = nowMilliseconds - _pressStartedAtMilliseconds;
        Phase = held < HoldMillisecondsEffective ? PictureAnchorPhase.Latched : PictureAnchorPhase.None;
        return Phase;
    }

    /// <summary>
    /// 按下了左键 / 右键。只有「单点留下的锚点」会被关掉；
    /// 按住中键的过程中按别的键不打断（那时手还按着中键，松开才是结束信号）。
    /// </summary>
    public PictureAnchorPhase OtherButtonDown()
    {
        if (Phase == PictureAnchorPhase.Latched) Phase = PictureAnchorPhase.None;
        return Phase;
    }

    /// <summary>强制关掉锚点（Esc、切换工具、鼠标捕获丢失等）。</summary>
    public void Reset() => Phase = PictureAnchorPhase.None;
}
