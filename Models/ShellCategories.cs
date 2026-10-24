using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 外壳维护的类别元数据清单（图标 / 描述 / 排序）。
///
/// <para>类别本身是各工具在 <see cref="ITool.CategoryPath"/> 里声明出来的，这里只负责“长什么样”：
/// 不在这里登记也能用（默认 📁、无描述）；登记了就显示 Emoji、描述，并按 <see cref="ToolCategoryInfo.Order"/> 排序。
/// 以后要加新大类，在这里补一条即可，界面不用改。</para>
/// </summary>
public static class ShellCategories
{
    /// <summary>「音游工具」类别名（osu! 系列工具都归这一类）。</summary>
    public const string MusicGame = "音游工具";

    /// <summary>全部已登记的类别元数据。</summary>
    public static IReadOnlyList<ToolCategoryInfo> All { get; } = new[]
    {
        new ToolCategoryInfo
        {
            Path = new[] { MusicGame },
            EmojiIcon = "🎵",
            Description = "osu! 谱面转换、LN 处理、等级计算与下图",
            Order = 10,
        },
    };
}
