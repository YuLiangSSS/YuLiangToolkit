using System.Collections.ObjectModel;
using System.Windows;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>侧边栏导航项。</summary>
public class NavItem
{
    public string Title { get; }
    public string Emoji { get; }
    public ITool? Tool { get; }
    public bool IsHome => Tool is null;

    private readonly Action _navigate;

    public NavItem(string title, string emoji, ITool? tool, Action navigate)
    {
        Title = title;
        Emoji = emoji;
        Tool = tool;
        _navigate = navigate;
    }

    public void Navigate() => _navigate();
}

/// <summary>主窗口视图模型：管理侧边栏、主页与工具导航。</summary>
public class MainViewModel : ObservableObject
{
    private bool _syncingNav;

    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<ITool> Tools { get; } = new();

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        private set => Set(ref _currentView, value);
    }

    private bool _backVisible;
    public bool BackVisible
    {
        get => _backVisible;
        private set => Set(ref _backVisible, value);
    }

    private NavItem? _selectedNavItem;
    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (Set(ref _selectedNavItem, value) && !_syncingNav) value?.Navigate();
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private string _currentTitle = "工具集主页";
    public string CurrentTitle
    {
        get => _currentTitle;
        set => Set(ref _currentTitle, value);
    }

    public RelayCommand GoHomeCommand { get; }
    public RelayCommand OpenToolCommand { get; }

    public MainViewModel(IEnumerable<ITool> tools)
    {
        GoHomeCommand = new RelayCommand(_ => GoHome());
        OpenToolCommand = new RelayCommand(p =>
        {
            if (p is ITool t) OpenTool(t);
        });

        NavItems.Add(new NavItem("首页", "🏠", null, GoHome));
        foreach (var t in tools)
        {
            Tools.Add(t);
            NavItems.Add(new NavItem(t.Name, t.EmojiIcon, t, () => OpenTool(t)));
        }

        var ffmpeg = FFmpegLocator.Find(null);
        StatusText = $"共加载 {Tools.Count} 个工具 · FFmpeg：{(ffmpeg is not null ? "已找到" : "未找到（可在工具内配置）")}"
                     + $" · 数据/配置目录：{AppPaths.Root} · v0.2.0";
        GoHome();
    }

    /// <summary>切换功能前通知上一个工具：落盘未保存的配置、停止后台任务。</summary>
    private void CloseCurrentTool()
    {
        if (CurrentView is FrameworkElement fe && fe.DataContext is ToolViewModelBase vm)
        {
            vm.NotifyClosed();
        }
    }

    private void GoHome()
    {
        CloseCurrentTool();
        _syncingNav = true;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.IsHome);
        _syncingNav = false;
        CurrentView = new HomeView { DataContext = this };
        BackVisible = false;
        CurrentTitle = "工具集主页";
    }

    private void OpenTool(ITool tool)
    {
        CloseCurrentTool();
        _syncingNav = true;
        SelectedNavItem = NavItems.FirstOrDefault(n => n.Tool == tool);
        _syncingNav = false;
        CurrentView = tool.CreateView();
        BackVisible = true;
        CurrentTitle = tool.Name;
    }
}
