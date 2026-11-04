using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Archive.ViewModels;

namespace YuLiangToolkit.Tools.Archive.Views;

/// <summary>
/// 「压缩包工具」界面代码后置：只做三件纯界面层的事，业务全在 <see cref="ArchiveViewModel"/>：
/// <list type="number">
///   <item><b>拖放分流</b>：从资源管理器拖文件 / 文件夹进来，交给
///         <see cref="ArchiveViewModel.AddDroppedPaths"/>（压缩包 → 解压列表，其它 → 压缩列表）；</item>
///   <item><b>密码框同步</b>：WPF 的 <c>PasswordBox.Password</c> 不是依赖属性、不能直接绑定，
///         所以这里手动和 ViewModel 的 <c>Password</c> 双向同步（「显示」勾选时改用普通 TextBox 展示明文）；</item>
///   <item><b>多选同步</b>：ListBox 支持 Extended 多选，「移除选中」命令的可用状态靠它同步。</item>
/// </list>
/// </summary>
public partial class ArchiveView : UserControl
{
    private ArchiveViewModel? _subscribedViewModel;
    private bool _syncingPassword;

    public ArchiveView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // 外壳会缓存工具视图：切走时 Unloaded、切回来再 Loaded（同一个 ViewModel），所以两边都要处理
        Loaded += (_, _) => AttachViewModel();
        Unloaded += (_, _) => DetachViewModel();
    }

    private ArchiveViewModel? ViewModel => DataContext as ArchiveViewModel;

    // =====================================================================
    //  拖放：压缩包 → 解压列表；文件 / 文件夹 → 压缩列表（分流规则在 ViewModel 里）
    // =====================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        try
        {
            vm.AddDroppedPaths(paths);
        }
        catch (Exception ex)
        {
            // 拖放回调直接由原生 OLE 调用，这里抛出去会把整个程序带崩
            vm.Log("✗ 处理拖入的文件失败：" + ex.Message);
        }
        finally
        {
            e.Handled = true;
        }
    }

    // =====================================================================
    //  密码：PasswordBox ↔ ViewModel.Password
    // =====================================================================

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        ViewModel?.SetPassword(PasswordInput.Password);
    }

    private void SyncPasswordBox()
    {
        var vm = ViewModel;
        if (vm is null) return;
        if (string.Equals(PasswordInput.Password, vm.Password, StringComparison.Ordinal)) return;

        _syncingPassword = true;
        try { PasswordInput.Password = vm.Password; }
        finally { _syncingPassword = false; }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchiveViewModel.Password) ||
            e.PropertyName == nameof(ArchiveViewModel.ShowPassword))
        {
            SyncPasswordBox();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        AttachViewModel();
    }

    private void AttachViewModel()
    {
        var vm = ViewModel;
        if (vm is null || ReferenceEquals(vm, _subscribedViewModel)) return;
        _subscribedViewModel = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        SyncPasswordBox();
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is null) return;
        _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = null;
    }

    // =====================================================================
    //  多选同步
    // =====================================================================

    private void OnArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel?.UpdateArchiveSelection(ArchiveList.SelectedItems);

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel?.UpdateSourceSelection(SourceList.SelectedItems);
}
