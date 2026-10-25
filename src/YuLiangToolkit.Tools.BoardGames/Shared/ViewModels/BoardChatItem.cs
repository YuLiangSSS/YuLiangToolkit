namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 房间聊天列表里的一条：玩家说话（<see cref="IsSystem"/> = false）或系统提示（灰字居中）。
///
/// <para>只有一行文字，不做好友头像 / 时间戳气泡那些重活 —— 房间聊天的作用是「约战 + 商量悔棋」，
/// 越简单越不容易在联机时出岔子。</para>
/// </summary>
public sealed class BoardChatItem
{
    /// <summary>发送者名字（系统提示时为空）。</summary>
    public string Name { get; init; } = "";

    /// <summary>消息正文（系统提示也在里面）。</summary>
    public string Text { get; init; } = "";

    /// <summary>是不是系统提示（用灰字显示）。</summary>
    public bool IsSystem { get; init; }

    /// <summary>是不是本端发的（本端消息名字前标「我」）。</summary>
    public bool IsSelf { get; init; }

    /// <summary>显示用的一行文本：玩家消息是「名字：内容」，系统提示就是内容本身。</summary>
    public string DisplayText => IsSystem ? Text : $"{Name}{(IsSelf ? "（我）" : "")}：{Text}";

    /// <summary>本端发的消息名字用强调色。</summary>
    public bool IsMine => !IsSystem && IsSelf;

    public static BoardChatItem System(string text) => new()
    {
        Text = text ?? "",
        IsSystem = true,
    };

    public static BoardChatItem Chat(string name, string text, bool isSelf) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "对方" : name.Trim(),
        Text = text ?? "",
        IsSelf = isSelf,
    };

    public override string ToString() => DisplayText;
}
