using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Calendar.ViewModels;

namespace YuLiangToolkit.Tools.Calendar.Views;

/// <summary>
/// 日历页代码后置：只处理「点某天」与键盘操作，日期计算全在 ViewModel / CalendarMath 里。
/// </summary>
public partial class CalendarView : UserControl
{
    public CalendarView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ViewModel?.OnPageLoaded();
            Focus();   // 让键盘操作一进来就能用
        };
        Unloaded += (_, _) => ViewModel?.OnPageUnloaded();
    }

    /// <summary>页面的 ViewModel。</summary>
    private CalendarViewModel? ViewModel => DataContext as CalendarViewModel;

    /// <summary>点某一天：选中。</summary>
    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DateTime date }) return;

        ViewModel?.Select(date);
        e.Handled = true;
    }

    /// <summary>
    /// 键盘：←→↑↓ 挪、PgUp/PgDn 换月、Home 回今天。
    /// <para>⚠ 只在日历页有焦点时生效（不抢全局），所以用 <c>OnPreviewKeyDown</c> 而不是全局热键。</para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var viewModel = ViewModel;
        if (viewModel is null) return;

        switch (e.Key)
        {
            case Key.Left:
                viewModel.MoveSelection(CalendarMove.Left);
                break;
            case Key.Right:
                viewModel.MoveSelection(CalendarMove.Right);
                break;
            case Key.Up:
                viewModel.MoveSelection(CalendarMove.Up);
                break;
            case Key.Down:
                viewModel.MoveSelection(CalendarMove.Down);
                break;
            case Key.PageUp:
                viewModel.MoveSelection(CalendarMove.PreviousMonth);
                break;
            case Key.PageDown:
                viewModel.MoveSelection(CalendarMove.NextMonth);
                break;
            case Key.Home:
                viewModel.GoToday();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
