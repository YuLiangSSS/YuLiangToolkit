using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.TextConverter;

/// <summary>一条自定义快捷键：按某个组合 → 对选中文本做某种转换。</summary>
public sealed class ConvertShortcut : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>稳定 Id（删除 / 启停用）。</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    private string _hotkey = "";

    /// <summary>快捷键文本（<c>Ctrl+Shift+T</c>）。</summary>
    public string Hotkey
    {
        get => _hotkey;
        set
        {
            if (!Set(ref _hotkey, value ?? "")) return;
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private string _conversionId = "emoji";

    /// <summary>要做的转换 Id（见 <see cref="TextTransformer.All"/>）。</summary>
    public string ConversionId
    {
        get => _conversionId;
        set
        {
            if (!Set(ref _conversionId, value ?? "")) return;
            OnPropertyChanged(nameof(ConversionName));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private bool _enabled = true;

    /// <summary>启用这条。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>转换的显示名。</summary>
    [JsonIgnore]
    public string ConversionName => TextTransformer.Find(ConversionId)?.Name ?? "（未知转换）";

    /// <summary>列表里的一行说明。</summary>
    [JsonIgnore]
    public string SummaryText => $"{ConversationDisplay} → {ConversionName}";

    /// <summary>快捷键显示（没设置写「未设置」）。</summary>
    [JsonIgnore]
    public string ConversationDisplay => string.IsNullOrWhiteSpace(Hotkey) ? "未设置" : Hotkey;

    /// <summary>开关状态。</summary>
    [JsonIgnore]
    public string StateText => Enabled ? "已启用" : "已禁用";
}

/// <summary>「文本转换器」的强类型配置 → <c>Config\text-converter\config.json</c>。</summary>
public sealed class TextConverterConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "text-converter";

    private string _input = "";

    /// <summary>输入框内容（保留上次的文本，重开还在）。</summary>
    public string Input
    {
        get => _input;
        set => Set(ref _input, value ?? "");
    }

    private string _lastConversionId = "emoji";

    /// <summary>上次选的转换（界面下拉框沿用）。</summary>
    public string LastConversionId
    {
        get => _lastConversionId;
        set => Set(ref _lastConversionId, value ?? "");
    }

    private System.Collections.ObjectModel.ObservableCollection<ConvertShortcut> _shortcuts = new();

    /// <summary>自定义快捷键列表。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ConvertShortcut> Shortcuts
    {
        get => _shortcuts;
        set => Set(ref _shortcuts, value ?? new System.Collections.ObjectModel.ObservableCollection<ConvertShortcut>());
    }

    private bool _shortcutsEnabled = true;

    /// <summary>整个工具的快捷键总开关。</summary>
    public bool ShortcutsEnabled
    {
        get => _shortcutsEnabled;
        set => Set(ref _shortcutsEnabled, value);
    }

    private bool _restoreClipboard = true;

    /// <summary>转换后把剪贴板还原成原来的内容（不弄丢用户复制的东西）。</summary>
    public bool RestoreClipboard
    {
        get => _restoreClipboard;
        set => Set(ref _restoreClipboard, value);
    }

    private NotificationSettings _notification = new()
    {
        Mode = NotificationMode.InApp,
        Corner = NotificationCorner.BottomRight,
    };

    /// <summary>通知偏好（「没有选中文本」这类提示走它）。</summary>
    public NotificationSettings Notification
    {
        get => _notification;
        set => Set(ref _notification, value ?? new NotificationSettings());
    }

    /// <summary>启用的快捷键条数（页头统计）。</summary>
    [JsonIgnore]
    public int EnabledShortcutCount => Shortcuts.Count(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Hotkey));
}
