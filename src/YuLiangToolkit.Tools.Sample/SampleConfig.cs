using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Sample;

/// <summary>
/// 示例功能的强类型配置 → <c>Config\sample-framework\config.json</c>。
///
/// <para>规则：<b>需要记录的选项就写成可读写属性</b>（默认值写在属性上），改动后最多 400ms 自动写盘，
/// XAML 里直接双向绑定即可，不需要任何“保存”按钮。</para>
///
/// <para>属性两种写法都行：自动属性 <c>{ get; set; }</c> 最省事（本文件多数属性就是）；
/// 用 <c>Set(ref 字段, value)</c> 会额外发界面通知，适合需要联动刷新的属性（见下面的 <see cref="Mode"/>）。</para>
/// </summary>
public sealed class SampleConfig : ToolConfig
{
    /// <summary>Textbox 示例：文本一改就存（XAML 用 UpdateSourceTrigger=PropertyChanged）。</summary>
    public string Note { get; set; } = "";

    /// <summary>Checkbox 示例：勾选状态即时记录。</summary>
    public bool EnableFeature { get; set; } = true;

    /// <summary>下拉框示例：存的是干净的值（不是界面上的中文标签）。</summary>
    private string _mode = "标准";
    public string Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                // 依赖 Mode 的计算属性要手动通知（界面联动才不会卡住）
                OnPropertyChanged(nameof(ModeIsFast));
            }
        }
    }

    /// <summary>数字输入示例（0 ~ 999）。</summary>
    public int Repeat { get; set; } = 3;

    /// <summary>滑块示例（0.0 ~ 1.0）。</summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>只读计算属性：不会被写进 JSON，可用于界面联动（如提示文字）。</summary>
    [JsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>带 setter 的计算属性必须显式标 [JsonIgnore]，否则会被当成配置项写进文件。</summary>
    [JsonIgnore]
    public bool ModeIsFast
    {
        get => Mode == "高速";
        set { if (value) Mode = "高速"; }
    }
}
