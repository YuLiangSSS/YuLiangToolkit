namespace YuLiangToolkit.App.Models;

/// <summary>
/// 本程序渲染用哪块显卡（v0.96.0，「主页设置 → 渲染与性能」）。
///
/// <para>WPF（云朵 / 光晕 / 粒子这些动画都靠它画）由 Windows 在**进程启动时**决定用哪块显卡；
/// 这个枚举就是写进 Windows「设置 → 系统 → 显示 → 图形」里本程序那条「图形性能首选项」的值
/// （<c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>，和系统设置界面是同一份数据）。
/// 改完需要重启本程序才生效。</para>
/// </summary>
public enum ShellRenderGpu
{
    /// <summary>不干预：让 Windows 按自己的规则挑显卡（默认）。</summary>
    Auto,

    /// <summary>高性能：优先独显（<c>GpuPreference=2</c>）。</summary>
    HighPerformance,

    /// <summary>节能：优先核显（<c>GpuPreference=1</c>），省电。</summary>
    PowerSaving,

    /// <summary>软件渲染：完全用 CPU 画（WPF <c>RenderMode.SoftwareOnly</c>）。只用来排查显卡 / 驱动问题。</summary>
    Software,
}

/// <summary>设置页「渲染显卡」下拉框里的一项（Id 是稳定取值，Name / Description 是界面文案）。</summary>
public sealed record ShellRenderGpuOption(string Id, string Name, string Description);

/// <summary>
/// 渲染 GPU 选择的**纯逻辑**（v0.96.0）：Id ↔ 枚举、Windows 图形首选项字符串的读写 / 合并、
/// 界面文案。真正的注册表读写和 WPF 渲染模式在 <c>Views/Rendering/GpuRenderPreference.cs</c>。
///
/// <para><b>为什么要自己拼字符串</b>：Windows 把每个程序的图形首选项存成一个分号分隔的串，
/// 例如 <c>GpuPreference=2;SwapChainBufferCount=3;</c>。我们只改 <c>GpuPreference=</c> 这一段，
/// 其它系统 / 驱动写进去的段原样保留，避免把别家的设置一起覆盖掉。</para>
/// </summary>
public static class ShellRenderGpuSettings
{
    /// <summary>Windows 图形首选项的注册表键（HKCU 下）。</summary>
    public const string RegistryKeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>注册表值里表示「GPU 偏好」的那一段。</summary>
    public const string PreferenceToken = "GpuPreference=";

    public const string AutoId = "auto";
    public const string HighPerformanceId = "high-performance";
    public const string PowerSavingId = "power-saving";
    public const string SoftwareId = "software";

    /// <summary>下拉框的四个选项（顺序 = 界面显示顺序，默认第一项「自动」）。</summary>
    public static IReadOnlyList<ShellRenderGpuOption> Options { get; } = new[]
    {
        new ShellRenderGpuOption(AutoId, "自动（让 Windows 决定）",
            "默认：不写死显卡，由 Windows 按自己的规则挑。Windows 一般会在核显 / 独显之间自动选。"),
        new ShellRenderGpuOption(HighPerformanceId, "高性能显卡（通常是独显）",
            "优先用独显渲染：云朵 / 光晕 / 粒子这些动画交给显卡，更顺滑、CPU 占用更低；笔记本会更费电。"),
        new ShellRenderGpuOption(PowerSavingId, "节能显卡（通常是核显）",
            "优先用核显渲染：省电、发热小；云朵动画在低端核显上可能不如独显顺。"),
        new ShellRenderGpuOption(SoftwareId, "软件渲染（CPU，仅排查用）",
            "完全不用显卡、由 CPU 画界面：只在显卡 / 驱动异常时用来排查问题，动画会更卡、CPU 占用更高。"),
    };

    /// <summary>枚举 → 下拉框 Id。</summary>
    public static string IdOf(ShellRenderGpu mode) => mode switch
    {
        ShellRenderGpu.HighPerformance => HighPerformanceId,
        ShellRenderGpu.PowerSaving => PowerSavingId,
        ShellRenderGpu.Software => SoftwareId,
        _ => AutoId,
    };

    /// <summary>下拉框 Id → 枚举（null / 未知 Id 返回 <see cref="ShellRenderGpu.Auto"/>）。</summary>
    public static ShellRenderGpu ParseId(string? id)
        => TryParseId(id, out var mode) ? mode : ShellRenderGpu.Auto;

