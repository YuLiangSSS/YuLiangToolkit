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
    /// 类别路径（可选，**任意层级**）：功能归属的多级目录，例如
    /// <c>new[] { "音游工具" }</c> 表示放在「音游工具」类别下，
    /// <c>new[] { "音游工具", "谱面处理" }</c> 表示再深一层。
    ///
    /// <para>这是接口默认实现（返回空数组 = 放在主页根目录），所以<b>已有工具不写它也能正常编译运行</b>；
    /// 写了就由外壳按路径归进类别树（见 <see cref="ToolCatalog"/>），主页卡片会收进类别页。
    /// 注意它<b>不会</b>自动拿 <see cref="Category"/> 当目录 —— 老工具的 Category 仍然只做标签 / 排序。</para>
    /// </summary>
    IReadOnlyList<string> CategoryPath => Array.Empty<string>();

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
