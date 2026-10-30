namespace YuLiangToolkit.Core.Maintenance;

/// <summary>
/// 维护列表里的「一个功能」：只带 Id + 展示名，不依赖具体 <see cref="Tools.ITool"/> 实例。
///
/// <para>「主页设置 → 重建配置」按它逐个列出 <c>Config\&lt;功能Id&gt;\</c>；
/// 外壳自己（<c>shell</c>）不是 <see cref="Tools.ITool"/>，但也用同一套配置机制，
/// 所以由外壳额外塞一条 <c>new ToolRef("shell", "主页 / 外壳设置")</c> 进来。</para>
/// </summary>
/// <param name="Id">功能 Id（= Config / Data / Cache 下的小文件夹名）。</param>
/// <param name="Name">界面展示名（工具名 / 「主页 / 外壳设置」）。</param>
public readonly record struct ToolRef(string Id, string Name);
