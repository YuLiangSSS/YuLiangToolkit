namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 跨工具的屏幕覆盖状态：截图覆盖层打开时，屏幕画笔必须暂停输入并保持画面可被截图捕获。
/// 放在 Core 是为了避免「截图工具」和「屏幕画笔」互相引用。
/// </summary>
public static class ScreenOverlayState
{
    private static bool _isScreenshotActive;

    /// <summary>截图选择 / 编辑覆盖层是否正在前台。</summary>
    public static bool IsScreenshotActive => _isScreenshotActive;

    /// <summary>状态变化事件（在调用线程触发，通常为 UI 线程）。</summary>
    public static event EventHandler? ScreenshotActiveChanged;

    /// <summary>由截图工具在覆盖层打开前 / 关闭后设置。</summary>
    public static void SetScreenshotActive(bool active)
    {
        if (_isScreenshotActive == active) return;
        _isScreenshotActive = active;
        ScreenshotActiveChanged?.Invoke(null, EventArgs.Empty);
    }
}
