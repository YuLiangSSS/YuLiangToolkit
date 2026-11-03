using YuLiangToolkit.Tools.ChatRoom.Net;

namespace YuLiangToolkit.Tools.ChatRoom.ViewModels;

/// <summary>
/// 通话界面与外壳之间的钩子（Lead 的协议 / 网络层只调这两个 partial 方法，
/// 具体实现在 <c>ChatRoomViewModel.Call.cs</c>（通话 UI 负责），
/// 这样两边可以各写各的文件、互不改对方文件）。
/// </summary>
public sealed partial class ChatRoomViewModel
{
    /// <summary>一次会话的网络层建好后调用：把 <see cref="ChatRoomSession"/> 的通话事件接到界面。</summary>
    partial void OnCallSessionWired(ChatRoomSession session);

    /// <summary>工具被真正关闭 / 断开连接时调用：挂断通话并复位界面状态。</summary>
    partial void OnCallUiHangUp();
}
