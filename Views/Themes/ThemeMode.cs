using Microsoft.Win32;

namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 外观模式（「主页设置 → 外观模式」）：深色 / 浅色 / 跟随系统。
/// <para>存进 <c>Config\shell\config.json</c> 的 <c>themeMode</c>；旧配置没有这个字段时读出来就是
/// <see cref="Dark"/>（升级后外观与以前一致）。</para>
/// </summary>
public enum ThemeMode
{
    /// <summary>深色（默认）。</summary>
    Dark,

    /// <summary>浅色。</summary>
    Light,

    /// <summary>跟随 Windows 的「应用模式」（设置 → 个性化 → 颜色 → 选择默认应用模式）。</summary>
    System,
}

/// <summary>
/// 「跟随系统」的判定：读 Windows 当前用户的应用模式（<c>AppsUseLightTheme</c>），
/// 并把模式解析成实际要套的深 / 浅色。
///
/// <para>为什么读注册表：WPF 没有跨版本稳定的“系统应用模式”API；
/// 这个键就是 Win10 1809+ / Win11 「设置 → 个性化 → 颜色 → 选择默认应用模式」写的地方，
/// 和 Edge / 资源管理器 / 终端用的是同一个开关。读失败一律按深色（和旧行为一致）。</para>
/// </summary>
public static class SystemThemeMode
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    /// <summary>Windows 当前是不是浅色应用模式（读不到 / 出错 → 深色）。</summary>
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(ValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把配置里的模式解析成实际生效的深 / 浅色（<see cref="ThemeMode.System"/> 会去问系统）。</summary>
    public static ThemeMode Resolve(ThemeMode mode)
        => mode switch
        {
            ThemeMode.Light => ThemeMode.Light,
            ThemeMode.System => IsLight() ? ThemeMode.Light : ThemeMode.Dark,
            _ => ThemeMode.Dark,
        };

    /// <summary>模式的中文名（设置页 / 自检用）。</summary>
    public static string DisplayName(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "浅色",
        ThemeMode.System => "跟随系统",
        _ => "深色",
    };

    /// <summary>一句话说明（设置页当前状态那一行用）。</summary>
    public static string Describe(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "当前：浅色",
        ThemeMode.System => $"跟随系统（当前系统是{(IsLight() ? "浅色" : "深色")}）",
        _ => "当前：深色",
    };

    /// <summary>宽松解析（<c>YLT_THEME_MODE</c> / 配置文件手改；认不出返回 null）。</summary>
    public static ThemeMode? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        return normalized switch
        {
            "dark" or "深色" or "暗色" or "0" => ThemeMode.Dark,
            "light" or "浅色" or "亮色" or "1" => ThemeMode.Light,
            "system" or "auto" or "follow" or "跟随系统" or "系统" or "2" => ThemeMode.System,
            _ => null,
        };
    }
}