    /// <summary>下拉框 Id → 枚举；未知 Id 返回 false（绑定层脏值时不要改配置）。</summary>
    public static bool TryParseId(string? id, out ShellRenderGpu mode)
    {
        switch (id?.Trim().ToLowerInvariant())
        {
            case AutoId:
                mode = ShellRenderGpu.Auto;
                return true;
            case HighPerformanceId:
                mode = ShellRenderGpu.HighPerformance;
                return true;
            case PowerSavingId:
                mode = ShellRenderGpu.PowerSaving;
                return true;
            case SoftwareId:
                mode = ShellRenderGpu.Software;
                return true;
            default:
                mode = ShellRenderGpu.Auto;
                return false;
        }
    }

    /// <summary>下拉框显示名（未知模式按「自动」处理）。</summary>
    public static string NameOf(ShellRenderGpu mode)
        => Options.FirstOrDefault(option => option.Id == IdOf(mode))?.Name ?? Options[0].Name;

    /// <summary>下拉框说明（未知模式按「自动」处理）。</summary>
    public static string Describe(ShellRenderGpu mode)
        => Options.FirstOrDefault(option => option.Id == IdOf(mode))?.Description ?? Options[0].Description;

    /// <summary>
    /// 这个模式要不要写 Windows 图形首选项：软件渲染是 WPF 进程内开关，不碰注册表；
    /// 其它三种都对应 <c>GpuPreference=0/1/2;</c>。
    /// </summary>
    public static bool UsesWindowsPreference(ShellRenderGpu mode) => mode != ShellRenderGpu.Software;

    /// <summary>模式对应的 <c>GpuPreference=</c> 数值：0 = 让 Windows 决定 / 1 = 节能 / 2 = 高性能；软件渲染 = -1（不适用）。</summary>
    public static int PreferenceValue(ShellRenderGpu mode) => mode switch
    {
        ShellRenderGpu.HighPerformance => 2,
        ShellRenderGpu.PowerSaving => 1,
        ShellRenderGpu.Auto => 0,
        _ => -1,
    };

    /// <summary>
    /// 生成要写进注册表的值：只替换 / 插入 <c>GpuPreference=</c> 段，保留 <paramref name="existing"/> 里
    /// 其它系统 / 驱动写的段（例如 <c>SwapChainBufferCount=3</c>），统一补一个结尾分号。
    /// </summary>
    public static string BuildPreferenceValue(ShellRenderGpu mode, string? existing = null)
    {
        var preference = PreferenceValue(mode);
        if (preference < 0) return existing ?? string.Empty;   // 软件渲染不写注册表

        var tokens = (existing ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !token.StartsWith(PreferenceToken, StringComparison.OrdinalIgnoreCase))
            .ToList();
        tokens.Insert(0, PreferenceToken + preference);
        return string.Join(';', tokens) + ";";
    }

    /// <summary>
    /// 解析 Windows 图形首选项字符串里的 GPU 偏好段。值不存在 / 不是 0~2 / 格式坏了返回 false
    /// （界面按「自动」显示，不改配置）。
    /// </summary>
    public static bool TryParsePreferenceValue(string? value, out ShellRenderGpu mode)
    {
        mode = ShellRenderGpu.Auto;
        if (string.IsNullOrWhiteSpace(value)) return false;

        foreach (var token in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith(PreferenceToken, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = token[PreferenceToken.Length..].Trim();
            var parsed = raw switch
            {
                "0" => ShellRenderGpu.Auto,
                "1" => ShellRenderGpu.PowerSaving,
                "2" => ShellRenderGpu.HighPerformance,
                _ => (ShellRenderGpu?)null,
            };
            if (parsed is not { } known) return false;
            mode = known;
            return true;
        }

        return false;
    }

    /// <summary>把注册表里的原始值翻译成一句人话（给设置页显示；没有 / 不认识就返回「未指定」）。</summary>
    public static string DescribePreferenceValue(string? value)
        => TryParsePreferenceValue(value, out var mode)
            ? $"{NameOf(mode)}（{PreferenceToken}{PreferenceValue(mode)}）"
            : "未单独指定（跟随 Windows 默认）";

    /// <summary>这个设置改了以后要不要重启本程序才生效（四种模式都是「下次启动生效」）。</summary>
    public static bool RequiresRestart(ShellRenderGpu current, ShellRenderGpu appliedAtStartup)
        => current != appliedAtStartup;
}
