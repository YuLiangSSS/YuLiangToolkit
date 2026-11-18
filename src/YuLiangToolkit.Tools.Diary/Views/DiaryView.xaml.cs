using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Tools.Diary.ViewModels;

namespace YuLiangToolkit.Tools.Diary.Views;

/// <summary>
/// 「日记本」工具页代码后置。这里负责三件界面层的事：
/// <list type="bullet">
///   <item><b>富文本编辑</b>：工具条 / 快捷键 → 对 <see cref="Editor"/> 选区施加格式，
///         并与 ViewModel 交换 HTML（<see cref="RichTextHtml"/> 负责 FlowDocument ⇄ HTML）；</item>
///   <item><b>普通文本</b>：切到普通文本时改用 <see cref="PlainBox"/>（纯文字，不解析格式）；</item>
///   <item><b>自动保存</b>：定时器发现改动就静默落盘，切走 / 关闭时再兜一次。</item>
/// </list>
///
/// <para>⚠ 白底编辑区是刻意的：写日记时看到的就是白纸黑字，换主题也不跟着变。</para>
/// </summary>
public partial class DiaryView : UserControl
{
    private DiaryViewModel? _subscription;
    private bool _loading;
    private bool _attached;
    private readonly DispatcherTimer _autoSave;

    public DiaryView()
    {
        InitializeComponent();

        _autoSave = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        _autoSave.Tick += (_, _) =>
        {
            if (_loading) return;
            if (ViewModel is { IsDirty: true }) ViewModel.SaveCurrentEntry();
        };

        DataContextChanged += (_, _) => Attach();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private DiaryViewModel? ViewModel => DataContext as DiaryViewModel;

    // ================================================================ 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
        _autoSave.Start();
        // 打开工具时按当前配置把「默认写法」体现在新建按钮行为里（NewEntry 自己会读 Cfg）。
        if (ViewModel?.SelectedEntry is { } selected) LoadEntry(selected);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoSave.Stop();
        ViewModel?.SaveCurrentEntry();
        Detach();
    }

    private void Attach()
    {
        var viewModel = ViewModel;
        if (ReferenceEquals(_subscription, viewModel)) return;

        if (_subscription is not null) _subscription.PropertyChanged -= OnViewModelPropertyChanged;
        _subscription = viewModel;
        if (viewModel is null) return;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.LoadEntryIntoEditor = LoadEntry;
        viewModel.ReadEditorContent = ReadContent;
        viewModel.FocusEditor = () => Editor.Focus();
        viewModel.ConfirmDelete = entry => MessageBox.Show(
            Window.GetWindow(this),
            $"确定删除这篇日记「{entry.DisplayTitle}」吗？删除后不能撤销。",
            "删除日记", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

        _attached = true;
    }

    private void Detach()
    {
        if (_subscription is not null) _subscription.PropertyChanged -= OnViewModelPropertyChanged;
        _subscription = null;
        _attached = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 切换写法后，工具条上的加粗 / 斜体等按钮状态要跟着刷新（用当前选区重新算一次）
        if (e.PropertyName == nameof(DiaryViewModel.SelectedEntry)) UpdateToggleState();
    }

    // ================================================================ 正文：编辑区 ⇄ 内容

    /// <summary>把一篇日记载入编辑区（富文本 → FlowDocument，普通文本 → 纯文本框）。</summary>
    private void LoadEntry(DiaryEntry entry)
    {
        _loading = true;
        try
        {
            if (entry.Format == DiaryFormat.RichText)
            {
                Editor.Document = RichTextHtml.FromHtml(entry.Content);
                Editor.Visibility = Visibility.Visible;
                PlainBox.Visibility = Visibility.Collapsed;
                RichModeRadio.IsChecked = true;
            }
            else
            {
                PlainBox.Text = entry.Content ?? "";
                PlainBox.Visibility = Visibility.Visible;
                Editor.Visibility = Visibility.Collapsed;
                PlainModeRadio.IsChecked = true;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>读回编辑区当前正文：富文本返回 HTML，普通文本返回纯文字。</summary>
    private string ReadContent()
    {
        var viewModel = ViewModel;
        if (viewModel?.SelectedEntry is null) return "";
        return viewModel.SelectedEntry.Format == DiaryFormat.RichText
            ? RichTextHtml.ToHtml(Editor.Document)
            : PlainBox.Text;
    }

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var viewModel = ViewModel;
        if (viewModel?.SelectedEntry is null) return;

        var target = PlainModeRadio.IsChecked == true ? DiaryFormat.PlainText : DiaryFormat.RichText;
        if (viewModel.SelectedEntry.Format == target) return;
        viewModel.ChangeFormat(target);
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var viewModel = ViewModel;
        if (viewModel?.SelectedEntry is null) return;
        if (!string.Equals(TitleBox.Text, viewModel.SelectedEntry.Title, StringComparison.Ordinal)) viewModel.MarkDirty();
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var viewModel = ViewModel;
        if (viewModel?.SelectedEntry is null) return;
        var content = ReadContent();
        if (!string.Equals(content, viewModel.SelectedEntry.Content, StringComparison.Ordinal)) viewModel.MarkDirty();
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e) => UpdateToggleState();

    // ================================================================ 心情

    private void OnMoodClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string emoji }) ViewModel?.SetMood(emoji);
    }

