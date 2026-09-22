namespace YuLiangToolkit.Core.Services;

/// <summary>
/// FFmpeg 定位器：依次从 配置路径 → PATH 环境变量 → 常见安装位置 查找 ffmpeg.exe。
/// </summary>
public static class FFmpegLocator
{
    public static string? Find(string? configuredPath)
    {
        // 1) 用户手动配置的路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try { if (File.Exists(configuredPath)) return configuredPath; } catch { /* 忽略 */ }
        }

        // 2) PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* 忽略无效目录 */ }
        }

        // 3) 常见位置
        var common = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
        };
        foreach (var c in common)
        {
            try { if (File.Exists(c)) return c; } catch { /* 忽略 */ }
        }

        return null;
    }

    /// <summary>根据 ffmpeg.exe 推断同目录下的 ffprobe.exe。</summary>
    public static string? FindFFprobe(string ffmpegPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var p = Path.Combine(dir, "ffprobe.exe");
            return File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }
}
