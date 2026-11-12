using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.NumberConverter.Core;
using YuLiangToolkit.Tools.NumberConverter.ViewModels;

namespace YuLiangToolkit.Tools.NumberConverter.Views;

/// <summary>
/// 「数字转换器」界面代码后置：只做纯界面层的事（业务全在 <see cref="NumberConverterViewModel"/>）：
/// 进页面把焦点放到输入框；单击结果行复制这一行；Ctrl+C / Esc 之类的常用键。
/// </summary>
public partial class NumberConverterView : UserControl
{
    public NumberConverterView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private NumberConverterViewModel? ViewModel => DataContext as NumberConverterViewModel;

    /// <summary>Enter = 全选输入框内容（方便直接打下一组数字）；不弹窗、不提交。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        InputBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>单击结果行 = 复制这一行（鼠标抬起、且没有拖动滚动条时才触发）。</summary>
    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.SelectedItem is ConversionRow row) ViewModel?.CopyRowCommand.Execute(row);
    }
}
