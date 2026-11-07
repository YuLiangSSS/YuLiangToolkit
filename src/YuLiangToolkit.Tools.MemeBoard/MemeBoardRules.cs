using System.Globalization;

namespace YuLiangToolkit.Tools.MemeBoard;

/// <summary>点击收藏图片之后的动作模式。</summary>
public enum MemeActionMode
{
    /// <summary>复制到剪贴板，并自动粘贴到 QQ。</summary>
    PasteToQq,

    /// <summary>只复制到剪贴板，完全不碰 QQ（防风控 / 手动粘贴时用）。</summary>
    ClipboardOnly,
}

/// <summary>
/// 检测到的一个 QQ 聊天窗口（主窗口或独立聊天窗口）。
/// <para>只做数据承载，不含任何窗口句柄的读写逻辑（那些在 <c>Services\QqWindowService</c> 里）。</para>
/// </summary>
/// <param name="Handle">顶层窗口句柄。</param>
/// <param name="ProcessId">所属进程 Id（QQ / TIM / QQEX）。</param>
/// <param name="ProcessName">进程名，例如 <c>QQ</c>。</param>
/// <param name="WindowTitle">窗口标题（QQ 主窗口固定是「QQ」）。</param>
/// <param name="ChatName">UIA 读到的当前会话名（例如「测试群」）。</param>
/// <param name="IsMinimized">窗口是否已最小化。</param>
public sealed record QqWindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string WindowTitle,
    string ChatName,
    bool IsMinimized)
{
    /// <summary>是否读到了会话名（读不到的窗口不会被列为粘贴目标）。</summary>
    public bool HasChatName => !string.IsNullOrWhiteSpace(ChatName);

    /// <summary>下拉框里显示的文字：会话名 + 窗口状态；读不到会话名时退回窗口标题。</summary>
    public string DisplayText
    {
        get
        {
            if (!HasChatName)
                return $"{WindowTitle}（未识别会话）";

            if (IsMinimized)
                return $"{ChatName} · 已最小化";

            // 主窗口标题固定是「QQ」，说明用户在主窗口里切到了这个会话；
            // 独立聊天窗口的标题通常就是会话名，这里用「QQ 窗口」区分。
            var state = string.Equals(WindowTitle, "QQ", StringComparison.Ordinal) ? "QQ 主窗口" : "QQ 窗口";
            return $"{ChatName} · {state}";
        }
    }

    /// <summary>
    /// <c>ComboBox</c> 的全局样式是自绘选中区（<c>ContentPresenter</c> + <c>SelectionBoxItem</c>），
    /// 不认 <c>DisplayMemberPath</c>，选中项会直接显示记录类型的 ToString；
    /// 这里退回 <see cref="DisplayText"/>，保证下拉框选中区也显示「会话名 · 窗口」而不是一串字段。
    /// </summary>
    public override string ToString() => DisplayText;
}

/// <summary>
/// 动作模式与配置值之间的转换（配置里存 <c>paste</c> / <c>clipboard</c> 两个干净的值，
/// 界面上显示中文）。
/// </summary>
public static class MemeActionModes
{
    /// <summary>配置值：复制并粘贴到 QQ。</summary>
    public const string PasteToQqValue = "paste";

    /// <summary>配置值：仅复制到剪贴板。</summary>
    public const string ClipboardOnlyValue = "clipboard";

    /// <summary>
    /// 解析配置值：<c>clipboard</c> → 仅复制；<c>paste</c> → 粘贴到 QQ；
    /// <b>其它（null / 空 / 未知值）一律安全回退到「仅复制」</b>，绝不在配置损坏时乱动 QQ。
    /// </summary>
    public static MemeActionMode Parse(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Equals(PasteToQqValue, StringComparison.OrdinalIgnoreCase)) return MemeActionMode.PasteToQq;
        return MemeActionMode.ClipboardOnly;
    }

    /// <summary>动作模式 → 配置值。</summary>
    public static string ToConfigValue(MemeActionMode mode)
        => mode == MemeActionMode.PasteToQq ? PasteToQqValue : ClipboardOnlyValue;

    /// <summary>动作模式 → 界面上的中文说明。</summary>
    public static string DisplayName(MemeActionMode mode)
        => mode == MemeActionMode.PasteToQq ? "复制并粘贴到 QQ" : "仅复制到剪贴板";
}

