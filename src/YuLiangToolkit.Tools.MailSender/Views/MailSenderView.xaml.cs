using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.MailSender.ViewModels;

namespace YuLiangToolkit.Tools.MailSender.Views;

/// <summary>
/// 「邮件推送」工具页的代码后置。这里只做三件事：
/// <list type="bullet">
///   <item><b>富文本编辑</b>：工具条按钮 / 快捷键 → 对 <see cref="Editor"/> 的选区施加格式，
///         并与 ViewModel 交换 HTML（<see cref="RichTextHtml"/> 负责转换）；</item>
///   <item><b>附件</b>：文件对话框、从资源管理器拖入；</item>
///   <item><b>预览</b>：WebView2（Chromium）渲染 HTML，脚本关闭、缓存进 <c>Cache\mail-sender\WebView2</c>；
///         没有运行时 / 还没就绪时退回纯文本，不影响发送。</item>
/// </list>
///
/// <para>⚠ 白底编辑区是刻意的：邮件正文长什么样，编辑时就该是什么样（换主题也不跟着变）。</para>
/// </summary>
public partial class MailSenderView : UserControl
{
    private MailSenderViewModel? _subscribed;

    private CoreWebView2Environment? _webViewEnvironment;
    private WebView2? _preview;
    private bool _webViewInitializing;

    // 撤销 / 重做（RichTextBox.IsUndoEnabled 自带，但工具条上不放按钮，只走 Ctrl+Z / Ctrl+Y）
    private bool _loadingDocument;

    /// <summary>截图 / 调试用：设了 <c>YLT_MAIL_SENDER_SOURCE=1</c> 时直接切到 HTML 源码标签页。</summary>
    private static bool WantSourceMode
        => Environment.GetEnvironmentVariable("YLT_MAIL_SENDER_SOURCE") == "1";

    public MailSenderView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MailSenderViewModel? ViewModel => DataContext as MailSenderViewModel;

    // ================================================================ 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        var viewModel = ViewModel;
        if (viewModel is null) return;

        // 编辑区此刻还是新建的空文档，所以一律以「配置里的草稿」为准；
        // 没有草稿（或只是空段落）时用示例正文。
        LoadDocument(DraftOrDefault(viewModel.Cfg.BodyHtml));

        if (WantSourceMode) SourceModeRadio.IsChecked = true;

