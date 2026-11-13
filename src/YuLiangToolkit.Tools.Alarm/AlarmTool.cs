using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Alarm.ViewModels;
using YuLiangToolkit.Tools.Alarm.Views;

namespace YuLiangToolkit.Tools.Alarm;

/// <summary>
/// 「闹钟」工具入口：多个闹钟、按星期重复、贪睡。
/// <para>到点检测靠 VM 里每秒的计时器，所以即使页面没打开也必须活着 —— 因此 VM 常驻。</para>
/// </summary>
public sealed class AlarmTool : ITool
{
    private readonly AlarmViewModel _viewModel;

    /// <summary>构造：建常驻 ViewModel（到点检测与页面无关）。</summary>
    public AlarmTool()
    {
        _viewModel = new AlarmViewModel();
    }

    /// <summary>常驻 ViewModel。</summary>
    public AlarmViewModel ViewModel => _viewModel;

    /// <summary>强类型配置。</summary>
    public AlarmConfig Config => _viewModel.Cfg;

    public string Id => AlarmConfig.ToolId;

    public string Name => "闹钟";

    public string Description =>
        "多个闹钟，可按星期重复（工作日 / 每天 / 自定义）、备注、贪睡；"
        + "到点弹通知并响铃（可设为反复播放直到你停止或贪睡）。";

    public string EmojiIcon => "⏰";

    public string Category => "系统";

    public IReadOnlyList<string> CategoryPath => new[] { "系统" };

    public IReadOnlyList<string> Tags => new[]
    {
        "闹钟", "定时", "提醒", "alarm", "起床", "贪睡",
    };

    /// <summary>排在系统分类里倒计时 (171) 之后。</summary>
    public int Order => 173;

    public FrameworkElement CreateView() => new AlarmView { DataContext = _viewModel };
}
