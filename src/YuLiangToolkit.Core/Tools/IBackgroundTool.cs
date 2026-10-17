namespace YuLiangToolkit.Core.Tools;

/// <summary>
/// 「切走之后还想继续干活」的工具（目前只有播放器用：后台播放）。
///
/// <para>外壳据此决定两件事：</para>
/// <list type="bullet">
///   <item>切走时**不销毁它的视图**（视图一销毁，里面的 <c>MediaElement</c> 就没了，声音也就断了）；</item>
///   <item>它不在前台时，在左下角显示一个小浮窗（迷你播放条），点一下能回到这个工具。</item>
/// </list>
///
/// <para>⚠ 外壳只在<b>切走的那一瞬间</b>读一次 <see cref="KeepAliveOnLeave"/>：
/// 要保活就把视图留着，之后再改这个值也不会自动释放（下次切走时才会重新判断）。</para>
/// </summary>
public interface IBackgroundTool
{
    /// <summary>现在切走的话要不要保住这个视图（保住 = 后台继续出声）。</summary>
    bool KeepAliveOnLeave { get; }

    /// <summary>不在前台时要不要显示左下角的小浮窗（例如“还播着东西”）。</summary>
    bool ShowMiniPanel { get; }

    /// <summary>上面两个值可能变化时触发（外壳只据此刷新小窗，不轮询）。</summary>
    event EventHandler? BackgroundStateChanged;
}

/// <summary>
/// 能“整窗全屏”的外壳。功能页里按全屏按钮时，沿着 <c>Window.GetWindow(this)</c> 往上找到它。
///
/// <para>外壳负责：藏掉标题栏 / 工具条 / 侧边栏 / 状态栏，把窗口铺满**整个屏幕**（含任务栏区域），
/// 以及还原回去。功能页只负责“我要进 / 出全屏”，不碰窗口。</para>
/// </summary>
public interface IFullscreenHost
{
    /// <summary>当前是不是全屏状态。</summary>
    bool IsFullscreen { get; }

    /// <summary>进入 / 退出全屏；返回操作之后的状态。不能全屏时返回 false。</summary>
    bool SetFullscreen(bool on);
}
