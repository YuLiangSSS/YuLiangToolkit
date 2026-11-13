using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 主页设置 → 快捷键（汇总页）的代码后置：只处理「按钮点了一下」这类界面动作，
/// 业务（改键 / 拆分 / 合并 / 启停 / 冲突检测）全在 Core 的 <see cref="ShortcutRegistry"/> 里。
///
/// <para>改键不在这里做：改键交给 <see cref="Core.Controls.HotkeyBox"/> 自己录（它保证
/// 「同时按、全部松开才定」），录完通过双向绑定写回工具配置。</para>
/// </summary>
public partial class ShortcutsSettingsView : UserControl
{
    /// <summary>
    /// 截图 / 调试用：<c>YLT_SHORTCUTS_EXPANDED=1</c> 打开设置页时自动展开全部（不写配置）。
    /// </summary>
    private static bool IsDebugExpandRequested()
        => Environment.GetEnvironmentVariable("YLT_SHORTCUTS_EXPANDED") is "1" or "on" or "true";

    /// <summary>构造。</summary>
    public ShortcutsSettingsView()
    {
        InitializeComponent();

        if (!IsDebugExpandRequested()) return;

        // DataContext 是在 XAML 里绑的，Loaded 那一刻可能还是 null：两个时机都试
        Loaded += (_, _) => ExpandForDebug();
        DataContextChanged += (_, _) => ExpandForDebug();
    }

    /// <summary>调试用：把整棵树展开，并在注册表刷新后保持展开。</summary>
    private void ExpandForDebug()
    {
        if (DataContext is not ShortcutRegistry registry) return;

        // YLT_SHORTCUTS_ONLY=系统：只展开这一个分类（截图验收看某一组时用）
        var only = Environment.GetEnvironmentVariable("YLT_SHORTCUTS_ONLY");

        registry.SetAllExpanded(false);
        if (!string.IsNullOrWhiteSpace(only))
        {
            registry.ExpandOnly(only.Trim());
        }
        else
        {
            registry.SetAllExpanded(true);
        }

        registry.Changed -= OnRegistryChangedForDebug;
        registry.Changed += OnRegistryChangedForDebug;
    }

    private void OnRegistryChangedForDebug(object? sender, EventArgs e)
    {
        if (sender is not ShortcutRegistry registry) return;

        var only = Environment.GetEnvironmentVariable("YLT_SHORTCUTS_ONLY");
        if (!string.IsNullOrWhiteSpace(only)) registry.ExpandOnly(only.Trim());
        else registry.SetAllExpanded(true);
    }

    /// <summary>当前注册表（DataContext 就是它，由主页设置页面给）。</summary>
    private ShortcutRegistry? Registry => DataContext as ShortcutRegistry;

    /// <summary>「合并」：分步 → 同时（两段按键取并集）。</summary>
    private void OnMergeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutRow row }) return;

        if (!row.Merge(out var message))
        {
            // 合不出来（超过键数上限）时说清楚原因，别让按钮像坏了一样
            MessageBox.Show(Window.GetWindow(this), message, "不能合并",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>「拆分」：同时 → 分步（原来那段作第一步，第二步等录入）。</summary>
    private void OnSplitClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutRow row }) return;
        row.Split(out _);
    }

    /// <summary>「禁用 / 启用」：只关这一条。</summary>
    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShortcutRow row }) row.ToggleEnabled();
    }

    /// <summary>全部展开。</summary>
    private void OnExpandAllClick(object sender, RoutedEventArgs e) => Registry?.SetAllExpanded(true);

    /// <summary>全部收起。</summary>
    private void OnCollapseAllClick(object sender, RoutedEventArgs e) => Registry?.SetAllExpanded(false);

    /// <summary>搜索框：按工具名 / 快捷键名过滤（收起不匹配的工具分组）。</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var registry = Registry;
        if (registry is null) return;

        var keyword = SearchBox.Text?.Trim() ?? "";
        if (keyword.Length == 0)
        {
            registry.Rebuild();
            return;
        }

        // 只展开命中的工具，收起其余；不清空集合（避免每次敲键都重建控件树）
        foreach (var category in registry.Groups)
        {
            var anyHit = false;
            foreach (var tool in category.Children.OfType<ShortcutGroupNode>())
            {
                var hit = tool.ToolName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                          || tool.Children.OfType<ShortcutRow>().Any(r =>
                              r.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                              || r.DisplayText.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));

                tool.IsExpanded = hit;
                anyHit |= hit;
            }

            category.IsExpanded = anyHit;
        }
    }
}
