using System.Windows.Controls;

namespace YuLiangToolkit.Tools.Calculator.Views;

/// <summary>
/// 日期计算模式的界面代码后置：纯 XAML 绑定，没有业务逻辑
/// （计算在 <see cref="ViewModels.DateCalculatorViewModel"/> 与 <c>Core/Dates/DateMath.cs</c> 里）。
/// </summary>
public partial class DateCalculatorView : UserControl
{
    public DateCalculatorView()
    {
        InitializeComponent();
    }
}
