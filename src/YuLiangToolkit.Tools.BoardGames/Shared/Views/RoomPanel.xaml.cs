using System.Windows.Controls;

namespace YuLiangToolkit.Tools.BoardGames.Shared.Views;

/// <summary>
/// 「房间」面板：模式徽标 / 同机双人 / 开房 / 加入 / 分享地址 / 状态 / 暂停与重连 / 离开。
///
/// <para>聊天已经拆到 <see cref="ChatPanel"/>（放页面最底部），两个控件都要求
/// <c>DataContext</c> 是游戏 ViewModel（继承 <c>BoardGameRoomViewModelBase</c>）。
/// 本文件只做 <c>InitializeComponent</c>，没有代码逻辑。</para>
/// </summary>
public partial class RoomPanel : UserControl
{
    public RoomPanel() => InitializeComponent();
}
