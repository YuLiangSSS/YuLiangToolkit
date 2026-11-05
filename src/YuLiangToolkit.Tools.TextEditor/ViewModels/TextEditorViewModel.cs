using System.Collections.ObjectModel;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Tools.TextEditor.Core;
using YuLiangToolkit.Tools.TextEditor.Core.Terminal;
using YuLiangToolkit.Tools.TextEditor.Core.Workspace;

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
public sealed class TextEditorViewModel : ToolViewModelBase, IFileLaunchTarget
{
    private EditorTab? _activeTab;
    private bool _isSettingsVisible;
    private int _nextUntitledIndex;
    private string _caretText = "行 1, 列 1";
    private string _charCountText = "0 字符";

    /// <summary>等着自动保存的标签（View 的防抖计时器到点后统一写盘）。</summary>
    private readonly HashSet<EditorTab> _pendingAutoSave = new();

    /// <summary>已经提示过「无标题标签要先 Ctrl+S」的标签（同一个只提示一次，不刷屏）。</summary>
    private readonly HashSet<EditorTab> _autoSaveHintedTabs = new();

    /// <summary>自动保存正在写盘（防止计时器 / 关工具兜底重入）。</summary>
    private bool _autoSaveBusy;

    private FileTreeNode? _workspaceRoot;
    private ConsoleTabViewModel? _activeConsole;
    private int _cmdConsoleSeq;
    private int _powerShellConsoleSeq;

