namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// 「用本工具的打开方式打开这个文件」的接收方契约。
///
/// <para><b>谁调用</b>：主程序（<c>App.xaml.cs</c>）在处理「打开方式」命令行 / 单实例转发时，
/// 先把目标工具页打开（<c>OpenToolCommand</c>，可能是新建视图，也可能是缓存复用的视图），
/// 再把文件路径交给工具页的 ViewModel（<see cref="OpenLaunchedFile"/>）。</para>
///
/// <para><b>实现要求</b>：</para>
/// <list type="number">
///   <item>实现方自己异步加载（<c>_ = LoadAsync(path)</c>），**不要**阻塞 UI 线程、不要弹窗；</item>
///   <item>文件不存在 / 读取失败 → 写工具状态栏或日志（同工具现有习惯），绝不抛异常给主程序；</item>
///   <item>要同时照顾「视图刚创建」和「视图被缓存复用」两种情况：</item>
/// </list>
///
/// <para>见 <c>docs/dev/pitfalls.md</c> 的文件关联一节与 <c>docs/tools/open-with.md</c>。</para>
/// </summary>
public interface IFileLaunchTarget
{
    /// <summary>
    /// 打开主程序交过来的文件（绝对路径优先，实现里仍要容忍相对路径）。
    /// 由主程序在工具页已经挂到界面上之后调用，可以重复调用（每次打开一个新文件）。
    /// </summary>
    void OpenLaunchedFile(string path);
}
