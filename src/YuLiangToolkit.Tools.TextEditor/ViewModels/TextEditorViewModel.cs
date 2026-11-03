using System.Collections.ObjectModel;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.TextEditor.Core;

namespace YuLiangToolkit.Tools.TextEditor.ViewModels;

/// <summary>
/// View 提供给 ViewModel 的编辑区操作入口（都作用于<b>激活标签</b>的编辑器）。
/// 与浏览器的 <c>IBrowserHost</c> 同一模式：VM 发号令，View 对着真正的控件执行。
/// </summary>
internal interface IEditorHost
{
    /// <summary>激活标签的编辑器已就绪（能执行撤销 / 剪贴板等操作）。</summary>
    bool IsEditorReady { get; }

    /// <summary>能否撤销 / 重做（决定菜单项可用性）。</summary>
    bool CanUndo { get; }
    bool CanRedo { get; }

    void Undo();
    void Redo();
    void Cut();
    void Copy();
    void Paste();
    void SelectAll();

    /// <summary>弹出查找条（<paramref name="withReplace"/> = 顺便展开替换行）。</summary>
    void ShowFindBar(bool withReplace);

    /// <summary>把焦点交回编辑器（关掉查找条 / 点完菜单后）。</summary>
    void FocusEditor();

    /// <summary>标签的文件路径变了（保存 / 另存为）：按新扩展名换语法高亮。</summary>
    void UpdateTabPath(EditorTab tab);
}

/// <summary>
/// 「文本编辑器」的 ViewModel：标签列表 / 激活标签、文件命令（新建 / 打开 / 保存 / 另存为 / 最近）、
/// 编辑命令（经 <see cref="IEditorHost"/> 路由到激活编辑器）、缩放与偏好设置面板开关、会话恢复。
/// </summary>
public sealed class TextEditorViewModel : ToolViewModelBase
{
    private EditorTab? _activeTab;
    private bool _isSettingsVisible;
    private int _nextUntitledIndex;
    private string _caretText = "行 1, 列 1";
    private string _charCountText = "0 字符";

