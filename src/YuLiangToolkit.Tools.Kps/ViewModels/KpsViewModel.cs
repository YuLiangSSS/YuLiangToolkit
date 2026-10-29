using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Kps.Models;
using YuLiangToolkit.Tools.Kps.Presets;
using YuLiangToolkit.Tools.Kps.Services;
using YuLiangToolkit.Tools.Kps.Statistics;

namespace YuLiangToolkit.Tools.Kps.ViewModels;

/// <summary>
/// 「KPS 按键统计」的主界面 ViewModel：键位预览 / 编辑、预设、统计卡片与折线图、
/// 外观与音符效果、Overlay 设置、全局热键、键音。
///
/// <para>真正的「全局输入 + Overlay 生命周期」在 <see cref="KpsSession"/>（整个进程一个）：
/// 页面切走时如果 Overlay 还开着，会话继续跑；本 ViewModel 会一直活着（会话持有引用），
/// 所以 Overlay 的数据绑定始终有效。</para>
/// </summary>
public sealed class KpsViewModel : ToolViewModelBase
{
    /// <summary>DmNote 项目主页（工具说明里标注来源用）。</summary>
    public const string DmNoteUrl = "https://github.com/DmNote-App/DmNote";

    private readonly Dictionary<string, ImageSource> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Brush> _brushCache = new(StringComparer.OrdinalIgnoreCase);

    private KpsKeyCell? _selectedCell;
    private KpsKey? _selectedModel;
    private bool _syncingPreset;
    private bool _isCapturingKey;
    private bool _isCapturingHotkey;
    private string _captureHint = "";
    private string _newPresetName = "";
    private IReadOnlyList<double> _graphValues = Array.Empty<double>();
    private double _graphMax = 1;
    private string _kpsText = "0";
    private string _peakText = "0";
    private string _averageText = "0";
    private string _totalText = "0";
    private string _durationText = "00:00";

    public KpsViewModel() : base(KpsTool.ToolId)
    {
        Cfg = LoadConfig<KpsConfig>();
        Session = KpsSession.Current;
        LayoutRows = new ObservableCollection<KpsRowView>();

        ToggleListeningCommand = new RelayCommand(_ => Session.ToggleListening());
        ToggleOverlayCommand = new RelayCommand(_ => Session.ToggleOverlay());
        ToggleOverlayLockCommand = new RelayCommand(_ => Session.ToggleOverlayLock());
        CenterOverlayCommand = new RelayCommand(_ => Session.CenterOverlay());
        ResetStatsCommand = new RelayCommand(_ => ResetStats());
        ApplyPresetCommand = new RelayCommand(p =>
        {
            if (p is OptionItem<string> option) SwitchPreset(option.Value);
            else if (p is string id) SwitchPreset(id);
        });
        SavePresetCommand = new RelayCommand(_ => SaveCustomPreset());
        DeletePresetCommand = new RelayCommand(_ => DeleteSelectedCustomPreset());
        ResetPresetLayoutCommand = new RelayCommand(_ => ResetCurrentPresetLayout());
        AddRowCommand = new RelayCommand(_ => AddRow());
        AddKeyCommand = new RelayCommand(_ => AddKey());
        DeleteKeyCommand = new RelayCommand(_ => DeleteSelectedKey(), _ => _selectedCell is not null);
        DeleteRowCommand = new RelayCommand(p => DeleteRow(p as KpsRowView));
        MoveKeyLeftCommand = new RelayCommand(_ => MoveSelectedKey(-1));
        MoveKeyRightCommand = new RelayCommand(_ => MoveSelectedKey(1));
        MoveRowUpCommand = new RelayCommand(_ => MoveSelectedRow(-1));
        MoveRowDownCommand = new RelayCommand(_ => MoveSelectedRow(1));
        SelectKeyCommand = new RelayCommand(p =>
        {
            if (p is KpsKeyCell cell) SelectCell(cell);
        });
        BeginCaptureKeyCommand = new RelayCommand(_ => BeginBindCapture());
        CancelCaptureKeyCommand = new RelayCommand(_ => CancelCapture());
        PickImageCommand = new RelayCommand(_ => PickImage());
        ClearImageCommand = new RelayCommand(_ => ClearImage());
        OpenDataFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        BrowseSoundCommand = new RelayCommand(_ => BrowseSound());
        ClearSoundCommand = new RelayCommand(_ => ClearSound());
        TestSoundCommand = new RelayCommand(_ => Session.TestSound());
        OpenSourceCommand = new RelayCommand(_ => OpenSource());
        SetHotkeyCommand = new RelayCommand(p => BeginHotkeyCapture(p as string));
        ClearHotkeyCommand = new RelayCommand(p => ClearHotkey(p as string));

        Cfg.PropertyChanged += OnConfigChanged;

        EnsureConfigSanity();
        RefreshPresetOptions();
        RebuildLayout();
        Log($"数据目录：{Storage.Directory}");
        Log($"配置文件：{Cfg.FilePath}");
        if (IsFirstRun)
        {
            Log("首次使用：默认 4K 布局（D F J K）。点右上角「显示 Overlay」即可在游戏上叠加键位显示。");
        }
        Log("功能灵感来自开源项目 DmNote（GPL-3.0）；本工具为独立重写，未包含其源码。");
    }

    // ===================== 配置 / 会话 =====================

    /// <summary>强类型配置（<c>Config\kps\config.json</c>，改一下自动保存）。</summary>
    public KpsConfig Cfg { get; }

    /// <summary>后台会话（输入 / 统计 / Overlay / 热键）。</summary>
    public KpsSession Session { get; }