    private void OnClearMoodClick(object sender, RoutedEventArgs e) => ViewModel?.SetMood("");

    // ================================================================ 富文本工具条

    private void OnBoldClick(object sender, RoutedEventArgs e) => ToggleInlineFormat(bold: true);
    private void OnItalicClick(object sender, RoutedEventArgs e) => ToggleInlineFormat(bold: false);

    private void ToggleInlineFormat(bool bold)
    {
        var selection = Editor.Selection;
        if (selection.IsEmpty) { Editor.Focus(); return; }

        var runs = EnumerateRuns(selection).ToList();
        var allOn = runs.Count > 0 && runs.All(r =>
            bold ? r.FontWeight >= FontWeights.SemiBold : r.FontStyle == FontStyles.Italic);

        foreach (var run in runs)
        {
            if (bold) run.FontWeight = allOn ? FontWeights.Normal : FontWeights.Bold;
            else run.FontStyle = allOn ? FontStyles.Normal : FontStyles.Italic;
        }

        AfterFormatting();
    }

    private void OnUnderlineClick(object sender, RoutedEventArgs e) => ToggleDecoration(TextDecorationLocation.Underline);
    private void OnStrikeClick(object sender, RoutedEventArgs e) => ToggleDecoration(TextDecorationLocation.Strikethrough);

    private void ToggleDecoration(TextDecorationLocation location)
    {
        var selection = Editor.Selection;
        if (selection.IsEmpty) return;

        var runs = EnumerateRuns(selection).ToList();
        var allOn = runs.Count > 0 && runs.All(r => RichTextHtml.HasDecorationAt(r, location));
        foreach (var run in runs)
        {
            var decorations = new TextDecorationCollection();
            if (run.TextDecorations is { Count: > 0 } existing)
            {
                foreach (var decoration in existing)
                {
                    if (allOn && decoration.Location == location) continue;
                    decorations.Add(decoration);
                }
            }
            if (!allOn && !decorations.Any(d => d.Location == location))
                decorations.Add(RichTextHtml.CreateDecoration(location));
            run.TextDecorations = decorations;
        }

        AfterFormatting();
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        if (!TryParseColor(hex, out var color)) return;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        foreach (var run in EnumerateRuns(Editor.Selection)) run.Foreground = brush;
        AfterFormatting();
    }

