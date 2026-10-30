using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>「清理缓存」列表里的一项（Cache 文件夹 / 最近浏览记录 / …）。</summary>
public sealed class MaintenanceCacheItem : ObservableObject
{
    private bool _isSelected;

    public MaintenanceCacheItem(CacheEntry entry, bool isSelected = true)
    {
        Entry = entry;
        _isSelected = isSelected;
    }

    /// <summary>引擎给出的缓存项（大小 / 说明 / 清理动作都在里面）。</summary>
    public CacheEntry Entry { get; }

    /// <summary>所属功能名。</summary>
    public string ToolName => Entry.ToolName;

    /// <summary>缓存项名称（「缓存文件夹」「最近浏览记录」…）。</summary>
    public string Name => Entry.Name;

    /// <summary>一句话说明（清什么、会不会影响设置）。</summary>
    public string Description => Entry.Description;

    /// <summary>右侧的大小 / 数量文案。</summary>
    public string SizeText => Entry.SizeText;

    /// <summary>是否勾选。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>「重建配置」列表里的一项（一个功能的 <c>Config\&lt;功能Id&gt;</c>）。</summary>
public sealed class MaintenanceConfigItem : ObservableObject
{
    private bool _isSelected;

    public MaintenanceConfigItem(ConfigTarget target, bool isSelected = false)
    {
        Target = target;
        _isSelected = isSelected;
    }

    /// <summary>引擎给出的配置现状。</summary>
    public ConfigTarget Target { get; }

    /// <summary>功能展示名（第一项是「主页 / 外壳设置」）。</summary>
    public string ToolName => Target.ToolName;

    /// <summary>功能 Id（= Config 下的小文件夹名）。</summary>
    public string ToolId => Target.ToolId;

    /// <summary>现状说明（有哪些配置文件 / 还没有配置文件）。</summary>
    public string Detail => Target.Detail;

    /// <summary>右侧大小文案（没有文件时为空）。</summary>
    public string SizeText => Target.SizeText;

    /// <summary>有没有有效的配置文件（没有时重建也安全，等于保持默认）。</summary>
    public bool HasConfigFile => Target.HasConfigFile;

    /// <summary>是否勾选。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// 主页设置页底部「清理缓存 / 重建配置」两块面板的 ViewModel。
///
/// <para><b>为什么单独一个类</b>：<see cref="MainViewModel"/> 已经很长了，而这两块只跟
/// <c>Core/Maintenance</c> 的引擎打交道；外壳只负责把「功能列表 + 配置现状 + 重建动作」传进来，
/// 重建外壳自己的配置后再调 <see cref="MainViewModel.RefreshAfterShellConfigReset"/> 刷新界面。</para>
///
/// <para><b>自动扩展到新工具</b>：列表每次 <see cref="Refresh"/> 都由
/// <see cref="CacheCleaner.List"/> / <see cref="ConfigResetter.List"/> 按「已发现工具」现算 ——
/// 以后新增工具（缓存放 <c>Cache\&lt;Id&gt;\</c>、配置放 <c>Config\&lt;Id&gt;\</c>）自动出现在这里，
/// 不用改设置页、也不用改本类。</para>
/// </summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IReadOnlyList<ToolRef> _configTargets;
    private readonly Func<string, bool> _resetConfig;

    public MaintenanceViewModel(
        IReadOnlyList<ITool> tools,
        IReadOnlyList<ToolRef> configTargets,
        Func<string, bool> resetConfig)
    {
        _tools = tools ?? Array.Empty<ITool>();
        _configTargets = configTargets ?? Array.Empty<ToolRef>();
        _resetConfig = resetConfig ?? (_ => false);

        RefreshCommand = new RelayCommand(_ => Refresh());
        SelectAllCacheCommand = new RelayCommand(_ => SetAllCacheSelected(true));
        SelectNoneCacheCommand = new RelayCommand(_ => SetAllCacheSelected(false));
        ClearCacheCommand = new RelayCommand(_ => ClearCache());
        OpenCacheFolderCommand = new RelayCommand(_ => OpenCacheFolder());
        SelectAllConfigCommand = new RelayCommand(_ => SetAllConfigSelected(true));
        SelectNoneConfigCommand = new RelayCommand(_ => SetAllConfigSelected(false));
        ResetConfigCommand = new RelayCommand(_ => ResetConfig());
        ResetLayoutCommand = new RelayCommand(_ => ResetLayout());

        CacheItems.CollectionChanged += (_, _) => UpdateSummaries();
        ConfigItems.CollectionChanged += (_, _) => UpdateSummaries();
        Refresh();
    }

