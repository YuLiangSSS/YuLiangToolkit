using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.TaskManager;
using YuLiangToolkit.Tools.TaskManager.Models;
using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.Tools.StartupManagerTool.ViewModels;

/// <summary>
/// 「开机启动管理」的 ViewModel：扫描注册表 Run / 启动文件夹的开机启动项，
/// 支持启用 / 禁用（写 Windows 的 StartupApproved）、删除、打开位置、复制命令，
/// 权限不足时引导「以管理员身份重启」（复用任务管理器的 <see cref="AppElevation"/>）。
///
/// <para>底层全部复用 <see cref="StartupManager"/>（在任务管理器工程里），这里只做页面。</para>
/// </summary>
public sealed class StartupManagerViewModel : ToolViewModelBase
{
    private readonly StartupManager _startupManager = new();

    private StartupEntry? _selected;
    private string _summaryText = "";
    private string _searchText = "";
    private bool _isElevated;

    public StartupManagerViewModel() : base("startup-manager")
    {
        IsElevated = AppElevation.IsCurrentProcessElevated;

        ItemsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;

        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleCommand = new AsyncRelayCommand(_ => ToggleAsync(), _ => CanOperateSelected);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => CanOperateSelected);
        OpenLocationCommand = new RelayCommand(_ => OpenLocation(), _ => SelectedItem is not null);
        CopyCommandCommand = new RelayCommand(_ => CopyToClipboard(SelectedItem?.Command ?? ""), _ => SelectedItem is not null);
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());

        Log(IsElevated
            ? "当前以管理员身份运行：可以修改系统级（所有用户）开机启动项。"
            : "当前是普通权限：修改系统级开机启动项时会提示并可「以管理员身份重启」（弹 UAC）。");

        Refresh();
    }

    // ---------------------------------------------------------------- 列表 / 选中

    /// <summary>全部启动项（筛选前）。</summary>
    public ObservableCollection<StartupEntry> Items { get; } = new();

    /// <summary>带过滤的视图（XAML 里 DataGrid.ItemsSource 绑它）。</summary>
    public ListCollectionView ItemsView { get; }

    public StartupEntry? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>当前选中的项能不能改（HKLM / 公共启动文件夹需管理员）。</summary>
    public bool CanOperateSelected => SelectedItem is { CanModify: true };

    /// <summary>「启用 / 禁用」按钮文案：当前已启用就显示「禁用」。</summary>
    public string ToggleText => SelectedItem is null ? "启用 / 禁用" : (SelectedItem.Enabled ? "禁用" : "启用");

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            ItemsView.Refresh();
        }
    }

    private bool FilterItem(object obj)
    {
        if (obj is not StartupEntry entry) return false;
        var keyword = _searchText;
        if (string.IsNullOrWhiteSpace(keyword)) return true;

        foreach (var token in keyword.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || entry.Command.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || entry.SourceText.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 文本 / 权限

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public bool IsElevated
    {
        get => _isElevated;
        private set
        {
            if (!Set(ref _isElevated, value)) return;
            OnPropertyChanged(nameof(CanRestartElevated));
            OnPropertyChanged(nameof(ElevationText));
        }
    }

    public bool CanRestartElevated => !IsElevated;
    public string ElevationText => IsElevated ? "管理员权限（可改系统级启动项）" : "普通权限（系统级启动项需管理员）";

    // ---------------------------------------------------------------- 命令

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand CopyCommandCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }

    // ---------------------------------------------------------------- 扫描

    private void Refresh()
    {
        try
        {
            var items = _startupManager.Scan();
            Items.Clear();
            foreach (var item in items) Items.Add(item);
            var disabled = items.Count(i => !i.Enabled);
            SummaryText = $"共 {items.Count} 项 · 已启用 {items.Count - disabled} 项 · 已禁用 {disabled} 项"
                          + (IsElevated ? "" : " · 系统级项目需要管理员权限才能修改");

            if (SelectedItem is not null && !Items.Contains(SelectedItem)) SelectedItem = null;
            ItemsView.Refresh();
        }
        catch (Exception ex)
        {
            SummaryText = "读取开机启动项失败：" + ex.Message;
            Log("扫描开机启动项失败：" + ex);
        }
    }

    // ---------------------------------------------------------------- 操作

    private async Task ToggleAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var targetEnabled = !item.Enabled;
        StatusText = $"正在{(targetEnabled ? "启用" : "禁用")}「{item.Name}」…";
        var message = "";
        var ok = await Task.Run(() => _startupManager.SetEnabled(item, targetEnabled, out message));
        StatusText = ok
            ? $"已{(targetEnabled ? "启用" : "禁用")}「{item.Name}」"
            : $"修改「{item.Name}」失败：{message}";
        if (!ok) OfferElevation($"修改开机启动项「{item.Name}」失败：{message}");
        Refresh();
    }

    private async Task DeleteAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var confirm = MessageBox.Show(
            $"确定删除开机启动项「{item.Name}」吗？\n\n来源：{item.SourceText}\n命令：{item.CommandSummary}\n\n"
            + "只会删除这一条启动记录 / 启动文件夹里的快捷方式，不会卸载程序本身。",
            "删除开机启动项", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        StatusText = $"正在删除「{item.Name}」…";
        var message = "";
        var ok = await Task.Run(() => _startupManager.Delete(item, out message));
        StatusText = ok ? $"已删除「{item.Name}」" : $"删除「{item.Name}」失败：{message}";
        if (!ok) OfferElevation($"删除开机启动项「{item.Name}」失败：{message}");
        Refresh();
    }

    private void OpenLocation()
    {
        var item = SelectedItem;
        if (item is null) return;

        if (item.Kind == StartupEntryKind.StartupFolder && !string.IsNullOrWhiteSpace(item.FolderPath))
        {
            RevealInExplorer(item.FolderPath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
        {
            RevealInExplorer(item.FilePath);
            return;
        }

        CopyToClipboard(item.Command);
        StatusText = "找不到启动程序的位置，已把启动命令复制到剪贴板";
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[开机启动管理] 打开位置失败：{ex.Message}");
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            StatusText = "已复制到剪贴板";
        }
        catch
        {
            StatusText = "复制失败（剪贴板被其它程序占用）";
        }
    }

    // ---------------------------------------------------------------- 权限

    private void RestartElevated()
    {
        if (IsElevated) return;

        var confirm = MessageBox.Show(
            "修改 / 删除系统级（所有用户）开机启动项需要管理员权限。\n\n"
            + "点「确定」后会弹出 Windows 的 UAC 授权窗口；授权后本程序会以管理员身份重新启动，"
            + "并自动回到「开机启动管理」页面。\n\n是否继续？",
            "获取管理员权限", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        if (!AppElevation.TryRestartElevated("startup-manager", out var error))
        {
            StatusText = "以管理员身份重启失败：" + (error ?? "未知原因");
            MessageBox.Show(
                $"没能以管理员身份重启：{error}\n\n也可以手动关闭本程序，右键 exe →「以管理员身份运行」。",
                "无法获取管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Log("已请求以管理员身份重新启动本程序（UAC），重启后自动回到开机启动管理。");
        AppElevation.RequestExit();
    }

    private void OfferElevation(string problem)
    {
        if (IsElevated)
        {
            MessageBox.Show(problem, "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(
            problem + "\n\n要以管理员身份重新启动本程序吗？\n"
            + "（会弹 UAC；重启后自动回到开机启动管理，刚才的操作需要再做一次。）",
            "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) RestartElevated();
    }

    protected override void OnClosed()
    {
        base.OnClosed();
    }
}
