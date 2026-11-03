using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Browser.ViewModels;

/// <summary>
/// 一个标签页的状态（<b>只有元数据</b>；WebView2 实例由 <c>BrowserView</c> 按 <see cref="BrowserViewModel.ActiveTab"/>
/// 创建 / 释放，同一时刻只显示激活标签的内核窗口）。
///
/// <para>View 在内核事件里改这里的属性；<see cref="BrowserViewModel"/> 监听 <c>PropertyChanged</c>，
/// 激活标签的变动会镜像到地址栏 / 按钮状态 / 状态文字。</para>
/// </summary>
public sealed class BrowserTab : ObservableObject
{
    private string _title;
    private string _url;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private string? _lastError;

    /// <summary>内部序号（View 用它排错日志，界面不显示）。</summary>
    internal int Id { get; }

    internal BrowserTab(int id, string url)
    {
        Id = id;
        _url = url ?? "";
        _title = "";
    }

    /// <summary>页面标题（内核 <c>DocumentTitleChanged</c> 时更新；空 = 标签条显示地址）。</summary>
    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value ?? "")) OnPropertyChanged(nameof(Header)); }
    }

    /// <summary>当前地址（内核没建好时是「待打开」的地址，建好跳转后跟随内核）。</summary>
    public string Url
    {
        get => _url;
        set { if (Set(ref _url, value ?? "")) OnPropertyChanged(nameof(Header)); }
    }

    /// <summary>是否正在加载（标签条显示加载点，状态文字显示「正在加载…」）。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    /// <summary>本页能否后退。</summary>
    public bool CanGoBack
    {
        get => _canGoBack;
        set => Set(ref _canGoBack, value);
    }

    /// <summary>本页能否前进。</summary>
    public bool CanGoForward
    {
        get => _canGoForward;
        set => Set(ref _canGoForward, value);
    }

    /// <summary>最近一次导航失败的错误描述（成功导航后清空；状态文字用它）。</summary>
    public string? LastError
    {
        get => _lastError;
        set => Set(ref _lastError, value);
    }

    /// <summary>标签条上显示的字：优先标题，没标题退回地址，都没有叫「新标签页」。</summary>
    public string Header
        => string.IsNullOrWhiteSpace(_title)
            ? (string.IsNullOrWhiteSpace(_url) ? "新标签页" : _url)
            : _title;

    /// <summary>恢复会话前打的一行快照（调试 / 排错用）。</summary>
    public override string ToString() => $"{Header} <{Url}>";
}
