using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Alarm.ViewModels;

namespace YuLiangToolkit.Tools.Alarm.Views;

/// <summary>闹钟页代码后置：页面可见 / 隐藏时告诉 ViewModel。</summary>
public partial class AlarmView : UserControl
{
    public AlarmView()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel?.OnPageLoaded();
    }

    /// <summary>页面的 ViewModel。</summary>
    private AlarmViewModel? ViewModel => DataContext as AlarmViewModel;
}
