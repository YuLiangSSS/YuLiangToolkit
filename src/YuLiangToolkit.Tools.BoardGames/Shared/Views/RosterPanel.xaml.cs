using System.Windows.Controls;

namespace YuLiangToolkit.Tools.BoardGames.Shared.Views;

/// <summary>
/// 「房间成员 / 上场席位 / 房主安排与自动轮换」面板（五子棋 / 中国象棋共用）。
/// <para>DataContext = 游戏 VM（继承 <see cref="ViewModels.BoardGameRoomViewModelBase"/>），
/// 自身没有代码逻辑，全部靠绑定。</para>
/// </summary>
public partial class RosterPanel : UserControl
{
    public RosterPanel()
    {
        InitializeComponent();
    }
}
