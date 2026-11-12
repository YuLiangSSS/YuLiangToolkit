using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Calculator.ViewModels;

namespace YuLiangToolkit.Tools.Calculator.Views;

/// <summary>
/// 辈分计算模式的界面代码后置：只做纯界面层的一件事 —— 点对照表里的一行，把它的关系装到左边。
/// 推导 / 配置 / 文案全部在 <see cref="KinCalculatorViewModel"/> 与 <c>Core/Kin/</c> 里。
/// </summary>
public partial class KinCalculatorView : UserControl
{
    public KinCalculatorView()
    {
        InitializeComponent();
    }

    private KinCalculatorViewModel? ViewModel => DataContext as KinCalculatorViewModel;

    /// <summary>单击对照表的一行 → 装进关系链；装完清掉选中，方便再点同一行。</summary>
    private void OnTermSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TermList.SelectedItem is not KinTermItem item) return;

        ViewModel?.UseTermCommand.Execute(item);
        TermList.SelectedIndex = -1;
    }
}
