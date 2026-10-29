using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Kps.ViewModels;
using YuLiangToolkit.Tools.Kps.Views;

namespace YuLiangToolkit.Tools.Kps;

/// <summary>
/// 「KPS 按键统计」工具入口：把全局键盘输入变成实时 KPS / 每键计数 / 键位可视化，并提供置顶透明的 Overlay。
///
/// <para>功能灵感来自开源项目 <b>DmNote</b>（<see href="https://github.com/DmNote-App/DmNote"/>，
/// GPL-3.0）：本实现是面向 WPF / YuLiangToolkit 的<b>独立重写</b>，没有复制它的源码；
/// 来源与致敬写在 <c>docs/tools/kps.md</c>。未实现直播插件 / OBS 浏览器源 / 自定义 CSS / JS 插件。</para>
/// </summary>
public sealed class KpsTool : ITool
{
    /// <summary>功能 Id（= Data / Config 下的小文件夹名，也是 Overlay 配置的归属）。</summary>
    public const string ToolId = "kps";

    public string Id => ToolId;

    public string Name => "KPS 按键统计";

    public string Description =>
        "实时 KPS / 每键计数 / 键位可视化 + 置顶透明 Overlay；支持 4K~8K 预设、改键、按键图片、音符效果、键音与全局热键（灵感来源 DmNote）";

    public string EmojiIcon => "🎹";

    public string Category => "音游工具";

    public IReadOnlyList<string> CategoryPath => new[] { "音游工具" };

    public IReadOnlyList<string> Tags => new[]
    {
        "KPS", "按键统计", "键位", "键 viewer", "Overlay", "音游", "DJMAX", "DmNote",
    };

    public int Order => 150;

    public FrameworkElement CreateView()
    {
        // 监听 / 统计 / Overlay 归会话管：页面重开时复用同一个 ViewModel（统计与 Overlay 不断）
        var session = KpsSession.Current;
        var viewModel = session.EnsureViewModel();
        session.Activate(viewModel);
        return new KpsView { DataContext = viewModel };
    }
}
