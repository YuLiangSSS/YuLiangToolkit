using System.Windows;

namespace YuLiangToolkit.Core.Tools;

/// <summary>
/// 工具契约：任何实现该接口的类型都会被工具集自动发现并展示在主页/侧边栏。
/// 新工具只需实现本接口（并提供无参构造函数），放入主程序集或 plugins 目录即可生效。
/// </summary>
public interface ITool
{
    /// <summary>稳定唯一标识，例如 "video-to-audio"。</summary>
    string Id { get; }

    /// <summary>展示名称。</summary>
    string Name { get; }

    /// <summary>一句话描述，显示在主页卡片上。</summary>
    string Description { get; }

    /// <summary>Emoji 图标（也支持任意字符串）。</summary>
    string EmojiIcon { get; }

    /// <summary>分类名，用于主页分组展示与搜索（一个功能只有一个分类）。</summary>
    string Category { get; }

    /// <summary>
    /// 标签（可选，可多个）：主页卡片上显示为蓝色小字，搜索时也能命中，例如
    /// "视频" / "音频" / "FFmpeg" / "批量"。
    ///
    /// <para>这是接口默认实现（返回空数组），所以<b>已有工具不写它也能正常编译运行</b>，
    /// 想加标签时再补一个属性即可。</para>
    /// </summary>
    IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>排序权重，越小越靠前（主页“默认顺序”排序用）。</summary>
    int Order { get; }

    /// <summary>创建该工具的主界面（FrameworkElement）。</summary>
    FrameworkElement CreateView();
}
