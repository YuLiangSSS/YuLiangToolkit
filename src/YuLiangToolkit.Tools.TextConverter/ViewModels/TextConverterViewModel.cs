using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.TextConverter.ViewModels;

/// <summary>
/// 文本转换器页的 ViewModel：输入框 + 转换下拉 + 结果 + 自定义快捷键列表。
///
/// <para><b>快捷键怎么做</b>：每条自定义快捷键注册到 <see cref="ShortcutHub"/>，
/// 按下时先给前台程序发 Ctrl+C 取选区、转换、再 Ctrl+V 粘回去（见 <see cref="SelectionReplacer"/>）。
/// 没选中任何文字时弹通知提示（用这个工具自己的通知设置）。</para>
/// </summary>
public sealed class TextConverterViewModel : ToolViewModelBase
{
    /// <summary>快捷键下拉里的一项（转换选择）。</summary>
    public sealed record ConversionOption(string Id, string Name, string Group)
    {
        /// <summary>分组展示用（「大小写 · 全大写」）。</summary>
        public string Display => $"{Group} · {Name}";
    }

    /// <summary>列表里的一行（包一层，便于刷新显示）。</summary>
    public sealed class ShortcutRow
    {
        /// <summary>底层对象。</summary>
        public required ConvertShortcut Entry { get; init; }

        /// <summary>快捷键显示。</summary>
        public string HotkeyText => Entry.ConversationDisplay;

        /// <summary>转换名。</summary>
        public string ConversionName => Entry.ConversionName;

        /// <summary>开关状态。</summary>
        public string StateText => Entry.StateText;

        /// <summary>没启用时整行变淡。</summary>
        public double RowOpacity => Entry.Enabled ? 1 : 0.45;
    }

    private readonly TextConverterTool _tool;
    private readonly DispatcherTimer _busyTimer;
    private bool _applying;

    public TextConverterViewModel(TextConverterTool tool) : base(TextConverterConfig.ToolId)
    {
        _tool = tool;
        Cfg = LoadConfig<TextConverterConfig>();

        ConvertCommand = new RelayCommand(_ => ConvertInput(), _ => Input.Length > 0);
        ClearCommand = new RelayCommand(_ => { Input = ""; Result = ""; StatusText = "已清空"; });
        CopyResultCommand = new RelayCommand(_ => CopyResult(), _ => Result.Length > 0);
        AddShortcutCommand = new RelayCommand(_ => AddShortcut());
        RemoveShortcutCommand = new RelayCommand(p => RemoveShortcut(p as ConvertShortcut), p => p is ConvertShortcut);
        ToggleShortcutCommand = new RelayCommand(p => ToggleShortcut(p as ConvertShortcut), p => p is ConvertShortcut);
        UseResultAsInputCommand = new RelayCommand(_ => { Input = Result; StatusText = "已把结果放回输入框（可以接着再转一次）"; },
            _ => Result.Length > 0);
        SendTestNotificationCommand = new RelayCommand(_ => SendTestNotification());

        // 转换选项（按分组排序）
        foreach (var conversion in TextTransformer.All)
        {
            ConversionOptions.Add(new ConversionOption(conversion.Id, conversion.Name, conversion.Group));
        }

        SelectedConversion = ConversionOptions.FirstOrDefault(o => o.Id == Cfg.LastConversionId)
                             ?? ConversionOptions.FirstOrDefault(o => o.Id == "emoji")
                             ?? ConversionOptions.First();

        Input = Cfg.Input;
        Cfg.PropertyChanged += OnConfigChanged;
        Cfg.Shortcuts.CollectionChanged += (_, _) =>
        {
            HookShortcuts();
            Ui(RefreshShortcutRows);
        };
        HookShortcuts();

        _busyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _busyTimer.Tick += (_, _) =>
        {
            _busyTimer.Stop();
            IsBusy = false;
        };

        RefreshShortcutRows();
        Log($"配置文件：{Cfg.FilePath}");
        Log($"自定义快捷键 {Cfg.Shortcuts.Count} 条（启用 {Cfg.EnabledShortcutCount} 条）");
    }

    /// <summary>强类型配置。</summary>
    public TextConverterConfig Cfg { get; }

    // ---------- 命令 ----------

    public RelayCommand ConvertCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyResultCommand { get; }
    public RelayCommand AddShortcutCommand { get; }
    public RelayCommand RemoveShortcutCommand { get; }
    public RelayCommand ToggleShortcutCommand { get; }
    public RelayCommand UseResultAsInputCommand { get; }
    public RelayCommand SendTestNotificationCommand { get; }

