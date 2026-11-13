using System.Windows.Controls;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>
/// 「通知设置」可复用界面块（代码后置）：DataContext 就是某个工具的
/// <see cref="NotificationSettings"/>，双向绑定到它的字段，改一下自动落盘到那个工具的配置里。
/// </summary>
public partial class NotificationSettingsView : UserControl
{
    /// <summary>构造。</summary>
    public NotificationSettingsView()
    {
        InitializeComponent();
    }
}
