using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Diary.ViewModels;
using YuLiangToolkit.Tools.Diary.Views;

namespace YuLiangToolkit.Tools.Diary;

/// <summary>
/// 「日记本」工具入口：实现 <see cref="ITool"/> 后会被外壳自动发现并挂到主页。
///
/// <para>功能定位：给用户写日记用的本子。支持两种写法——<b>富文本</b>（跟邮箱编辑器一样：
/// 加粗 / 斜体 / 下划线 / 颜色 / 标题 / 列表 / 对齐 / 图片）和<b>普通文本</b>；
/// 左侧是历史日记列表（按日期、可搜索），右侧编辑区边写边自动保存。</para>
///
/// <para>数据都在本机：一篇日记一个 JSON，放 <c>Data\diary\entries\</c>；不联网、不上传。</para>
/// </summary>
public sealed class DiaryTool : ITool
{
    public string Id => DiaryConfig.ToolId;

    public string Name => "日记本";

    public string Description =>
        "给自己写日记：支持富文本（加粗 / 颜色 / 标题 / 列表 / 图片，跟邮箱编辑器一样）和普通文本两种写法，"
        + "右侧编辑区边写边自动保存；左侧历史日记按日期排列、可搜索，点开就能接着写。";

    public string EmojiIcon => "📔";

    public string Category => "生活";

    public IReadOnlyList<string> CategoryPath => new[] { "生活" };

    public IReadOnlyList<string> Tags => new[] { "日记", "记事", "写作", "富文本", "心情", "备忘" };

    /// <summary>排在阅读 / 写作类工具附近。</summary>
    public int Order => 24;

    public FrameworkElement CreateView()
        => new DiaryView { DataContext = new DiaryViewModel() };
}
