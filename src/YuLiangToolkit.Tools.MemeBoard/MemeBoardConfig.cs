using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.MemeBoard;

/// <summary>
/// 「表情包收藏夹」的强类型配置 → <c>Config\meme-board\config.json</c>。
///
/// <para>所有选项改一下就自动落盘（最多 400ms），没有「保存」按钮；界面上直接双向绑定。
/// 图片本体存在 <c>Data\meme-board\images\</c> 里。</para>
///
/// <para><b>规则</b>：带 setter 的计算属性（界面联动用）必须标 <see cref="JsonIgnoreAttribute"/>，
/// 否则会被当成配置项写进 JSON。</para>
/// </summary>
public sealed class MemeBoardConfig : ToolConfig
{
    // ---------------------------------------------------------------- 持久化字段

    /// <summary>点击图片后的动作：<c>paste</c>（复制并粘贴到 QQ）/ <c>clipboard</c>（仅复制）。</summary>
    private string _actionMode = MemeActionModes.PasteToQqValue;

    /// <summary>
    /// 点击图片后的动作（配置值）：<c>paste</c> = 复制并粘贴到 QQ；<c>clipboard</c> = 仅复制到剪贴板。
    /// 未知值会被规范成「仅复制」（安全回退）。
    /// </summary>
    public string ActionMode
    {
        get => _actionMode;
        set
        {
            var normalized = MemeActionModes.ToConfigValue(MemeActionModes.Parse(value));
            if (!Set(ref _actionMode, normalized)) return;
            OnPropertyChanged(nameof(IsPasteToQq));
            OnPropertyChanged(nameof(IsClipboardOnly));
        }
    }

    /// <summary>目标 QQ 会话名（例如「测试群」）。空 = 还没选目标。</summary>
    private string _targetChat = "";

    /// <summary>目标 QQ 会话名（例如「测试群」）；空字符串表示还没选目标。</summary>
    public string TargetChat
    {
        get => _targetChat;
        set => Set(ref _targetChat, value ?? "");
    }

    /// <summary>粘贴确认后是否自动发送（模拟回车 / 点发送按钮）。</summary>
    public bool AutoSend { get; set; } = true;

    /// <summary>自动发送前随机等待的下限（毫秒）。</summary>
    private int _sendDelayMinMs = 500;

    /// <summary>自动发送前随机等待的下限（毫秒），自动夹到 [200, 10000]。</summary>
    public int SendDelayMinMs
    {
        get => _sendDelayMinMs;
        set
        {
            var clamped = MemeTiming.ClampDelayMs(value);
            if (!Set(ref _sendDelayMinMs, clamped)) return;
            if (_sendDelayMaxMs < clamped) SendDelayMaxMs = clamped;
            OnPropertyChanged(nameof(DelayMinSeconds));
            OnPropertyChanged(nameof(DelayRangeText));
        }
    }

    /// <summary>自动发送前随机等待的上限（毫秒）。</summary>
    private int _sendDelayMaxMs = 1500;

    /// <summary>自动发送前随机等待的上限（毫秒），自动夹到 [200, 10000]。</summary>
    public int SendDelayMaxMs
    {
        get => _sendDelayMaxMs;
        set
        {
            var clamped = MemeTiming.ClampDelayMs(value);
            if (!Set(ref _sendDelayMaxMs, clamped)) return;
            if (_sendDelayMinMs > clamped) SendDelayMinMs = clamped;
            OnPropertyChanged(nameof(DelayMaxSeconds));
            OnPropertyChanged(nameof(DelayRangeText));
        }
    }

    /// <summary>粘贴前是否校验「当前会话」就是目标会话（不一致就中止，图片留在剪贴板）。</summary>
    public bool VerifyTargetChat { get; set; } = true;

    // ---------------------------------------------------------------- 界面联动计算属性

    /// <summary>界面单选：复制并粘贴到 QQ。</summary>
    [JsonIgnore]
    public bool IsPasteToQq
    {
        get => MemeActionModes.Parse(ActionMode) == MemeActionMode.PasteToQq;
        set { if (value) ActionMode = MemeActionModes.PasteToQqValue; }
    }

    /// <summary>界面单选：仅复制到剪贴板。</summary>
    [JsonIgnore]
    public bool IsClipboardOnly
    {
        get => MemeActionModes.Parse(ActionMode) == MemeActionMode.ClipboardOnly;
        set { if (value) ActionMode = MemeActionModes.ClipboardOnlyValue; }
    }

    /// <summary>自动发送前随机等待的下限（秒，供滑块绑定）。</summary>
    [JsonIgnore]
    public double DelayMinSeconds
    {
        get => _sendDelayMinMs / 1000.0;
        set
        {
            if (!double.IsFinite(value)) return;
            SendDelayMinMs = (int)Math.Round(value * 1000, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>自动发送前随机等待的上限（秒，供滑块绑定）。</summary>
    [JsonIgnore]
    public double DelayMaxSeconds
    {
        get => _sendDelayMaxMs / 1000.0;
        set
        {
            if (!double.IsFinite(value)) return;
            SendDelayMaxMs = (int)Math.Round(value * 1000, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>「随机 0.5~1.5 秒后发送」这类说明文字。</summary>
    [JsonIgnore]
    public string DelayRangeText
        => $"随机 {DelayMinSeconds:0.0}~{DelayMaxSeconds:0.0} 秒后发送";

    /// <summary>是否已经选了目标会话（只读计算属性）。</summary>
    [JsonIgnore]
    public bool HasTargetChat => !string.IsNullOrWhiteSpace(TargetChat);
}
