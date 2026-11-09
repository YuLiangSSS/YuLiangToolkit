using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.AppManager.Models;

namespace YuLiangToolkit.Tools.AppManager.ViewModels;

/// <summary>
/// 列表里的一行 = 一条已安装应用 + 已经算好的展示文本 + 图标。
///
/// <para>展示文本在构造时一次算好（不改名、不重扫就不会变），界面直接绑定，
/// 避免在 DataTemplate 里反复调格式化函数。</para>
/// </summary>
public sealed class InstalledAppRow : ObservableObject
{
    private BitmapSource? _icon;
    private bool _isRunning;

    public InstalledAppRow(InstalledApp app)
    {
        App = app;
        SizeText = AppCatalogRules.FormatSize(app.SizeInKilobytes);
        InstallDateText = app.HasInstallDate
            ? AppCatalogRules.FormatInstallDate(app.InstallDate)
            : "安装日期未知";
        DriveText = app.DriveLetter.Length > 0 ? app.DriveLetter + ":" : "未记录位置";
        SubtitleText = BuildSubtitle(app);
    }

    /// <summary>原始数据（排序 / 筛选比对用）。</summary>
    public InstalledApp App { get; }

    /// <summary>应用名。</summary>
    public string Name => App.Name;

    /// <summary>副标题：<c>发布者 · v1.2.3</c>（缺哪个就少哪个）。</summary>
    public string SubtitleText { get; }

    /// <summary>占用大小文本（<c>1.2 GB</c> / <c>未知</c>）。</summary>
    public string SizeText { get; }

    /// <summary>安装日期文本（<c>2026/06/26</c> / <c>安装日期未知</c>）。</summary>
    public string InstallDateText { get; }

    /// <summary>装在哪个盘（<c>C:</c> / <c>未记录位置</c>）。</summary>
    public string DriveText { get; }

    /// <summary>能不能卸载（按钮 IsEnabled）。</summary>
    public bool CanUninstall => App.CanUninstall;

    /// <summary>能不能修改（按钮 IsEnabled）。</summary>
    public bool CanModify => App.CanModify;

    /// <summary>卸载按钮的悬停提示（没有卸载命令时说清为什么不能点）。</summary>
    public string UninstallHintText => CanUninstall
        ? "运行注册表里的卸载命令：" + App.UninstallCommand
        : "这个应用没有登记卸载命令（注册表里没有 UninstallString），只能到它的安装目录手动卸载。";

    /// <summary>修改按钮的悬停提示。</summary>
    public string ModifyHintText => CanModify
        ? "运行注册表里的修改 / 修复命令：" + App.ModifyCommand
        : "这个应用没有登记修改 / 修复入口（注册表里没有 ModifyPath）。";

    /// <summary>整行悬停提示：把详情面板里放不下的字段都列出来。</summary>
    public string DetailText
    {
        get
        {
            var lines = new List<string> { "名称：" + Name };
            if (App.Publisher.Length > 0) lines.Add("发布者：" + App.Publisher);
            if (App.Version.Length > 0) lines.Add("版本：" + App.Version);
            lines.Add("大小：" + SizeText);
            lines.Add("安装日期：" + InstallDateText);
            lines.Add("位置：" + (App.InstallLocation.Length > 0 ? App.InstallLocation : DriveText));
            if (App.UninstallCommand.Length > 0) lines.Add("卸载命令：" + App.UninstallCommand);
            if (App.ModifyCommand.Length > 0) lines.Add("修改命令：" + App.ModifyCommand);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>应用图标（后台线程加载，加载好再通知界面）。</summary>
    public BitmapSource? Icon
    {
        get => _icon;
        set
        {
            if (!Set(ref _icon, value)) return;
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    /// <summary>有没有加载到图标（界面据此在「图片 / 占位方块」之间切换）。</summary>
    public bool HasIcon => _icon is not null;

    /// <summary>是不是这个应用的卸载 / 修改进程正在跑（行尾显示「运行中」，按钮禁点）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>行尾的状态小字。</summary>
    public string StateText => IsRunning ? "运行中…" : "";

    private static string BuildSubtitle(InstalledApp app)
    {
        var parts = new List<string>(2);
        if (app.Publisher.Length > 0) parts.Add(app.Publisher);
        if (app.Version.Length > 0) parts.Add("v" + app.Version);
        return parts.Count == 0 ? "（注册表没有登记发布者与版本）" : string.Join(" · ", parts);
    }
}