    // ---------- 输入 / 输出 ----------

    private string _input = "";

    /// <summary>输入文本。</summary>
    public string Input
    {
        get => _input;
        set
        {
            if (!Set(ref _input, value ?? "")) return;
            Cfg.Input = _input;
            OnPropertyChanged(nameof(InputSummary));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private string _result = "";

    /// <summary>转换结果。</summary>
    public string Result
    {
        get => _result;
        private set
        {
            if (!Set(ref _result, value)) return;
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>有没有结果。</summary>
    public bool HasResult => Result.Length > 0;

    /// <summary>输入是不是空的。</summary>
    public bool IsEmpty => Input.Length == 0;

    /// <summary>输入字数统计。</summary>
    public string InputSummary
    {
        get
        {
            var chars = Input.Length;
            var lines = Input.Length == 0 ? 0 : Input.Split('\n').Length;
            return chars == 0 ? "输入或粘贴文本，然后点「转换」" : $"{chars} 个字符 · {lines} 行";
        }
    }

    // ---------- 转换选择 ----------

    /// <summary>全部转换选项。</summary>
    public ObservableCollection<ConversionOption> ConversionOptions { get; } = new();

    private ConversionOption? _selectedConversion;

    /// <summary>当前选的转换。</summary>
    public ConversionOption? SelectedConversion
    {
        get => _selectedConversion;
        set
        {
            if (!Set(ref _selectedConversion, value) || value is null) return;
            Cfg.LastConversionId = value.Id;
            OnPropertyChanged(nameof(SelectedConversionDescription));
        }
    }

    /// <summary>当前转换的说明。</summary>
    public string SelectedConversionDescription
        => TextTransformer.Find(_selectedConversion?.Id)?.Description ?? "";

    // ---------- 快捷键 ----------

    /// <summary>快捷键列表。</summary>
    public ObservableCollection<ShortcutRow> ShortcutRows { get; } = new();

    /// <summary>一条快捷键都没有（空状态）。</summary>
    public bool NoShortcuts => ShortcutRows.Count == 0;

    /// <summary>快捷键统计。</summary>
    public string ShortcutSummary
        => Cfg.Shortcuts.Count == 0
            ? "还没有自定义快捷键"
            : $"共 {Cfg.Shortcuts.Count} 条 · 启用 {Cfg.EnabledShortcutCount} 条";

    private bool _isBusy;

    /// <summary>正在转换（防连按）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    // ---------- 操作 ----------

    /// <summary>转换输入框里的文本（不用快捷键的普通用法）。</summary>
    public void ConvertInput()
    {
        if (_selectedConversion is null) return;

        var (converted, error) = TextTransformer.Apply(Input, _selectedConversion.Id);
        if (error.Length > 0)
        {
            Result = "";
            StatusText = error;
            return;
        }

        Result = converted;
        StatusText = string.Equals(converted, Input, StringComparison.Ordinal)
            ? "转换后没有变化（这段文字不适合这种转换）"
            : $"已转换（{_selectedConversion.Name}）";
    }

    /// <summary>复制结果到剪贴板。</summary>
    public void CopyResult()
    {
        try
        {
            System.Windows.Clipboard.SetText(Result);
            StatusText = "已复制结果";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败（剪贴板被占用？）：" + ex.Message;
        }
    }

    /// <summary>加一条快捷键。</summary>
    public void AddShortcut()
    {
        var shortcut = new ConvertShortcut
        {
            Hotkey = "",
            ConversionId = _selectedConversion?.Id ?? "emoji",
            Enabled = true,
        };

        Cfg.Shortcuts.Add(shortcut);
        StatusText = "已添加一条，点框设置快捷键";
        SyncShortcuts();
    }

    /// <summary>删一条。</summary>
    public void RemoveShortcut(ConvertShortcut? shortcut)
    {
        if (shortcut is null || !Cfg.Shortcuts.Remove(shortcut)) return;

        StatusText = "已删除一条快捷键";
        SyncShortcuts();
    }

    /// <summary>开 / 关一条。</summary>
    public void ToggleShortcut(ConvertShortcut? shortcut)
    {
        if (shortcut is null) return;

        shortcut.Enabled = !shortcut.Enabled;
        SyncShortcuts();
    }

    /// <summary>
    /// 按当前配置注册所有自定义快捷键。
    /// </summary>
    public void SyncShortcuts()
    {
        var engine = ShortcutHub.Engine;
        if (engine is null)
        {
            RefreshShortcutRows();
            return;
        }

        // 先全撤掉（配置改了就重建，避免旧组合残留）
        foreach (var shortcut in Cfg.Shortcuts) engine.Unregister(IdOf(shortcut));

        if (Cfg.ShortcutsEnabled)
        {
            foreach (var shortcut in Cfg.Shortcuts)
            {
                if (!shortcut.Enabled || string.IsNullOrWhiteSpace(shortcut.Hotkey)) continue;
                if (!KeyCombo.TryParse(shortcut.Hotkey, out var combo) || combo.IsEmpty) continue;

                var conversionId = shortcut.ConversionId;
                var ok = engine.Register(IdOf(shortcut), ShortcutKind.Single, combo, null,
                    _ => RunSelectionConversion(conversionId), out var error);

                if (!ok && !string.IsNullOrWhiteSpace(error)) Log($"快捷键 {shortcut.Hotkey} 注册失败：{error}");
            }
        }

        RefreshShortcutRows();
        OnPropertyChanged(nameof(ShortcutSummary));
        OnPropertyChanged(nameof(NoShortcuts));
    }

    /// <summary>每条快捷键的引擎编号（用 Id 的稳定哈希，重启后也不会和别的工具撞）。</summary>
    private static int IdOf(ConvertShortcut shortcut)
    {
        // 取 Id 的哈希，再压到正整数区间；同一工具内 Id 唯一就够了
        var hash = StringComparer.Ordinal.GetHashCode(shortcut.Id) & 0x3FFFFFFF;
        return 0x6000_0000 | hash;
    }

    /// <summary>
    /// 快捷键按下：转换当前前台程序的选中文本。
    /// <para>⚠ 这里在快捷键的键盘钩子回调里，必须尽快返回，所以 <c>async void</c> 排个任务出去。</para>
    /// </summary>
    private void RunSelectionConversion(string conversionId)
    {
        if (IsBusy) return;

        IsBusy = true;
        _busyTimer.Stop();
        _busyTimer.Start();

        _ = ConvertSelectionAsync(conversionId);
    }

    private async Task ConvertSelectionAsync(string conversionId)
    {
        try
        {
            var result = await SelectionReplacer.ConvertSelectionAsync(conversionId, Cfg.RestoreClipboard)
                .ConfigureAwait(true);

            if (result.Success)
            {
                StatusText = $"已转换：{result.Before} → {result.After}";
                Log($"转换成功（{TextTransformer.Find(conversionId)?.Name}）：{result.Before} → {result.After}");

                // 顺便把结果填进页面，方便用户看
                Result = result.After;
                return;
            }

            StatusText = result.Message;
            Log("转换失败：" + result.Message);

            // 「没有选中文本」这类要弹通知（用户在别的程序里，不弹根本不知道失败）
            NotificationCenter.Show(Cfg.Notification,
                new NotificationRequest("文本转换", result.Message, NotificationKind.Warning,
                    TextConverterConfig.ToolId));
        }
        catch (Exception ex)
        {
            StatusText = "转换出错：" + ex.Message;
            Log("转换出错：" + ex);
        }
        finally
        {
            IsBusy = false;
            _busyTimer.Stop();
        }
    }

    /// <summary>发一条测试通知。</summary>
    public void SendTestNotification()
    {
        var ok = NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest("文本转换器 · 测试通知",
                $"通道：{(Cfg.Notification.Mode == NotificationMode.InApp ? "应用内" : "Windows")}"
                + $" · 位置：{NotificationSettings.CornerName(Cfg.Notification.Corner)}",
                NotificationKind.Success, TextConverterConfig.ToolId));

        StatusText = ok ? "已发送测试通知" : "通知没发出去（通道不可用）";
    }

    // ---------- 内部 ----------

    private void RefreshShortcutRows()
    {
        ShortcutRows.Clear();
        foreach (var shortcut in Cfg.Shortcuts)
        {
            ShortcutRows.Add(new ShortcutRow { Entry = shortcut });
        }

        OnPropertyChanged(nameof(NoShortcuts));
        OnPropertyChanged(nameof(ShortcutSummary));
    }

    private void HookShortcuts()
    {
        foreach (var shortcut in Cfg.Shortcuts)
        {
            shortcut.PropertyChanged -= OnShortcutChanged;
            shortcut.PropertyChanged += OnShortcutChanged;
        }
    }

    private void OnShortcutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;

        _applying = true;
        try
        {
            SyncShortcuts();
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextConverterConfig.ShortcutsEnabled)
            or nameof(TextConverterConfig.RestoreClipboard))
        {
            SyncShortcuts();
        }
    }

    /// <summary>工具被切走 / 关闭：停掉忙碌计时器。</summary>
    protected override void OnClosed() => _busyTimer.Stop();
}
