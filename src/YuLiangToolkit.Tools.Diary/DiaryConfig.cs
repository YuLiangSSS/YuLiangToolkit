using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Diary;

/// <summary>
/// 「日记本」的强类型配置 → <c>Config\diary\config.json</c>。
/// 只放「下次打开还要用」的界面偏好；真正的日记内容一篇一篇存在 <c>Data\diary\entries\*.json</c>。
/// </summary>
public sealed class DiaryConfig : ToolConfig
{
    /// <summary>工具 Id。</summary>
    [JsonIgnore]
    public static string ToolId => "diary";

    private bool _sortNewestFirst = true;

    /// <summary>历史列表排序：true = 最新创建的在上面（默认），false = 最旧的在上面。</summary>
    public bool SortNewestFirst
    {
        get => _sortNewestFirst;
        set => Set(ref _sortNewestFirst, value);
    }

    private DiaryFormat _defaultFormat = DiaryFormat.RichText;

    /// <summary>新建日记时默认用哪种写法。</summary>
    public DiaryFormat DefaultFormat
    {
        get => _defaultFormat;
        set => Set(ref _defaultFormat, value);
    }

    /// <summary>排序说明（界面用）。</summary>
    [JsonIgnore]
    public string SortText => SortNewestFirst ? "最新在前" : "最早在前";
}
