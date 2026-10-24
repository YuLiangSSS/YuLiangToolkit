using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.OsuMania.ViewModels;
using YuLiangToolkit.Tools.OsuMania.Views;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 「osu! Key 转换器」：把低键数的 osu!mania 谱面转换成更高键数（旧项目 KeyConverter 的移植版）。
///
/// <para>界面 / 参数 / 配置见 <see cref="ViewModels.OsuKeyConverterViewModel"/> 与
/// <see cref="OsuKeyConverterConfig"/>；算法在 <c>Core/KeyConverterEngine.cs</c>（由 osu-core 移植，
/// 行为与旧版逐行一致）。三个 osu 工具同属「音游工具」类别，Order 51 / 52 / 53 连号排列。</para>
/// </summary>
public class OsuKeyConverterTool : ITool
{
    public string Id => OsuKeyConverterConfig.ToolId;

    public string Name => "osu! Key 转换器";

    public string Description => "把低键数 mania 谱面转成更高键数（1k~10k）：可调空白列 / 去子弹 / 转换间隔 / 随机种子，自动改名保存";

    public string EmojiIcon => "🎹";

    public string Category => "音游工具";

    /// <summary>收进「音游工具」类别页（外壳按 CategoryPath 建目录树，见 AGENTS.md 第 2 节）。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "音游工具" };

    public IReadOnlyList<string> Tags => new[]
    {
        "osu", "osu!", "mania", "音游", "谱面", "beatmap", "转换", "转k", "key", "键数", "keyconverter",
    };

    public int Order => 51;

    public FrameworkElement CreateView() => new OsuKeyConverterView { DataContext = new OsuKeyConverterViewModel() };
}
