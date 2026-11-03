using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Indentation;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.TextEditor.Core;
using YuLiangToolkit.Tools.TextEditor.Core.Markdown;
using YuLiangToolkit.Tools.TextEditor.ViewModels;

// 本项目根命名空间就叫 YuLiangToolkit.Tools.TextEditor，会遮住 AvalonEdit 的 TextEditor 类型，起别名用
using AvalonEditor = ICSharpCode.AvalonEdit.TextEditor;
// System.Windows.Controls 里也有个同名 TextSearch（附加属性），同样用别名固定指到自己的纯逻辑类
using TextSearch = YuLiangToolkit.Tools.TextEditor.Core.TextSearch;

namespace YuLiangToolkit.Tools.TextEditor.Views;

/// <summary>
/// 「文本编辑器」界面：<b>多标签</b>——每个标签一个 AvalonEdit <see cref="TextEditor"/> 实例，
/// 全部叠在 <c>EditorHost</c> 里、只把激活标签的设为 Visible（各标签的撤销栈 / 光标 / 滚动互不影响；
/// 编辑器是纯 WPF 控件不是原生窗口，切走时不必销毁，回来直接接着用）。
///
/// <para>本类还负责：把配置（字体 / 字号 × 缩放 / 颜色 / 行号 / 自动换行 / 缩进）刷到所有编辑器、
/// 宿主窗口快捷键（Ctrl+N/O/S/W/F/H、Ctrl+±0、F3、Esc）、Ctrl+滚轮缩放、文件拖放、
/// 查找替换条（在激活文档上环绕查找）。</para>
/// </summary>
public partial class TextEditorView : UserControl, IEditorHost
{
    /// <summary>每个标签的编辑器控件（只有激活标签 Visible，其余 Collapsed）。</summary>
    private readonly Dictionary<EditorTab, AvalonEditor> _editors = new();

    /// <summary>选择区颜色：主题蓝加透明（文字还要看得见）。</summary>
    private static readonly Color SelectionColor = Color.FromArgb(0x50, 0x4C, 0x8D, 0xFF);

    /// <summary>「偏好设置」面板的默认 / 最小 / 最大宽度（像素；拖过的实际宽度由 PaneSplitter 记在 Config\layout.json）。</summary>
    private const double SettingsPanelDefaultWidth = 304;
    private const double SettingsPanelMinWidth = 260;
    private const double SettingsPanelMaxWidth = 640;

    /// <summary>面板列宽的布局记忆键（PaneSplitter 对「一侧固定像素」的布局用 <c>.px</c> 存那一侧宽度）。</summary>
    private static readonly string SettingsPaneKey = TextEditorConfig.ToolId + ".偏好设置与编辑区";

    /// <summary>Markdown 预览高度的默认 / 最小 / 最大像素（拖过的实际高度由 PaneSplitter 记在 Config\layout.json）。</summary>
    private const double PreviewDefaultHeight = 360;
    private const double PreviewMinHeight = 160;
    private const double PreviewMaxHeight = 900;

    /// <summary>超过这么多字符就暂停实时预览（防止超大文件每次敲键都全量解析）。</summary>
    private const int PreviewMaxChars = 400_000;

    /// <summary>预览高度 / 分隔条的布局记忆键。</summary>
    private static readonly string PreviewPaneKey = TextEditorConfig.ToolId + ".Markdown预览与编辑区";

    private Window? _hostWindow;
    private bool _vmWired;
    private DispatcherTimer? _previewTimer;
    private string? _lastPreviewText;
    private string? _lastPreviewStyleKey;

    public TextEditorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private TextEditorViewModel? Vm => DataContext as TextEditorViewModel;

    private TextEditorConfig? Cfg => Vm?.Cfg;

    /// <summary>激活标签的编辑器（没创建过 = null）。</summary>
    private AvalonEditor? ActiveEditor
        => Vm?.ActiveTab is { } tab && _editors.TryGetValue(tab, out var editor) ? editor : null;

