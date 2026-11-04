namespace YuLiangToolkit.Tools.ContextMenu;

/// <summary>「添加打开菜单」的结果（成功时给出创建的键，界面可以据此提示 / 定位）。</summary>
/// <param name="Success">是否全部写入成功。</param>
/// <param name="Message">给用户看的中文消息。</param>
/// <param name="CreatedKeys">实际创建的注册表键（失败时可能为空或部分）。</param>
public sealed record ContextMenuAddResult(bool Success, string Message, IReadOnlyList<ContextMenuKeyRef> CreatedKeys)
{
    public static ContextMenuAddResult Ok(string message, IReadOnlyList<ContextMenuKeyRef> createdKeys)
        => new(true, message, createdKeys);

    public static ContextMenuAddResult Fail(string message)
        => new(false, message, Array.Empty<ContextMenuKeyRef>());
}
