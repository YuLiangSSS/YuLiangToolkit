using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>
/// 「邮箱代收」的强类型配置 → <c>Config\mail-collector\config.json</c>。
///
/// <para>账号列表放在配置里（<see cref="ObservableCollection{T}"/> 只是为了界面增删方便），
/// 每个账号的授权码已在 <see cref="MailAccountConfig.Password"/> 里用 DPAPI 加密；
/// 改任何一项都会在 400ms 内自动落盘，不需要「保存」按钮。</para>
/// </summary>
public sealed class MailCollectorConfig : ToolConfig
{
    /// <summary>要代收的邮箱账号（顺序 = 界面列表顺序）。</summary>
    public ObservableCollection<MailAccountConfig> Accounts { get; set; } = new();

    /// <summary>每个账号一次拉取最近多少封（5 ~ 200；默认 30）。</summary>
    public int FetchCount { get; set; } = 30;

    /// <summary>打开工具页时自动刷新一次（关闭则只显示上次列表 / 手动点刷新）。</summary>
    public bool RefreshOnOpen { get; set; } = true;

    /// <summary>上次选中的账号 Id（重新打开工具时恢复选中）。</summary>
    public string LastSelectedAccountId { get; set; } = "";

    /// <summary>实际使用的拉取数量（越界自动夹取；只读计算属性不落盘）。</summary>
    [JsonIgnore]
    public int EffectiveFetchCount => Math.Clamp(FetchCount, 5, 200);
}
