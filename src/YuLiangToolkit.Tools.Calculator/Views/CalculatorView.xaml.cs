using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Calculator.Core;
using YuLiangToolkit.Tools.Calculator.ViewModels;

namespace YuLiangToolkit.Tools.Calculator.Views;

/// <summary>
/// 「计算器」界面代码后置：只做纯界面层的事（业务全在 <see cref="CalculatorViewModel"/>）：
/// <list type="number">
///   <item>整个页面拦截 <b>Enter</b>（求值并记历史）与 <b>Esc</b>（清空输入）；</item>
///   <item>点键盘按钮后把焦点交还表达式输入框（否则连着按键会落到按钮上、光标也不在末尾）；</item>
///   <item>历史记录单击一行 → 回填表达式。</item>
/// </list>
/// </summary>
public partial class CalculatorView : UserControl
{
    public CalculatorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private CalculatorViewModel? ViewModel => DataContext as CalculatorViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 进入页面就能直接打字，不用先点一下输入框
        FocusExpressionBox();
    }

    /// <summary>
    /// Enter = 求值并记入历史；Esc = 清空输入（在预览阶段拦下来，按钮 / 列表有焦点时也生效）。
    /// <para>⚠ 只在「标准计算」模式拦：辈分 / 日期计算模式里不能把 Enter 当成求值，
    /// 否则在日期框里敲回车会顺手清掉或算一遍表达式。</para>
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.IsStandardMode) return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.EvaluateCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.ClearCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 点键盘按钮后把焦点还给输入框，并把光标放到末尾（按钮追加的内容在最后）。
    /// 用 <see cref="System.Windows.Threading.DispatcherPriority.Background"/> 推迟一拍：
    /// Button 的 Click 事件比命令执行更早触发，这里要等表达式真正写进 TextBox 之后再定位光标。
    /// </summary>
    private void OnKeypadButtonClick(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(FocusExpressionBox),
            System.Windows.Threading.DispatcherPriority.Background);


    /// <summary>历史记录单击回填：填完取消选中，方便再点同一条。</summary>
    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not CalculatorHistoryItem item) return;

        ViewModel?.UseHistory(item);
        HistoryList.SelectedIndex = -1;
        FocusExpressionBox();
    }

    private void FocusExpressionBox()
    {
        ExpressionBox.Focus();
        ExpressionBox.CaretIndex = ExpressionBox.Text.Length;
    }
}