    public TextEditorViewModel() : base(TextEditorConfig.ToolId)
    {
        Cfg = LoadConfig<TextEditorConfig>();
        // 写坏回默认：字号 / 缩放 / 缩进 / 字体链
        Cfg.FontSize = TextEditorConfig.ClampFontSize(Cfg.FontSize);
        Cfg.ZoomPercent = TextEditorConfig.ClampZoomPercent(Cfg.ZoomPercent);
        Cfg.IndentSize = TextEditorConfig.ClampIndentSize(Cfg.IndentSize);

        // 「Markdown 预览」开关一变，预览窗显隐跟着变（View 订阅 IsPreviewVisible 重排）
        Cfg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextEditorConfig.ShowMarkdownPreview))
                OnPropertyChanged(nameof(IsPreviewVisible));
        };

        Tabs = new ObservableCollection<EditorTab>();

        NewFileCommand = new RelayCommand(_ => NewTab());
        OpenFileCommand = new RelayCommand(_ => OpenFilesByDialog());
        OpenPathCommand = new RelayCommand(p => OpenPath(p as string));
        SaveCommand = new RelayCommand(_ => SaveTab(ActiveTab), _ => ActiveTab is not null);
        SaveAsCommand = new RelayCommand(_ => SaveTabAs(ActiveTab), _ => ActiveTab is not null);
        SaveAllCommand = new RelayCommand(_ => SaveAll(), _ => Tabs.Any(t => t.IsDirty));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as EditorTab ?? ActiveTab), _ => ActiveTab is not null);
        CloseAllCommand = new RelayCommand(_ => CloseAll(), _ => Tabs.Count > 0);

        UndoCommand = new RelayCommand(_ => Host?.Undo(), _ => Host is { IsEditorReady: true } && Host.CanUndo);
        RedoCommand = new RelayCommand(_ => Host?.Redo(), _ => Host is { IsEditorReady: true } && Host.CanRedo);
        CutCommand = new RelayCommand(_ => Host?.Cut(), _ => Host is { IsEditorReady: true });
        CopyCommand = new RelayCommand(_ => Host?.Copy(), _ => Host is { IsEditorReady: true });
        PasteCommand = new RelayCommand(_ => Host?.Paste(), _ => Host is { IsEditorReady: true });
        SelectAllCommand = new RelayCommand(_ => Host?.SelectAll(), _ => Host is { IsEditorReady: true });

        FindCommand = new RelayCommand(_ => Host?.ShowFindBar(false), _ => Host is { IsEditorReady: true });
        ReplaceCommand = new RelayCommand(_ => Host?.ShowFindBar(true), _ => Host is { IsEditorReady: true });

        ZoomInCommand = new RelayCommand(_ => Cfg.ZoomPercent = TextEditorConfig.StepZoomPercent(Cfg.ZoomPercent, 1));
        ZoomOutCommand = new RelayCommand(_ => Cfg.ZoomPercent = TextEditorConfig.StepZoomPercent(Cfg.ZoomPercent, -1));
        ResetZoomCommand = new RelayCommand(_ => Cfg.ZoomPercent = TextEditorConfig.DefaultZoomPercent);

        ShowSettingsCommand = new RelayCommand(_ => IsSettingsVisible = !IsSettingsVisible);
        CloseSettingsCommand = new RelayCommand(_ => IsSettingsVisible = false);

        RestoreSession();
    }

    /// <summary>配置（<c>Config\text-editor\config.json</c>）。</summary>
    public TextEditorConfig Cfg { get; }

    /// <summary>当前承载编辑器的视图（切走 / 关闭时为 <c>null</c>）。</summary>
    internal IEditorHost? Host { get; set; }

    // ---------------- 标签 ----------------

    /// <summary>打开着的所有标签（顺序即标签条顺序；关到 0 个会立刻补一个无标题标签）。</summary>
    public ObservableCollection<EditorTab> Tabs { get; }

    /// <summary>激活标签（编辑区显示的就是它）。</summary>
    public EditorTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!Set(ref _activeTab, value)) return;
            RefreshActiveStats();
            RefreshCommandState();
            RefreshSessionConfig();
            OnPropertyChanged(nameof(ActiveIsMarkdown));
            OnPropertyChanged(nameof(IsPreviewVisible));
        }
    }

    /// <summary>当前标签是不是 Markdown（.md / .markdown / .mdown / .mkd）——决定「实际效果预览」显不显示。</summary>
    public bool ActiveIsMarkdown => EditorSyntaxDefinitions.IsMarkdownFile(ActiveTab?.FilePath);

    /// <summary>Markdown 预览窗当前是否应该显示：激活的是 Markdown 且设置里没关预览。</summary>
    public bool IsPreviewVisible => Cfg.ShowMarkdownPreview && ActiveIsMarkdown;

    /// <summary>是否显示偏好设置面板（页内浮层，Esc 关）。</summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => Set(ref _isSettingsVisible, value);
    }

    /// <summary>最近文件列表（菜单「打开最近」的子项；变化后要手动通知）。</summary>
    public IReadOnlyList<string> RecentItems => Cfg.RecentFiles;

    // ---------------- 状态栏 ----------------

    /// <summary>光标位置「行 12, 列 34」（View 在光标移动时喂进来）。</summary>
    public string CaretText
    {
        get => _caretText;
        private set => Set(ref _caretText, value);
    }

    /// <summary>激活文档的字符数。</summary>
    public string CharCountText
    {
        get => _charCountText;
        private set => Set(ref _charCountText, value);
    }

    /// <summary>激活文档的编码名（状态栏展示）。</summary>
    public string EncodingText => ActiveTab is null ? "UTF-8" : TextFileCodec.FriendlyEncodingName(ActiveTab.Encoding);

    /// <summary>激活文档的路径（状态栏展示，无标题给提示）。</summary>
    public string ActivePathText => ActiveTab?.FilePathText ?? "";

    // ---------------- 命令 ----------------

    public RelayCommand NewFileCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    /// <summary>打开指定路径（最近列表 / 拖放 / 恢复会话都走这里；重复路径直接激活已有标签）。</summary>
    public RelayCommand OpenPathCommand { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand CloseAllCommand { get; }

    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand SelectAllCommand { get; }

    public RelayCommand FindCommand { get; }
    public RelayCommand ReplaceCommand { get; }

    /// <summary>放大 / 缩小 / 重置缩放（只缩编辑区文字，Zed 风格）。</summary>
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetZoomCommand { get; }

    /// <summary>开 / 关偏好设置面板。</summary>
    public RelayCommand ShowSettingsCommand { get; }

    /// <summary>收起偏好设置面板（面板标题栏的 × / Esc）。</summary>
    public RelayCommand CloseSettingsCommand { get; }

    // ---------------- 文件操作（View / 自检可调） ----------------

    /// <summary>弹系统对话框选文件打开（支持多选）。</summary>
    internal void OpenFilesByDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开文件",
            Multiselect = true,
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var file in dialog.FileNames) OpenPath(file);
    }

    /// <summary>打开一个文件；已是标签只激活；读失败进状态栏并从最近列表摘掉。</summary>
    internal void OpenPath(string? path, bool activate = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex)
        {
            // 最近列表 / 拖放可能传来非法路径（含奇怪字符、超长、空盘符…）：只提示，不崩
            StatusText = $"路径无效：{ex.Message}";
            return;
        }

        var existing = Tabs.FirstOrDefault(t =>
            t.FilePath is not null && string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (activate || ActiveTab is null) ActiveTab = existing;
            return;
        }

        var content = TextFileCodec.TryRead(full, out var error);
        if (content is null)
        {
            StatusText = error ?? "打开失败";
            Cfg.RecentFiles = RecentFileList.Remove(Cfg.RecentFiles, full);
            OnPropertyChanged(nameof(RecentItems));
            return;
        }

        var tab = EditorTab.FromFile(full, content);
        HookTab(tab);
        Tabs.Add(tab);
        if (activate || ActiveTab is null) ActiveTab = tab;

        Cfg.RecentFiles = RecentFileList.Push(Cfg.RecentFiles, full);
        OnPropertyChanged(nameof(RecentItems));
        RefreshSessionConfig();
        StatusText = $"已打开 {Path.GetFileName(full)}";
    }

    /// <summary>保存（无路径走另存为）。返回是否真的保存成功（用户取消另存为 = false）。</summary>
    internal bool SaveTab(EditorTab? tab)
    {
        if (tab is null) return false;
        if (tab.FilePath is null) return SaveTabAs(tab);

        if (!TextFileCodec.TryWrite(tab.FilePath, tab.Document.Text, tab.Encoding, tab.Newline, out var error))
        {
            StatusText = error ?? "保存失败";
            return false;
        }

        tab.MarkSaved(tab.FilePath, tab.Encoding, tab.Newline);
        Host?.UpdateTabPath(tab);
        Cfg.RecentFiles = RecentFileList.Push(Cfg.RecentFiles, tab.FilePath);
        OnPropertyChanged(nameof(RecentItems));
        OnPropertyChanged(nameof(EncodingText));
        RefreshSessionConfig();
        StatusText = $"已保存 {Path.GetFileName(tab.FilePath)}";
        return true;
    }

    /// <summary>另存为（弹对话框选位置；编码 / 换行沿用当前标签的）。</summary>
    internal bool SaveTabAs(EditorTab? tab)
    {
        if (tab is null) return false;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            FileName = tab.Title,
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return false;

        if (!TextFileCodec.TryWrite(dialog.FileName, tab.Document.Text, tab.Encoding, tab.Newline, out var error))
        {
            StatusText = error ?? "保存失败";
            return false;
        }

        tab.MarkSaved(dialog.FileName, tab.Encoding, tab.Newline);
        Host?.UpdateTabPath(tab);
        Cfg.RecentFiles = RecentFileList.Push(Cfg.RecentFiles, dialog.FileName);
        OnPropertyChanged(nameof(RecentItems));
        OnPropertyChanged(nameof(EncodingText));
        RefreshSessionConfig();
        StatusText = $"已保存 {Path.GetFileName(dialog.FileName)}";
        return true;
    }

    private void SaveAll()
    {
        var failed = 0;
        foreach (var tab in Tabs.Where(t => t.IsDirty).ToList())
        {
            if (!SaveTab(tab)) failed++;
        }
        StatusText = failed == 0 ? "已保存全部更改" : $"{failed} 个文件未能保存";
    }

    /// <summary>新建无标题标签并激活（标签全关过就重新从「无标题」开始编号）。</summary>
    internal EditorTab NewTab()
    {
        if (Tabs.Count == 0) _nextUntitledIndex = 0;
        var tab = EditorTab.Untitled(++_nextUntitledIndex);
        HookTab(tab);
        Tabs.Add(tab);
        ActiveTab = tab;
        RefreshSessionConfig();
        StatusText = "新建了无标题标签（Ctrl+S 保存到文件）";
        return tab;
    }

    /// <summary>关闭标签；有未保存更改先问（保存 / 不保存 / 取消）；关到 0 个补无标题标签。</summary>
    internal void CloseTab(EditorTab? tab)
    {
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (tab.IsDirty && !ConfirmDiscard(tab)) return;

        var activeIndex = ActiveTab is null ? index : Tabs.IndexOf(ActiveTab);
        var nextIndex = EditorTab.NextActiveAfterRemove(Tabs.Count, index, activeIndex < 0 ? index : activeIndex);
        Tabs.RemoveAt(index); // View 的 CollectionChanged 负责摘掉这个标签的编辑器控件

        if (nextIndex < 0 || Tabs.Count == 0)
        {
            NewTab();
            StatusText = "已关闭标签";
            return;
        }
        ActiveTab = Tabs[Math.Clamp(nextIndex, 0, Tabs.Count - 1)];
        RefreshSessionConfig();
    }

    /// <summary>全部关闭（每个未保存的都单独确认）。</summary>
    internal void CloseAll()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (tab.IsDirty && !ConfirmDiscard(tab))
            {
                RefreshSessionConfig();   // 取消 = 停止关剩下的；已关掉的标签要从会话里去掉
                return;
            }
            Tabs.Remove(tab);
        }
        NewTab();
    }

    private bool ConfirmDiscard(EditorTab tab)
    {
        var result = System.Windows.MessageBox.Show(
            $"「{tab.Title}」有未保存的更改，要保存吗？",
            "文本编辑器",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);
        return result switch
        {
            System.Windows.MessageBoxResult.Yes => SaveTab(tab),
            System.Windows.MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ---------------- 状态刷新（View 调） ----------------

    /// <summary>光标动了 / 文档变了：刷新状态栏（行:列 / 字符数 / 编码 / 路径）。</summary>
    internal void UpdateCaret(int line, int column)
    {
        CaretText = $"行 {line}, 列 {column}";
        CharCountText = $"{ActiveTab?.Document.TextLength ?? 0} 字符";
    }

    /// <summary>切标签 / 保存后：整条状态栏 + 命令可用性重算。</summary>
    internal void RefreshActiveStats()
    {
        CaretText = "行 1, 列 1";
        CharCountText = $"{ActiveTab?.Document.TextLength ?? 0} 字符";
        OnPropertyChanged(nameof(EncodingText));
        OnPropertyChanged(nameof(ActivePathText));
    }

    // ---------------- 会话（恢复 / 保存） ----------------

    /// <summary>构造时按配置恢复上次的标签（只恢复有文件路径且文件还存在的）。</summary>
    private void RestoreSession()
    {
        var session = Cfg.RememberSession ? RecentFileList.SanitizeSession(Cfg.SessionFiles) : new List<string>();
        // ⚠ 先把「上次激活的序号」抢下来：下面 OpenPath → RefreshSessionConfig 会按「当前激活标签」
        // 覆盖 Cfg.SessionActiveIndex，等恢复完再读就已经变成 0 了（切回来的标签永远变回第一个）。
        var savedActiveIndex = Cfg.SessionActiveIndex;

        foreach (var path in session)
        {
            if (File.Exists(path)) OpenPath(path, activate: false);
        }

        if (Tabs.Count == 0)
        {
            NewTab();
            return;
        }

        ActiveTab = Tabs[TextEditorConfig.ClampSessionIndex(savedActiveIndex, Tabs.Count)];
        StatusText = $"已恢复 {Tabs.Count} 个标签";
    }

    /// <summary>把当前标签列表写回配置（切走 / 退出时都会走到；只记有文件路径的）。</summary>
    private void SaveSession() => RefreshSessionConfig();

    /// <summary>
    /// 把「打开着的标签 + 激活项」立刻写进配置（只记有文件路径的标签；无标题草稿靠关闭确认兜底）。
    /// <para>⭐ 不能只在 <see cref="OnClosed"/> 里写：外壳切走工具时只调 <c>ConfigStore.Flush()</c>、
    /// 根本不调 NotifyClosed（视图进缓存，切回来接着用），而且 NotifyClosed 也是「先 Flush 再 OnClosed」。
    /// 所以标签增删 / 切换 / 保存后都要刷新一次，让后台自动保存（400ms 合并窗口）把它落盘。</para>
    /// </summary>
    private void RefreshSessionConfig()
    {
        var files = Tabs
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => t.FilePath!.Trim())
            .ToList();
        var sanitized = RecentFileList.SanitizeSession(files);
        Cfg.SessionFiles = sanitized;

        var activePath = ActiveTab?.FilePath;
        var activeIndex = activePath is null
            ? 0
            : sanitized.FindIndex(p => string.Equals(p, activePath, StringComparison.OrdinalIgnoreCase));
        Cfg.SessionActiveIndex = TextEditorConfig.ClampSessionIndex(activeIndex < 0 ? 0 : activeIndex, sanitized.Count);
    }

    /// <summary>工具被真正关闭（缓存淘汰 / 重建配置 / 退出）：记下会话并立刻落盘，摘掉视图引用。</summary>
    protected override void OnClosed()
    {
        SaveSession();
        Cfg.SaveNow();   // OnClosed 在 NotifyClosed 的 Flush 之后才调，必须自己补一次落盘
        Host = null;
    }

    // ---------------- 实现 ----------------

    /// <summary>监听标签：激活标签的脏标记 / 内容变化刷新命令与状态栏。</summary>
    private void HookTab(EditorTab tab)
    {
        tab.TextChanged += (_, _) =>
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                CharCountText = $"{tab.Document.TextLength} 字符";
                RefreshCommandState();
            }
        };
        tab.PropertyChanged += (_, e) =>
        {
            if (!ReferenceEquals(tab, ActiveTab)) return;
            if (e.PropertyName is nameof(EditorTab.IsDirty) or nameof(EditorTab.Header))
                RefreshCommandState();

            // 另存为 / 保存会让标题 / 路径变：Markdown 预览要不要显示跟着重算
            if (e.PropertyName is nameof(EditorTab.Header) or nameof(EditorTab.FilePathText))
            {
                OnPropertyChanged(nameof(ActiveIsMarkdown));
                OnPropertyChanged(nameof(IsPreviewVisible));
            }
        };
    }

    /// <summary>菜单可用性依赖 Host / 文档状态，RelayCommand 走 CommandManager 重查询。</summary>
    private static void RefreshCommandState() => CommandManager.InvalidateRequerySuggested();
}