    private void EnsureConfigSanity()
    {
        // 手改配置 / 老版本缺字段时兜底，保证界面至少有一套可用布局
        Cfg.KeyRows ??= KpsPresets.Default.CloneRows();
        foreach (var row in Cfg.KeyRows) row.Keys ??= new List<KpsKey>();
        Cfg.KeyRows.RemoveAll(row => row is null);
        if (Cfg.KeyRows.Count == 0) Cfg.KeyRows = KpsPresets.Default.CloneRows();

        if (KpsPresets.Find(Cfg.ActivePresetId) is null
            && !Cfg.CustomPresets.Any(p => string.Equals(p.Id, Cfg.ActivePresetId, StringComparison.OrdinalIgnoreCase)))
        {
            Cfg.ActivePresetId = KpsPresets.DefaultId;
        }

        Cfg.CustomPresets ??= new List<KpsPreset>();
        Cfg.PresetLayouts ??= new Dictionary<string, List<KpsKeyRow>>();
        Cfg.SavedKeyCounters ??= new Dictionary<string, long>();
    }

    // ===================== 生命周期 =====================

    /// <summary>页面关闭（切走）：交给会话决定继续跑（Overlay 开着）还是停下。</summary>
    protected override void OnClosed()
    {
        try { Session.Deactivate(); } catch { /* 关闭流程不抛异常 */ }
    }