    public TextEditorViewModel() : base(TextEditorConfig.ToolId)
    {
        Cfg = LoadConfig<TextEditorConfig>();
        // 写坏回默认：字号 / 缩放 / 缩进 / 字体链
        Cfg.FontSize = TextEditorConfig.ClampFontSize(Cfg.FontSize);
        Cfg.ZoomPercent = TextEditorConfig.ClampZoomPercent(Cfg.ZoomPercent);
        Cfg.IndentSize = TextEditorConfig.ClampIndentSize(Cfg.IndentSize);

        // 标签集合先建好：下面给 Cfg 挂的 PropertyChanged 回调里会读 Tabs（关/开自动保存时补队列）
        Tabs = new ObservableCollection<EditorTab>();

        // 「Markdown 预览」开关一变，预览窗显隐跟着变（View 订阅 IsPreviewVisible 重排）
        Cfg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextEditorConfig.ShowMarkdownPreview))
            {
                OnPropertyChanged(nameof(IsPreviewVisible));
            }
            else if (e.PropertyName == nameof(TextEditorConfig.AutoSave))
            {
                // 关掉自动保存：清空待写队列（已经改脏的标签保留脏标记，Ctrl+S 手动保存）
                if (!Cfg.AutoSave)
                {
                    _pendingAutoSave.Clear();
                }
                else
                {
                    // 打开自动保存：把当前已经改脏、有路径的标签补进队列，View 会启动计时器
                    // （「打开方式」打开的标签带 AutoSaveSuppressed，永远不进自动保存队列）
                    foreach (var tab in Tabs.Where(t => t.IsDirty && t.FilePath is not null && !t.AutoSaveSuppressed))
                        _pendingAutoSave.Add(tab);
                }
            }
            else if (e.PropertyName == nameof(TextEditorConfig.ShowWorkspaceSidebar))
            {
                OnPropertyChanged(nameof(IsSidebarVisible));
            }
            else if (e.PropertyName == nameof(TextEditorConfig.ShowTerminalPanel))
            {
                OnPropertyChanged(nameof(IsTerminalVisible));
            }
        };

        // 控制台标签集合：侧栏/面板的显隐与命令可用性都跟着它走
        Consoles = new ObservableCollection<ConsoleTabViewModel>();

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

        // 工作区（左侧文件浏览器）
        OpenFolderCommand = new RelayCommand(_ => OpenFolderByDialog());
        CloseFolderCommand = new RelayCommand(_ => CloseFolder(), _ => HasWorkspace);
        RefreshFolderCommand = new RelayCommand(_ => RefreshFolder(), _ => HasWorkspace);
        ToggleSidebarCommand = new RelayCommand(
            _ => { if (HasWorkspace) Cfg.ShowWorkspaceSidebar = !Cfg.ShowWorkspaceSidebar; });

        // 底部控制台（CMD / PowerShell 标签页）
        ToggleTerminalCommand = new RelayCommand(_ => ToggleTerminal());
        NewCmdConsoleCommand = new RelayCommand(_ => NewConsole(ConsoleShellKind.Cmd));
        NewPowerShellConsoleCommand = new RelayCommand(_ => NewConsole(ConsoleShellKind.PowerShell));
        CloseConsoleCommand = new RelayCommand(_ => CloseConsole(ActiveConsole), _ => ActiveConsole is not null);
        ClearConsoleCommand = new RelayCommand(_ => ActiveConsole?.Clear(), _ => ActiveConsole is not null);
        StopConsoleCommand = new RelayCommand(_ => StopConsole(), _ => ActiveConsole is { IsRunning: true });
        RunConsoleCommand = new RelayCommand(_ => _ = RunConsoleAsync(), _ => ActiveConsole is { IsRunning: false });

        RestoreSession();
        RestoreWorkspace();

        // 上次关工具时控制台面板是开着的：恢复时先补一个 CMD 控制台（标签页不持久化）
        if (Cfg.ShowTerminalPanel && Consoles.Count == 0)
            NewConsole(ConsoleShellKind.Cmd);
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

    // ---------------- 工作区（打开文件夹 → 左侧文件浏览器） ----------------

    /// <summary>打开文件夹（弹系统文件夹选择框）。</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>关闭当前文件夹（文件浏览器整体收起）。</summary>
    public RelayCommand CloseFolderCommand { get; }

    /// <summary>重新读一遍文件夹目录树（外部新增 / 删除文件后手动刷新）。</summary>
    public RelayCommand RefreshFolderCommand { get; }

    /// <summary>Ctrl+B：有文件夹时展开 / 收起左侧文件浏览器。</summary>
    public RelayCommand ToggleSidebarCommand { get; }

    /// <summary>显示 / 隐藏底部控制台（打开时若没有控制台会建一个 CMD）。</summary>
    public RelayCommand ToggleTerminalCommand { get; }

    /// <summary>新建一个 CMD 控制台标签页。</summary>
    public RelayCommand NewCmdConsoleCommand { get; }

    /// <summary>新建一个 PowerShell 控制台标签页。</summary>
    public RelayCommand NewPowerShellConsoleCommand { get; }

    /// <summary>关闭当前控制台标签页。</summary>
    public RelayCommand CloseConsoleCommand { get; }

    /// <summary>清空当前控制台输出。</summary>
    public RelayCommand ClearConsoleCommand { get; }

    /// <summary>停止当前控制台里正在跑的命令。</summary>
    public RelayCommand StopConsoleCommand { get; }

    /// <summary>运行输入框里的命令（回车 / 点「运行」都会走它）。</summary>
    public RelayCommand RunConsoleCommand { get; }

    /// <summary>文件浏览器的根节点（<c>null</c> = 没打开文件夹，整栏不显示）。</summary>
    public FileTreeNode? WorkspaceRoot
    {
        get => _workspaceRoot;
        private set
        {
            if (!Set(ref _workspaceRoot, value)) return;
            OnPropertyChanged(nameof(HasWorkspace));
            OnPropertyChanged(nameof(WorkspaceName));
            OnPropertyChanged(nameof(IsSidebarVisible));
            RefreshCommandState();
        }
    }

    /// <summary>是否打开了文件夹。</summary>
    public bool HasWorkspace => _workspaceRoot is not null;

    /// <summary>文件夹显示名（根节点名字；没打开时为空）。</summary>
    public string WorkspaceName => _workspaceRoot?.Name ?? "";

    /// <summary>左侧文件浏览器当前是否显示：有文件夹且用户没把它收起来。</summary>
    public bool IsSidebarVisible => HasWorkspace && Cfg.ShowWorkspaceSidebar;

    /// <summary>弹系统文件夹选择框并打开。</summary>
    internal void OpenFolderByDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "打开文件夹（作为编辑器工作区）",
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true) OpenFolder(dialog.FolderName);
    }

    /// <summary>打开一个文件夹作为工作区（会记进配置，下次打开工具自动恢复）；失败只写状态栏。</summary>
    internal bool OpenFolder(string? path)
    {
        var root = WorkspaceTree.TryCreateRoot(path);
        if (root is null)
        {
            StatusText = "打开文件夹失败：目录不存在或没有权限";
            return false;
        }

        WorkspaceRoot = root;
        Cfg.LastFolder = root.FullPath;
        Cfg.ShowWorkspaceSidebar = true;
        StatusText = $"已打开文件夹：{root.Name}";
        Log($"打开文件夹：{root.FullPath}");
        return true;
    }

    /// <summary>关闭工作区文件夹（文件浏览器消失；已经打开的标签不受影响）。</summary>
    internal void CloseFolder()
    {
        if (!HasWorkspace) return;
        WorkspaceRoot = null;
        Cfg.LastFolder = "";
        StatusText = "已关闭文件夹";
    }

    /// <summary>刷新文件浏览器（重新建根节点，展开状态会重置）。</summary>
    internal void RefreshFolder()
    {
        var path = _workspaceRoot?.FullPath ?? Cfg.LastFolder;
        if (string.IsNullOrWhiteSpace(path)) return;

        var root = WorkspaceTree.TryCreateRoot(path);
        if (root is null)
        {
            StatusText = "刷新失败：文件夹可能已被删除 / 移到别处";
            return;
        }
        WorkspaceRoot = root;
        StatusText = "已刷新文件浏览器";
    }

    /// <summary>启动时恢复上次打开的文件夹（文件夹已不存在就静默清掉记录）。</summary>
    private void RestoreWorkspace()
    {
        if (string.IsNullOrWhiteSpace(Cfg.LastFolder)) return;

        var root = WorkspaceTree.TryCreateRoot(Cfg.LastFolder);
        if (root is null)
        {
            Cfg.LastFolder = "";
            return;
        }
        WorkspaceRoot = root;
    }

    /// <summary>新控制台默认工作目录：有工作区用工作区，否则用当前用户目录。</summary>
    public string DefaultConsoleDirectory
        => HasWorkspace ? WorkspaceRoot!.FullPath : ConsoleCommandLine.NormalizeWorkingDirectory(null);

    // ---------------- 底部控制台（CMD / PowerShell 标签页） ----------------

    /// <summary>打开着的控制台标签页（顺序即右侧标签列表顺序）。</summary>
    public ObservableCollection<ConsoleTabViewModel> Consoles { get; }

    /// <summary>当前激活的控制台。</summary>
    public ConsoleTabViewModel? ActiveConsole
    {
        get => _activeConsole;
        set
        {
            if (!Set(ref _activeConsole, value)) return;
            OnPropertyChanged(nameof(HasConsoles));
            RefreshCommandState();
        }
    }

    /// <summary>是否有控制台标签页。</summary>
    public bool HasConsoles => Consoles.Count > 0;

    /// <summary>底部控制台面板当前是否显示。</summary>
    public bool IsTerminalVisible => Cfg.ShowTerminalPanel;

    /// <summary>「终端」菜单 / 工具栏：显示或隐藏底部控制台（打开时若没有控制台就建一个 CMD）。</summary>
    internal void ToggleTerminal()
    {
        if (Cfg.ShowTerminalPanel)
        {
            Cfg.ShowTerminalPanel = false;
            return;
        }

        Cfg.ShowTerminalPanel = true;
        if (Consoles.Count == 0) NewConsole(ConsoleShellKind.Cmd);
    }

    /// <summary>新建一个控制台标签页并激活（默认面板会展开）。</summary>
    internal ConsoleTabViewModel NewConsole(ConsoleShellKind kind, string? workingDirectory = null)
    {
        var index = kind == ConsoleShellKind.Cmd ? ++_cmdConsoleSeq : ++_powerShellConsoleSeq;
        var console = new ConsoleTabViewModel(kind, index, workingDirectory ?? DefaultConsoleDirectory);

        Consoles.Add(console);
        OnPropertyChanged(nameof(HasConsoles));
        ActiveConsole = console;
        Cfg.ShowTerminalPanel = true;
        RefreshCommandState();
        StatusText = $"已新建 {console.Title} 控制台（工作目录 {console.WorkingDirectory}）";
        return console;
    }

    /// <summary>关闭一个控制台标签页（正在跑的命令先停掉；关到 0 个时面板自动收起）。</summary>
    internal void CloseConsole(ConsoleTabViewModel? console)
    {
        if (console is null) return;
        var index = Consoles.IndexOf(console);
        if (index < 0) return;

        try { console.Cancellation?.Cancel(); } catch { /* 已经结束 */ }
        Consoles.RemoveAt(index);
        OnPropertyChanged(nameof(HasConsoles));

        if (Consoles.Count == 0)
        {
            ActiveConsole = null;
            Cfg.ShowTerminalPanel = false;
            StatusText = "已关闭最后一个控制台";
        }
        else
        {
            ActiveConsole = Consoles[Math.Clamp(index, 0, Consoles.Count - 1)];
        }
        RefreshCommandState();
    }

    /// <summary>停止激活控制台正在跑的命令（杀进程树）。</summary>
    internal void StopConsole()
    {
        try { ActiveConsole?.Cancellation?.Cancel(); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 在激活控制台里执行一条命令（参数为空时取输入框里的内容）。
    /// <c>cls</c>/<c>clear</c> 清屏、<c>cd</c> 直接换工作目录（不启动进程），其它交给 CMD / PowerShell 跑。
    /// </summary>
    internal async Task RunConsoleAsync(string? command = null)
    {
        var console = ActiveConsole;
        if (console is null) return;

        var trimmed = (command ?? console.Input).Trim();
        if (trimmed.Length == 0) return;
        if (console.IsRunning)
        {
            StatusText = "控制台还有命令在跑，先点「停止」或等它结束";
            return;
        }

        if (ConsoleCommandLine.IsClearCommand(trimmed))
        {
            console.Clear();
            console.Input = "";
            console.ResetHistoryCursor();
            return;
        }

        console.RecordHistory(trimmed);
        console.Append($"{console.Prompt} {trimmed}{Environment.NewLine}");
        console.Input = "";
        console.ResetHistoryCursor();

        if (ConsoleCommandLine.TryResolveChangeDirectory(trimmed, console.WorkingDirectory, out var directory))
        {
            console.WorkingDirectory = directory;
            console.Append($"{console.Prompt}{Environment.NewLine}");
            StatusText = $"控制台工作目录：{directory}";
            return;
        }

        var cancellation = new CancellationTokenSource();
        console.Cancellation = cancellation;
        console.IsRunning = true;
        RefreshCommandState();

        try
        {
            var exitCode = await ConsoleProcessRunner.RunAsync(
                console.Kind,
                trimmed,
                console.WorkingDirectory,
                line => Ui(() => console.Append(line + Environment.NewLine)),
                cancellation.Token);

            if (cancellation.IsCancellationRequested)
            {
                console.Append($"[已停止]{Environment.NewLine}");
                StatusText = "控制台命令已停止";
            }
            else
            {
                console.Append($"[退出码 {exitCode}]{Environment.NewLine}");
                StatusText = $"控制台命令结束（退出码 {exitCode}）";
            }
        }
        catch (Exception ex)
        {
            console.Append($"[启动失败] {ex.Message}{Environment.NewLine}");
            StatusText = "控制台命令启动失败：" + ex.Message;
            Log($"控制台命令失败：{trimmed} → {ex.Message}");
        }
        finally
        {
            console.IsRunning = false;
            console.Cancellation = null;
            cancellation.Dispose();
            RefreshCommandState();
        }
    }

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

    /// <summary>
    /// 「用文本方式打开」（Windows「打开方式」/ 主程序 <see cref="IFileLaunchTarget"/> 调用）：
    /// 复用 <see cref="OpenPath"/> 打开文件，然后给这个标签置 <see cref="EditorTab.AutoSaveSuppressed"/>，
    /// 让自动保存永远不碰它（文件可能是 .exe / .zip 等二进制，静默写回会把它写坏）。
    /// <para>容错约定：路径非法 / 文件不存在 / 读取失败都只写状态栏与日志，绝不抛给主程序；
    /// 同一路径已开过标签时沿用 <see cref="OpenPath"/> 的「激活已有标签」行为。</para>
    /// </summary>
    public void OpenLaunchedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "打开方式：没有收到文件路径";
            Log("✗ 打开方式：没有收到文件路径");
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            StatusText = $"打开方式：路径无效（{ex.Message}）";
            Log($"✗ 打开方式：路径无效（{ex.Message}）");
            return;
        }

        if (!File.Exists(full))
        {
            StatusText = "打开方式：文件不存在或已被删除";
            Log($"✗ 打开方式：文件不存在 {full}");
            return;
        }

        try
        {
            OpenPath(full);
        }
        catch (Exception ex)
        {
            // 契约要求：读文件失败只提示，绝不把异常抛回主程序（二进制文件解码等意外都在这里兜住）
            StatusText = $"打开方式：打开失败（{ex.Message}）";
            Log($"✗ 打开方式：打开失败（{ex.Message}）");
            return;
        }

        // OpenPath 读失败时不会建标签（状态栏已给提示），这里只处理成功建 / 复用标签的情况
        var tab = Tabs.FirstOrDefault(t =>
            t.FilePath is not null && string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (tab is null) return;

        tab.AutoSaveSuppressed = true;
        _pendingAutoSave.Remove(tab);   // 万一这个标签之前已排进自动保存队列（比如本来就开着），立刻摘掉

        StatusText = $"以文本方式打开 {tab.Title}：自动保存已暂停（Ctrl+S 仍可手动保存）";
        Log($"以文本方式打开：{full}（自动保存已暂停，Ctrl+S 仍可手动保存）");
    }

    /// <summary>保存（无路径走另存为）。返回是否真的保存成功（用户取消另存为 = false）。</summary>
    internal bool SaveTab(EditorTab? tab)
    {
        if (tab is null) return false;
        if (tab.FilePath is null) return SaveTabAs(tab);

        if (!WriteTab(tab, tab.FilePath, updateRecent: true)) return false;
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

        if (!WriteTab(tab, dialog.FileName, updateRecent: true)) return false;
        StatusText = $"已保存 {Path.GetFileName(dialog.FileName)}";
        return true;
    }

    /// <summary>
    /// 把标签内容写到指定路径（成功 = 清脏标记 + 记录新路径 / 编码 / 换行 / 文件修改时间 + 刷新语法高亮 / 会话）。
    /// <paramref name="updateRecent"/> = false 时不动「最近文件」列表（自动保存不反复把它顶到最前）。
    /// </summary>
    private bool WriteTab(EditorTab tab, string path, bool updateRecent)
    {
        if (!TextFileCodec.TryWrite(path, tab.Document.Text, tab.Encoding, tab.Newline, out var error))
        {
            StatusText = error ?? "保存失败";
            return false;
        }

        _pendingAutoSave.Remove(tab);
        tab.MarkSaved(path, tab.Encoding, tab.Newline);
        Host?.UpdateTabPath(tab);

        if (updateRecent)
        {
            Cfg.RecentFiles = RecentFileList.Push(Cfg.RecentFiles, path);
            OnPropertyChanged(nameof(RecentItems));
        }

        OnPropertyChanged(nameof(EncodingText));
        RefreshSessionConfig();
        return true;
    }

    /// <summary>
    /// 把排队等自动保存的标签立刻写回原文件（View 的防抖计时器到点 / 关工具前兜底 / 自检调用）。
    /// 返回真正写成功的标签数。
    /// <para>只写「有文件路径 + 脏 + 没被外部改过」的标签；无标题标签没路径，等用户 Ctrl+S 选一次位置。</para>
    /// </summary>
    internal int AutoSavePendingTabs()
    {
        if (_pendingAutoSave.Count == 0 || !Cfg.AutoSave)
        {
            _pendingAutoSave.Clear();
            return 0;
        }
        if (_autoSaveBusy) return 0;

        _autoSaveBusy = true;
        var savedTabs = new List<EditorTab>();
        var failed = 0;
        var blocked = false;
        try
        {
            foreach (var tab in _pendingAutoSave.ToList())
            {
                _pendingAutoSave.Remove(tab);
                if (!Tabs.Contains(tab) || !tab.IsDirty || tab.FilePath is null
                    || tab.AutoSaveBlocked || tab.AutoSaveSuppressed) continue;

                if (IsExternallyModified(tab))
                {
                    // 文件被别的程序改过：先不覆盖，避免把外部改动冲掉
                    tab.AutoSaveBlocked = true;
                    blocked = true;
                    continue;
                }

                if (WriteTab(tab, tab.FilePath, updateRecent: false)) savedTabs.Add(tab);
                else failed++;
            }
        }
        finally
        {
            _autoSaveBusy = false;
        }

        if (savedTabs.Count > 0)
        {
            StatusText = savedTabs.Count == 1
                ? $"已自动保存 {savedTabs[0].Title}"
                : $"已自动保存 {savedTabs.Count} 个文件";
        }
        else if (blocked)
        {
            StatusText = "检测到文件已被外部修改，自动保存已暂停（Ctrl+S 手动保存会覆盖）";
        }
        else if (failed > 0)
        {
            StatusText = "自动保存失败：文件可能被占用或只读（Ctrl+S 可重试）";
        }

        return savedTabs.Count;
    }

    /// <summary>文档变了就排进自动保存队列（开关关着 / 不脏 / 无标题标签在这里过滤）。</summary>
    private void QueueAutoSave(EditorTab tab)
    {
        if (!Cfg.AutoSave || !tab.IsDirty) return;

        // 「打开方式」打开的标签：只允许手动保存，绝不进自动保存队列
        if (tab.AutoSaveSuppressed) return;

        if (tab.FilePath is null)
        {
            if (_autoSaveHintedTabs.Add(tab))
                StatusText = "无标题标签没有文件路径：Ctrl+S 选一次位置后，改动就会自动保存";
            return;
        }

        if (tab.AutoSaveBlocked) return;
        _pendingAutoSave.Add(tab);
    }

    /// <summary>文件是否在「我们上次读 / 写之后」被外部程序改过（时间戳比较；算不出来就当没改）。</summary>
    private static bool IsExternallyModified(EditorTab tab)
    {
        if (tab.FilePath is not { } path || tab.LastKnownWriteTimeUtc is not { } known) return false;

        try
        {
            if (!File.Exists(path)) return false;
            return File.GetLastWriteTimeUtc(path) != known;
        }
        catch
        {
            return false;
        }
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
        ForgetAutoSaveState(tab);

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
            ForgetAutoSaveState(tab);
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

    /// <summary>工具被真正关闭（缓存淘汰 / 重建配置 / 退出）：先把自动保存队列落盘，再记会话并落盘。</summary>
    protected override void OnClosed()
    {
        // 切走 / 退出前把「还没到防抖时间」的自动保存队列补写掉，避免改动只留在内存里
        if (Cfg.AutoSave) AutoSavePendingTabs();

        // 正在跑的控制台命令先停掉（工具视图可能被缓存，但进程不该在后台继续跑）
        foreach (var console in Consoles)
        {
            try { console.Cancellation?.Cancel(); }
            catch { /* 忽略 */ }
        }

        SaveSession();
        Cfg.SaveNow();   // OnClosed 在 NotifyClosed 的 Flush 之后才调，必须自己补一次落盘
        Host = null;
    }

    /// <summary>标签关掉后忘掉它的自动保存排队 / 提示状态（避免集合里留着已关闭标签）。</summary>
    private void ForgetAutoSaveState(EditorTab tab)
    {
        _pendingAutoSave.Remove(tab);
        _autoSaveHintedTabs.Remove(tab);
    }

    // ---------------- 实现 ----------------

    /// <summary>监听标签：激活标签的脏标记 / 内容变化刷新命令与状态栏。</summary>
    private void HookTab(EditorTab tab)
    {
        tab.TextChanged += (_, _) =>
        {
            QueueAutoSave(tab);
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
