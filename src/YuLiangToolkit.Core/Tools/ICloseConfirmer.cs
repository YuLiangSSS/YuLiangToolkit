namespace YuLiangToolkit.Core.Tools;

/// <summary>
/// 「退出程序前可能还有没保存的东西」的工具。
///
/// <para>外壳在**真正退出**（托盘「退出」/ 系统关机 / 截图模式收尾）之前，
/// 会依次问每个还活着的工具（当前页 + 缓存页）：<see cref="ConfirmClose"/>。
/// 工具在这里保存 / 弹确认框；返回 false 就中止这次退出。</para>
///
/// <para><b>为什么放在 Core</b>：外壳不能认识具体工具（`App` 里没有、也不该有对
/// `OsuManiaEditorViewModel` 的引用）。定个约定，谁需要谁实现。</para>
///
/// <para>⚠ 只用于**真正退出**，不要拿它拦「切走工具页」—— 切走时视图会进缓存，
/// 拦下来会让用户莫名其妙地走不掉。</para>
/// </summary>
public interface ICloseConfirmer
{
    /// <summary>
    /// 真要退出了，给一次保存 / 确认的机会。
    /// </summary>
    /// <returns>true = 可以继续退出；false = 用户要求取消退出。</returns>
    bool ConfirmClose();
}
