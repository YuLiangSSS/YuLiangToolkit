using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Live2D.ViewModels;
using YuLiangToolkit.Tools.Live2D.Views;

namespace YuLiangToolkit.Tools.Live2D;

/// <summary>
/// 「Live2D 桌宠」工具入口：内置 Live2D 模型测试台 + 桌面宠物 + 眼睛跟随鼠标。
///
/// <para>模型运行时与桌宠窗口归 <see cref="Live2DSession"/> 管（与 KPS 的 Overlay 同一套思路）：
/// 切到别的工具页 / 回主页时，桌宠窗口继续显示、继续跟着鼠标看，只有关掉桌宠才真正停下。</para>
///
/// <para><b>启动时自动放出桌宠</b>：<see cref="Live2DConfig.AutoStartPet"/> 开启时，本工具被
/// <see cref="ToolDiscovery"/> 发现（启动早期、MainWindow 还没建）只登记一次延迟回调，
/// 等 Dispatcher 空闲后再创建会话 / 加载模型 / 显示桌宠 —— 构造函数里不做任何耗时操作。
/// 开发调试截图（环境变量 <c>YLT_LIVE2D_SHOT_PET</c>）也走同一条延迟回调，强制放出桌宠。</para>
/// </summary>
public sealed class Live2DTool : ITool
{
    /// <summary>功能 Id（= Data / Config 下的小文件夹名，也是自检组名）。</summary>
    public const string ToolId = "live2d";

    public Live2DTool()
    {
        // ToolDiscovery 会在启动早期（MainWindow 还没建）new 出所有工具：这里绝不能同步加载模型 / Show 窗口。
        if (Application.Current is null) return;   // 自检 / 无 UI 进程：直接 no-op

        var shotPetPath = Environment.GetEnvironmentVariable("YLT_LIVE2D_SHOT_PET");
        var selfTest = Environment.GetEnvironmentVariable("YLT_LIVE2D_PET_SELFTEST");
        var wantDebugPet = !string.IsNullOrWhiteSpace(shotPetPath) || selfTest is "1" or "on" or "true";
        var autoStart = false;
        try
        {
            // 只有配置文件已经存在才去读：没开过这个工具的用户不该因为自动发现就多出一个 Config 文件
            var store = ToolConfigStore.For(ToolId);
            if (wantDebugPet || store.Exists)
            {
                var config = store.Load<Live2DConfig>();
                autoStart = config.AutoStartPet;
            }
        }
        catch
        {
            // 配置读不出来就不自动启动（不影响工具正常打开）
        }

        if (!autoStart && !wantDebugPet) return;

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var session = Live2DSession.Current;
                session.EnsureViewModel();
                session.ShowPet();
            }
            catch
            {
                // 自动放出桌宠失败不影响程序启动（用户还可以在测试页手动点「显示桌宠」）
            }
        }));
    }

    public string Id => ToolId;

    public string Name => "Live2D 桌宠";

    public string Description =>
        "内置 Live2D 模型测试台 + 桌面宠物：参数 / 部件 / 动作 / 表情 / 物理逐项调试，透明置顶、可拖动、眼睛跟随鼠标、点击穿透；" +
        "可接 DeepSeek AI 聊天（自填 API Key，语音 + 气泡 + 表情动作联动、主动搭话、粉色好感度系统）";

    public string EmojiIcon => "🐟";

    public string Category => "桌面";

    public IReadOnlyList<string> CategoryPath => new[] { "桌面" };

    public IReadOnlyList<string> Tags => new[]
    {
        "Live2D", "桌宠", "看板娘", "模型", "眼睛跟随", "Cubism", "VTuber", "物理",
        "AI", "DeepSeek", "聊天", "好感度", "语音",
    };

    public int Order => 160;

    public FrameworkElement CreateView()
    {
        // 模型加载 / 桌宠归会话管：页面重开时复用同一个 ViewModel（模型不重复加载）
        var session = Live2DSession.Current;
        var viewModel = session.EnsureViewModel();
        session.Activate(viewModel);
        return new Live2DView { DataContext = viewModel };
    }
}
