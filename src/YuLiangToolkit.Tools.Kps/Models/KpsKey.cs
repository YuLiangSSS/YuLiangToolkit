using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.Kps.Models;

/// <summary>
/// 布局里的一个按键（可序列化，存进 <c>Config\kps\config.json</c> 的 <c>keyRows</c> / <c>customPresets</c>）。
///
/// <para>一个格子对应一个<b>物理按键</b>：<see cref="VirtualKey"/> 是 Windows 虚拟键码（0 = 还没绑定）。
/// 同一个物理键可以出现在多个格子里（它们的按下状态会一起亮），但计数按格子分别统计。</para>
/// </summary>
public sealed class KpsKey
{
    /// <summary>Windows 虚拟键码（<c>VK_*</c>）；0 表示尚未绑定。</summary>
    public int VirtualKey { get; set; }

    /// <summary>格子显示文字；空字符串时用 <c>KeyNames.Display(VirtualKey)</c>。</summary>
    public string Label { get; set; } = "";

    /// <summary>是否显示这个格子（禁用后布局里不占位，但保留配置）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>按下时的图片（相对 <c>Data\kps\</c> 的路径；空 = 不用图片）。</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>按下颜色覆盖（<c>#AARRGGBB</c>；空 = 用全局按下颜色）。</summary>
    public string ColorHex { get; set; } = "";

    /// <summary>宽度倍数：1 = 标准键宽，2 = 两倍宽（空格键之类）。</summary>
    public int WidthUnits { get; set; } = 1;

    /// <summary>有没有绑定物理按键（没绑定的格子显示成占位）。</summary>
    [JsonIgnore]
    public bool HasBinding => VirtualKey != 0;

    /// <summary>复制一份（预设切换 / 另存预设时避免改到同一个对象）。</summary>
    public KpsKey Clone() => new()
    {
        VirtualKey = VirtualKey,
        Label = Label,
        Enabled = Enabled,
        ImagePath = ImagePath,
        ColorHex = ColorHex,
        WidthUnits = WidthUnits,
    };
}

/// <summary>布局里的一行按键（一行内从左到右排列，行与行从上到下）。</summary>
public sealed class KpsKeyRow
{
    /// <summary>这一行的按键。</summary>
    public List<KpsKey> Keys { get; set; } = new();

    /// <summary>深拷贝一整行。</summary>
    public KpsKeyRow Clone() => new() { Keys = Keys.Select(key => key.Clone()).ToList() };
}

/// <summary>
/// 一套键位预设（内置 4K / 5K / 6K / 8K / 方向键，也可以把当前布局「另存为」自定义预设）。
/// 内置预设的 <see cref="Rows"/> 由 <c>KpsPresets</c> 生成，不写进配置文件；
/// 自定义预设整套存在 <c>config.json</c> 的 <c>customPresets</c> 里。
/// </summary>
public sealed class KpsPreset
{
    /// <summary>稳定 Id（内置预设是固定字符串，自定义预设是保存时生成的名字）。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面上显示的名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>是不是内置预设（内置的不能删除 / 改名）。</summary>
    public bool BuiltIn { get; set; } = true;

    /// <summary>预设里的键位布局。</summary>
    public List<KpsKeyRow> Rows { get; set; } = new();

    /// <summary>深拷贝所有行。</summary>
    public List<KpsKeyRow> CloneRows() => Rows.Select(row => row.Clone()).ToList();
}
