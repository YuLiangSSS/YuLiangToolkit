using System.Collections.ObjectModel;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Diary.ViewModels;

/// <summary>
/// 「日记本」的 ViewModel：管历史列表 / 选中 / 新建 / 删除 / 搜索 / 排序 / 自动保存。
///
/// <para><b>编辑体在界面层</b>：富文本编辑区是 WPF 的 <c>RichTextBox</c>（还要跟 HTML 互转），
/// 所以 ViewModel 只通过两个委托跟界面交换正文——
/// <see cref="LoadEntryIntoEditor"/>（把一篇日记载入编辑区）与
/// <see cref="ReadEditorContent"/>（读回当前正文）。这样它自己仍然可以无头调用（自检 / 自动化不建窗口）。</para>
///
/// <para><b>保存时机</b>：切换日记 / 新建 / 删除 / 点保存 / 切换写法 / 关闭工具时保存；
/// 边打字时由界面上的定时器调用 <see cref="SaveCurrentEntry"/> 静默落盘（见 DiaryView 代码后置）。</para>
/// </summary>
public sealed class DiaryViewModel : ToolViewModelBase
{
    private readonly List<DiaryEntry> _all = new();
    private DiaryEntry? _selected;
    private string _draftTitle = "";
    private string _draftMood = "";
    private string _keyword = "";
    private string _savedText = "";
    private bool _suppressSave;

    public DiaryViewModel() : base(DiaryConfig.ToolId)
    {
        Cfg = LoadConfig<DiaryConfig>();
        Store = new DiaryStore(Storage);

        NewCommand = new RelayCommand(_ => NewEntry());
        DeleteCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedEntry is not null);
        SaveCommand = new RelayCommand(_ => SaveCurrentEntry(explicitSave: true));

        LoadAllEntries();

        if (Entries.Count > 0) SelectedEntry = Entries[0];

