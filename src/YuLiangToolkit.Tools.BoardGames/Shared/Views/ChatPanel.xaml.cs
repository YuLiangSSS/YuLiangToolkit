using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Shared.Views;

/// <summary>
/// 「房间聊天」面板（列表 + 输入框 + 发送 / 清空），从原 RoomPanel 里拆出来单独放到页面最底部。
/// DataContext 是游戏 ViewModel（继承 <see cref="BoardGameRoomViewModelBase"/>）；
/// 这里只做一件代码后置的事：聊天输入框里按回车 = 点发送（用户不用去点按钮）。
/// </summary>
public partial class ChatPanel : UserControl
{
    public ChatPanel()
    {
        InitializeComponent();
        ChatInputBox.PreviewKeyDown += OnChatInputKeyDown;
    }

    private void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (DataContext is not BoardGameRoomViewModelBase vm) return;
        e.Handled = true;
        if (vm.SendChatCommand.CanExecute(null)) vm.SendChatCommand.Execute(null);
    }
}
