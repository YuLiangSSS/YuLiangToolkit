using System.Text.Json;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Notifications;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.NotificationLog;

/// <summary>
/// 通知历史的持久化：把 <see cref="NotificationHistory"/> 的条目存成
/// <c>Config\notification-history\history.json</c>，启动时读回来。
///
/// <para>与工具配置分开存（配置走 <c>ToolConfigStore</c>，历史条数多、写盘频率也高），
/// 但同一个目录，清理 / 重建配置时一起被删掉。</para>
///
/// <para>写盘用「先写临时文件再替换」：程序在写一半时被杀掉也不会留下半截 JSON。</para>
/// </summary>
public static class NotificationLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>历史文件的完整路径。</summary>
    public static string PathOf()
    {
        var configPath = ToolConfigStore.For(NotificationLogConfig.ToolId, "config.json").FilePath;
        var directory = System.IO.Path.GetDirectoryName(configPath) ?? ".";
        return System.IO.Path.Combine(directory, NotificationLogConfig.HistoryFileName);
    }

    /// <summary>读回历史（文件不存在 / 坏了都返回空列表，不抛异常）。</summary>
    public static IReadOnlyList<NotificationRecord> Load()
    {
        try
        {
            var path = PathOf();
            if (!System.IO.File.Exists(path)) return Array.Empty<NotificationRecord>();

            var json = System.IO.File.ReadAllText(path);
            var records = JsonSerializer.Deserialize<List<NotificationRecord>>(json, JsonOptions);
            return records is null
                ? Array.Empty<NotificationRecord>()
                : records.Where(r => r is not null).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NotificationHistory] 读历史失败：{ex.Message}");
            return Array.Empty<NotificationRecord>();
        }
    }

    /// <summary>写盘（<paramref name="remember"/> 为 false 时删除已有文件）。</summary>
    public static void Save(IEnumerable<NotificationRecord> records, bool remember)
    {
        try
        {
            var path = PathOf();

            if (!remember)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(records, JsonOptions);
            var temp = path + ".tmp";
            System.IO.File.WriteAllText(temp, json);

            // 原子替换：先写临时文件再覆盖
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(temp, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NotificationHistory] 写历史失败：{ex.Message}");
        }
    }
}
