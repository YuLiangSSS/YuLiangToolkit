using System.IO;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.Rendering;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「渲染 GPU」纯逻辑自检（v0.96.0，<c>shell</c> 分组）。
///
/// <para>覆盖四块，都不写真实注册表、不开窗口：</para>
/// <list type="bullet">
///   <item><b>选项清单</b>（<see cref="ShellRenderGpuSettings"/>）：四个选项、Id 唯一、Id ↔ 枚举往返、
///         未知 Id / 空值不会把配置改乱；</item>
///   <item><b>Windows 图形首选项字符串</b>：<c>GpuPreference=0/1/2;</c> 的生成 / 解析 / 与系统其它段合并
///         （例如 <c>SwapChainBufferCount</c> 原样保留）、软件渲染不写注册表；</item>
///   <item><b>配置兼容</b>（<see cref="ShellConfig.RenderGpu"/>）：老配置没有这个字段时读出来是 <c>null</c>
///         （启动时继承 Windows 里已有的设置），新值按枚举名字符串往返；</item>
///   <item><b>显卡信息文案 / 重启参数</b>：显存格式化、空列表不抛异常、DXGI 枚举只读不崩、
///         「重启本程序」的隐藏参数能被摘干净且不污染文件参数。</item>
/// </list>
/// </summary>
internal static class GpuRenderChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;   // 本组断言都带明细

        void Check(string name, bool ok, string detail = "") => check("[GPU] " + name, ok, detail);

        CheckOptions(Check);
        CheckPreferenceString(Check);
        CheckConfig(Check);
        CheckAdapterText(Check);
        CheckRestartArguments(Check);
    }

    // ---------------------------------------------------------------- 选项清单

    private static void CheckOptions(CheckFn check)
    {
        var options = ShellRenderGpuSettings.Options;
        check("四个渲染模式（自动 / 高性能 / 节能 / 软件渲染）",
            options.Count == 4,
            string.Join(",", options.Select(option => option.Id)));
        check("默认第一项是「自动」",
            options[0].Id == ShellRenderGpuSettings.AutoId,
            options[0].Id);
        check("选项 Id 唯一、名称 / 说明都不为空",
            options.Select(option => option.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == options.Count
            && options.All(option => !string.IsNullOrWhiteSpace(option.Name)
                                     && !string.IsNullOrWhiteSpace(option.Description)));

        var allModes = Enum.GetValues<ShellRenderGpu>();
        check("每个枚举值都能映射到唯一 Id 并解析回来",
            allModes.Length == 4
            && allModes.Select(ShellRenderGpuSettings.IdOf)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == allModes.Length
            && allModes.All(mode =>
            {
                var id = ShellRenderGpuSettings.IdOf(mode);
                return ShellRenderGpuSettings.TryParseId(id, out var parsed) && parsed == mode;
            }));

        check("未知 / 空 Id 解析失败（绑定层脏值不会改配置），ParseId 退回自动",
            !ShellRenderGpuSettings.TryParseId(null, out _)
            && !ShellRenderGpuSettings.TryParseId("bogus", out _)
            && ShellRenderGpuSettings.ParseId("bogus") == ShellRenderGpu.Auto
            && ShellRenderGpuSettings.ParseId("  ") == ShellRenderGpu.Auto);
        check("选择说明随模式变化（高性能 / 软件渲染各有一句）",
            ShellRenderGpuSettings.Describe(ShellRenderGpu.HighPerformance).Contains("独显")
            && ShellRenderGpuSettings.Describe(ShellRenderGpu.Software).Contains("CPU"));
    }

    // ---------------------------------------------------------------- Windows 图形首选项字符串

    private static void CheckPreferenceString(CheckFn check)
    {
        check("GpuPreference 数值映射：自动 0 / 节能 1 / 高性能 2 / 软件渲染 -1",
            ShellRenderGpuSettings.PreferenceValue(ShellRenderGpu.Auto) == 0
            && ShellRenderGpuSettings.PreferenceValue(ShellRenderGpu.PowerSaving) == 1
            && ShellRenderGpuSettings.PreferenceValue(ShellRenderGpu.HighPerformance) == 2
            && ShellRenderGpuSettings.PreferenceValue(ShellRenderGpu.Software) == -1);

        check("新建值：高性能 → GpuPreference=2; / 节能 → 1 / 自动 → 0",
            ShellRenderGpuSettings.BuildPreferenceValue(ShellRenderGpu.HighPerformance) == "GpuPreference=2;"
            && ShellRenderGpuSettings.BuildPreferenceValue(ShellRenderGpu.PowerSaving) == "GpuPreference=1;"
            && ShellRenderGpuSettings.BuildPreferenceValue(ShellRenderGpu.Auto) == "GpuPreference=0;");

        check("改选时替换旧的 GpuPreference 段",
            ShellRenderGpuSettings.BuildPreferenceValue(
                ShellRenderGpu.HighPerformance, "GpuPreference=1;") == "GpuPreference=2;");

        check("系统 / 驱动写的其它段原样保留（例如 SwapChainBufferCount）",
            ShellRenderGpuSettings.BuildPreferenceValue(
                ShellRenderGpu.HighPerformance,
                "SwapChainBufferCount=3;GpuPreference=1;") == "GpuPreference=2;SwapChainBufferCount=3;",
            ShellRenderGpuSettings.BuildPreferenceValue(
                ShellRenderGpu.HighPerformance,
                "SwapChainBufferCount=3;GpuPreference=1;"));

        check("软件渲染不写注册表（UsesWindowsPreference = false，Build 原样返回旧值）",
            !ShellRenderGpuSettings.UsesWindowsPreference(ShellRenderGpu.Software)
            && ShellRenderGpuSettings.UsesWindowsPreference(ShellRenderGpu.Auto)
            && ShellRenderGpuSettings.BuildPreferenceValue(
                ShellRenderGpu.Software, "GpuPreference=1;") == "GpuPreference=1;");

        check("解析：0 / 1 / 2 + 混合段 + 大小写不敏感",
            ShellRenderGpuSettings.TryParsePreferenceValue("GpuPreference=0;", out var auto)
            && auto == ShellRenderGpu.Auto
            && ShellRenderGpuSettings.TryParsePreferenceValue("GpuPreference=1;", out var power)
            && power == ShellRenderGpu.PowerSaving
            && ShellRenderGpuSettings.TryParsePreferenceValue(
                "GpuPreference=2;SwapChainBufferCount=3;", out var high)
            && high == ShellRenderGpu.HighPerformance
            && ShellRenderGpuSettings.TryParsePreferenceValue("gpupreference=2;", out var lower)
            && lower == ShellRenderGpu.HighPerformance);

        check("解析不认识的格式返回失败（不瞎猜成某个模式）",
            !ShellRenderGpuSettings.TryParsePreferenceValue(null, out _)
            && !ShellRenderGpuSettings.TryParsePreferenceValue("", out _)
            && !ShellRenderGpuSettings.TryParsePreferenceValue("SwapChainBufferCount=3;", out _)
            && !ShellRenderGpuSettings.TryParsePreferenceValue("GpuPreference=9;", out _)
            && !ShellRenderGpuSettings.TryParsePreferenceValue("GpuPreference=abc;", out _));

        check("注册表值 → 人话：没设置说「未单独指定」，高性能带名字和数值",
            ShellRenderGpuSettings.DescribePreferenceValue(null).Contains("未单独指定")
            && ShellRenderGpuSettings.DescribePreferenceValue("GpuPreference=2;").Contains("高性能")
            && ShellRenderGpuSettings.DescribePreferenceValue("GpuPreference=2;").Contains("2"));

        check("改了设置才需要重启（同模式不用重启）",
            !ShellRenderGpuSettings.RequiresRestart(ShellRenderGpu.Auto, ShellRenderGpu.Auto)
            && ShellRenderGpuSettings.RequiresRestart(ShellRenderGpu.HighPerformance, ShellRenderGpu.Auto)
            && ShellRenderGpuSettings.RequiresRestart(ShellRenderGpu.Software, ShellRenderGpu.HighPerformance));
    }

    // ---------------------------------------------------------------- 配置兼容

    private static void CheckConfig(CheckFn check)
    {
        var defaults = new ShellConfig();
        check("新配置默认没有 renderGpu（null = 老配置 / 首次启动时继承 Windows 已有设置）",
            defaults.RenderGpu is null);

        var path = SelfCheckScratch.MakeFile("shell-render-gpu.json");
        var written = new ShellConfig { RenderGpu = ShellRenderGpu.HighPerformance };
        JsonFileStore.Write(path, written);
        var roundTrip = JsonFileStore.Read<ShellConfig>(path);
        check("renderGpu 按枚举名字符串存盘并读回（\"HighPerformance\"）",
            roundTrip?.RenderGpu == ShellRenderGpu.HighPerformance
            && File.ReadAllText(path).Contains("\"HighPerformance\""),
            File.ReadAllText(path).Replace(Environment.NewLine, " "));

        var oldPath = SelfCheckScratch.MakeFile("shell-render-gpu-old.json");
        File.WriteAllText(oldPath, """{ "uiTheme": "CloudSky", "cloudDensity": 12 }""");
        var oldConfig = JsonFileStore.Read<ShellConfig>(oldPath);
        check("老配置没有 renderGpu 时读成 null，其它字段照常",
            oldConfig is not null
            && oldConfig.RenderGpu is null
            && oldConfig.UiTheme == YuLiangToolkit.App.Themes.UiTheme.CloudSky
            && oldConfig.CloudDensity == 12);
    }

    // ---------------------------------------------------------------- 显卡信息文案

    private static void CheckAdapterText(CheckFn check)
    {
        check("显存格式化：0 = 共享内存 / 8 GB = 8.0 GB 显存",
            GpuAdapterProbe.FormatMemory(0) == "共享内存"
            && GpuAdapterProbe.FormatMemory(8UL * 1024 * 1024 * 1024) == "8.0 GB 显存",
            GpuAdapterProbe.FormatMemory(8UL * 1024 * 1024 * 1024));

        var discrete = new GpuAdapterInfo(
            "NVIDIA GeForce RTX 4060", 8UL * 1024 * 1024 * 1024, 16UL * 1024 * 1024 * 1024, 0x10DE, false, false);
        check("显卡描述带名称和显存（独显不额外标注）",
            GpuAdapterProbe.DescribeAdapter(discrete)
                == "NVIDIA GeForce RTX 4060（8.0 GB 显存）",
            GpuAdapterProbe.DescribeAdapter(discrete));

        var software = new GpuAdapterInfo("Microsoft Basic Render Driver", 0, 0, 0x1414, true, false);
        check("软件适配器会标出来（免得用户把它当成独显）",
            GpuAdapterProbe.DescribeAdapter(software).Contains("软件适配器"),
            GpuAdapterProbe.DescribeAdapter(software));

        check("没有显卡信息时也有一句可读的提示（远程桌面 / 无驱动不会崩）",
            GpuAdapterProbe.DescribeAdapters(Array.Empty<GpuAdapterInfo>()).Contains("没检测到显卡信息")
            && GpuAdapterProbe.DescribeAdapters(null).Length > 0);

        var two = new[] { discrete, new GpuAdapterInfo("Intel UHD", 128UL * 1024 * 1024, 8UL * 1024 * 1024 * 1024, 0x8086, false, false) };
        check("多块显卡时显示数量并列出每一块",
            GpuAdapterProbe.DescribeAdapters(two).Contains("2 块显卡")
            && GpuAdapterProbe.DescribeAdapters(two).Contains("Intel UHD")
            && GpuAdapterProbe.DescribeAdapters(two).Contains("NVIDIA"),
            GpuAdapterProbe.DescribeAdapters(two));

        check("同一块显卡被枚举多遍时按名字去重（不会显示成好几块）",
            GpuAdapterProbe.DescribeAdapters(new[] { discrete, discrete, discrete })
                == "检测到 1 块显卡：NVIDIA GeForce RTX 4060（8.0 GB 显存）。",
            GpuAdapterProbe.DescribeAdapters(new[] { discrete, discrete, discrete }));

        check("只剩软件适配器时明确说「没检测到硬件显卡」",
            GpuAdapterProbe.DescribeAdapters(new[] { software }).Contains("没检测到硬件显卡")
            && GpuAdapterProbe.DescribeAdapters(new[] { software }).Contains("软件适配器"));

        // 真枚举一次：只断言「不抛异常、不上报假信息」，不断言一定要有显卡（CI / 远程桌面可能为空）
        var adapters = GpuAdapterProbe.Enumerate(refresh: true);
        check("真实 DXGI 枚举不抛异常（结果允许为空）",
            adapters is not null
            && GpuAdapterProbe.DescribeAdapters(adapters).Length > 0,
            $"枚举到 {adapters?.Count ?? 0} 块");
    }

    // ---------------------------------------------------------------- 重启参数

    private static void CheckRestartArguments(CheckFn check)
    {
        check("重启参数拼装（--ylt-restart-pid <PID>）",
            RestartLaunchArguments.BuildForProcess(4321) == "--ylt-restart-pid 4321");

        var kept = RestartLaunchArguments.Strip(
            new[] { "--ylt-restart-pid", "4321", @"C:\pics\a.png" }, out var pid);
        check("摘掉重启参数、保留文件参数、拿到旧 PID",
            pid == 4321 && kept.SequenceEqual(new[] { @"C:\pics\a.png" }),
            $"pid={pid} rest={string.Join(",", kept)}");

        var normal = RestartLaunchArguments.Strip(new[] { @"C:\pics\b.png" }, out var normalPid);
        check("普通启动（没有重启参数）原样保留全部参数",
            normalPid == 0 && normal.SequenceEqual(new[] { @"C:\pics\b.png" }));

        var broken = RestartLaunchArguments.Strip(
            new[] { "--ylt-restart-pid", "not-a-pid", "x.png" }, out var brokenPid);
        check("重启参数格式坏了：不继承 PID，隐藏开关被摘掉、后面的文件参数照常保留",
            brokenPid == 0
            && !broken.Contains("--ylt-restart-pid")
            && broken.Contains("x.png"),
            string.Join(",", broken));

        check("空命令行 / null 安全",
            RestartLaunchArguments.Strip(null, out var nullPid).Length == 0
            && nullPid == 0
            && RestartLaunchArguments.Strip(Array.Empty<string>(), out var emptyPid).Length == 0
            && emptyPid == 0);
    }
}