        // 开发调试：YLT_MAIL_SENDER_AUTOSEND=1 时自动点一次「发送」（配合 YLT_SHOT 验收发送链路：
        // 截图里能看到状态行 / 日志里的真实返回）。正常使用不会执行。
        if (Environment.GetEnvironmentVariable("YLT_MAIL_SENDER_AUTOSEND") == "1")
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                viewModel.Log("[截图调试] 自动触发一次发送");
                if (viewModel.SendCommand.CanExecute(null)) viewModel.SendCommand.Execute(null);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        UpdateBodySummary();
        _ = InitializePreviewAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        FlushDocumentToConfig();
        AttachViewModel(detach: true);
        DisposePreview();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel();
        if (!IsLoaded) return;
        LoadDocument(DraftOrDefault(ViewModel?.Cfg.BodyHtml));
    }

    private void AttachViewModel(bool detach = false)
    {
        if (detach)
        {
            if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed = null;
            return;
        }

        var viewModel = ViewModel;
        if (ReferenceEquals(_subscribed, viewModel)) return;
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = viewModel;
        if (viewModel is null) return;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.GetHtml = BuildHtmlForSend;
        viewModel.LoadHtml = LoadDocument;
        viewModel.AddAttachmentsRequested = ShowAttachmentDialog;
        viewModel.ScrollLogToEnd = () => LogBox.ScrollToEnd();

        // 关掉工具 / 程序退出时，把编辑区内容回写到配置（正文草稿不能丢）
        viewModel.WriteBack = FlushDocumentToConfig;

        SyncApiKeyBoxes();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 点 👁 之后要在密码框 / 明文框之间换控件
            case nameof(MailSenderViewModel.ApiKeyVisible):
                SyncApiKeyBoxes();
                break;
            case nameof(MailSenderViewModel.LastSendSucceeded):
                Dispatcher.BeginInvoke(new Action(() => _ = RenderPreviewAsync()), System.Windows.Threading.DispatcherPriority.Background);
                break;
        }
    }

    // ---------- API Key：PasswordBox（默认遮住）⇄ 明文 TextBox ----------

    private void SyncApiKeyBoxes()
    {
        var viewModel = ViewModel;
        var showPlain = viewModel?.ApiKeyVisible == true;

        if (viewModel is not null && ApiKeyPassword.Password != viewModel.ApiKey)
            ApiKeyPassword.Password = viewModel.ApiKey;

        ApiKeyPassword.Visibility = showPlain ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyPlain.Visibility = showPlain ? Visibility.Visible : Visibility.Collapsed;
        if (showPlain) ApiKeyPlain.Text = viewModel?.ApiKey ?? "";
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.ApiKeyVisible) return;
        viewModel.ApiKey = ApiKeyPassword.Password;
    }

    // ================================================================ 正文：编辑区 ⇄ HTML

    /// <summary>把编辑区内容刷进配置的 <see cref="MailSenderConfig.BodyHtml"/>（草稿自动保存靠它）。</summary>
    private void FlushDocumentToConfig()
    {
        var viewModel = ViewModel;
        if (viewModel is null || _loadingDocument) return;

        // 源码框在「切到源码模式」的过程中会触发 TextChanged：此时编辑器内容还是旧的，
        // 不能拿它回写配置，否则会把草稿冲掉（只能读源码框那一份）。
        var html = SourceModeRadio.IsChecked == true ? SourceBox.Text : RichTextHtml.ToHtml(Editor.Document);
        if (!string.Equals(viewModel.Cfg.BodyHtml, html, StringComparison.Ordinal)) viewModel.Cfg.BodyHtml = html;
    }

    /// <summary>当前正文 HTML（行内图片仍然是本机路径 / data:，由各解析器决定最终形态）。</summary>
    private string CurrentHtml()
        => SourceModeRadio.IsChecked == true ? SourceBox.Text : RichTextHtml.ToHtml(Editor.Document);

    /// <summary>发送用：行内图片换 <c>cid:…</c> 并登记到 ViewModel（随邮件作为附件上传）。</summary>
    private string BuildHtmlForSend()
    {
        var viewModel = ViewModel;
        if (viewModel is null) return "";
        viewModel.ResetInlineImages();
        var html = CurrentHtml();
        if (SourceModeRadio.IsChecked == true) return html;   // 源码模式用户自己写的 cid: 原样保留

        // 先登记编辑区里的每张图片（同一路径只登记一次），再据此改写 src
        var images = CollectInlineImages(Editor.Document);
        foreach (var path in images) viewModel.RegisterInlineImage(path);
        return RichTextHtml.ToHtml(Editor.Document, src => ResolveCid(src, viewModel));
    }

    private static string ResolveCid(string src, MailSenderViewModel viewModel)
        => src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? src                                            // 源码模式贴进来的内联图，原样发
            : "cid:" + viewModel.RegisterInlineImage(src);

    /// <summary>编辑区里所有行内图片的本机路径（去重，保持出现顺序）。</summary>
    private static List<string> CollectInlineImages(FlowDocument document)
    {
        var paths = new List<string>();
        foreach (var image in EnumerateImages(document.Blocks))
        {
            var source = image.Tag as string ?? "";
            if (source.Length == 0) continue;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!paths.Contains(source, StringComparer.OrdinalIgnoreCase)) paths.Add(source);
        }
        return paths;
    }

    private static IEnumerable<Image> EnumerateImages(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var inline in paragraph.Inlines)
                        foreach (var image in EnumerateImages(inline)) yield return image;
                    break;
                case BlockUIContainer { Child: Image direct }:
                    yield return direct;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var image in EnumerateImages(item.Blocks)) yield return image;
                    break;
                case Table table:
                    foreach (var group in table.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var image in EnumerateImages(cell.Blocks)) yield return image;
                    break;
                case Section section:
                    foreach (var image in EnumerateImages(section.Blocks)) yield return image;
                    break;
            }
        }
    }

    private static IEnumerable<Image> EnumerateImages(Inline inline)
    {
        if (inline is InlineUIContainer { Child: Image image })
        {
            yield return image;
            yield break;
        }
        if (inline is Span span)
        {
            foreach (var child in span.Inlines)
                foreach (var nested in EnumerateImages(child)) yield return nested;
        }
    }

    /// <summary>把 HTML 载入编辑区（打开工具时恢复草稿 / 从源码切回富文本）。</summary>
    private void LoadDocument(string? html)
    {
        _loadingDocument = true;
        try
        {
            var text = html ?? "";
            if (SourceModeRadio.IsChecked == true) SourceBox.Text = text;
            Editor.Document = RichTextHtml.FromHtml(text);
        }
        finally
        {
            _loadingDocument = false;
        }
    }

    /// <summary>打开工具时该显示哪份内容：配置里的草稿，没有（或只是空段落）就用示例正文。</summary>
    private static string DraftOrDefault(string? draft)
        => string.IsNullOrWhiteSpace(draft) || MailHtml.IsEmptyBody(draft) ? MailSenderDefaults.BodyHtml : draft;

    private void OnEditorModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var toSource = SourceModeRadio.IsChecked == true;

        if (toSource)
        {
            // 富文本 → 源码：把当前内容（含图片路径）导出成 HTML
            SourceBox.Text = CurrentHtml();
            SourceBox.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Collapsed;
            RichToolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 源码 → 富文本：重新解析源码
            Editor.Document = RichTextHtml.FromHtml(SourceBox.Text);
            SourceBox.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            RichToolbar.Visibility = Visibility.Visible;
        }

        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnSourceBoxTextChanged(object sender, TextChangedEventArgs e) => UpdateBodySummary();

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateToggleState();
        UpdateBodySummary();
    }

    private void UpdateBodySummary()
    {
        ViewModel?.UpdateBodySummary(CurrentHtml());
    }

    // ================================================================ 富文本工具条

    private void OnBoldClick(object sender, RoutedEventArgs e) => ToggleInlineFormat(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);
    private void OnItalicClick(object sender, RoutedEventArgs e) => ToggleInlineFormat(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);

    private void OnUnderlineClick(object sender, RoutedEventArgs e)
        => ToggleDecoration(TextDecorationLocation.Underline);

    private void OnStrikeClick(object sender, RoutedEventArgs e)
        => ToggleDecoration(TextDecorationLocation.Strikethrough);

    private void ToggleInlineFormat(DependencyProperty property, object on, object off)
    {
        var selection = Editor.Selection;
        if (selection.IsEmpty)
        {
            Editor.Focus();
            return;
        }

        // 选区里只要有一段没加粗，就整体加上；全都有则整体去掉（和 Word 一致）
        var allOn = true;
        foreach (var run in EnumerateRuns(selection))
        {
            if (property == TextElement.FontWeightProperty)
            {
                if (run.FontWeight < FontWeights.SemiBold) allOn = false;
            }
            else if (run.FontStyle != FontStyles.Italic) allOn = false;
        }

        foreach (var run in EnumerateRuns(selection))
        {
            if (property == TextElement.FontWeightProperty)
                run.FontWeight = allOn ? (FontWeight)off : (FontWeight)on;
            else if (property == TextElement.FontStyleProperty)
                run.FontStyle = allOn ? (FontStyle)off : (FontStyle)on;
        }

        UpdateToggleState();
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void ToggleDecoration(TextDecorationLocation location)
    {
        var selection = Editor.Selection;
        if (selection.IsEmpty) return;

        var runs = EnumerateRuns(selection).ToList();
        var allOn = runs.Count > 0 && runs.All(run => RichTextHtml.HasDecorationAt(run, location));
        foreach (var run in runs)
        {
            var decorations = new TextDecorationCollection();
            if (run.TextDecorations is { Count: > 0 } existing)
            {
                foreach (var decoration in existing)
                {
                    if (allOn && decoration.Location == location) continue;   // 关掉这一种
                    decorations.Add(decoration);
                }
            }
            if (!allOn && !decorations.Any(d => d.Location == location))
                decorations.Add(RichTextHtml.CreateDecoration(location));
            run.TextDecorations = decorations;
        }

        UpdateToggleState();
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void UpdateToggleState()
    {
        if (Editor.Selection is null) return;
        var runs = EnumerateRuns(Editor.Selection).ToList();
        BoldToggle.IsChecked = runs.Count > 0 && runs.All(r => r.FontWeight >= FontWeights.SemiBold);
        ItalicToggle.IsChecked = runs.Count > 0 && runs.All(r => r.FontStyle == FontStyles.Italic);
        UnderlineToggle.IsChecked = runs.Count > 0 && runs.All(r => RichTextHtml.HasDecorationAt(r, TextDecorationLocation.Underline));
        StrikeToggle.IsChecked = runs.Count > 0 && runs.All(r => RichTextHtml.HasDecorationAt(r, TextDecorationLocation.Strikethrough));
    }

    private void OnForegroundColorClick(object sender, RoutedEventArgs e) => ApplyColor(TextElement.ForegroundProperty, withBackground: false);
    private void OnBackgroundColorClick(object sender, RoutedEventArgs e) => ApplyColor(TextElement.BackgroundProperty, withBackground: true);

    private void ApplyColor(DependencyProperty property, bool withBackground)
    {
        var picker = ColorPickerWindow.ForText(!withBackground);
        if (picker.ShowDialog() != true || picker.PickedColor is null) return;

        var brush = new SolidColorBrush(picker.PickedColor.Value);
        brush.Freeze();
        foreach (var run in EnumerateRuns(Editor.Selection))
        {
            if (property == TextElement.ForegroundProperty) run.Foreground = brush;
            else run.Background = brush;
        }

        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnGrowFontClick(object sender, RoutedEventArgs e) => ResizeFont(+2);
    private void OnShrinkFontClick(object sender, RoutedEventArgs e) => ResizeFont(-2);

    private void ResizeFont(int delta)
    {
        foreach (var run in EnumerateRuns(Editor.Selection))
        {
            var size = double.IsNaN(run.FontSize) ? RichTextHtml.DefaultFontSize : run.FontSize;
            run.FontSize = Math.Clamp(size + delta, 9, 40);
        }
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnAlignLeftClick(object sender, RoutedEventArgs e) => SetAlignment(TextAlignment.Left);
    private void OnAlignCenterClick(object sender, RoutedEventArgs e) => SetAlignment(TextAlignment.Center);
    private void OnAlignRightClick(object sender, RoutedEventArgs e) => SetAlignment(TextAlignment.Right);

    private void SetAlignment(TextAlignment alignment)
    {
        foreach (var paragraph in EnumerateParagraphs(Editor.Selection))
            paragraph.TextAlignment = alignment;
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnBulletsClick(object sender, RoutedEventArgs e) => ApplyList(TextMarkerStyle.Disc);
    private void OnNumberingClick(object sender, RoutedEventArgs e) => ApplyList(TextMarkerStyle.Decimal);

    private void ApplyList(TextMarkerStyle style)
    {
        // 已经是列表就改标记样式，否则把当前段落包进列表
        if (Editor.Selection.Start.Paragraph?.Parent is ListItem)
        {
            var list = (List)((ListItem)Editor.Selection.Start.Paragraph!.Parent).Parent;
            list.MarkerStyle = style;
        }
        else
        {
            var range = new TextRange(Editor.Selection.Start, Editor.Selection.End);
            try { EditingCommands.ToggleBullets.Execute(null, Editor); }
            catch { /* 没有段落时忽略 */ }
            _ = range;
            if (style == TextMarkerStyle.Decimal)
            {
                try { EditingCommands.ToggleNumbering.Execute(null, Editor); } catch { /* 忽略 */ }
            }
        }

        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnIndentIncreaseClick(object sender, RoutedEventArgs e)
    {
        try { EditingCommands.IncreaseIndentation.Execute(null, Editor); } catch { /* 忽略 */ }
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnIndentDecreaseClick(object sender, RoutedEventArgs e)
    {
        try { EditingCommands.DecreaseIndentation.Execute(null, Editor); } catch { /* 忽略 */ }
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnFormatBlockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || Editor is null) return;
        if (FormatBlockCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string ?? "p";
        var size = tag switch
        {
            "h1" => 30d,
            "h2" => 24d,
            "h3" => 19d,
            _ => RichTextHtml.DefaultFontSize,
        };

        foreach (var paragraph in EnumerateParagraphs(Editor.Selection))
            paragraph.FontSize = size;

        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnInsertLinkClick(object sender, RoutedEventArgs e)
    {
        var dialog = new InsertLinkWindow(SelectedPlainText()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        var link = new Hyperlink(new Run(dialog.LinkText)) { Tag = dialog.LinkUrl, NavigateUri = SafeUri(dialog.LinkUrl) };
        link.Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xDB));
        link.TextDecorations = new TextDecorationCollection { RichTextHtml.CreateDecoration(TextDecorationLocation.Underline) };
        // TextPointer 没有「插入 Inline」的 API：插到光标所在段落的末尾（用户随后还能继续打字）
        var paragraph = Editor.CaretPosition.Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            Editor.Document.Blocks.Add(paragraph);
        }
        paragraph.Inlines.Add(link);
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private static Uri? SafeUri(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;

    private string SelectedPlainText()
    {
        var text = new TextRange(Editor.Selection.Start, Editor.Selection.End).Text;
        return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
    }

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要插入正文的图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        InsertImageFile(dialog.FileName);
    }

    /// <summary>把一张本机图片作为行内图片插入正文（发送时转 <c>cid:</c> 连附件一起上传）。</summary>
    private void InsertImageFile(string path)
    {
        if (!RichTextHtml.TryLoadBitmap(path, out var bitmap))
        {
            ViewModel?.Log($"插入图片失败（读不到或格式不支持）：{path}");
            return;
        }

        var image = new Image
        {
            Source = bitmap,
            Tag = path,
            Width = Math.Min(bitmap.PixelWidth, 640),
            Stretch = Stretch.Uniform,
        };

        // 光标在段落里就插进行内，否则新建一段
        var paragraph = Editor.Selection.Start.Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            Editor.Document.Blocks.Add(paragraph);
        }
        paragraph.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });

        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnInsertTableClick(object sender, RoutedEventArgs e)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
        for (var i = 0; i < 3; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var row = 0; row < 3; row++)
        {
            var tableRow = new TableRow();
            for (var column = 0; column < 3; column++)
            {
                var cell = new TableCell(new Paragraph(new Run(row == 0 ? $"列 {column + 1}" : "")))
                {
                    Padding = new Thickness(6, 4, 6, 4),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE)),
                    BorderThickness = new Thickness(1),
                };
                tableRow.Cells.Add(cell);
            }
            group.Rows.Add(tableRow);
        }

        Editor.Document.Blocks.Add(table);
        Editor.Document.Blocks.Add(new Paragraph());
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnInsertRuleClick(object sender, RoutedEventArgs e)
    {
        var paragraph = new Paragraph(new Run(new string('─', 40)))
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCF, 0xDD)),
        };
        Editor.Document.Blocks.Add(paragraph);
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    private void OnClearFormatClick(object sender, RoutedEventArgs e)
    {
        foreach (var run in EnumerateRuns(Editor.Selection))
        {
            run.ClearValue(TextElement.FontWeightProperty);
            run.ClearValue(TextElement.FontStyleProperty);
            run.ClearValue(TextElement.FontSizeProperty);
            run.ClearValue(TextElement.ForegroundProperty);
            run.ClearValue(TextElement.BackgroundProperty);
            run.ClearValue(TextElement.FontFamilyProperty);
            run.TextDecorations = new TextDecorationCollection();
        }

        UpdateToggleState();
        UpdateBodySummary();
        FlushDocumentToConfig();
    }

    // ---------- 选区遍历（工具条施加格式用） ----------

    /// <summary>
    /// 选区覆盖到的 Run（工具条施加粗体 / 斜体 / 颜色等）。
    ///
    /// <para>用 <see cref="TextPointer.GetNextContextPosition"/> 逐段前进而不是按「段落 → 段内 Inline」遍历：
    /// 后者要自己处理列表项 / 表格单元格的嵌套结构（很容易漏字或死循环），
    /// TextPointer 已经把整份文档拍成一条逻辑流。</para>
    /// </summary>
    private static IEnumerable<Run> EnumerateRuns(TextSelection? selection)
    {
        if (selection is null) yield break;
        var end = selection.End;
        var seen = new HashSet<Run>();
        TextPointer? pointer = selection.Start;
        while (pointer is not null)
        {
            if (pointer.Parent is Run run && seen.Add(run)) yield return run;
            if (pointer.CompareTo(end) >= 0) break;
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
    }

    /// <summary>选区覆盖到的段落（对齐 / 标题 / 列表用）。</summary>
    private static IEnumerable<Paragraph> EnumerateParagraphs(TextSelection? selection)
    {
        if (selection is null) yield break;
        var end = selection.End;
        var seen = new HashSet<Paragraph>();
        TextPointer? pointer = selection.Start;
        while (pointer is not null)
        {
            if (pointer.Paragraph is { } paragraph && seen.Add(paragraph)) yield return paragraph;
            if (pointer.CompareTo(end) >= 0) break;
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
    }

    // ---------- 键盘快捷键 ----------

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.B:
                BoldToggle.IsChecked = !(BoldToggle.IsChecked ?? false);
                OnBoldClick(sender, e);
                e.Handled = true;
                break;
            case Key.I:
                OnItalicClick(sender, e);
                e.Handled = true;
                break;
            case Key.U:
                OnUnderlineClick(sender, e);
                e.Handled = true;
                break;
        }
    }

    // ---------- 拖入图片 / 文本 ----------

    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (var file in files)
        {
            if (IsImage(file)) InsertImageFile(file);
            else ViewModel?.AddAttachmentPaths(new[] { file });
        }
        e.Handled = true;
    }

    private static bool IsImage(string path) => MailAttachmentItem.ContentTypeOf(path).StartsWith("image/", StringComparison.Ordinal);

    // ================================================================ 附件

    private void ShowAttachmentDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要作为附件发送的文件",
            Filter = "所有文件|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) ViewModel?.AddAttachmentPaths(dialog.FileNames);
    }

    private void OnAttachmentDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAttachmentDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        ViewModel?.AddAttachmentPaths(files);
        e.Handled = true;
    }

    // ================================================================ 预览（WebView2）

    private async Task InitializePreviewAsync()
    {
        if (_webViewEnvironment is not null || _webViewInitializing) return;
        _webViewInitializing = true;
        try
        {
            var cache = ToolCache.For("mail-sender");
            cache.EnsureCreated();
            var userDataFolder = cache.PathOf("WebView2");
            Directory.CreateDirectory(userDataFolder);

            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            if (!IsLoaded)
            {
                _webViewEnvironment = null;
                return;
            }

            var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            PreviewHost.Children.Add(webView);
            await webView.EnsureCoreWebView2Async(_webViewEnvironment);
            if (!IsLoaded)
            {
                PreviewHost.Children.Remove(webView);
                try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
                return;
            }

            var core = webView.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = false;                 // 正文里的脚本一律不跑
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;

            _preview = webView;
            await RenderPreviewAsync();
            SchedulePreviewRefresh();
            TryCaptureForScreenshot();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowPreviewFallback("没有找到 WebView2 运行时（Edge 的内核），预览改用纯文本显示。安装 WebView2 Runtime 后可看真实排版；这不影响发送。");
        }
        catch (Exception ex)
        {
            ShowPreviewFallback("预览启动失败，改用纯文本显示：" + ex.Message);
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    private string? _lastRenderedHtml;

    private Task RenderPreviewAsync()
    {
        var viewModel = ViewModel;
        if (viewModel is null) return Task.CompletedTask;

        var html = CurrentHtml();
        if (SourceModeRadio.IsChecked == true)
            viewModel.ResetInlineImages();   // 源码模式没有编辑区图片清单

        if (_preview?.CoreWebView2 is null)
        {
            // 还没就绪 / 没有运行时：显示纯文本兜底
            if (PreviewFallback.Visibility == Visibility.Visible)
                PreviewFallback.Text = MailHtml.ToPlainText(html);
            return Task.CompletedTask;
        }

        var document = MailHtml.BuildPreviewDocument(html);
        if (string.Equals(document, _lastRenderedHtml, StringComparison.Ordinal)) return Task.CompletedTask;
        _lastRenderedHtml = document;
        try
        {
            _preview.CoreWebView2.NavigateToString(document);
        }
        catch (Exception ex)
        {
            ShowPreviewFallback("预览渲染失败，改用纯文本显示：" + ex.Message);
        }
        return Task.CompletedTask;
    }

    private void ShowPreviewFallback(string message)
    {
        PreviewFallback.Text = message;
        PreviewFallback.Visibility = Visibility.Visible;
        PreviewHost.Visibility = Visibility.Collapsed;
        ViewModel?.Log(message);
    }

    private void OnRefreshPreviewClick(object sender, RoutedEventArgs e)
    {
        _lastRenderedHtml = null;
        if (_preview?.CoreWebView2 is null)
        {
            _ = InitializePreviewAsync();
            if (PreviewFallback.Visibility == Visibility.Visible)
            {
                PreviewFallback.Visibility = Visibility.Collapsed;
                PreviewHost.Visibility = Visibility.Visible;
            }
        }
        _ = RenderPreviewAsync();
        ViewModel?.UpdateBodySummary(CurrentHtml());
        FlushDocumentToConfig();
    }

    /// <summary>
    /// WebView2 内核就绪后补一次渲染：<see cref="InitializePreviewAsync"/> 里第一次渲染发生在
    /// 控件还没进可视树 / 还没拿到尺寸的时刻，有时会被丢掉（表现为预览区一片空白）。
    /// 这里在内核初始化完成后再排一次渲染，稳妥。
    /// </summary>
    private void SchedulePreviewRefresh()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastRenderedHtml = null;
            _ = RenderPreviewAsync();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void DisposePreview()
    {
        var preview = _preview;
        _preview = null;
        if (preview is null) return;
        try { PreviewHost.Children.Remove(preview); } catch { /* 忽略 */ }
        try { preview.Dispose(); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// <c>YLT_SHOT</c> 截图走的是 WPF RenderTargetBitmap，抓不到 WebView2（它是独立 HWND，主截图里那块会是空白），
    /// 所以截图模式下再用 WebView2 自己的 <c>CapturePreviewAsync</c> 另存一张 <c>&lt;截图&gt;.web.png</c>，
    /// 用来核对 Chromium 真的把正文渲染出来了（正常使用不会执行；做法同「网页浏览器」）。
    /// </summary>
    private async void TryCaptureForScreenshot()
    {
        var shotPath = Environment.GetEnvironmentVariable("YLT_SHOT");
        var core = _preview?.CoreWebView2;
        if (_screenshotCaptured || string.IsNullOrWhiteSpace(shotPath) || core is null) return;
        _screenshotCaptured = true;

        try
        {
            var delay = 400;
            if (int.TryParse(Environment.GetEnvironmentVariable("YLT_SHOT_DELAY_MS"), out var custom) && custom > 0)
                delay = Math.Min(custom / 2, 4000);
            await Task.Delay(delay);
            await using var stream = File.Create(shotPath + ".web.png");
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        catch (Exception ex)
        {
            ViewModel?.Log("截图辅助（预览抓图）失败：" + ex.Message);
        }
    }

    private bool _screenshotCaptured;
}
