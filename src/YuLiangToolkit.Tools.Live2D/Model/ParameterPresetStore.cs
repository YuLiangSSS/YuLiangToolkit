using System.Text.Json;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 面部测试 / 身体测试 / 手臂卡的「预设」持久化（<c>Data\live2d\parameter-presets.json</c>）。
///
/// <para>结构按模块（face / body / arms）分组，每组保存「参数 Id → 值」+ 保存时间；
/// 应用预设 = 把值写回参数并开启该模块的「锁定」（每帧强制回填，不被待机 / 动作 / 晃动覆盖）。</para>
///
/// <para>纯静态、自检可测：JSON 序列化 / 反序列化走 <see cref="Serialize"/> / <see cref="Deserialize"/>，
/// 文件读写走 <see cref="Save"/> / <see cref="Load"/>（ToolStorage 的 Data 根，属用户数据）。</para>
/// </summary>
public static class ParameterPresetStore
{
    private const string FileName = "parameter-presets.json";
    private const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>一组预设：模块（face / body / arms）→ 参数值字典。</summary>
    public sealed class PresetFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>模块 → (保存时间, 参数 Id → 值)。</summary>
        public Dictionary<string, PresetGroup> Groups { get; set; } = new();
    }

    public sealed class PresetGroup
    {
        public string Title { get; set; } = "";
        public string SavedAt { get; set; } = "";
        public Dictionary<string, float> Params { get; set; } = new();
    }

    /// <summary>把一组参数值写进预设文件（其它模块的预设保留）。</summary>
    public static void Save(ToolStorage storage, string group, string title,
        IReadOnlyCollection<(string Id, float Value)> entries)
    {
        if (storage is null || string.IsNullOrWhiteSpace(group)) return;

        var file = LoadFile(storage) ?? new PresetFile();
        var presetGroup = new PresetGroup
        {
            Title = title,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Params = entries.ToDictionary(e => e.Id, e => e.Value),
        };
        file.Groups[group] = presetGroup;

        try
        {
            storage.WriteText(FileName, Serialize(file));
        }
        catch
        {
            // 预设写盘失败不打断渲染 / 互动（下次保存再试）
        }
    }

    /// <summary>读取某个模块的预设（没有 / 文件坏返回 null）。</summary>
    public static PresetGroup? Load(ToolStorage storage, string group)
    {
        if (storage is null || string.IsNullOrWhiteSpace(group)) return null;
        var file = LoadFile(storage);
        return file is not null && file.Groups.TryGetValue(group, out var preset) ? preset : null;
    }

    /// <summary>删除某个模块的预设。</summary>
    public static void Remove(ToolStorage storage, string group)
    {
        if (storage is null) return;
        var file = LoadFile(storage);
        if (file is null || !file.Groups.Remove(group)) return;
        try
        {
            storage.WriteText(FileName, Serialize(file));
        }
        catch
        {
            // 删除失败不打断主流程
        }
    }

    /// <summary>序列化（自检断言用）。</summary>
    public static string Serialize(PresetFile file) => JsonSerializer.Serialize(file, JsonOptions);

    /// <summary>反序列化（自检断言用；坏 JSON 返回 null）。</summary>
    public static PresetFile? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PresetFile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PresetFile? LoadFile(ToolStorage storage)
    {
        try
        {
            var json = storage.ReadText(FileName);
            return string.IsNullOrWhiteSpace(json) ? null : Deserialize(json);
        }
        catch
        {
            return null;   // 文件缺失 / 坏 JSON：按没有预设处理
        }
    }
}