/// <summary>
/// 目标会话匹配：把「实际读到的会话名」和「用户保存的目标会话名」做严格比较。
/// <para>只做<b>精确</b>匹配（去掉空白与标点后忽略大小写），<b>不允许模糊包含</b>：
/// 宁可提示“找不到目标会话”，也不要把表情包误发到别的群。</para>
/// </summary>
public static class ChatTargetMatcher
{
    /// <summary>
    /// 归一化：去掉所有空白与非字母数字字符（CJK 算 letter，所以中文会保留），保留大小写。
    /// 例如 <c>"测试 群（2）"</c> → <c>"测试群2"</c>。
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var buffer = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) buffer.Append(ch);
        }
        return buffer.ToString();
    }

    /// <summary>两个会话名是否匹配：都非空，且归一化后忽略大小写完全相等。</summary>
    public static bool IsMatch(string? actual, string? expected)
    {
        var left = Normalize(actual);
        var right = Normalize(expected);
        return left.Length > 0 && right.Length > 0
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从候选窗口里挑出目标窗口：没有保存目标 / 没有候选 / 全都不匹配时返回 <c>null</c>；
    /// 多个命中时优先<b>非最小化</b>的窗口，再按句柄升序取第一个。
    /// </summary>
    public static QqWindowInfo? Resolve(IReadOnlyList<QqWindowInfo>? windows, string? savedChat)
    {
        if (windows is null || windows.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(savedChat)) return null;

        QqWindowInfo? best = null;
        foreach (var window in windows)
        {
            if (window is null || !window.HasChatName) continue;
            if (!IsMatch(window.ChatName, savedChat)) continue;

            if (best is null
                || (best.IsMinimized && !window.IsMinimized)
                || (best.IsMinimized == window.IsMinimized && window.Handle.ToInt64() < best.Handle.ToInt64()))
            {
                best = window;
            }
        }
        return best;
    }
}

/// <summary>随机发送延迟的夹取与取值（纯逻辑，界面与自检共用）。</summary>
public static class MemeTiming
{
    /// <summary>允许的最短延迟（毫秒）。</summary>
    public const int MinDelayMs = 200;

    /// <summary>允许的最长延迟（毫秒）。</summary>
    public const int MaxDelayMs = 10000;

    /// <summary>把毫秒延迟夹到 [<see cref="MinDelayMs"/>, <see cref="MaxDelayMs"/>]。</summary>
    public static int ClampDelayMs(int ms) => Math.Clamp(ms, MinDelayMs, MaxDelayMs);

    /// <summary>
    /// 归一化一对延迟：各自夹取后再保证 <c>Min &lt;= Max</c>（顺序反了就交换）。
    /// </summary>
    public static (int Min, int Max) NormalizeRange(int minMs, int maxMs)
    {
        var min = ClampDelayMs(minMs);
        var max = ClampDelayMs(maxMs);
        return min <= max ? (min, max) : (max, min);
    }

    /// <summary>
    /// 在 [Min, Max] 里随机取一个延迟（毫秒，闭区间）；<paramref name="random"/> 传 null 时用
    /// <see cref="Random.Shared"/>（自检可以传固定种子复现）。
    /// </summary>
    public static int PickDelayMs(int minMs, int maxMs, Random? random = null)
    {
        var (min, max) = NormalizeRange(minMs, maxMs);
        if (min >= max) return min;
        var rng = random ?? Random.Shared;
        return rng.Next(min, max + 1);
    }
}

/// <summary>收藏图片的文件规则：支持哪些格式、从剪贴板导入时叫什么名字。</summary>
public static class MemeFiles
{
    /// <summary>支持收藏的图片扩展名（判断时大小写不敏感）。</summary>
    private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    /// <summary>「导入图片」对话框的筛选器文本。</summary>
    public const string FileDialogFilter =
        "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp|PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|GIF 图片|*.gif|BMP 图片|*.bmp|所有文件|*.*";

    /// <summary>这个路径看起来是不是受支持的图片（只按扩展名判断，不检查文件是否存在）。</summary>
    public static bool IsSupportedImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string extension;
        try { extension = Path.GetExtension(path.Trim()); }
        catch { return false; }
        if (string.IsNullOrEmpty(extension)) return false;

        foreach (var supported in SupportedExtensions)
        {
            if (extension.Equals(supported, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>从剪贴板保存图片时用的文件名：<c>剪贴板-20250101-123456.png</c>。</summary>
    public static string ClipboardImportName(DateTime now)
        => "剪贴板-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png";
}
