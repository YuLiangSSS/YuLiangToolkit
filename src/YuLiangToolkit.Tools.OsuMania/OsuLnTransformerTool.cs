using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.OsuMania.ViewModels;
using YuLiangToolkit.Tools.OsuMania.Views;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu! LN 转换器」：把普通音符批量变成 LN（面条）。
///
/// <para>算法在 <c>Core/LnTransformerEngine.cs</c>；参数在 <see cref="OsuLnTransformerConfig"/>，
/// 各 Level 档位的含义见配置类里的 <see cref="OsuLnTransformerConfig.LevelHint"/>。</para>
/// </summary>
public class OsuLnTransformerTool : ITool
{
    public string Id => OsuLnTransformerConfig.ToolId;

    public string Name => "osu! LN 转换器";

    public string Description => "把普通音符批量变成 LN（面条）：密度分布 / 完全随机 / 反相 / 保留原 LN / 修复尾判误差";

    public string EmojiIcon => "🍜";

    public string Category => "音游工具";

    /// <summary>收进「音游工具」类别页。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "音游工具" };

    public IReadOnlyList<string> Tags => new[]
    {
        "osu", "osu!", "mania", "音游", "谱面", "beatmap", "转换", "ln", "长条", "面条", "lntransformer",
    };

    public int Order => 52;

    public FrameworkElement CreateView() => new OsuLnTransformerView { DataContext = new OsuLnTransformerViewModel() };
}
