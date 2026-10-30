using System.Runtime.CompilerServices;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Configuration;

/// <summary>
/// 强类型功能配置基类：<b>只要属性值变了，就会自动写回
/// <c>Config\&lt;功能名&gt;\&lt;文件名&gt;.json</c></b>，不需要任何手动 Save 调用。
///
/// <para><b>用法</b>（三步）：</para>
/// <code>
/// // 1) 声明配置类（持久化字段 = 可读写属性，默认值写在属性上；自动属性就行）
/// public sealed class FlashDownloaderConfig : ToolConfig
/// {
///     public string SaveFolder { get; set; } = "";
///     public bool AutoRename { get; set; } = true;
///     public int TimeoutSeconds { get; set; } = 30;
///
///     // 只读计算属性不会被写进 JSON；带 setter 的计算属性请显式加 [JsonIgnore]
///     [JsonIgnore] public bool HasFolder => SaveFolder.Length > 0;
/// }
///
/// // 2) 在 ViewModel 里加载（内部已自动挂上保存 + 首次落盘）
/// Cfg = LoadConfig&lt;FlashDownloaderConfig&gt;();
///
/// // 3) XAML 直接双向绑定：CheckBox / TextBox / ComboBox 改一下就自动存一次
/// &lt;CheckBox IsChecked="{Binding Cfg.AutoRename}" /&gt;
/// &lt;TextBox Text="{Binding Cfg.SaveFolder, UpdateSourceTrigger=PropertyChanged}" /&gt;
/// </code>
///
/// <para><b>两种属性写法都能自动保存</b>（保存由后台“内容比对”完成，不依赖变更通知）：</para>
/// <list type="bullet">
///   <item><c>public string Name { get; set; }</c> —— 最省事，推荐；</item>
///   <item><c>public string Name { get => _name; set => Set(ref _name, value); }</c> —— 额外发出
///         <see cref="System.ComponentModel.INotifyPropertyChanged"/> 通知：绑定了同一属性的<b>其它</b>控件会立即刷新，
///         联动/计算属性也靠它触发（例如 Mode 变了要让 IsMp3Mode 跟着变）。</item>
/// </list>
/// </summary>
public abstract class ToolConfig : ObservableObject
{
    private ToolConfigStore? _store;

    /// <summary>所属的配置存储（加载后自动注入；未加载时为 null）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ToolConfigStore? Store => _store;

    /// <summary>配置文件完整路径（未加载时为空字符串）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath => _store?.FilePath ?? "";

    /// <summary>由 <see cref="ToolConfigStore"/> 在加载时调用：挂上自动保存。</summary>
    internal void AttachTo(ToolConfigStore store)
    {
        if (ReferenceEquals(_store, store)) return;
        if (_store is not null) PropertyChanged -= OnAnyPropertyChanged;
        _store = store;
        PropertyChanged += OnAnyPropertyChanged;
    }

    private void OnAnyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => _store?.ScheduleSave();

    /// <summary>
    /// 赋值（等价于基类 <see cref="ObservableObject.Set{T}"/>）：比自动属性多一个“界面通知”，
    /// 适合需要联动刷新的属性；纯持久化字段用自动属性 <c>{ get; set; }</c> 即可，同样会自动保存。
    /// </summary>
    protected bool SetAndSave<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        => Set(ref field, value, propertyName);

    /// <summary>
    /// 重建配置用（<see cref="ToolConfigStore.ResetToDefaults"/> 会调它）：让界面重新读一遍所有属性。
    /// 传空属性名 = WPF 认为“所有属性都变了”，这样自动属性也能刷新（它们平时不发通知）。
    /// </summary>
    internal void NotifyAllPropertiesChanged() => OnPropertyChanged(string.Empty);

    /// <summary>当前的 JSON 文本（用于界面预览 / 排错）。</summary>
    public string ToJson() => JsonFileStore.ToJson(this);

    /// <summary>立刻把当前值落盘（正常情况下不需要手动调用，防抖保存会在 400ms 内自动完成）。</summary>
    public void SaveNow() => _store?.SaveNow();
}