    /// <summary>危险操作前的二次确认出口（默认弹 <see cref="MessageBox"/>；自检可以换成“永远确定”）。</summary>
    public Func<string, string, bool> Confirm { get; set; } = static (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    // ---------- 清理缓存 ----------

    /// <summary>可清理的缓存项（Cache 文件夹 / 最近浏览 / 最近会话 / 残留目录…）。</summary>
    public ObservableCollection<MaintenanceCacheItem> CacheItems { get; } = new();

    /// <summary>清空列表时显示的空状态文案。</summary>
    public string CacheEmptyHint => "现在没有可清理的缓存 —— 各功能的 Cache 文件夹都是空的。";

    private string _cacheSummaryText = "";
    /// <summary>「共 N 项 · 已选 M 项 · 占用 X」。</summary>
    public string CacheSummaryText
    {
        get => _cacheSummaryText;
        private set => Set(ref _cacheSummaryText, value);
    }

    private bool _hasCacheItems;
    /// <summary>有没有可清理的缓存（没有就显示空状态，藏起列表和按钮）。</summary>
    public bool HasCacheItems
    {
        get => _hasCacheItems;
        private set => Set(ref _hasCacheItems, value);
    }

    private string _cacheStatusText = "";
    /// <summary>上一次清理的结果提示。</summary>
    public string CacheStatusText
    {
        get => _cacheStatusText;
        private set => Set(ref _cacheStatusText, value);
    }

    // ---------- 重建配置 ----------

    /// <summary>可重建的功能配置（第一个是「主页 / 外壳设置」）。</summary>
    public ObservableCollection<MaintenanceConfigItem> ConfigItems { get; } = new();

    /// <summary>配置列表为空时的文案（正常不会出现，兜底）。</summary>
    public string ConfigEmptyHint => "还没有任何功能配置。";

    private string _configSummaryText = "";
    /// <summary>「共 N 个功能 · 已选 M 个 · 其中 K 个已有配置文件」。</summary>
    public string ConfigSummaryText
    {
        get => _configSummaryText;
        private set => Set(ref _configSummaryText, value);
    }

    private bool _hasConfigItems;
    /// <summary>有没有可重建的配置。</summary>
    public bool HasConfigItems
    {
        get => _hasConfigItems;
        private set => Set(ref _hasConfigItems, value);
    }

    private string _configStatusText = "";
    /// <summary>上一次重建的结果提示。</summary>
    public string ConfigStatusText
    {
        get => _configStatusText;
        private set => Set(ref _configStatusText, value);
    }

    // ---------- 界面布局（分隔条拖出来的尺寸；v0.41.0） ----------

    private string _layoutSummaryText = "";
    /// <summary>当前记住了哪些页面 / 几处分隔条位置（「恢复默认布局」按钮上方那行）。</summary>
    public string LayoutSummaryText
    {
        get => _layoutSummaryText;
        private set => Set(ref _layoutSummaryText, value);
    }

    private string _layoutStatusText = "";
    /// <summary>上一次恢复默认布局的结果提示。</summary>
    public string LayoutStatusText
    {
        get => _layoutStatusText;
        private set => Set(ref _layoutStatusText, value);
    }

    // ---------- 命令 ----------

    /// <summary>重新扫描缓存与配置（打开设置页 / 操作完成后自动调用）。</summary>
    public RelayCommand RefreshCommand { get; }

    public RelayCommand SelectAllCacheCommand { get; }
    public RelayCommand SelectNoneCacheCommand { get; }
    public RelayCommand ClearCacheCommand { get; }
    public RelayCommand OpenCacheFolderCommand { get; }

    public RelayCommand SelectAllConfigCommand { get; }
    public RelayCommand SelectNoneConfigCommand { get; }
    public RelayCommand ResetConfigCommand { get; }

    /// <summary>「恢复默认布局」：清掉所有工具页记住的分隔条尺寸（<c>Config\layout.json</c>）。</summary>
    public RelayCommand ResetLayoutCommand { get; }

    // ---------- 行为 ----------

    /// <summary>
    /// 重新扫描：缓存项默认全选（清理缓存是安全的）；配置项保留上次勾选，新出现的不选（避免误重建）。
    /// </summary>
    public void Refresh()
    {
        // 缓存
        var previousCache = CacheItems.Count > 0
            ? CacheItems.Where(item => item.IsSelected).Select(item => item.Entry.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        CacheItems.Clear();
        foreach (var entry in CacheCleaner.List(_tools))
        {
            // 第一次刷新（previousCache 为 null）默认全选；手动点「全不选」后再刷新也保持“全不选”的意图
            var selected = previousCache is null || previousCache.Contains(entry.Key);
            CacheItems.Add(Wire(new MaintenanceCacheItem(entry, selected)));
        }

        // 配置：保留刷新前的勾选（新出现的功能默认不选，避免误重建）
        var previousConfig = ConfigItems
            .Where(item => item.IsSelected)
            .Select(item => item.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ConfigItems.Clear();
        foreach (var target in ConfigResetter.List(_configTargets))
        {
            var selected = previousConfig.Contains(target.ToolId);
            ConfigItems.Add(Wire(new MaintenanceConfigItem(target, selected)));
        }

        UpdateSummaries();
        UpdateLayoutSummary();
    }

    /// <summary>勾选 / 取消全部缓存项。</summary>
    private void SetAllCacheSelected(bool selected)
    {
        foreach (var item in CacheItems) item.IsSelected = selected;
    }

    /// <summary>勾选 / 取消全部配置项。</summary>
    private void SetAllConfigSelected(bool selected)
    {
        foreach (var item in ConfigItems) item.IsSelected = selected;
    }

    /// <summary>清理勾选的缓存（先确认；不会动 Data 里的用户数据，也不会删配置）。</summary>
    private void ClearCache()
    {
        var selected = CacheItems.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            CacheStatusText = "先在下面勾选要清理的缓存项（或点「全选」）。";
            return;
        }

        var bytes = selected.Sum(item => item.Entry.SizeBytes);
        var preview = string.Join("\n", selected.Take(12).Select(item => $"· {item.ToolName} · {item.Name}（{item.SizeText}）"));
        if (selected.Count > 12) preview += $"\n… 还有 {selected.Count - 12} 项";
        var message = $"要清理选中的 {selected.Count} 项缓存吗？\n\n{preview}\n\n"
                      + $"清理后这些缓存会从头生成（可能下次打开功能稍慢）；当前约 {FileSize.Format(bytes)}。\n"
                      + "Data 里的下载内容 / 聊天记录等用户数据、Config 里的设置都**不会**动。";
        if (!Confirm(message, "清理缓存")) return;

        var (cleared, freed) = CacheCleaner.Clear(selected.Select(item => item.Entry));
        var freedText = freed > 0 ? $"（约 {FileSize.Format(freed)}）" : "";
        CacheStatusText = selected.Count == cleared
            ? $"已清理 {cleared} 项缓存{freedText}。"
            : $"已清理 {cleared} / {selected.Count} 项缓存{freedText}，其余可能正被占用，稍后重试即可。";
        Refresh();
    }

    /// <summary>在资源管理器里打开缓存根目录（方便直接看 / 手动删）。</summary>
    private void OpenCacheFolder()
    {
        CacheStatusText = ToolCache.OpenRootInExplorer()
            ? $"已打开缓存文件夹：{AppPaths.CacheRoot}"
            : $"打不开缓存文件夹，请手动复制路径：{AppPaths.CacheRoot}";
    }

    /// <summary>重建勾选的配置（= 恢复默认设置；先确认）。</summary>
    private void ResetConfig()
    {
        var selected = ConfigItems.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ConfigStatusText = "先在下面勾选要重建配置的功能（或点「全选」）。";
            return;
        }

        var names = string.Join("、", selected.Take(10).Select(item => item.ToolName));
        if (selected.Count > 10) names += $" 等 {selected.Count} 个功能";
        var message = $"要重建选中的 {selected.Count} 个功能的配置吗？\n\n{names}\n\n"
                      + "重建 = 删掉这些功能的 config.json，并把内存里已加载的配置改回默认值（= 恢复默认设置）。\n"
                      + "自定义过的选项会丢失；损坏备份 *.bad-*.json 不会被删。";
        if (!Confirm(message, "重建配置")) return;

        var ok = 0;
        var failed = 0;
        foreach (var item in selected)
        {
            try
            {
                if (_resetConfig(item.ToolId)) ok++;
                else failed++;
            }
            catch { failed++; }
        }

        ConfigStatusText = failed == 0
            ? $"已重建 {ok} 个功能的配置，全部恢复默认设置。"
            : $"重建完成：成功 {ok} 个，失败 {failed} 个（配置文件可能被占用，重试一次即可）。";

        // 重新扫描（勾选状态会保留，方便接着重建别的功能）
        Refresh();
    }

    /// <summary>
    /// 「恢复默认布局」= 清掉所有工具页记住的分隔条尺寸（<c>Config\layout.json</c>），
    /// 再打开的页面就按默认大小显示。先确认一次（不会动任何设置 / 数据）。
    /// </summary>
    private void ResetLayout()
    {
        if (!LayoutMemory.HasSaved)
        {
            LayoutStatusText = "现在就是默认布局，没有需要恢复的东西。";
            UpdateLayoutSummary();
            return;
        }

        var count = LayoutMemory.SavedCount;
        var groups = LayoutMemory.SavedGroups;
        var names = groups.Select(DescribeGroup).ToList();
        var message = $"要把记住的 {count} 处分隔条位置全部恢复默认吗？\n\n"
                      + $"涉及：{string.Join("、", names)}\n\n"
                      + "只影响区块大小，不会动任何设置、缓存、数据；以后拖分隔条会重新记住。";
        if (!Confirm(message, "恢复默认布局")) return;

        var ok = LayoutMemory.ResetAll();
        LayoutStatusText = ok
            ? $"已恢复默认布局（{count} 处分隔条位置已清空）。"
            : $"布局已恢复默认，但配置文件 Config\\layout.json 删不掉（可能被占用），下次拖分隔条时会覆盖。";
        UpdateLayoutSummary();
    }

    /// <summary>把布局键里的工具 Id 换成界面上看到的功能名（设置页摘要用）。</summary>
    private string DescribeGroup(string id)
    {
        if (id.Equals("shell", StringComparison.OrdinalIgnoreCase)) return "主页 / 外壳设置";
        return _tools.FirstOrDefault(tool => string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
    }

    /// <summary>「恢复默认布局」上方那行：记住过哪些页面、几处。</summary>
    private void UpdateLayoutSummary()
    {
        var groups = LayoutMemory.SavedGroups;
        if (groups.Count == 0)
        {
            LayoutSummaryText = "当前是默认布局：各页面都按默认尺寸显示。"; 
            return;
        }

        LayoutSummaryText = $"已记住 {LayoutMemory.SavedCount} 处分隔条位置："
                            + string.Join("、", groups.Select(DescribeGroup)) + "。";
    }

    /// <summary>挂上选中变化通知（摘要文案里的「已选 N」要实时更新）。</summary>
    private T Wire<T>(T item) where T : ObservableObject
    {
        item.PropertyChanged += OnItemPropertyChanged;
        return item;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaintenanceCacheItem.IsSelected)
            || e.PropertyName == nameof(MaintenanceConfigItem.IsSelected))
        {
            UpdateSummaries();
        }
    }

    /// <summary>重算两块面板顶部的计数 / 占用文案。</summary>
    private void UpdateSummaries()
    {
        var cacheSelected = CacheItems.Where(item => item.IsSelected).ToList();
        HasCacheItems = CacheItems.Count > 0;
        // 列表为空时只在「空状态提示」里说一句，摘要留空（否则两行一样的话）
        CacheSummaryText = CacheItems.Count == 0
            ? ""
            : $"共 {CacheItems.Count} 项 · 已选 {cacheSelected.Count} 项 · "
              + $"共 {FileSize.Format(CacheItems.Sum(item => item.Entry.SizeBytes))}";

        var configSelected = ConfigItems.Where(item => item.IsSelected).ToList();
        HasConfigItems = ConfigItems.Count > 0;
        ConfigSummaryText = ConfigItems.Count == 0
            ? ""
            : $"共 {ConfigItems.Count} 个功能 · 已选 {configSelected.Count} 个 · "
              + $"其中 {ConfigItems.Count(item => item.HasConfigFile)} 个已有配置文件";
    }
}
