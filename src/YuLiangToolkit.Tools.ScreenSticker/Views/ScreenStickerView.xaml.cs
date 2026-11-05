using System.Windows.Controls;

namespace YuLiangToolkit.Tools.ScreenSticker.Views;

/// <summary>
/// 「屏幕贴纸」工具页：选图 / 最近使用缩略图 / 桌面贴纸管理 / 设置。
/// <para>页面逻辑全在 <see cref="ViewModels.ScreenStickerViewModel"/>；
/// 这里只做 <c>InitializeComponent</c>，不需要额外的代码后置（文件对话框等交互也在 ViewModel 里）。</para>
/// </summary>
public partial class ScreenStickerView : UserControl
{
    public ScreenStickerView()
    {
        InitializeComponent();
    }
}
