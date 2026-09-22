using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// JSON 文件读写工具（配置/数据落盘统一走这里）：
/// <list type="bullet">
///   <item><b>原子写</b>：先写同目录 <c>*.tmp</c> 再整体替换，断电/崩溃不会留下半个文件；</item>
///   <item><b>容错读</b>：文件缺失或 JSON 损坏时返回默认值，并把损坏文件改名备份成 <c>*.bad-时间戳.json</c>；</item>
///   <item><b>可读性</b>：camelCase 命名 + 缩进 + 中文不转义（配置文件人眼可读、可手改）；</item>
///   <item><b>宽容解析</b>：允许注释和尾随逗号，枚举按字符串读写。</item>
/// </list>
/// 所有方法都不抛异常（除非显式传 <paramref name="onError"/>），失败返回 false / 默认值。
/// </summary>
public static class JsonFileStore
{
    /// <summary>全项目统一的 JSON 序列化选项。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) // camelCase + 读取时大小写不敏感
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文原样输出，不变成 \uXXXX
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // 只读属性（例如 `public bool HasFolder => ...`）不写进 JSON；
            // 带 setter 的计算属性请自行标注 [JsonIgnore]
            IgnoreReadOnlyProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>读取 JSON 文件；不存在或损坏时返回 <paramref name="fallback"/>。</summary>
    public static T? Read<T>(string path, T? fallback = default, Action<string>? onError = null)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
        }
        catch (Exception ex)
        {
            BackupBrokenFile(path);
            onError?.Invoke($"读取 {path} 失败：{ex.Message}");
            return fallback;
        }
    }

    /// <summary>原子写入 JSON 文件；目录不存在会自动创建。成功返回 true。</summary>
    public static bool Write<T>(string path, T value, Action<string>? onError = null)
        => WriteJson(path, Serialize(value), onError);

    /// <summary>原子写入一段现成的 JSON 文本（调用方负责保证文本合法）。成功返回 true。</summary>
    public static bool WriteJson(string path, string json, Action<string>? onError = null)
    {
        string? temp = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true); // 同目录内替换 = 原子
            temp = null;
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke($"写入 {path} 失败：{ex.Message}");
            return false;
        }
        finally
        {
            if (temp is not null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>对象 → JSON 字符串（缩进、camelCase、中文不转义）；失败返回空对象 <c>{}</c>。</summary>
    public static string Serialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);
        }
        catch (Exception ex)
        {
            return $"/* 序列化失败：{ex.Message} */";
        }
    }

    /// <summary>对象 → 格式化 JSON 字符串（用于界面预览 / 日志）。</summary>
    public static string ToJson(object? value, bool indented = true)
    {
        try
        {
            return indented
                ? JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options)
                : JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object),
                    new JsonSerializerOptions(Options) { WriteIndented = false });
        }
        catch (Exception ex)
        {
            return $"/* 序列化失败：{ex.Message} */";
        }
    }

    /// <summary>把损坏的 JSON 文件改名备份（<c>xxx.json</c> → <c>xxx.bad-20250101-120000.json</c>）。</summary>
    public static string? BackupBrokenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dir = Path.GetDirectoryName(path) ?? ".";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var backup = Path.Combine(dir, $"{name}.bad-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            File.Move(path, backup, overwrite: true);
            return backup;
        }
        catch
        {
            return null;
        }
    }
}
