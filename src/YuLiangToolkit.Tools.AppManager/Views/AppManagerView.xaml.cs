using System.Windows.Controls;
using YuLiangToolkit.Tools.AppManager.ViewModels;

namespace YuLiangToolkit.Tools.AppManager.Views;

/// <summary>
/// 「应用管理器」界面代码后置：业务全在 <see cref="AppManagerViewModel"/>，
/// 界面本身没有需要额外代码的交互（筛选 / 排序 / 卸载 / 修改都是命令绑定）。
/// </summary>
public partial class AppManagerView : UserControl
{
    public AppManagerView()
    {
        InitializeComponent();
    }
}
