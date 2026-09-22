using System.Text.Json;
using YuLiangToolkit.Core.Models;

namespace YuLiangToolkit.Core.Services;

/// <summary>全局设置读写服务。所有 IO 都有保护，失败时静默回退到默认值（例如沙箱/只读环境）。</summary>
public class AppSettingsService
{
    /// <summary>全局设置目录：<c>%AppData%\YuLiangToolkit</c>。</summary>
    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuLiangToolkit");

    /// <summary>全局设置文件：<c>%AppData%\YuLiangToolkit\settings.json</c>。</summary>
    public static string SettingsFilePath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Settings { get; }

    public AppSettingsService()
    {
        Settings = Load();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* 损坏或不可读时使用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 无法写入时静默忽略 */ }
    }
}