        Log("===== 日记本 =====");
        Log($"数据目录：{Store.EntriesDirectory}");
        Log($"已有日记：{_all.Count} 篇");
        if (_all.Count == 0) Log("还没有日记，点右上角「＋ 新建日记」写第一篇。");
    }

    // ---------------- 基础设施 ----------------

    /// <summary>强类型配置。</summary>
    public DiaryConfig Cfg { get; }

    /// <summary>日记持久化（每篇一个 JSON）。</summary>
    public DiaryStore Store { get; }

    // ---------------- 界面委托（由 DiaryView 代码后置注入） ----------------

    /// <summary>界面提供：把一篇日记载入编辑区（富文本 → FlowDocument，普通文本 → 纯文本框）。</summary>
    public Action<DiaryEntry>? LoadEntryIntoEditor { get; set; }

    /// <summary>界面提供：读回编辑区当前正文（富文本返回 HTML，普通文本返回纯文字）。</summary>
    public Func<string>? ReadEditorContent { get; set; }

    /// <summary>界面提供：删除前确认（返回 false 取消）。为空时视为直接确认。</summary>
    public Func<DiaryEntry, bool>? ConfirmDelete { get; set; }

    /// <summary>界面提供：把焦点放到正文编辑区（新建后方便直接写）。</summary>
    public Action? FocusEditor { get; set; }

    // ---------------- 列表 ----------------

    /// <summary>筛选 / 排序后的日记列表（界面 ListBox 绑这个）。</summary>
    public ObservableCollection<DiaryEntry> Entries { get; } = new();

    /// <summary>当前选中的日记。</summary>
    public DiaryEntry? SelectedEntry
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;

            if (_selected is not null) SaveCurrentEntry();   // 先把手头这篇的改动落盘

            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNoSelection));
            OnPropertyChanged(nameof(SelectedFullDateText));
            OnPropertyChanged(nameof(WordCountText));
            OnPropertyChanged(nameof(SelectedUpdatedText));

            if (value is not null)
            {
                _draftTitle = value.Title;
                _draftMood = value.Mood;
                IsDirty = false;
                SavedText = "已保存";
                LoadEntryIntoEditor?.Invoke(value);
            }
            else
            {
                _draftTitle = "";
                _draftMood = "";
            }

            OnPropertyChanged(nameof(DraftTitle));
            OnPropertyChanged(nameof(DraftMood));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>有没有选中日记（编辑区可见）。</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>没选中（显示空状态提示）。</summary>
    public bool HasNoSelection => _selected is null;

    /// <summary>标题输入框绑它：改标题即时反映到列表（那一篇的 Title 会发通知），并标脏等自动保存。</summary>
    public string DraftTitle
    {
        get => _draftTitle;
        set
        {
            if (!Set(ref _draftTitle, value ?? "")) return;
            if (_selected is null) return;
            _selected.Title = value ?? "";
            _selected.UpdatedAt = DateTime.Now;
            MarkDirty();
        }
    }

    /// <summary>心情 emoji。</summary>
    public string DraftMood
    {
        get => _draftMood;
        set
        {
            if (!Set(ref _draftMood, value ?? "")) return;
            if (_selected is null) return;
            _selected.Mood = value ?? "";
            _selected.UpdatedAt = DateTime.Now;
            MarkDirty();
        }
    }

    /// <summary>编辑区顶部显示的完整日期。</summary>
    public string SelectedFullDateText => _selected?.FullDateText ?? "";

    /// <summary>最后修改时间文字。</summary>
    public string SelectedUpdatedText => _selected?.UpdatedText ?? "";

    /// <summary>当前字数文字。</summary>
    public string WordCountText => _selected?.WordCountText ?? "0 字";

    /// <summary>列表是否为空（没有任何日记，或搜索没命中）。</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>是否一篇日记都还没有（区别于「搜不到」）。</summary>
    public bool HasAnyEntry => _all.Count > 0;

    /// <summary>是否搜索没命中（有日记但筛选后为空）。</summary>
    public bool IsFilteredEmpty => _all.Count > 0 && Entries.Count == 0;

    /// <summary>底部统计文字。</summary>
    public string SummaryText
    {
        get
        {
            var total = _all.Count;
            var shown = Entries.Count;
            return shown == total ? $"共 {total} 篇" : $"显示 {shown} / 共 {total} 篇";
        }
    }

    /// <summary>搜索关键词（标题 / 正文）。</summary>
    public string Keyword
    {
        get => _keyword;
        set
        {
            if (!Set(ref _keyword, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>排序：true = 最新创建在前。</summary>
    public bool SortNewestFirst
    {
        get => Cfg.SortNewestFirst;
        set
        {
            if (Cfg.SortNewestFirst == value) return;
            Cfg.SortNewestFirst = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SortText));
            ApplyFilter();
        }
    }

    /// <summary>排序说明文字。</summary>
    public string SortText => Cfg.SortNewestFirst ? "最新在前" : "最早在前";

    /// <summary>有没有未保存的改动（界面定时器据此决定要不要落盘）。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>保存状态文字（编辑区底部）。</summary>
    public string SavedText
    {
        get => _savedText;
        private set => Set(ref _savedText, value ?? "");
    }

    // ---------------- 命令 ----------------

    public RelayCommand NewCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand SaveCommand { get; }

    // ---------------- 编辑操作 ----------------

    /// <summary>标记「正文 / 标题有改动」，等界面定时器（或切换 / 关闭）落盘。</summary>
    public void MarkDirty() => IsDirty = true;

    /// <summary>给心情挑一个 emoji（点了立刻改）。</summary>
    public void SetMood(string emoji) => DraftMood = emoji ?? "";

    /// <summary>
    /// 把当前编辑区内容写回选中的日记并落盘。
    /// <paramref name="explicitSave"/> = 用户点了「保存」：即使没标脏也强制写一次。
    /// </summary>
    public void SaveCurrentEntry(bool explicitSave = false)
    {
        if (_suppressSave) return;
        var entry = _selected;
        if (entry is null) return;

        var content = ReadEditorContent?.Invoke();
        var contentChanged = content is not null && !string.Equals(entry.Content, content, StringComparison.Ordinal);
        if (content is not null) entry.Content = content;

        if (!contentChanged && !IsDirty && !explicitSave) return;

        entry.UpdatedAt = DateTime.Now;
        IsDirty = false;
        var ok = Store.Save(entry);
        SavedText = ok ? $"已保存 · {DateTime.Now:HH:mm:ss}" : "保存失败（磁盘只读？）";
        StatusText = ok ? "已保存" : "保存失败";
        OnPropertyChanged(nameof(WordCountText));
        OnPropertyChanged(nameof(SelectedUpdatedText));
    }

    /// <summary>切换写法：富文本 ⇄ 普通文本（保留文字内容，尽量保住格式 / 换行）。</summary>
    public void ChangeFormat(DiaryFormat target)
    {
        var entry = _selected;
        if (entry is null || entry.Format == target) return;

        var content = ReadEditorContent?.Invoke() ?? entry.Content;   // 此时 entry.Format 还是「旧写法」

        var newContent = target == DiaryFormat.PlainText
            ? DiaryText.ToPlainText(content, entry.Format)                  // 富文本 → 纯文字
            : DiaryText.FromPlainText(DiaryText.ToPlainText(content, entry.Format)); // 纯文字 → 富文本

        entry.Format = target;
        entry.Content = newContent;
        entry.UpdatedAt = DateTime.Now;
        IsDirty = false;

        var ok = Store.Save(entry);
        SavedText = ok ? $"已保存 · {DateTime.Now:HH:mm:ss}" : "保存失败";
        StatusText = ok ? $"已切换为{entry.FormatText}" : "切换写法后保存失败";

        LoadEntryIntoEditor?.Invoke(entry);   // 让编辑区换成对应的显示
        OnPropertyChanged(nameof(WordCountText));
        OnPropertyChanged(nameof(SelectedUpdatedText));
    }

    // ---------------- 新建 / 删除 ----------------

    /// <summary>新建一篇今天的日记。</summary>
    public void NewEntry(DiaryFormat? format = null)
    {
        var entry = new DiaryEntry
        {
            Id = DiaryStore.NewId(),
            Title = "",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            Format = format ?? Cfg.DefaultFormat,
            Content = "",
        };

        _all.Insert(0, entry);
        Store.Save(entry);
        ApplyFilter();

        SelectedEntry = entry;      // setter 会保存上一篇 + 载入这一篇
        MarkDirty();
        FocusEditor?.Invoke();
        StatusText = "已新建一篇日记";
        Log($"新建日记：{entry.Id}");
    }

    /// <summary>删除当前选中的日记（删除前会问一次）。</summary>
    public void DeleteSelected()
    {
        var entry = _selected;
        if (entry is null) return;

        if (ConfirmDelete is not null && !ConfirmDelete(entry))
        {
            StatusText = "已取消删除";
            return;
        }

        _suppressSave = true;      // 防止选下一篇时把刚删的那篇又保存回来
        try
        {
            Store.Delete(entry.Id);
            _all.Remove(entry);
            ApplyFilter();
            SelectedEntry = Entries.FirstOrDefault();
        }
        finally
        {
            _suppressSave = false;
        }

        StatusText = "已删除一篇日记";
        Log($"删除日记：{entry.DisplayTitle}");
    }

    // ---------------- 加载 / 过滤 ----------------

    /// <summary>从磁盘读回全部日记并刷新列表。</summary>
    public void LoadAllEntries()
    {
        _all.Clear();
        _all.AddRange(Store.LoadAll());
        ApplyFilter();
    }

    /// <summary>按关键词筛选 + 按创建时间排序，重建界面列表。重建期间尽量保住当前选中的那一篇。</summary>
    private void ApplyFilter()
    {
        var keep = _selected;
        var keyword = _keyword.Trim();
        IEnumerable<DiaryEntry> query = _all;

        if (keyword.Length > 0)
        {
            query = query.Where(entry =>
                entry.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                || DiaryText.ToPlainText(entry.Content, entry.Format)
                    .Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        query = Cfg.SortNewestFirst
            ? query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.UpdatedAt)
            : query.OrderBy(e => e.CreatedAt).ThenBy(e => e.UpdatedAt);

        // 重建列表时 ListBox 会先把 SelectedItem 置空（触发 SelectedEntry 的 setter）；
        // 这里临时捂住保存，避免「只是搜一下 / 换个排序」就写盘、还把当前这篇的编辑器关掉。
        var previousSuppress = _suppressSave;
        _suppressSave = true;
        try
        {
            Entries.Clear();
            foreach (var entry in query) Entries.Add(entry);
        }
        finally
        {
            _suppressSave = previousSuppress;
        }

        // 当前这篇还在筛选结果里：把它重新选回来（编辑器保持原样，不重新载入）。
        if (keep is not null && Entries.Contains(keep))
        {
            _selected = keep;
            _draftTitle = keep.Title;
            _draftMood = keep.Mood;
            OnPropertyChanged(nameof(SelectedEntry));
            OnPropertyChanged(nameof(DraftTitle));
            OnPropertyChanged(nameof(DraftMood));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNoSelection));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAnyEntry));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>工具被切走 / 关闭：把手头这篇的改动落盘。</summary>
    protected override void OnClosed()
    {
        try { SaveCurrentEntry(); }
        catch { /* 退出流程忽略 */ }
    }
}
