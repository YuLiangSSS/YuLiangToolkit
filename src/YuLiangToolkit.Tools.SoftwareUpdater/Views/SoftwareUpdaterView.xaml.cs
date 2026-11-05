using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using YuLiangToolkit.Tools.SoftwareUpdater.ViewModels;

namespace YuLiangToolkit.Tools.SoftwareUpdater.Views;

/// <summary>
/// 「软件升级器」界面代码后置：只做两件纯界面层的事，业务全在 <see cref="SoftwareUpdaterViewModel"/>。
/// <list type="number">
///   <item><b>多选同步</b>：把 ListBox 的 <c>SelectedItems</c>（Ctrl / Shift 多选、框选、快捷键全选）
///         交给 VM，「勾选选中行 / 取消勾选选中行」才能作用于多行；</item>
///   <item><b>表头三态复选框</b>：点一下在「全选 / 全不选」之间切换，随后强制刷新一次绑定目标，
///         免得 WPF 自己把 <c>IsChecked</c> 循环成中间态后盖掉 VM 算出来的结果。</item>
/// </list>
/// </summary>
public partial class SoftwareUpdaterView : UserControl
{
    public SoftwareUpdaterView()
    {
        InitializeComponent();
    }

    private SoftwareUpdaterViewModel? ViewModel => DataContext as SoftwareUpdaterViewModel;

    private void OnPackageSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel?.UpdateSelection(PackageList.SelectedItems);

    private void OnHeaderCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleHeaderCommand.Execute(null);
        // IsChecked 是 OneWay 绑定：点击会在本地改写它，这里把 VM 算好的状态再拉回控件。
        BindingOperations.GetBindingExpression(HeaderCheckBox, ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }
}