    /// <summary>会话状态变了（监听 / Overlay / 热键 / 捕获）：刷新界面上的状态文字。</summary>
    public void UpdateRuntimeState()
    {
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(ListeningStatusText));
        OnPropertyChanged(nameof(ListeningButtonText));
        OnPropertyChanged(nameof(OverlayVisibleText));
        OnPropertyChanged(nameof(OverlayLockButtonText));
        OnPropertyChanged(nameof(HotkeyStatusText));
        OnPropertyChanged(nameof(OverlayUnlocked));
        OnPropertyChanged(nameof(HasHotkeyIssues));
        OnPropertyChanged(nameof(HasCaptureHint));
        OnPropertyChanged(nameof(CaptureHintText));
        OnPropertyChanged(nameof(IsCapturingKey));
        OnPropertyChanged(nameof(IsCapturingHotkey));
    }

    /// <summary>页面重新打开 / 启动时，按配置重建一遍键位与设置显示。</summary>
    public void RefreshFromConfig()
    {
        EnsureConfigSanity();
        RefreshPresetOptions();
        RebuildLayout();
        RefreshCellStyles();
        RaiseVisualBindings();
        UpdateRuntimeState();
    }

    // ===================== 键位布局 =====================

    /// <summary>当前布局的界面行（主界面预览 + Overlay 共用；重建时整体替换）。</summary>
    public ObservableCollection<KpsRowView> LayoutRows { get; }

    /// <summary>按配置重建界面行（切预设 / 增删键 / 启停用之后调用）。保留当前选中的键。</summary>
    private void RebuildLayout()
    {
        var selectedModel = _selectedModel;
        LayoutRows.Clear();

        for (var rowIndex = 0; rowIndex < Cfg.KeyRows.Count; rowIndex++)
        {
            var row = Cfg.KeyRows[rowIndex];
            var cells = new List<KpsKeyCell>();
            foreach (var key in row.Keys)
            {
                if (key is null || !key.Enabled) continue;
                var cell = new KpsKeyCell(key, row, rowIndex, cells.Count)
                {
                    Count = Session.GetDisplayKeyCount(key.VirtualKey),
                };
                cell.ApplyStyle(Cfg);
                LoadCellImage(cell);
                cells.Add(cell);
            }
            LayoutRows.Add(new KpsRowView(row, rowIndex, cells));
        }

        OnPropertyChanged(nameof(LayoutRows));
        OnPropertyChanged(nameof(HasLayout));

        // 恢复选中态（选中的键可能被删 / 被禁用）
        KpsKeyCell? restore = null;
        if (selectedModel is not null)
        {
            restore = LayoutRows.SelectMany(row => row.Cells)
                .FirstOrDefault(cell => ReferenceEquals(cell.Model, selectedModel));
        }
        SelectCell(restore, raiseNotifications: false);
        RefreshCounts();
        RaiseSelectedBindings();
    }

    private void RefreshCellStyles()
    {
        foreach (var cell in LayoutRows.SelectMany(row => row.Cells))
        {
            cell.ApplyStyle(Cfg);
            LoadCellImage(cell);
        }
    }

    /// <summary>刷新所有格子的计数（统计刷新 / 重置后调用）。</summary>
    public void RefreshCounts()
    {
        foreach (var cell in LayoutRows.SelectMany(row => row.Cells))
        {
            cell.Count = Session.GetDisplayKeyCount(cell.VirtualKey);
            cell.ShowCounter = Cfg.ShowKeyCounters && cell.Count > 0;
            if (Cfg.ShowKeyCounters && cell.Count > 0) cell.ApplyStyle(Cfg);
        }
        TotalText = KpsCountFormat.Format(ComputeLayoutTotal());
    }

    private long ComputeLayoutTotal()
    {
        var seen = new HashSet<int>();
        long total = 0;
        foreach (var cell in LayoutRows.SelectMany(row => row.Cells))
        {
            var vk = cell.VirtualKey;
            if (vk == 0 || !seen.Add(vk)) continue;
            total += Session.GetDisplayKeyCount(vk);
        }
        return total;
    }

    /// <summary>某个物理键在不在当前布局里（「只统计绑定的键」选项用）。</summary>
    public bool IsBoundKey(int virtualKey)
        => virtualKey != 0 && LayoutRows.Any(row => row.Cells.Any(cell => cell.VirtualKey == virtualKey));

    /// <summary>布局里是否有格子（XAML 空状态提示用）。</summary>
    public bool HasLayout => LayoutRows.Count > 0;

    /// <summary>某个物理键按下 / 抬起（会话回调，UI 线程）。</summary>
    public void OnKeyPressed(int virtualKey)
    {
        var changed = false;
        foreach (var cell in LayoutRows.SelectMany(row => row.Cells))
        {
            if (cell.VirtualKey != virtualKey || cell.IsPressed) continue;
            cell.IsPressed = true;
            changed = true;
        }
        if (changed) RefreshCounts();
    }

    /// <summary>某个物理键抬起（会话回调，UI 线程）。</summary>
    public void OnKeyReleased(int virtualKey)
    {
        foreach (var cell in LayoutRows.SelectMany(row => row.Cells))
        {
            if (cell.VirtualKey == virtualKey && cell.IsPressed) cell.IsPressed = false;
        }
    }

    // ===================== 选中 / 编辑 =====================

    /// <summary>当前选中的按键格子（主界面预览高亮 + 编辑面板）。</summary>
    public KpsKeyCell? SelectedCell
    {
        get => _selectedCell;
        private set
        {
            if (!Set(ref _selectedCell, value)) return;
            RaiseSelectedBindings();
        }
    }

    /// <summary>有没有选中按键。</summary>
    public bool HasSelectedKey => _selectedCell is not null;

    /// <summary>选中提示文字。</summary>
    public string SelectedCellText
    {
        get
        {
            if (_selectedCell is null) return "点预览里的按键选中它，然后在这里改键 / 改文字 / 换图片。";
            var vk = _selectedCell.VirtualKey;
            return vk == 0
                ? "已选中一个未绑定的键（点「改键」绑定物理键）"
                : $"已选中：{_selectedCell.Label}（{KeyNames.Display(vk)}）";
        }
    }

    /// <summary>选中键的显示文字（空 = 自动用键名）。</summary>
    public string SelectedLabel
    {
        get => _selectedModel?.Label ?? "";
        set
        {
            if (_selectedModel is null) return;
            _selectedModel.Label = value ?? "";
            _selectedCell?.ApplyStyle(Cfg);
            OnPropertyChanged();
        }
    }

    /// <summary>选中键的宽度倍数（1~4，空格键可以更宽）。</summary>
    public int SelectedWidthUnits
    {
        get => Math.Clamp(_selectedModel?.WidthUnits ?? 1, 1, 4);
        set
        {
            if (_selectedModel is null) return;
            _selectedModel.WidthUnits = Math.Clamp(value, 1, 4);
            RebuildLayout();
            RaiseSelectedBindings();
        }
    }

    /// <summary>选中键的宽度倍数（Slider 绑定用；int 属性的 double 包装）。</summary>
    public double SelectedWidthUnitsValue
    {
        get => SelectedWidthUnits;
        set => SelectedWidthUnits = (int)Math.Round(Math.Clamp(value, 1, 4));
    }

    /// <summary>选中键的按下颜色覆盖（空 = 用全局按下颜色）。</summary>
    public string SelectedColorHex
    {
        get => _selectedModel?.ColorHex ?? "";
        set
        {
            if (_selectedModel is null) return;
            _selectedModel.ColorHex = (value ?? "").Trim();
            _selectedCell?.ApplyStyle(Cfg);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedColorIsValid));
        }
    }

    /// <summary>选中键的颜色是否合法（编辑框提示用；空也算合法 = 用全局色）。</summary>
    public bool SelectedColorIsValid
        => string.IsNullOrWhiteSpace(SelectedColorHex) || KpsColor.IsValid(SelectedColorHex);

    /// <summary>选中键是否启用（关掉后布局里不占位，但保留配置）。</summary>
    public bool SelectedEnabled
    {
        get => _selectedModel?.Enabled ?? true;
        set
        {
            if (_selectedModel is null) return;
            _selectedModel.Enabled = value;
            RebuildLayout();
        }
    }

    /// <summary>选中键的图片说明文字。</summary>
    public string SelectedImageText
        => string.IsNullOrWhiteSpace(_selectedModel?.ImagePath)
            ? "未设置图片"
            : Path.GetFileName(_selectedModel!.ImagePath);

    /// <summary>选中的物理键名（改键按钮旁边显示）。</summary>
    public string SelectedPhysicalKeyText
        => _selectedModel is null || _selectedModel.VirtualKey == 0
            ? "未绑定"
            : KeyNames.Display(_selectedModel.VirtualKey);

    private void SelectCell(KpsKeyCell? cell, bool raiseNotifications = true)
    {
        foreach (var candidate in LayoutRows.SelectMany(row => row.Cells))
            candidate.IsSelected = ReferenceEquals(candidate, cell);

        _selectedCell = cell;
        _selectedModel = cell?.Model;
        if (raiseNotifications)
        {
            OnPropertyChanged(nameof(SelectedCell));
            RaiseSelectedBindings();
        }
    }

    private void RaiseSelectedBindings()
    {
        OnPropertyChanged(nameof(HasSelectedKey));
        OnPropertyChanged(nameof(SelectedCellText));
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(SelectedWidthUnits));
        OnPropertyChanged(nameof(SelectedWidthUnitsValue));
        OnPropertyChanged(nameof(SelectedColorHex));
        OnPropertyChanged(nameof(SelectedColorIsValid));
        OnPropertyChanged(nameof(SelectedEnabled));
        OnPropertyChanged(nameof(SelectedImageText));
        OnPropertyChanged(nameof(SelectedPhysicalKeyText));
    }

    // ===================== 结构编辑命令 =====================

    private void AddRow()
    {
        Cfg.KeyRows.Add(new KpsKeyRow { Keys = new List<KpsKey> { new() } });
        RebuildLayout();
        var row = LayoutRows.LastOrDefault();
        if (row is not null) SelectCell(row.Cells.LastOrDefault());
        SetStatus("已添加一行（点「改键」绑定物理键）");
    }

    private void AddKey()
    {
        var row = _selectedModel is null ? Cfg.KeyRows.LastOrDefault() : _selectedCell?.Row;
        if (row is null)
        {
            AddRow();
            return;
        }
        row.Keys.Add(new KpsKey());
        RebuildLayout();
        var rowView = LayoutRows.FirstOrDefault(view => ReferenceEquals(view.Model, row));
        SelectCell(rowView?.Cells.LastOrDefault());
        SetStatus("已添加按键");
    }

    private void DeleteSelectedKey()
    {
        if (_selectedCell is null) return;
        var row = _selectedCell.Row;
        row.Keys.Remove(_selectedCell.Model);
        if (row.Keys.Count == 0) Cfg.KeyRows.Remove(row);
        SelectCell(null, raiseNotifications: false);
        RebuildLayout();
        SetStatus("已删除按键");
    }

    private void DeleteRow(KpsRowView? row)
    {
        if (row is null) return;
        Cfg.KeyRows.Remove(row.Model);
        RebuildLayout();
        SetStatus("已删除一行");
    }

    private void MoveSelectedKey(int offset)
    {
        if (_selectedCell is null) return;
        var row = _selectedCell.Row;
        var index = row.Keys.IndexOf(_selectedCell.Model);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= row.Keys.Count) return;
        (row.Keys[index], row.Keys[target]) = (row.Keys[target], row.Keys[index]);
        var model = _selectedCell.Model;
        RebuildLayout();
        SelectCell(LayoutRows.SelectMany(view => view.Cells).FirstOrDefault(cell => ReferenceEquals(cell.Model, model)));
    }

    private void MoveSelectedRow(int offset)
    {
        if (_selectedCell is null) return;
        var row = _selectedCell.Row;
        var index = Cfg.KeyRows.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Cfg.KeyRows.Count) return;
        (Cfg.KeyRows[index], Cfg.KeyRows[target]) = (Cfg.KeyRows[target], Cfg.KeyRows[index]);
        var model = _selectedCell.Model;
        RebuildLayout();
        SelectCell(LayoutRows.SelectMany(view => view.Cells).FirstOrDefault(cell => ReferenceEquals(cell.Model, model)));
    }

    // ===================== 改键 / 图片 =====================

    /// <summary>是否正在等待按下要绑定的键。</summary>
    public bool IsCapturingKey
    {
        get => _isCapturingKey;
        private set
        {
            if (Set(ref _isCapturingKey, value)) OnPropertyChanged(nameof(HasCaptureHint));
        }
    }

    /// <summary>是否正在等待按下热键组合。</summary>
    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        private set
        {
            if (Set(ref _isCapturingHotkey, value)) OnPropertyChanged(nameof(HasCaptureHint));
        }
    }

    /// <summary>捕获提示文字（例如「请按下要绑定的键…」）。</summary>
    public string CaptureHintText
    {
        get => _captureHint;
        private set
        {
            if (Set(ref _captureHint, value)) OnPropertyChanged(nameof(HasCaptureHint));
        }
    }

    /// <summary>有没有捕获提示（XAML 用它显示提示条）。</summary>
    public bool HasCaptureHint => !string.IsNullOrWhiteSpace(_captureHint);

    private void BeginBindCapture()
    {
        if (_selectedCell is null)
        {
            SetStatus("先在预览里点一个按键");
            return;
        }
        var cell = _selectedCell;
        Session.BeginKeyCapture(vk => OnBindingCaptured(cell, vk));
        if (!Session.IsCapturing)
        {
            SetStatus("无法改键：全局键盘监听没有启动（先点「开始监听」）");
            return;
        }
        CaptureHintText = "请按下要绑定的键…（Esc 取消）";
    }

    private void OnBindingCaptured(KpsKeyCell cell, int virtualKey)
    {
        if (virtualKey == 0x1B)
        {
            CancelCapture();
            return;
        }
        cell.Model.VirtualKey = virtualKey;
        cell.ApplyStyle(Cfg);
        IsCapturingKey = false;
        CaptureHintText = "";
        RaiseSelectedBindings();
        RefreshCounts();
        SetStatus($"已绑定：{KeyNames.Display(virtualKey)}");
    }

    private void CancelCapture()
    {
        Session.CancelKeyCapture();
        IsCapturingKey = false;
        IsCapturingHotkey = false;
        CaptureHintText = "";
    }

    private void PickImage()
    {
        if (_selectedCell is null)
        {
            SetStatus("先在预览里点一个按键");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择按键图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var imported = Storage.Import(dialog.FileName);
            if (imported is null)
            {
                SetStatus("图片导入失败（文件可能被占用或没有写入权限）");
                return;
            }
            _selectedCell.Model.ImagePath = imported.RelativePath;
            _imageCache.Remove(imported.RelativePath);
            LoadCellImage(_selectedCell);
            RaiseSelectedBindings();
            SetStatus($"已设置图片：{imported.Name}");
        }
        catch (Exception ex)
        {
            SetStatus($"图片导入失败：{ex.Message}");
        }
    }

    private void ClearImage()
    {
        if (_selectedCell is null) return;
        _selectedCell.Model.ImagePath = "";
        _selectedCell.Image = null;
        RaiseSelectedBindings();
        SetStatus("已清除按键图片");
    }

    private void LoadCellImage(KpsKeyCell cell)
    {
        var relative = cell.Model.ImagePath;
        if (string.IsNullOrWhiteSpace(relative))
        {
            cell.Image = null;
            return;
        }
        if (_imageCache.TryGetValue(relative, out var cached))
        {
            cell.Image = cached;
            return;
        }
        try
        {
            var fullPath = Path.Combine(Storage.Directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                cell.Image = null;
                return;
            }
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            _imageCache[relative] = bitmap;
            cell.Image = bitmap;
        }
        catch
        {
            cell.Image = null;
        }
    }

    // ===================== 预设 =====================

    private IReadOnlyList<OptionItem<string>> _presetOptions = Array.Empty<OptionItem<string>>();

    /// <summary>预设下拉选项（内置 + 自定义）。</summary>
    public IReadOnlyList<OptionItem<string>> PresetOptions
    {
        get => _presetOptions;
        private set => Set(ref _presetOptions, value);
    }

    /// <summary>当前预设（ComboBox 用；切换后加载那套布局）。</summary>
    public OptionItem<string>? SelectedPreset
    {
        get => OptionItem<string>.Find(PresetOptions, Cfg.ActivePresetId) ?? PresetOptions.FirstOrDefault();
        set
        {
            if (_syncingPreset || value is null) return;
            SwitchPreset(value.Value);
        }
    }

    /// <summary>当前预设名字。</summary>
    public string ActivePresetName => Cfg.ActivePresetName;

    /// <summary>「另存为」输入的新预设名。</summary>
    public string NewPresetName
    {
        get => _newPresetName;
        set => Set(ref _newPresetName, value ?? "");
    }

    private void RefreshPresetOptions()
    {
        var options = new List<OptionItem<string>>();
        foreach (var preset in KpsPresets.BuiltIn)
            options.Add(new OptionItem<string>(preset.Id, preset.Name));
        foreach (var preset in Cfg.CustomPresets)
            options.Add(new OptionItem<string>(preset.Id, preset.Name + "（自定义）"));

        _syncingPreset = true;
        PresetOptions = options;
        _syncingPreset = false;
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(ActivePresetName));
        OnPropertyChanged(nameof(HasCustomPresets));
        OnPropertyChanged(nameof(SelectedPresetIsCustom));
    }

    /// <summary>有没有自定义预设（删除按钮用）。</summary>
    public bool HasCustomPresets => Cfg.CustomPresets.Count > 0;

    /// <summary>当前选中的是不是自定义预设。</summary>
    public bool SelectedPresetIsCustom
        => Cfg.CustomPresets.Any(p => string.Equals(p.Id, Cfg.ActivePresetId, StringComparison.OrdinalIgnoreCase));

    private void SwitchPreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)
            || string.Equals(presetId, Cfg.ActivePresetId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SaveCurrentLayoutToCache();
        var rows = LoadPresetRows(presetId);
        if (rows.Count == 0)
        {
            SetStatus("这个预设没有布局数据，已忽略");
            return;
        }

        _syncingPreset = true;
        try
        {
            Cfg.ActivePresetId = presetId;
            Cfg.KeyRows = rows;
        }
        finally
        {
            _syncingPreset = false;
        }

        SelectCell(null, raiseNotifications: false);
        RebuildLayout();
        RefreshPresetOptions();
        SetStatus($"已切换到预设：{Cfg.ActivePresetName}");
    }

    private void SaveCurrentLayoutToCache()
    {
        var rows = Cfg.KeyRows.Select(row => row.Clone()).ToList();
        Cfg.PresetLayouts[Cfg.ActivePresetId] = rows;
    }

    private List<KpsKeyRow> LoadPresetRows(string presetId)
    {
        if (Cfg.PresetLayouts.TryGetValue(presetId, out var cached) && cached is { Count: > 0 })
            return cached.Select(row => row.Clone()).ToList();

        var custom = Cfg.CustomPresets.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (custom is { Rows.Count: > 0 }) return custom.CloneRows();

        var builtIn = KpsPresets.Find(presetId);
        if (builtIn is not null) return builtIn.CloneRows();

        return new List<KpsKeyRow>();
    }

    private void SaveCustomPreset()
    {
        SaveCurrentLayoutToCache();
        var name = NewPresetName.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = $"自定义 {Cfg.CustomPresets.Count + 1}";

        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        var rows = Cfg.KeyRows.Select(row => row.Clone()).ToList();
        Cfg.CustomPresets.Add(new KpsPreset { Id = id, Name = name, BuiltIn = false, Rows = rows });
        Cfg.PresetLayouts[id] = rows.Select(row => row.Clone()).ToList();
        Cfg.ActivePresetId = id;
        NewPresetName = "";
        RefreshPresetOptions();
        SetStatus($"已另存为预设：{name}");
    }

    private void DeleteSelectedCustomPreset()
    {
        var preset = Cfg.CustomPresets.FirstOrDefault(p =>
            string.Equals(p.Id, Cfg.ActivePresetId, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            SetStatus("当前是内置预设，不能删除；可以先「另存为」自己的预设");
            return;
        }

        Cfg.CustomPresets.Remove(preset);
        Cfg.PresetLayouts.Remove(preset.Id);
        RefreshPresetOptions();
        SwitchPreset(KpsPresets.DefaultId);
        SetStatus($"已删除预设：{preset.Name}");
    }

    private void ResetCurrentPresetLayout()
    {
        var builtIn = KpsPresets.Find(Cfg.ActivePresetId);
        var custom = Cfg.CustomPresets.FirstOrDefault(p =>
            string.Equals(p.Id, Cfg.ActivePresetId, StringComparison.OrdinalIgnoreCase));

        List<KpsKeyRow>? rows = builtIn is not null
            ? builtIn.CloneRows()
            : custom is { Rows.Count: > 0 } ? custom.CloneRows() : null;

        if (rows is null)
        {
            SetStatus("没有可恢复的默认布局");
            return;
        }

        SaveCurrentLayoutToCache();
        Cfg.PresetLayouts[Cfg.ActivePresetId] = rows.Select(row => row.Clone()).ToList();
        Cfg.KeyRows = rows;
        SelectCell(null, raiseNotifications: false);
        RebuildLayout();
        SetStatus("已恢复该预设的默认布局");
    }

    // ===================== 统计展示 =====================

    /// <summary>当前 KPS 文本（整数，DmNote 口径）。</summary>
    public string KpsText
    {
        get => _kpsText;
        private set => Set(ref _kpsText, value);
    }

    /// <summary>峰值 KPS 文本。</summary>
    public string PeakText
    {
        get => _peakText;
        private set => Set(ref _peakText, value);
    }

    /// <summary>平均 KPS 文本。</summary>
    public string AverageText
    {
        get => _averageText;
        private set => Set(ref _averageText, value);
    }

    /// <summary>总按键数文本。</summary>
    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    /// <summary>统计时长文本。</summary>
    public string DurationText
    {
        get => _durationText;
        private set => Set(ref _durationText, value);
    }

    /// <summary>折线图数据（每次刷新都换成新数组，让控件 DP 触发重绘）。</summary>
    public IReadOnlyList<double> GraphValues
    {
        get => _graphValues;
        private set => Set(ref _graphValues, value);
    }

    /// <summary>折线图纵轴上限（动态，至少 1）。</summary>
    public double GraphMax
    {
        get => _graphMax;
        private set => Set(ref _graphMax, value);
    }

    /// <summary>会话每 100ms 回调一次：刷新大数字、总按键、折线图。</summary>
    public void ApplySnapshot(KpsSnapshot snapshot)
    {
        KpsText = FormatKps(snapshot.CurrentKps);
        PeakText = FormatKps(snapshot.PeakKps);
        AverageText = FormatKps(snapshot.AverageKps);
        DurationText = FormatDuration(snapshot.ElapsedSeconds);
        GraphValues = snapshot.GraphValues;
        GraphMax = Math.Max(1, snapshot.GraphMax);
        RefreshCounts();
    }

    private static string FormatKps(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0");

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "00:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private void ResetStats()
    {
        Session.ResetStats();
        GraphValues = Array.Empty<double>();
        GraphMax = 1;
        SetStatus("统计与每键计数器已重置");
    }

    // ===================== 运行状态 / Overlay 文本 =====================

    /// <summary>是否正在监听。</summary>
    public bool IsListening => Session.IsListening;

    /// <summary>监听状态文字。</summary>
    public string ListeningStatusText
    {
        get
        {
            if (Session.IsListening) return $"监听中 · {Session.BackendName}";
            return Session.InputError is { Length: > 0 } error ? $"未启动：{error}" : "已暂停";
        }
    }

    /// <summary>监听按钮文字。</summary>
    public string ListeningButtonText => Session.IsListening ? "暂停监听" : "开始监听";

    /// <summary>Overlay 状态文字。</summary>
    public string OverlayVisibleText => Session.IsOverlayVisible ? "Overlay 显示中" : "Overlay 已关闭";

    /// <summary>锁定按钮文字。</summary>
    public string OverlayLockButtonText
        => Cfg.OverlayLocked ? "解锁 Overlay（可拖动）" : "锁定 Overlay（点击穿透）";

    /// <summary>热键状态文字。</summary>
    public string HotkeyStatusText
        => Session.HotkeyIssues.Count == 0
            ? "全局热键已就绪（空的热键不会注册）"
            : string.Join("；", Session.HotkeyIssues);

    /// <summary>热键有没有问题。</summary>
    public bool HasHotkeyIssues => Session.HotkeyIssues.Count > 0;

    /// <summary>Overlay 是否未锁定（Overlay 窗口显示拖动提示用）。</summary>
    public bool OverlayUnlocked => !Cfg.OverlayLocked;

    /// <summary>Overlay 缩放基准点选项（滚轮缩放时固定哪个角）。</summary>
    public IReadOnlyList<OptionItem<string>> ScaleAnchorOptions { get; } = OptionItem.From(
        ("TopLeft", "左上角（默认）"),
        ("TopRight", "右上角"),
        ("BottomLeft", "左下角"),
        ("BottomRight", "右下角"),
        ("Center", "中心"));

    /// <summary>当前缩放基准点（绑定 ComboBox）。</summary>
    public OptionItem<string>? SelectedScaleAnchor
    {
        get => OptionItem<string>.Find(ScaleAnchorOptions, Cfg.OverlayScaleAnchor) ?? ScaleAnchorOptions[0];
        set
        {
            if (value is null) return;
            Cfg.OverlayScaleAnchor = value.Value;
            OnPropertyChanged();
        }
    }

    // ===================== Overlay 绑定包装（只读，供 XAML 绑定） =====================

    public double OverlayScale => Cfg.OverlayScale;
    public bool OverlayShowKps => Cfg.OverlayShowKps;
    public bool OverlayShowKeys => Cfg.OverlayShowKeys;
    public bool OverlayShowTotal => Cfg.OverlayShowTotal;
    public bool OverlayShowPeak => Cfg.OverlayShowPeak;
    public bool OverlayShowAverage => Cfg.OverlayShowAverage;
    public bool OverlayShowGraph => Cfg.OverlayShowGraph;
    public bool OverlayShowCounters => Cfg.OverlayShowCounters;
    public double KeyWidth => Cfg.KeyWidth;
    public double KeyHeight => Cfg.KeyHeight;
    public double KeyGap => Cfg.KeyGap;
    public double KeyCornerRadius => Cfg.KeyCornerRadius;
    public double KeyFontSize => Cfg.KeyFontSize;
    public double CounterFontSize => Cfg.CounterFontSize;
    public double PressedScale => Cfg.EffectScale ? 0.9 : 1.0;
    public bool GlowEnabled => Cfg.EffectGlow;
    public bool FlashEnabled => Cfg.EffectFlash;
    public int EffectDurationMs => Cfg.EffectDurationMs;
    public bool NoteEffectEnabled => Cfg.NoteEffectEnabled;
    public double NoteEffectOpacity => Cfg.NoteEffectOpacity;
    public int NoteEffectDurationMs => Cfg.NoteEffectDurationMs;
    public double NoteEffectHeight => Cfg.NoteEffectHeight;
    public bool NoteEffectReverse => Cfg.NoteEffectReverse;

    /// <summary>音符颜色（冻结画刷；配置变时整个实例替换，绑定能收到通知）。</summary>
    public Brush NoteEffectColor
        => GetCachedBrush(Cfg.NoteEffectColor, Color.FromArgb(0xB3, 0xFF, 0xC8, 0x57));

    private Brush GetCachedBrush(string text, Color fallback)
    {
        var key = text ?? "";
        if (_brushCache.TryGetValue(key, out var brush)) return brush;
        brush = KpsColor.ParseBrush(key, fallback);
        _brushCache[key] = brush;
        return brush;
    }

    /// <summary>配置变化后通知所有「外观 / Overlay 绑定」属性刷新。</summary>
    public void RaiseOverlayBindings() => RaiseVisualBindings();

    private void RaiseVisualBindings()
    {
        OnPropertyChanged(nameof(OverlayScale));
        OnPropertyChanged(nameof(OverlayShowKps));
        OnPropertyChanged(nameof(OverlayShowKeys));
        OnPropertyChanged(nameof(OverlayShowTotal));
        OnPropertyChanged(nameof(OverlayShowPeak));
        OnPropertyChanged(nameof(OverlayShowAverage));
        OnPropertyChanged(nameof(OverlayShowGraph));
        OnPropertyChanged(nameof(OverlayShowCounters));
        OnPropertyChanged(nameof(KeyWidth));
        OnPropertyChanged(nameof(KeyHeight));
        OnPropertyChanged(nameof(KeyGap));
        OnPropertyChanged(nameof(KeyCornerRadius));
        OnPropertyChanged(nameof(KeyFontSize));
        OnPropertyChanged(nameof(CounterFontSize));
        OnPropertyChanged(nameof(PressedScale));
        OnPropertyChanged(nameof(GlowEnabled));
        OnPropertyChanged(nameof(FlashEnabled));
        OnPropertyChanged(nameof(EffectDurationMs));
        OnPropertyChanged(nameof(NoteEffectEnabled));
        OnPropertyChanged(nameof(NoteEffectColor));
        OnPropertyChanged(nameof(NoteEffectOpacity));
        OnPropertyChanged(nameof(NoteEffectDurationMs));
        OnPropertyChanged(nameof(NoteEffectHeight));
        OnPropertyChanged(nameof(NoteEffectReverse));
        OnPropertyChanged(nameof(OverlayUnlocked));
        OnPropertyChanged(nameof(OverlayLockButtonText));
        OnPropertyChanged(nameof(SelectedScaleAnchor));
    }

    // ===================== 配置变化分发 =====================

    private static readonly HashSet<string> AppearancePropertyNames = new()
    {
        nameof(KpsConfig.KeyIdleColor), nameof(KpsConfig.KeyPressedColor), nameof(KpsConfig.KeyBorderColor),
        nameof(KpsConfig.KeyTextColor), nameof(KpsConfig.KeyPressedTextColor), nameof(KpsConfig.KeyCounterColor),
        nameof(KpsConfig.KeyWidth), nameof(KpsConfig.KeyHeight), nameof(KpsConfig.KeyGap),
        nameof(KpsConfig.KeyCornerRadius), nameof(KpsConfig.KeyFontSize), nameof(KpsConfig.CounterFontSize),
        nameof(KpsConfig.ShowKeyCounters),
        nameof(KpsConfig.EffectScale), nameof(KpsConfig.EffectGlow), nameof(KpsConfig.EffectFlash),
        nameof(KpsConfig.EffectDurationMs),
        nameof(KpsConfig.NoteEffectEnabled), nameof(KpsConfig.NoteEffectColor), nameof(KpsConfig.NoteEffectOpacity),
        nameof(KpsConfig.NoteEffectDurationMs), nameof(KpsConfig.NoteEffectHeight), nameof(KpsConfig.NoteEffectReverse),
    };

    private static readonly HashSet<string> OverlayPropertyNames = new()
    {
        nameof(KpsConfig.OverlayLocked), nameof(KpsConfig.OverlayTopmost), nameof(KpsConfig.OverlayLeft),
        nameof(KpsConfig.OverlayTop), nameof(KpsConfig.OverlayScale), nameof(KpsConfig.OverlayOpacity),
        nameof(KpsConfig.OverlayBackgroundColor), nameof(KpsConfig.OverlayScaleAnchor),
        nameof(KpsConfig.OverlayShowKps), nameof(KpsConfig.OverlayShowKeys), nameof(KpsConfig.OverlayShowTotal),
        nameof(KpsConfig.OverlayShowPeak), nameof(KpsConfig.OverlayShowAverage), nameof(KpsConfig.OverlayShowGraph),
        nameof(KpsConfig.OverlayShowCounters),
    };

    private static readonly HashSet<string> HotkeyPropertyNames = new()
    {
        nameof(KpsConfig.HotkeyToggleOverlay), nameof(KpsConfig.HotkeyToggleLock),
        nameof(KpsConfig.HotkeyResetStats), nameof(KpsConfig.HotkeyToggleListening),
    };

    private static readonly HashSet<string> SoundPropertyNames = new()
    {
        nameof(KpsConfig.KeySoundEnabled), nameof(KpsConfig.KeySoundVolume), nameof(KpsConfig.KeySoundPath),
    };

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName ?? "";
        try
        {
            if (AppearancePropertyNames.Contains(name))
            {
                RefreshCellStyles();
                RaiseVisualBindings();
            }
            else if (OverlayPropertyNames.Contains(name))
            {
                Session.ApplyOverlayConfig();
                RaiseVisualBindings();
            }
            else if (HotkeyPropertyNames.Contains(name))
            {
                Session.RefreshHotkeys();
            }
            else if (SoundPropertyNames.Contains(name))
            {
                Session.ApplySoundSettings();
            }
            else if (name == nameof(KpsConfig.GraphSeconds))
            {
                Session.ApplyStatsSettings();
            }
            else if (name == nameof(KpsConfig.CustomPresets) || name == nameof(KpsConfig.PresetLayouts))
            {
                if (!_syncingPreset) RefreshPresetOptions();
            }
            else if (name == nameof(KpsConfig.PersistKeyCounters))
            {
                // 只影响持久化时机，界面不用变
            }
        }
        catch { /* 设置联动不能把输入线程带崩 */ }
    }

    // ===================== 热键设置 =====================

    private void BeginHotkeyCapture(string? which)
    {
        if (string.IsNullOrWhiteSpace(which)) return;
        if (Session.IsCapturing) Session.CancelKeyCapture();
        Session.BeginKeyCapture(vk => OnHotkeyCaptured(which, vk));
        if (!Session.IsCapturing)
        {
            SetStatus("无法设置热键：全局键盘监听没有启动（先点「开始监听」）");
            return;
        }
        IsCapturingHotkey = true;
        CaptureHintText = "请按下热键组合（例如 Ctrl+Shift+O）…（Esc 取消）";
    }

    private void OnHotkeyCaptured(string which, int virtualKey)
    {
        if (virtualKey == 0x1B)
        {
            CancelCapture();
            return;
        }

        if (KeyNames.IsModifier(virtualKey))
        {
            // 只按了修饰键：继续等主键
            Session.BeginKeyCapture(vk => OnHotkeyCaptured(which, vk));
            CaptureHintText = "还需要一个主键（例如字母 / F 键）…（Esc 取消）";
            return;
        }

        var modifiers = Session.GetHeldModifiers();
        if (modifiers == 0)
        {
            Session.BeginKeyCapture(vk => OnHotkeyCaptured(which, vk));
            CaptureHintText = "全局热键至少要带一个修饰键（Ctrl / Alt / Shift / Win）…（Esc 取消）";
            return;
        }

        var gesture = new HotkeyGesture(modifiers, virtualKey);
        switch (which)
        {
            case "overlay": Cfg.HotkeyToggleOverlay = gesture.ToString(); break;
            case "lock": Cfg.HotkeyToggleLock = gesture.ToString(); break;
            case "reset": Cfg.HotkeyResetStats = gesture.ToString(); break;
            case "listen": Cfg.HotkeyToggleListening = gesture.ToString(); break;
        }

        Session.RefreshHotkeys();
        IsCapturingHotkey = false;
        CaptureHintText = "";
        SetStatus($"已设置热键：{gesture}");
    }

    private void ClearHotkey(string? which)
    {
        switch (which)
        {
            case "overlay": Cfg.HotkeyToggleOverlay = ""; break;
            case "lock": Cfg.HotkeyToggleLock = ""; break;
            case "reset": Cfg.HotkeyResetStats = ""; break;
            case "listen": Cfg.HotkeyToggleListening = ""; break;
            default: return;
        }
        Session.RefreshHotkeys();
        SetStatus("已清除热键（空的热键不会注册）");
    }

    // ===================== 键音 / 文件夹 / 来源 =====================

    private void BrowseSound()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择键音（WAV）",
            Filter = "WAV 音频|*.wav|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        Cfg.KeySoundPath = dialog.FileName;
        Session.ApplySoundSettings();
        SetStatus($"键音：{Path.GetFileName(dialog.FileName)}");
    }

    private void ClearSound()
    {
        Cfg.KeySoundPath = "";
        Session.ApplySoundSettings();
        SetStatus("已改用内置合成点击声");
    }

    private void OpenSource()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DmNoteUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"打开来源链接失败：{ex.Message}");
        }
    }

    /// <summary>会话 / 命令里统一改状态文字的小入口。</summary>
    public void SetStatus(string text)
    {
        StatusText = text;
    }

    // ===================== 命令声明 =====================

    public RelayCommand ToggleListeningCommand { get; }
    public RelayCommand ToggleOverlayCommand { get; }
    public RelayCommand ToggleOverlayLockCommand { get; }
    public RelayCommand CenterOverlayCommand { get; }
    public RelayCommand ResetStatsCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand ResetPresetLayoutCommand { get; }
    public RelayCommand AddRowCommand { get; }
    public RelayCommand AddKeyCommand { get; }
    public RelayCommand DeleteKeyCommand { get; }
    public RelayCommand DeleteRowCommand { get; }
    public RelayCommand MoveKeyLeftCommand { get; }
    public RelayCommand MoveKeyRightCommand { get; }
    public RelayCommand MoveRowUpCommand { get; }
    public RelayCommand MoveRowDownCommand { get; }
    public RelayCommand SelectKeyCommand { get; }
    public RelayCommand BeginCaptureKeyCommand { get; }
    public RelayCommand CancelCaptureKeyCommand { get; }
    public RelayCommand PickImageCommand { get; }
    public RelayCommand ClearImageCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand BrowseSoundCommand { get; }
    public RelayCommand ClearSoundCommand { get; }
    public RelayCommand TestSoundCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public RelayCommand SetHotkeyCommand { get; }
    public RelayCommand ClearHotkeyCommand { get; }
}