    private void OnFormatBlockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || FormatBlockCombo.SelectedItem is not ComboBoxItem item) return;
        var size = (item.Tag as string) switch
        {
            "h1" => 30d,
            "h2" => 24d,
            "h3" => 19d,
            _ => RichTextHtml.DefaultFontSize,
        };

        foreach (var paragraph in EnumerateParagraphs(Editor.Selection)) paragraph.FontSize = size;
        AfterFormatting();
    }

    private void OnAlignLeftClick(object sender, RoutedEventArgs e) => SetAlignment(TextAlignment.Left);
    private void OnAlignCenterClick(object sender, RoutedEventArgs e) => SetAlignment(TextAlignment.Center);
    private void OnAlignRightClick(object sender, RoutedEventArgs e) => SetAlignment(TextAlignment.Right);

    private void SetAlignment(TextAlignment alignment)
    {
        foreach (var paragraph in EnumerateParagraphs(Editor.Selection)) paragraph.TextAlignment = alignment;
        AfterFormatting();
    }

    private void OnBulletsClick(object sender, RoutedEventArgs e) => ApplyList(TextMarkerStyle.Disc);
    private void OnNumberingClick(object sender, RoutedEventArgs e) => ApplyList(TextMarkerStyle.Decimal);

    private void ApplyList(TextMarkerStyle style)
    {
        if (Editor.Selection.Start.Paragraph?.Parent is ListItem listItem && listItem.Parent is List list)
        {
            list.MarkerStyle = style;
        }
        else
        {
            try { EditingCommands.ToggleBullets.Execute(null, Editor); } catch { /* 没有段落时忽略 */ }
            if (style == TextMarkerStyle.Decimal)
            {
                try { EditingCommands.ToggleNumbering.Execute(null, Editor); } catch { /* 忽略 */ }
            }
        }
        AfterFormatting();
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
        AfterFormatting();
    }

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要插入日记的图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true) InsertImageFile(dialog.FileName);
    }

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

        var paragraph = Editor.Selection.Start.Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            Editor.Document.Blocks.Add(paragraph);
        }
        paragraph.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        AfterFormatting();
    }

    private void AfterFormatting()
    {
        UpdateToggleState();
        ViewModel?.MarkDirty();
    }

    private void UpdateToggleState()
    {
        if (!_attached || Editor is null) return;
        var runs = EnumerateRuns(Editor.Selection).ToList();
        BoldToggle.IsChecked = runs.Count > 0 && runs.All(r => r.FontWeight >= FontWeights.SemiBold);
        ItalicToggle.IsChecked = runs.Count > 0 && runs.All(r => r.FontStyle == FontStyles.Italic);
        UnderlineToggle.IsChecked = runs.Count > 0 && runs.All(r => RichTextHtml.HasDecorationAt(r, TextDecorationLocation.Underline));
        StrikeToggle.IsChecked = runs.Count > 0 && runs.All(r => RichTextHtml.HasDecorationAt(r, TextDecorationLocation.Strikethrough));
    }

    // ================================================================ 选区遍历

    /// <summary>选区覆盖到的 Run（工具条施加格式用）。用 TextPointer 逐段前进，天然处理列表 / 表格的嵌套。</summary>
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

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            color = Colors.Black;
            return false;
        }
    }

    // ================================================================ 键盘快捷键

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.B: OnBoldClick(sender, e); e.Handled = true; return;
                case Key.I: OnItalicClick(sender, e); e.Handled = true; return;
                case Key.U: OnUnderlineClick(sender, e); e.Handled = true; return;
                case Key.S: ViewModel?.SaveCurrentEntry(explicitSave: true); e.Handled = true; return;
                case Key.N: ViewModel?.NewEntry(); e.Handled = true; return;
            }
        }
    }

    private void OnPlainKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.S: ViewModel?.SaveCurrentEntry(explicitSave: true); e.Handled = true; break;
            case Key.N: ViewModel?.NewEntry(); e.Handled = true; break;
        }
    }

    // ================================================================ 拖入图片

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
        }
        e.Handled = true;
    }

    private static bool IsImage(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    }
}
