using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.TextEditor.Core;

namespace YuLiangToolkit.Tools.TextEditor.ViewModels;

/// <summary>
/// 一个编辑器标签：文件路径（<c>null</c> = 无标题）、标题、未保存标记，以及文档与编码信息。
/// <see cref="Document"/> 是 AvalonEdit 的 <see cref="ICSharpCode.AvalonEdit.Document.TextDocument"/>，
/// 由标签持有（View 里每个标签一个 <c>TextEditor</c> 控件挂同一份文档，切标签不丢光标 / 撤销栈）。
/// </summary>
public sealed class EditorTab : ObservableObject
{
    private string? _filePath;
    private string _title;
    private bool _isDirty;
    private TextFileEncodingKind _encoding = TextFileCodec.DefaultEncoding;
    private TextNewlineStyle _newline = TextNewlineStyle.CrLf;
    private bool _suppressDirty;
    private DateTime? _lastKnownWriteTimeUtc;

    public EditorTab(string? filePath, string title)
    {
        _filePath = filePath;
        _title = title;
        Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
        Document.TextChanged += OnDocumentTextChanged;
    }

    /// <summary>从文件内容建标签（编码 / 换行风格按探测结果记录，保存时原样写回）。</summary>
    public static EditorTab FromFile(string path, TextFileContent content)
    {
        var tab = new EditorTab(path, Path.GetFileName(path))
        {
            Encoding = content.Encoding,
            Newline = content.Newline,
        };
        tab.SetTextSilently(content.Text);
        tab.RememberFileWriteTime();
        return tab;
    }

    /// <summary>建一个「无标题」标签（<paramref name="index"/> 从 1 开始：无标题 / 无标题 2 / …）。</summary>
    public static EditorTab Untitled(int index) => new(null, UntitledTitle(index));

    /// <summary>文档（数据层；界面层的 <c>TextEditor.Document</c> 指向它）。</summary>
    public ICSharpCode.AvalonEdit.Document.TextDocument Document { get; }

    /// <summary>文件完整路径；<c>null</c> = 还没保存过文件的无标题标签。</summary>
    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (Set(ref _filePath, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(FilePathText));
            }
        }
    }

    /// <summary>标签标题（文件名 / 无标题 N；另存为后更新）。</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value)) OnPropertyChanged(nameof(Header));
        }
    }

    /// <summary>是否有未保存的更改（文档 TextChanged 置位，保存后清零）。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(Header));
        }
    }

    /// <summary>标签上显示的文字：未保存加 ● 前缀（<see cref="ComposeHeader"/>）。</summary>
    public string Header => ComposeHeader(IsDirty, Title);

    /// <summary>状态栏里显示的文件位置（无标题时给一句说明）。</summary>
    public string FilePathText => FilePath ?? "（尚未保存到文件）";

    /// <summary>文件编码（读入时探测；新建 = UTF-8 无 BOM；保存按它写回）。</summary>
    public TextFileEncodingKind Encoding
    {
        get => _encoding;
        set => Set(ref _encoding, value);
    }

    /// <summary>换行风格（读入时探测；保存时把全文归一成它）。</summary>
    public TextNewlineStyle Newline
    {
        get => _newline;
        set => Set(ref _newline, value);
    }

    /// <summary>文档内容变化（含载入；状态栏字数统计跟着刷）。</summary>
    public event EventHandler? TextChanged;

    /// <summary>
    /// 最近一次读 / 写文件时记下的文件修改时间（UTC）——自动保存用它判断文件是否被别的程序改过，
    /// 避免悄悄覆盖外部改动。读文件失败 / 无此文件时为 <c>null</c>（不做外部改动判断）。
    /// </summary>
    public DateTime? LastKnownWriteTimeUtc
    {
        get => _lastKnownWriteTimeUtc;
        private set => Set(ref _lastKnownWriteTimeUtc, value);
    }

    /// <summary>
    /// 自动保存是否因为这个文件被外部改动而暂停：暂停后自动保存不再写这个标签，
    /// 用户手动 Ctrl+S 保存（或重新打开文件）会解除。
    /// </summary>
    public bool AutoSaveBlocked { get; set; }

    /// <summary>记下文件当前的修改时间（读入 / 保存成功后调用；文件不存在 / 无权限时静默）。</summary>
    public void RememberFileWriteTime()
    {
        if (FilePath is not { } path)
        {
            LastKnownWriteTimeUtc = null;
            return;
        }

        try
        {
            LastKnownWriteTimeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            LastKnownWriteTimeUtc = null;
        }
    }

    /// <summary>把内容塞进文档（载入 / 全部替换）而不点亮未保存标记。</summary>
    public void SetTextSilently(string text)
    {
        _suppressDirty = true;
        try
        {
            Document.Text = text;
        }
        finally
        {
            _suppressDirty = false;
        }
        IsDirty = false;
    }

    /// <summary>保存成功后调用：清未保存标记，记录最终路径 / 标题 / 编码 / 换行。</summary>
    public void MarkSaved(string path, TextFileEncodingKind encoding, TextNewlineStyle newline)
    {
        _suppressDirty = true;
        try
        {
            FilePath = path;
            Title = Path.GetFileName(path);
            Encoding = encoding;
            Newline = newline;
        }
        finally
        {
            _suppressDirty = false;
        }
        IsDirty = false;
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(FilePathText));
        AutoSaveBlocked = false;
        RememberFileWriteTime();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (!_suppressDirty) IsDirty = true;
        TextChanged?.Invoke(this, e);
    }

    // ---------------- 纯函数（自检直接断言） ----------------

    /// <summary>标签标题规则：未保存加 ● 前缀。</summary>
    public static string ComposeHeader(bool isDirty, string title)
        => isDirty ? "● " + title : title;

    /// <summary>无标题标签的命名：第一个叫「无标题」，之后「无标题 2 / 3 / …」。</summary>
    public static string UntitledTitle(int index)
        => index <= 1 ? "无标题" : $"无标题 {Math.Max(1, index)}";

    /// <summary>
    /// 关掉一个标签后该激活谁（纯函数）：
    /// 关激活标签 → 原地补位（末尾退一格）；关别的 → 激活位置不动（在前面退一格）；全关 → <c>-1</c>。
    /// </summary>
    public static int NextActiveAfterRemove(int count, int removeIndex, int activeIndex)
    {
        if (count <= 1) return -1;
        var active = Math.Clamp(activeIndex, 0, count - 1);
        var removed = Math.Clamp(removeIndex, 0, count - 1);
        if (removed < active) return active - 1;
        if (removed > active) return active;
        return Math.Min(active, count - 2);
    }
}