    // ---------- 视图加载 / 卸载 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        // 订阅只做一次（ViewModel / 配置 / 标签集合跟视图同生命周期）；
        // 但「Host 赋值 + 宿主窗口快捷键 + 标签可见性」**每次挂回都要重做**——
        // 外壳会缓存工具视图，切走时 Unloaded 已把快捷键摘掉，切回来必须重新装上。
        if (!_vmWired)
        {
            _vmWired = true;
            WireViewModel(vm);
        }

        vm.Host = this;

        // 快捷键挂到宿主 Window（PreviewKeyDown 隧道先于编辑器，Ctrl+S 等不会被吃掉）
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewKeyDown += OnHostPreviewKeyDown;
        }

        UpdateTabVisibility();
        ApplySettingsToAllEditors();
        ApplySettingsColumnWidth(vm.IsSettingsVisible);
        UpdatePreviewVisibility();
        ApplyScreenshotHooks();

        // 布局完成后把焦点交回编辑器（切到这个工具就能直接打字）
        Dispatcher.BeginInvoke(() => ActiveEditor?.Focus(), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 截图 / 调试钩子（只影响本次运行，不写配置）：
    /// <c>YLT_EDITOR_SETTINGS=1</c> 展开偏好设置面板；<c>YLT_EDITOR_FIND=文字</c> 展开查找条并填入内容。
    /// </summary>
    private void ApplyScreenshotHooks()
    {
        if (Vm is null) return;

        if (Environment.GetEnvironmentVariable("YLT_EDITOR_SETTINGS") == "1")
            Vm.IsSettingsVisible = true;

        var find = Environment.GetEnvironmentVariable("YLT_EDITOR_FIND");
        if (!string.IsNullOrEmpty(find))
        {
            FindBox.Text = find;
            ShowFindBarInternal(false);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow = null;
        }

        // 门面接口断开：视图被外壳缓存时编辑器命令 / 查找不可用，切回来时 OnLoaded 会重新接上
        if (Vm is { } vm) vm.Host = null;
        _previewTimer?.Stop();
        // 编辑器实例与 VM 都保留：外壳缓存视图，切回来接着用
    }

    private void WireViewModel(TextEditorViewModel vm)
    {
        vm.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Remove || e.OldItems is null) return;
            foreach (var removed in e.OldItems.OfType<EditorTab>())
            {
                if (!_editors.TryGetValue(removed, out var editor)) continue;
                editor.TextArea.Caret.PositionChanged -= OnActiveCaretMoved;
                removed.TextChanged -= OnTabTextChanged;
                EditorHost.Children.Remove(editor);
                _editors.Remove(removed);
            }
        };

        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(TextEditorViewModel.ActiveTab):
                    UpdateTabVisibility();
                    PushCaretInfo();
                    RefreshFindBarCount();
                    // 标签多到横向滚出视野时，把激活标签滚出来
                    if (vm.ActiveTab is not null) TabList.ScrollIntoView(vm.ActiveTab);
                    UpdatePreviewVisibility();   // 切到 .md 标签自动出预览，切走自动收起
                    break;
                case nameof(TextEditorViewModel.IsPreviewVisible):
                    UpdatePreviewVisibility();
                    break;
                case nameof(TextEditorViewModel.IsSettingsVisible):
                    // 列宽显隐由 ApplySettingsColumnWidth 管（304 / 0，拖过的宽度由 PaneSplitter 记忆）；
                    // 收起时顺手把焦点交回编辑器，免得用 Esc 关面板后按键落到已经藏起来的控件上
                    ApplySettingsColumnWidth(vm.IsSettingsVisible);
                    if (!vm.IsSettingsVisible && !FindBarOpen) ActiveEditor?.Focus();
                    break;
            }
        };

        // 配置任何一项变化（字号 / 缩放 / 颜色 / 行号 / 换行 / 缩进 / 字体）→ 全部编辑器重刷；
        // Markdown 预览的字体 / 颜色 / 缩放也跟着变，重排一次（预览窗开着才做）
        vm.Cfg.PropertyChanged += (_, e) =>
        {
            ApplySettingsToAllEditors();
            if (e.PropertyName == nameof(TextEditorConfig.ShowMarkdownPreview))
                UpdatePreviewVisibility();
            else if (vm.IsPreviewVisible)
                SchedulePreviewRefresh();
        };
    }

    // ---------- 标签 → 编辑器 ----------

    private void UpdateTabVisibility()
    {
        var vm = Vm;
        if (vm is null) return;
        if (vm.ActiveTab is not null) EnsureEditor(vm.ActiveTab);

        foreach (var (tab, editor) in _editors)
            editor.Visibility = ReferenceEquals(tab, vm.ActiveTab) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按需创建一个标签的编辑器（挂好文档 / 高亮 / 配置 / 事件）。</summary>
    private void EnsureEditor(EditorTab tab)
    {
        if (_editors.ContainsKey(tab) || Cfg is null) return;
        var cfg = Cfg;

        var editor = new AvalonEditor
        {
            Document = tab.Document,
            FontFamily = new FontFamily(TextEditorConfig.BuildFontFamily(cfg.FontName)),
            WordWrap = cfg.WordWrap,
            ShowLineNumbers = cfg.ShowLineNumbers,
            Padding = new Thickness(8, 6, 0, 0),
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Options =
            {
                ConvertTabsToSpaces = cfg.ConvertTabsToSpaces,
                IndentationSize = cfg.IndentSize,
                AllowScrollBelowDocument = true,
                // Alt+拖动 = 列选择（写表格 / 批量加前缀时好用）
                EnableRectangularSelection = true,
            },
        };
        // 回车自动沿用上一行的缩进（写代码的默认手感；AvalonEdit 不自带这个策略就会顶格）
        editor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
        editor.SyntaxHighlighting = EditorHighlighting.ForFile(tab.FilePath);
        ApplyColors(editor, cfg);
        editor.FontSize = EffectiveFontSize(cfg);

        editor.TextArea.Caret.PositionChanged += OnActiveCaretMoved;
        tab.TextChanged += OnTabTextChanged;

        EditorHost.Children.Add(editor);
        _editors[tab] = editor;
    }

    /// <summary>实际字号 = 基础字号 × 缩放（夹在能显示的范围里）。</summary>
    private static double EffectiveFontSize(TextEditorConfig cfg)
        => Math.Clamp(cfg.FontSize * cfg.ZoomPercent / 100.0, 4, 100);

    /// <summary>把背景 / 文字 / 行号 / 选区颜色刷到一个编辑器上（解析不了回默认深色）。</summary>
    private void ApplyColors(AvalonEditor editor, TextEditorConfig cfg)
    {
        if (!HexToBrushConverter.TryParseHex(cfg.BackgroundHex, out var background))
            _ = HexToBrushConverter.TryParseHex(TextEditorConfig.DefaultBackgroundHex, out background);
        if (!HexToBrushConverter.TryParseHex(cfg.ForegroundHex, out var foreground))
            _ = HexToBrushConverter.TryParseHex(TextEditorConfig.DefaultForegroundHex, out foreground);

        editor.Background = Frozen(background);
        editor.Foreground = Frozen(foreground);

        // 行号列：文字色降透明度（跟主题无关，随用户选的文字颜色走）
        editor.LineNumbersForeground = Frozen(Color.FromArgb(110, foreground.R, foreground.G, foreground.B));
        editor.TextArea.SelectionBrush = Frozen(SelectionColor);
        editor.TextArea.SelectionForeground = null;   // 选中时保持文字原色
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>配置变化（任何一项）→ 所有编辑器重刷外观。</summary>
    private void ApplySettingsToAllEditors()
    {
        if (Cfg is null) return;
        var cfg = Cfg;
        foreach (var editor in _editors.Values)
        {
            editor.FontFamily = new FontFamily(TextEditorConfig.BuildFontFamily(cfg.FontName));
            editor.FontSize = EffectiveFontSize(cfg);
            editor.WordWrap = cfg.WordWrap;
            editor.ShowLineNumbers = cfg.ShowLineNumbers;
            editor.Options.ConvertTabsToSpaces = cfg.ConvertTabsToSpaces;
            editor.Options.IndentationSize = cfg.IndentSize;
            ApplyColors(editor, cfg);
        }
    }

    // ---------- 光标 / 字数 / 查找计数联动 ----------

    private void OnActiveCaretMoved(object? sender, EventArgs e)
    {
        var vm = Vm;
        if (vm?.ActiveTab is null) return;
        // 只有激活编辑器的光标事件才推进状态栏（隐藏标签的编辑器不会动，保险起见再判一次）
        if (!_editors.TryGetValue(vm.ActiveTab, out var editor) || !ReferenceEquals(editor.TextArea, sender)) return;
        PushCaretInfo();
        CommandManager.InvalidateRequerySuggested();   // 撤销 / 重做菜单可用性
    }

    private void OnTabTextChanged(object? sender, EventArgs e)
    {
        var vm = Vm;
        if (vm is null || sender is not EditorTab tab || !ReferenceEquals(tab, vm.ActiveTab)) return;
        PushCaretInfo();
        RefreshFindBarCount();
        SchedulePreviewRefresh();
    }

    /// <summary>把激活编辑器的光标位置（行 / 列 / 字符数）推进状态栏。</summary>
    private void PushCaretInfo()
    {
        var vm = Vm;
        var editor = ActiveEditor;
        if (vm is null || editor is null) return;
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        vm.UpdateCaret(line.LineNumber, editor.CaretOffset - line.Offset + 1);
    }

    // ---------- 查找 / 替换条 ----------

    private bool FindBarOpen => FindBar.Visibility == Visibility.Visible;

    private string FindPattern => FindBox.Text;

    private bool MatchCase => FindCaseBox.IsChecked == true;

    private StringComparison StringComparisonMode
        => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void ShowFindBarInternal(bool withReplace)
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = withReplace ? Visibility.Visible : Visibility.Collapsed;
        if (withReplace)
        {
            ReplaceBox.Focus();
        }
        else
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }
        RefreshFindBarCount();
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ActiveEditor?.Focus();
    }

    private void OnFindContextChanged(object sender, RoutedEventArgs e) => RefreshFindBarCount();

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // 回车 = 下一个；Shift+回车 = 上一个
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FindPrevious();
        else FindNext();
        e.Handled = true;
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext();

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => FindPrevious();

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFindBar();

    private void OnReplaceToggleClick(object sender, RoutedEventArgs e)
    {
        ReplaceRow.Visibility = ReplaceRow.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ReplaceCurrent();
        e.Handled = true;
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void OnReplaceAllClick(object sender, RoutedEventArgs e) => ReplaceAll();

    /// <summary>查找条开着时刷新「n 个匹配」计数。</summary>
    private void RefreshFindBarCount()
    {
        if (!FindBarOpen) return;
        var editor = ActiveEditor;
        if (editor is null || string.IsNullOrEmpty(FindPattern))
        {
            FindCountText.Text = "";
            return;
        }
        var count = TextSearch.FindAll(editor.Document.Text, FindPattern, MatchCase).Count;
        FindCountText.Text = count == 0 ? "无结果" : $"{count} 个匹配";
    }

    /// <summary>从当前光标往后找（环绕）；光标正选中一个完整匹配时先跳过它。</summary>
    private void FindNext(int? fromOverride = null)
    {
        var editor = ActiveEditor;
        if (editor is null || string.IsNullOrEmpty(FindPattern))
        {
            FindCountText.Text = "无结果";
            return;
        }

        var from = fromOverride ?? editor.CaretOffset;
        if (fromOverride is null
            && editor.SelectionLength == FindPattern.Length && editor.SelectionStart == from)
            from += FindPattern.Length;

        var index = TextSearch.NextMatch(editor.Document.Text, FindPattern, MatchCase, from);
        if (index is null)
        {
            FindCountText.Text = "无结果";
            return;
        }
        SelectMatch(editor, index.Value);
    }

    /// <summary>从当前光标往前找（环绕）；光标正选中一个完整匹配时从它的起点往前。</summary>
    private void FindPrevious()
    {
        var editor = ActiveEditor;
        if (editor is null || string.IsNullOrEmpty(FindPattern))
        {
            FindCountText.Text = "无结果";
            return;
        }

        var from = editor.CaretOffset;
        if (editor.SelectionLength == FindPattern.Length
            && editor.SelectionStart + editor.SelectionLength == from)
            from = editor.SelectionStart;

        var index = TextSearch.PreviousMatch(editor.Document.Text, FindPattern, MatchCase, from);
        if (index is null)
        {
            FindCountText.Text = "无结果";
            return;
        }
        SelectMatch(editor, index.Value);
    }

    /// <summary>选中一个匹配并滚到可见处，顺带更新计数「i/n」。</summary>
    private void SelectMatch(AvalonEditor editor, int start)
    {
        editor.Select(start, FindPattern.Length);
        editor.TextArea.Caret.Offset = start + FindPattern.Length;
        editor.ScrollToLine(editor.Document.GetLineByOffset(start).LineNumber);

        var matches = TextSearch.FindAll(editor.Document.Text, FindPattern, MatchCase);
        var position = matches.IndexOf(start);
        FindCountText.Text = matches.Count == 0
            ? "无结果"
            : position < 0 ? $"{matches.Count} 个匹配" : $"{position + 1}/{matches.Count}";
    }

    /// <summary>
    /// 替换当前选中的匹配（如果选中的正是要找的），然后从替换结果之后继续找下一个
    /// （不是从替换文本开头找，避免「把 A 换成 A 一样的词」时原地打转）。
    /// </summary>
    private void ReplaceCurrent()
    {
        var editor = ActiveEditor;
        if (editor is null || string.IsNullOrEmpty(FindPattern)) return;

        int nextFrom;
        if (editor.SelectionLength == FindPattern.Length
            && string.Equals(editor.Document.GetText(editor.SelectionStart, editor.SelectionLength),
                FindPattern, StringComparisonMode))
        {
            var start = editor.SelectionStart;
            var replacement = ReplaceBox.Text ?? "";
            editor.Document.Replace(start, editor.SelectionLength, replacement);
            nextFrom = start + replacement.Length;
        }
        else
        {
            nextFrom = editor.CaretOffset;
        }
        FindNext(nextFrom);
    }

    /// <summary>
    /// 全部替换：倒序逐个 Replace（倒序替换的位置不受前一次改长度影响），整批包在一个撤销组里，
    /// 用户按一次 Ctrl+Z 就能整批撤销。
    /// </summary>
    private void ReplaceAll()
    {
        var editor = ActiveEditor;
        if (editor is null || string.IsNullOrEmpty(FindPattern)) return;

        var matches = TextSearch.FindAll(editor.Document.Text, FindPattern, MatchCase);
        if (matches.Count == 0)
        {
            FindCountText.Text = "无结果";
            return;
        }

        editor.Document.UndoStack.StartUndoGroup();
        try
        {
            for (var i = matches.Count - 1; i >= 0; i--)
                editor.Document.Replace(matches[i], FindPattern.Length, ReplaceBox.Text);
        }
        finally
        {
            editor.Document.UndoStack.EndUndoGroup();
        }

        FindCountText.Text = $"已替换 {matches.Count} 处";
        if (Vm is not null) Vm.StatusText = $"已替换 {matches.Count} 处";
        RefreshFindBarCount();
    }

    // ---------- 快捷键（宿主窗口隧道） ----------

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (key, ctrl, shift)
        {
            case (Key.N, true, false): vm.NewFileCommand.Execute(null); e.Handled = true; break;
            case (Key.O, true, false): vm.OpenFileCommand.Execute(null); e.Handled = true; break;
            case (Key.S, true, false): vm.SaveCommand.Execute(null); e.Handled = true; break;
            case (Key.S, true, true): vm.SaveAsCommand.Execute(null); e.Handled = true; break;
            case (Key.W, true, _): vm.CloseTabCommand.Execute(null); e.Handled = true; break;
            case (Key.F, true, false): ShowFindBarInternal(false); e.Handled = true; break;
            case (Key.H, true, false): ShowFindBarInternal(true); e.Handled = true; break;
            case (Key.M, true, true):
                if (Cfg is { } markdownCfg) markdownCfg.ShowMarkdownPreview = !markdownCfg.ShowMarkdownPreview;
                e.Handled = true; break;
            // Ctrl+= 在多数键盘布局上要按 Shift 才是「+」，所以 Ctrl+Shift+= 也当放大
            case (Key.OemPlus or Key.Add, true, _):
                vm.ZoomInCommand.Execute(null); e.Handled = true; break;
            case (Key.OemMinus or Key.Subtract, true, _):
                vm.ZoomOutCommand.Execute(null); e.Handled = true; break;
            case (Key.D0 or Key.NumPad0, true, false):
                vm.ResetZoomCommand.Execute(null); e.Handled = true; break;
            case (Key.F3, false, _):
                if (!FindBarOpen) ShowFindBarInternal(false);
                if (shift) FindPrevious();
                else FindNext();
                e.Handled = true; break;
            case (Key.Escape, false, false):
                if (FindBarOpen) { CloseFindBar(); e.Handled = true; }
                else if (vm.IsSettingsVisible) { vm.CloseSettingsCommand.Execute(null); e.Handled = true; }
                break;
        }
    }

    // ---------- Ctrl+滚轮缩放 / 拖放 / 标签条事件 ----------

    private void OnEditorHostPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var cfg = Cfg;
        if (cfg is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        cfg.ZoomPercent = TextEditorConfig.StepZoomPercent(cfg.ZoomPercent, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnDragOverFile(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFile(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(File.Exists))
                Vm?.OpenPath(file);
        }
        e.Handled = true;
    }

    /// <summary>标签上的 × 按钮。</summary>
    private void OnTabCloseClick(object sender, RoutedEventArgs e)
        => Vm?.CloseTab((sender as FrameworkElement)?.DataContext as EditorTab);

    /// <summary>中键点击标签 = 关闭（浏览器 / 编辑器惯例）。</summary>
    private void OnTabListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var tab = FindTabFromEvent(e.OriginalSource);
        if (tab is null) return;
        Vm?.CloseTab(tab);
        e.Handled = true;
    }

    /// <summary>
    /// 标签多到横向溢出时，滚轮在标签条上滚 = 横向滚动标签（否则滚轮会去滚编辑区）。
    /// 标签条本身没有可见的横向滚动条（Hidden），靠这里滚动 / 切标签时 ScrollIntoView 保证能看全。
    /// </summary>
    private void OnTabListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(TabList);
        if (scrollViewer is null || scrollViewer.ScrollableWidth <= 0) return;
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>在视觉树里找第一个指定类型的子元素（标签条的 ScrollViewer 由 ListBox 模板生成）。</summary>
    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>「文件 → 打开最近」点某一项（菜单项在 Popup 里，用 EventSetter 走这里最稳）。</summary>
    private void OnRecentFileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string path) return;
        Vm?.OpenPath(path);
        e.Handled = true;
    }

    private static EditorTab? FindTabFromEvent(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null && node is not ListBoxItem)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }
        return (node as ListBoxItem)?.DataContext as EditorTab;
    }

    // ---------- Markdown「实际效果预览」 ----------

    /// <summary>
    /// 按当前标签 / 设置决定预览窗显不显示：显示时用记忆高度（默认 360）铺开并立刻渲染，
    /// 不显示时把行高归零（编辑区拿回全部高度）。
    /// <para>⚠ 先设行高、再改分隔条可见性：PaneSplitter 在变可见时会捕获当前行高作为「双击复位」默认值，
    /// 顺序反了就会把 0 记成默认高度。</para>
    /// </summary>
    private void UpdatePreviewVisibility()
    {
        var vm = Vm;
        var visible = vm?.IsPreviewVisible == true;
        if (!visible)
        {
            PreviewRow.MinHeight = 0;
            PreviewRow.MaxHeight = double.PositiveInfinity;
            PreviewRow.Height = new GridLength(0);
            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewSplitter.Visibility = Visibility.Collapsed;
            PreviewViewer.Document = null;
            _lastPreviewText = null;
            _lastPreviewStyleKey = null;
            return;
        }

        PreviewRow.MinHeight = PreviewMinHeight;
        PreviewRow.MaxHeight = PreviewMaxHeight;
        PreviewRow.Height = new GridLength(LoadPreviewHeight());
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewSplitter.Visibility = Visibility.Visible;
        RenderPreviewNow();
    }

    private static double LoadPreviewHeight()
    {
        var saved = LayoutMemory.GetPixel(PreviewPaneKey + ".px");
        return saved is { } pixel
            ? Math.Clamp(pixel, PreviewMinHeight, PreviewMaxHeight)
            : PreviewDefaultHeight;
    }

    /// <summary>输入延迟合并：敲字时每 260ms 最多重新解析一次（预览只是锦上添花，不能拖慢打字）。</summary>
    private void SchedulePreviewRefresh()
    {
        if (Vm?.IsPreviewVisible != true) return;

        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(260),
            };
            _previewTimer.Tick += OnPreviewTimerTick;
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer?.Stop();
        RenderPreviewNow();
    }

    /// <summary>把激活 Markdown 标签的内容渲染到预览窗（编辑 / 切标签 / 改字体颜色 / 开关预览都会走这里）。</summary>
    private void RenderPreviewNow()
    {
        var vm = Vm;
        var cfg = Cfg;
        if (vm?.ActiveTab is not { } tab || cfg is null || !vm.IsPreviewVisible) return;
        if (PreviewPanel.Visibility != Visibility.Visible) return;

        var text = tab.Document.Text;
        var styleKey = BuildPreviewStyleKey(cfg, tab);
        if (string.Equals(text, _lastPreviewText, StringComparison.Ordinal) && styleKey == _lastPreviewStyleKey)
            return;

        _lastPreviewText = text;
        _lastPreviewStyleKey = styleKey;

        var scrollViewer = FindDescendant<ScrollViewer>(PreviewViewer);
        var savedOffset = scrollViewer?.VerticalOffset ?? 0;

        try
        {
            if (text.Length > PreviewMaxChars)
            {
                PreviewViewer.Document = BuildPreviewHintDocument(
                    $"文档太大（{text.Length:N0} 字符），实时预览已暂停；关掉预览继续编辑不会受影响。");
                return;
            }

            var style = BuildPreviewStyle(tab);
            var blocks = MarkdownParser.Parse(text);
            PreviewViewer.Document = MarkdownPreviewRenderer.Build(blocks, style, OpenPreviewLink);

            // 换了一份 FlowDocument，等布局完成再把滚动位置放回去
            if (savedOffset > 0)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var viewer = FindDescendant<ScrollViewer>(PreviewViewer);
                    if (viewer is not null)
                        viewer.ScrollToVerticalOffset(Math.Min(savedOffset, viewer.ScrollableHeight));
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            PreviewViewer.Document = BuildPreviewHintDocument("预览渲染失败：" + ex.Message);
        }
    }

    /// <summary>预览用的一套配色（跟编辑器背景 / 文字颜色、字体、字号 × 缩放走）。</summary>
    private MarkdownPreviewStyle BuildPreviewStyle(EditorTab? tab)
    {
        var cfg = Cfg ?? new TextEditorConfig();

        if (!HexToBrushConverter.TryParseHex(cfg.BackgroundHex, out var background))
            _ = HexToBrushConverter.TryParseHex(TextEditorConfig.DefaultBackgroundHex, out background);
        if (!HexToBrushConverter.TryParseHex(cfg.ForegroundHex, out var foreground))
            _ = HexToBrushConverter.TryParseHex(TextEditorConfig.DefaultForegroundHex, out foreground);

        var fontSize = Math.Clamp(cfg.FontSize * cfg.ZoomPercent / 100.0, 6, 64);
        var baseDirectory = tab?.FilePath is { } path ? Path.GetDirectoryName(path) : null;
        return MarkdownPreviewStyle.Create(
            background, foreground, TextEditorConfig.BuildFontFamily(cfg.FontName), fontSize, baseDirectory);
    }

    private static string BuildPreviewStyleKey(TextEditorConfig cfg, EditorTab tab)
        => string.Join('|', cfg.FontName, cfg.FontSize, cfg.ZoomPercent, cfg.BackgroundHex, cfg.ForegroundHex,
            tab.FilePath ?? "");

    private FlowDocument BuildPreviewHintDocument(string message)
    {
        var style = BuildPreviewStyle(Vm?.ActiveTab);
        var document = new FlowDocument
        {
            Background = MarkdownPreviewStyle.Frozen(style.Background),
            Foreground = MarkdownPreviewStyle.Frozen(style.Muted),
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            PagePadding = new Thickness(16, 12, 16, 24),
            ColumnWidth = double.PositiveInfinity,
        };
        document.Blocks.Add(new Paragraph(new Run(message)));
        return document;
    }

    /// <summary>预览里的链接：http/https/mailto 交给系统默认程序，本地存在的文件用资源管理器打开。</summary>
    private static void OpenPreviewLink(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" or "mailto")
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }

            if (File.Exists(url))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开链接不影响编辑
        }
    }

    private void OnPreviewCloseClick(object sender, RoutedEventArgs e)
    {
        if (Cfg is { } cfg) cfg.ShowMarkdownPreview = false;
        e.Handled = true;
    }

    // ---------- 设置面板 ----------

    /// <summary>
    /// 按「偏好设置」面板的显隐设置它所在列的宽度：
    /// 关着 = 0（编辑区不会被一条空列挤窄）；打开 = 上次拖过的宽度（PaneSplitter 记在
    /// <c>Config\layout.json</c> 的 <c>.px</c> 键），没拖过 = 304。
    /// <para>⚠ 别用 Binding 绑 <c>ColumnDefinition.Width</c>：它不在逻辑树 / 视觉树里，拿不到 DataContext，
    /// 绑定会静默失效（列宽永远保持默认）。</para>
    /// </summary>
    private void ApplySettingsColumnWidth(bool visible)
    {
        if (!visible)
        {
            SettingsColumn.MinWidth = 0;
            SettingsColumn.Width = new GridLength(0);
            return;
        }

        SettingsColumn.MinWidth = SettingsPanelMinWidth;
        var saved = LayoutMemory.GetPixel(SettingsPaneKey + ".px");
        var width = saved is { } pixel
            ? Math.Clamp(pixel, SettingsPanelMinWidth, SettingsPanelMaxWidth)
            : SettingsPanelDefaultWidth;
        SettingsColumn.Width = new GridLength(width);
    }

    private void OnBackgroundPresetClick(object sender, RoutedEventArgs e)
    {
        if (Cfg is null) return;
        if ((sender as FrameworkElement)?.DataContext is TextEditorConfig.ColorPreset preset)
        {
            Cfg.BackgroundHex = preset.Background;
            Cfg.ForegroundHex = preset.Foreground;
        }
    }

    // ---------- IEditorHost（给 ViewModel 的编辑区操作，都作用在激活编辑器上） ----------

    bool IEditorHost.IsEditorReady => ActiveEditor is not null;

    bool IEditorHost.CanUndo => ActiveEditor?.CanUndo ?? false;

    bool IEditorHost.CanRedo => ActiveEditor?.CanRedo ?? false;

    void IEditorHost.Undo() => ActiveEditor?.Undo();

    void IEditorHost.Redo() => ActiveEditor?.Redo();

    void IEditorHost.Cut() => ActiveEditor?.Cut();

    void IEditorHost.Copy() => ActiveEditor?.Copy();

    void IEditorHost.Paste() => ActiveEditor?.Paste();

    void IEditorHost.SelectAll() => ActiveEditor?.SelectAll();

    void IEditorHost.ShowFindBar(bool withReplace) => ShowFindBarInternal(withReplace);

    void IEditorHost.FocusEditor() => ActiveEditor?.Focus();

    void IEditorHost.UpdateTabPath(EditorTab tab)
    {
        if (_editors.TryGetValue(tab, out var editor))
            editor.SyntaxHighlighting = EditorHighlighting.ForFile(tab.FilePath);
    }
}
