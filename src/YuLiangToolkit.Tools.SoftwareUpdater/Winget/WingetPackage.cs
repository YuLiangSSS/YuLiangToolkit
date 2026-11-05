using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.SoftwareUpdater.Winget;

/// <summary>
/// <c>winget list</c> / <c>winget upgrade</c> 表格里的一行（已安装应用 + 可用版本）。
/// <para>只是纯数据，不引用任何 WPF 类型，自检可以直接断言解析器结果。</para>
/// </summary>
public sealed record WingetPackage(
    string Name,
    string Id,
    string Version,
    string AvailableVersion,
    string Source)
{
    /// <summary>winget 认为这一项有新版本（"Unknown" / 空 / 占位符不算）。</summary>
    [JsonIgnore]
    public bool HasUpdate
    {
        get
        {
            var available = (AvailableVersion ?? "").Trim();
            if (available.Length == 0) return false;
            if (available is "-" or "--" or "N/A" or "n/a") return false;
            return !available.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>终端太窄时 winget 会把名称 / ID 截断成 "…"；被截断的 ID 不能拿去升级。</summary>
    [JsonIgnore]
    public bool IsIdTruncated
        => (Id ?? "").Contains('…') || (Id ?? "").EndsWith("...", StringComparison.Ordinal);
}
