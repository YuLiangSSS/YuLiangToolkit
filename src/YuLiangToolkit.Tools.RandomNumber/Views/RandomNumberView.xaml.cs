using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.RandomNumber.ViewModels;

namespace YuLiangToolkit.Tools.RandomNumber.Views;

/// <summary>
/// 「随机数生成器」界面代码后置：只做纯界面层的事（业务全在 <see cref="RandomNumberViewModel"/>）：
/// 进页面把焦点放到下限输入框，Enter 直接生成（不用先点按钮）。
/// </summary>
public partial class RandomNumberView : UserControl
{
    public RandomNumberView()
    {
        InitializeComponent();
        Loaded += (_, _) => LowerBox.Focus();
    }

    private RandomNumberViewModel? ViewModel => DataContext as RandomNumberViewModel;

    /// <summary>Enter = 生成（在下限 / 上限 / 精确度输入框里敲完直接回车也能用）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel?.GenerateCommand.Execute(null);
        e.Handled = true;
    }
}
