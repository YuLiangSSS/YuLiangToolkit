using System.Diagnostics;
using System.Text.Json;
using YuLiangToolkit.Tools.TaskManager;
using YuLiangToolkit.Tools.TaskManager.Models;
using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「任务管理器」自检：数字格式化轮子 / 结束任务三级方案 / 开机启动命令解析 /
/// StartupApproved 启停标记 / 系统关键进程名单 / 进程行原地合并与筛选 / 配置默认值与 JSON 往返，
/// 以及**只读**的本机真实采样（自己的进程 / 端口表 / 整机内存）。
///
/// <para><b>安全边界</b>：本模块<b>绝不</b>结束任何进程、不写注册表、不修改开机启动项，
/// 也不创建 WPF 窗口 / ViewModel（避免启动后台监测循环）。</para>
/// </summary>
internal static class TaskManagerChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckDisplayFormat(assert);
        CheckDiskActivity(assert);
        CheckKillPlan(assert);
        CheckStartupApproved(assert);
        CheckStartupCommandParser(assert);
        CheckProcessProtection(assert);
        CheckPortBinding(assert);
        CheckProcessInfo(assert);
        CheckStartupEntry(assert);
        CheckConfig(assert);
        CheckElevationHelpers(assert);
        CheckReadOnlySampling(assert);
        CheckTerminateOwnChild(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new TaskManagerTool();
        check("[任务管理器] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "task-manager" && tool.Name == "任务管理器"
            && tool.Category == "系统" && tool.Order == 46,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[任务管理器] 标签含「任务管理器」「进程」「端口」「开机启动」等主页筛选用词",
            tool.Tags.Contains("任务管理器") && tool.Tags.Contains("进程")
            && tool.Tags.Contains("端口") && tool.Tags.Contains("开机启动")
            && tool.Tags.Contains("GPU") && tool.Tags.Contains("PID"),
            string.Join(" / ", tool.Tags));

        check("[任务管理器] 描述写明 CPU / 端口 / 结束任务 / 开机启动，且实现了 ITool",
            tool.Description.Contains("CPU") && tool.Description.Contains("端口")
            && tool.Description.Contains("结束任务") && tool.Description.Contains("开机启动项")
            && tool is YuLiangToolkit.Core.Tools.ITool && tool.EmojiIcon.Length > 0,
            tool.Description);
    }

    // ---------------------------------------------------------------- 数字格式化

    private static void CheckDisplayFormat(CheckFn check)
    {
        check("[任务管理器] Bytes：0 / 512 / 1024 / 1536 / 1 MB / 1 GB 的文本",
            DisplayFormat.Bytes(0) == "0 B"
            && DisplayFormat.Bytes(512) == "512 B"
            && DisplayFormat.Bytes(1024) == "1 KB"
            && DisplayFormat.Bytes(1536) == "1.5 KB"
            && DisplayFormat.Bytes(1024L * 1024) == "1 MB"
            && DisplayFormat.Bytes(1024L * 1024 * 1024) == "1 GB",
            $"{DisplayFormat.Bytes(1536)} / {DisplayFormat.Bytes(1024L * 1024 * 1024)}");

        check("[任务管理器] Bytes：负数显示「—」（不让界面出现负容量）",
            DisplayFormat.Bytes(-1) == "—");

        check("[任务管理器] Rate：0 / 512 B/s / 1.5 KB/s / 2.5 MB/s",
            DisplayFormat.Rate(0) == "0 B/s"
            && DisplayFormat.Rate(0.4) == "0 B/s"
            && DisplayFormat.Rate(512) == "512 B/s"
            && DisplayFormat.Rate(1536) == "1.5 KB/s"
            && DisplayFormat.Rate(2.5 * 1024 * 1024) == "2.5 MB/s",
            DisplayFormat.Rate(2.5 * 1024 * 1024));

        check("[任务管理器] Percent：0 / 12.34 / 100 / 超界夹到 100 / NaN 显示「—」",
            DisplayFormat.Percent(0) == "0.0%"
            && DisplayFormat.Percent(12.34) == "12.3%"
            && DisplayFormat.Percent(100) == "100.0%"
            && DisplayFormat.Percent(150) == "100.0%"
            && DisplayFormat.Percent(double.NaN) == "—",
            $"{DisplayFormat.Percent(12.34)} / {DisplayFormat.Percent(150)}");

        check("[任务管理器] PercentValue：不带 % 且夹取",
            DisplayFormat.PercentValue(12.34).StartsWith("12.3")
            && DisplayFormat.PercentValue(-5) == "0.0");

        check("[任务管理器] Uptime：不足一天 / 超过一天两种文本",
            DisplayFormat.Uptime(TimeSpan.FromSeconds(3723)) == "01:02:03"
            && DisplayFormat.Uptime(new TimeSpan(3, 4, 12, 33)) == "3 天 04:12:33",
            DisplayFormat.Uptime(new TimeSpan(3, 4, 12, 33)));

        check("[任务管理器] TcpState：LISTENING / ESTABLISHED / TIME_WAIT / 未知码原样",
            DisplayFormat.TcpState(2) == "LISTENING"
            && DisplayFormat.TcpState(5) == "ESTABLISHED"
            && DisplayFormat.TcpState(11) == "TIME_WAIT"
            && DisplayFormat.TcpState(99) == "99");

        check("[任务管理器] Ports 摘要：去重 / 排序 / 超上限显示 +N / 空集合空串",
            DisplayFormat.Ports(Array.Empty<PortBinding>()) == ""
            && DisplayFormat.Ports(new[] { Port(8080, 1), Port(80, 2), Port(8080, 3), Port(443, 4) }) == "80, 443, 8080"
            && DisplayFormat.Ports(new[] { Port(1, 1), Port(2, 2), Port(3, 3), Port(4, 4), Port(5, 5) }, 3) == "1, 2, 3 +2",
            DisplayFormat.Ports(new[] { Port(1, 1), Port(2, 2), Port(3, 3), Port(4, 4), Port(5, 5) }, 3));

        check("[任务管理器] ShortPath：短路径原样、长路径中间省略且保留末尾文件名",
            DisplayFormat.ShortPath(@"C:\a\b.exe") == @"C:\a\b.exe"
            && DisplayFormat.ShortPath(@"C:\very\long\folder\name\program-1.0.exe", 24).Contains('…')
            && DisplayFormat.ShortPath(@"C:\very\long\folder\name\program-1.0.exe", 24).EndsWith(".exe"),
            DisplayFormat.ShortPath(@"C:\very\long\folder\name\program-1.0.exe", 24));
    }

    // ---------------------------------------------------------------- 物理磁盘逐盘读写

    private static void CheckDiskActivity(CheckFn check)
    {
        check("[任务管理器] 物理磁盘读写：PDH 实例名转显示名（0 c: → C: / 2 → 磁盘 2 / _Total 不显示）",
            DiskPerformanceInfo.FormatName("0 c:") == "C:"
            && DiskPerformanceInfo.FormatName("0 c: d:") == "C: D:"
            && DiskPerformanceInfo.FormatName("2") == "磁盘 2"
            && DiskPerformanceInfo.FormatName("_Total") == ""
            && DiskPerformanceInfo.FormatName(null) == ""
            && DiskPerformanceInfo.FormatName("  ") == "",
            $"0 c: → {DiskPerformanceInfo.FormatName("0 c:")} / 2 → {DiskPerformanceInfo.FormatName("2")}");

        check("[任务管理器] 物理磁盘读写：实例里的磁盘序号可解析（排序用）",
            DiskPerformanceInfo.ParseDiskNumber("0 C:") == 0
            && DiskPerformanceInfo.ParseDiskNumber("12 D:") == 12
            && DiskPerformanceInfo.ParseDiskNumber("7") == 7
            && DiskPerformanceInfo.ParseDiskNumber("_Total") == int.MaxValue
            && DiskPerformanceInfo.ParseDiskNumber(null) == int.MaxValue);

        var disk = DiskPerformanceInfo.Create("0 C:", 5.5 * 1024 * 1024, 232 * 1024);
        check("[任务管理器] 物理磁盘读写：读 / 写各自格式化，行文案同时包含两者",
            disk.Name == "C:" && disk.ReadText == "5.5 MB/s" && disk.WriteText == "232 KB/s"
            && disk.DisplayText.Contains("C:") && disk.DisplayText.Contains("读")
            && disk.DisplayText.Contains("写"),
            disk.DisplayText);

        var clamped = DiskPerformanceInfo.Create("1 D:", -1, double.NaN);
        check("[任务管理器] 物理磁盘读写：负数 / NaN 按 0 处理（界面不会出现负速度）",
            clamped.ReadBytesPerSecond == 0 && clamped.WriteBytesPerSecond == 0
            && clamped.ReadText == "0 B/s" && clamped.WriteText == "0 B/s");

        var withDisks = new SystemSnapshot
        {
            Disks = new[] { DiskPerformanceInfo.Create("0 C:", 1024, 2048) },
        };
        check("[任务管理器] 逐盘数据：有磁盘列表时 HasPerDiskActivity = true，空列表 = false",
            withDisks.HasPerDiskActivity && !new SystemSnapshot().HasPerDiskActivity);
    }

    private static PortBinding Port(int localPort, int pid) => new() { LocalPort = localPort, Pid = pid };

    // ---------------------------------------------------------------- 结束任务三级方案

    private static void CheckKillPlan(CheckFn check)
    {
        check("[任务管理器] 三级顺序：Process.Kill → taskkill → wmic（固定不变）",
            KillPlan.Stages.Count == 3
            && KillPlan.Stages[0] == KillStage.ProcessKill
            && KillPlan.Stages[1] == KillStage.Taskkill
            && KillPlan.Stages[2] == KillStage.Wmic);

        check("[任务管理器] taskkill 参数：/PID {pid} /T /F",
            KillPlan.BuildTaskkillArguments(4321) == "/PID 4321 /T /F"
            && KillPlan.BuildTaskkillArgumentList(4321).SequenceEqual(new[] { "/PID", "4321", "/T", "/F" }),
            KillPlan.BuildTaskkillArguments(4321));

        check("[任务管理器] wmic 参数：process where name='chrome.exe' delete",
            KillPlan.BuildWmicArguments("chrome") == "process where name='chrome.exe' delete"
            && KillPlan.BuildWmicArguments(@"C:\Apps\Tool.EXE") == "process where name='Tool.EXE' delete",
            KillPlan.BuildWmicArguments("chrome"));

        check("[任务管理器] 进程名规范化：补 .exe / 去路径 / 转义单引号 / 空值",
            KillPlan.NormalizeProcessName("chrome") == "chrome.exe"
            && KillPlan.NormalizeProcessName(" chrome.exe ") == "chrome.exe"
            && KillPlan.NormalizeProcessName(@"C:\Apps\Tool.EXE") == "Tool.EXE"
            && KillPlan.NormalizeProcessName("weird'name") == "weird''name.exe"
            && KillPlan.NormalizeProcessName(null) == ""
            && KillPlan.NormalizeProcessName("   ") == "");

        check("[任务管理器] 上一级失败才继续下一级（成功立即停止）",
            KillPlan.ShouldContinue(false) && !KillPlan.ShouldContinue(true));

        var failed = new KillAttempt(KillStage.ProcessKill, false, "拒绝访问");
        var ok = new KillAttempt(KillStage.Taskkill, true, "退出码 0");
        var success = KillOutcome.FromAttempts(1234, new[] { failed, ok });
        check("[任务管理器] 第 2 级成功：Success = true、摘要写明已结束、保留全部尝试记录",
            success.Success && success.Summary.Contains("已结束") && success.Attempts.Count == 2
            && success.Attempts[0].Stage == KillStage.ProcessKill,
            success.Summary);

        var allFailed = KillOutcome.FromAttempts(1234, new[]
        {
            new KillAttempt(KillStage.ProcessKill, false, "拒绝访问"),
            new KillAttempt(KillStage.Taskkill, false, "退出码 1"),
            new KillAttempt(KillStage.Wmic, false, "此 Windows 已移除 wmic"),
        });
        check("[任务管理器] 三级全失败：Success = false、摘要提示「失败」并建议管理员身份",
            !allFailed.Success && allFailed.Summary.Contains("失败")
            && allFailed.Summary.Contains("管理员") && allFailed.Attempts.Count == 3,
            allFailed.Summary);

        check("[任务管理器] 进程本来就不存在：按成功处理（AlreadyExited）",
            KillOutcome.AlreadyExited(999).Success && KillOutcome.AlreadyExited(999).Attempts.Count == 0);
    }

    // ---------------------------------------------------------------- StartupApproved

    private static void CheckStartupApproved(CheckFn check)
    {
        check("[任务管理器] StartupApproved：没有记录 / 长度不足按「启用」处理",
            StartupApprovedState.ParseEnabled(null) is null
            && StartupApprovedState.IsEnabled(null)
            && StartupApprovedState.ParseEnabled(Array.Empty<byte>()) is null
            && StartupApprovedState.ParseEnabled(new byte[] { 2, 0 }) is null);

        var enabled = StartupApprovedState.Create(true, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var disabled = StartupApprovedState.Create(false, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        check("[任务管理器] StartupApproved：Create 生成 12 字节，往返解析一致（启用 / 禁用）",
            enabled.Length == 12 && StartupApprovedState.ParseEnabled(enabled) == true
            && disabled.Length == 12 && StartupApprovedState.ParseEnabled(disabled) == false,
            $"enabled={BitConverter.ToString(enabled)} disabled={BitConverter.ToString(disabled)}");

        check("[任务管理器] StartupApproved：状态 6 也算启用、未知状态按「没有记录」",
            StartupApprovedState.ParseEnabled(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) == true
            && StartupApprovedState.ParseEnabled(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) is null);

        check("[任务管理器] StartupApproved：Describe 文本可读",
            StartupApprovedState.Describe(null).Contains("已启用")
            && StartupApprovedState.Describe(disabled).Contains("已禁用"));
    }

    // ---------------------------------------------------------------- 开机启动命令解析

    private static void CheckStartupCommandParser(CheckFn check)
    {
        check("[任务管理器] 启动命令：带引号路径 + 参数只取程序",
            StartupCommandParser.ExtractExecutable("\"C:\\Program Files\\App\\app.exe\" --autostart")
                == @"C:\Program Files\App\app.exe");

        check("[任务管理器] 启动命令：无引号带空格路径 + 参数（取到 .exe 为止）",
            StartupCommandParser.ExtractExecutable("C:\\Program Files\\App\\app.exe /silent")
                == @"C:\Program Files\App\app.exe");

        check("[任务管理器] 启动命令：普通 exe / rundll32 / cmd 前缀",
            StartupCommandParser.ExtractExecutable("C:\\Apps\\Tool.exe") == @"C:\Apps\Tool.exe"
            && StartupCommandParser.ExtractExecutable("rundll32.exe C:\\Apps\\hook.dll,Entry") == "rundll32.exe"
            && StartupCommandParser.ExtractExecutable("cmd /c start app.exe") == "cmd");

        check("[任务管理器] 启动命令：环境变量会展开",
            ExpandEnvAndExtract() == @"C:\YltTm\app.exe");

        check("[任务管理器] 启动命令：空 / 空白 / 只有引号返回空串",
            StartupCommandParser.ExtractExecutable(null) == ""
            && StartupCommandParser.ExtractExecutable("   ") == ""
            && StartupCommandParser.ExtractExecutable("\"\"") == "");

        check("[任务管理器] CleanDisplay：多行压成一行并保留内容",
            StartupCommandParser.CleanDisplay("a\r\nb\tc") == "a b c",
            StartupCommandParser.CleanDisplay("a\r\nb\tc"));
    }

    private static string? ExpandEnvAndExtract()
    {
        var name = "YLT_TM_TEST_DIR";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, @"C:\YltTm");
            return StartupCommandParser.ExtractExecutable("%YLT_TM_TEST_DIR%\\app.exe");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    // ---------------------------------------------------------------- 关键进程名单

    private static void CheckProcessProtection(CheckFn check)
    {
        check("[任务管理器] 关键进程：csrss / winlogon / lsass / services（带不带 .exe 都认）",
            ProcessProtection.IsCritical("csrss") && ProcessProtection.IsCritical("csrss.exe")
            && ProcessProtection.IsCritical("winlogon.exe") && ProcessProtection.IsCritical("lsass.exe")
            && ProcessProtection.IsCritical("services.EXE") && ProcessProtection.IsCritical("System"));

        check("[任务管理器] 关键进程：普通进程（chrome / explorer）不算关键",
            !ProcessProtection.IsCritical("chrome") && !ProcessProtection.IsCritical("explorer.exe")
            && !ProcessProtection.IsCritical(null) && !ProcessProtection.IsCritical(""));

        check("[任务管理器] 桌面外壳：explorer / dwm 会被识别并给额外提醒",
            ProcessProtection.IsShell("explorer.exe") && ProcessProtection.IsShell("dwm")
            && !ProcessProtection.IsShell("chrome")
            && ProcessProtection.DescribeKind("explorer") == "桌面外壳进程"
            && ProcessProtection.DescribeKind("csrss") == "系统关键进程");

        check("[任务管理器] 关键进程提示语写明「拒绝访问」（与任务管理器截图一致）",
            ProcessProtection.CriticalWarning.Contains("拒绝访问")
            && ProcessProtection.ShellWarning.Contains("桌面"));
    }

    // ---------------------------------------------------------------- 端口 / 进程行

    private static void CheckPortBinding(CheckFn check)
    {
        check("[任务管理器] 端点文本：IPv4 / IPv6 加方括号 / 端口 0 / 空地址",
            PortBinding.FormatEndpoint("127.0.0.1", 8080) == "127.0.0.1:8080"
            && PortBinding.FormatEndpoint("::", 443) == "[::]:443"
            && PortBinding.FormatEndpoint("0.0.0.0", 0) == "0.0.0.0"
            && PortBinding.FormatEndpoint("", 0) == "*");

        var binding = new PortBinding
        {
            Protocol = "TCP",
            LocalAddress = "0.0.0.0",
            LocalPort = 135,
            State = "LISTENING",
            Pid = 1234,
        };
        check("[任务管理器] 端口行 DisplayText 含协议 / 本地端点 / 状态",
            binding.DisplayText.Contains("TCP") && binding.DisplayText.Contains("0.0.0.0:135")
            && binding.DisplayText.Contains("LISTENING") && binding.LocalEndpoint == "0.0.0.0:135"
            && !binding.IsIPv6);

        var v6 = new PortBinding { Protocol = "TCPv6", LocalAddress = "::1", LocalPort = 80 };
        check("[任务管理器] IPv6 端口行：IsIPv6 为 true、端点带方括号",
            v6.IsIPv6 && v6.LocalEndpoint == "[::1]:80", v6.LocalEndpoint);
    }

    private static void CheckProcessInfo(CheckFn check)
    {
        var row = new ProcessInfo(1234);
        row.Update(new ProcessSampleData
        {
            Pid = 1234,
            Name = "chrome.exe",
            Path = @"C:\Apps\chrome.exe",
            CpuPercent = 12.5,
            WorkingSetBytes = 256L * 1024 * 1024,
            PrivateBytes = 300L * 1024 * 1024,
            GpuPercent = 3.5,
            GpuMemoryBytes = 128L * 1024 * 1024,
            Ports = new[] { Port(443, 1234), Port(80, 1234) },
        });

        check("[任务管理器] 进程行：PID / 名称 / 路径 / CPU / 内存 / GPU 都合进来",
            row.Pid == 1234 && row.Name == "chrome.exe" && row.Path == @"C:\Apps\chrome.exe"
            && Math.Abs(row.CpuPercent - 12.5) < 0.001 && row.WorkingSetBytes == 256L * 1024 * 1024
            && Math.Abs(row.GpuPercent - 3.5) < 0.001 && row.GpuMemoryBytes == 128L * 1024 * 1024,
            $"{row.Name} {row.CpuText} {row.MemoryText}");

        check("[任务管理器] 进程行显示文本：CPU / 内存 / GPU / 端口摘要",
            row.CpuText == "12.5%" && row.MemoryText == "256 MB" && row.GpuText == "3.5%"
            && row.PortsText == "80, 443" && row.PortCount == 2,
            $"{row.CpuText} / {row.MemoryText} / {row.GpuText} / {row.PortsText}");

        check("[任务管理器] 进程行搜索：进程名（忽略大小写）/ PID / 路径 / 端口都能命中",
            row.Matches("chrome") && row.Matches("CHROME") && row.Matches("1234")
            && row.Matches(@"C:\Apps") && row.Matches("443") && row.Matches("")
            && !row.Matches("notepad"));

        check("[任务管理器] 进程行：端口变化后摘要跟着更新、没有 GPU 数据时显示「—」",
            ChangePorts(row) && row.PortsText == "443" && row.PortCount == 1);

        var noGpu = new ProcessInfo(1);
        noGpu.Update(new ProcessSampleData { Pid = 1, Name = "idle.exe", Path = @"C:\idle.exe" });
        check("[任务管理器] 没有 GPU 数据时 GpuText / GpuMemoryText 显示「—」",
            noGpu.GpuText == "—" && noGpu.GpuMemoryText == "—" && noGpu.PortsText == "");

        check("[任务管理器] IsSystemProcess：读不到路径 / 关键进程算系统进程，普通 exe 不算",
            RowWith("idle.exe", @"C:\idle.exe").IsSystemProcess == false
            && RowWith("svchost.exe", @"C:\Windows\System32\svchost.exe").IsSystemProcess == true
            && RowWith("weird.exe", "").IsSystemProcess == true);

        check("[任务管理器] IsCriticalProcess：csrss 为 true、chrome 为 false",
            RowWith("csrss.exe", "").IsCriticalProcess && !RowWith("chrome.exe", "").IsCriticalProcess);
    }

    private static bool ChangePorts(ProcessInfo row)
    {
        row.Update(new ProcessSampleData
        {
            Pid = row.Pid,
            Name = row.Name,
            Path = row.Path,
            CpuPercent = row.CpuPercent,
            WorkingSetBytes = row.WorkingSetBytes,
            GpuPercent = row.GpuPercent,
            GpuMemoryBytes = row.GpuMemoryBytes,
            Ports = new[] { Port(443, row.Pid) },
        });
        return true;
    }

    private static ProcessInfo RowWith(string name, string path)
    {
        var row = new ProcessInfo(7);
        row.Update(new ProcessSampleData { Pid = 7, Name = name, Path = path });
        return row;
    }

    // ---------------------------------------------------------------- 开机启动项显示

    private static void CheckStartupEntry(CheckFn check)
    {
        var registry = new StartupEntry
        {
            Kind = StartupEntryKind.RegistryRun,
            Name = "OneDrive",
            Command = "\"C:\\Users\\Me\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe\" /background --minimized",
            Scope = "当前用户",
            Location = @"Software\Microsoft\Windows\CurrentVersion\Run",
            ValueName = "OneDrive",
            Enabled = false,
        };
        check("[任务管理器] 启动项：来源 / 状态文本",
            registry.KindText == "注册表" && registry.SourceText == "注册表 · 当前用户"
            && registry.StateText == "已禁用");

        check("[任务管理器] 启动项：命令摘要过长时截断，TargetText 退回命令",
            registry.CommandSummary.Length <= 72 && registry.CommandSummary.EndsWith("…")
            && registry.TargetText == registry.CommandSummary,
            registry.CommandSummary);

        var folder = new StartupEntry
        {
            Kind = StartupEntryKind.StartupFolder,
            Name = "微信.lnk",
            Command = @"C:\Users\Me\Desktop\微信.lnk",
            Scope = "当前用户",
            Enabled = true,
        };
        check("[任务管理器] 启动项：启动文件夹来源 / 启用状态 / 命令摘要保留",
            folder.KindText == "启动文件夹" && folder.SourceText == "启动文件夹 · 当前用户"
            && folder.StateText == "已启用" && folder.CommandSummary == folder.Command);
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var cfg = new TaskManagerConfig();
        check("[任务管理器] 配置默认值：2 秒刷新 / CPU 降序 / 结束前确认 / 采集 GPU 与端口",
            cfg.RefreshIntervalMs == 2000 && cfg.SortMode == "cpu" && cfg.SortDescending
            && cfg.ConfirmBeforeKill && cfg.MonitorGpu && cfg.MonitorPorts
            && !cfg.HideSystemProcesses && cfg.SearchText == "",
            $"{cfg.RefreshIntervalMs} / {cfg.SortMode}");

        check("[任务管理器] 刷新间隔夹到 0.5 ~ 10 秒",
            TaskManagerConfig.ClampRefreshInterval(10) == 500
            && TaskManagerConfig.ClampRefreshInterval(2000) == 2000
            && TaskManagerConfig.ClampRefreshInterval(999999) == 10000);

        check("[任务管理器] 排序方式归一：未知值回退 CPU、大小写不敏感",
            TaskManagerConfig.NormalizeSortMode("MEMORY") == "memory"
            && TaskManagerConfig.NormalizeSortMode("gpu") == "gpu"
            && TaskManagerConfig.NormalizeSortMode("weird") == "cpu"
            && TaskManagerConfig.NormalizeSortMode(null) == "cpu"
            && TaskManagerConfig.NormalizeSortMode(" PID ") == "pid");

        check("[任务管理器] 排序方式中文名",
            TaskManagerConfig.SortModeLabel("cpu") == "CPU"
            && TaskManagerConfig.SortModeLabel("memory") == "内存"
            && TaskManagerConfig.SortModeLabel("gpu") == "GPU"
            && TaskManagerConfig.SortModeLabel("ports") == "端口数");

        cfg.RefreshIntervalMs = 5000;
        cfg.SortMode = "memory";
        cfg.SortDescending = false;
        cfg.SearchText = "chrome";
        cfg.HideSystemProcesses = true;
        cfg.ConfirmBeforeKill = false;
        cfg.MonitorGpu = false;
        cfg.MonitorPorts = false;
        var json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<TaskManagerConfig>(json)!;
        check("[任务管理器] 配置 JSON 往返：所有设置项原样回来",
            back.RefreshIntervalMs == 5000 && back.SortMode == "memory" && !back.SortDescending
            && back.SearchText == "chrome" && back.HideSystemProcesses && !back.ConfirmBeforeKill
            && !back.MonitorGpu && !back.MonitorPorts,
            json);

        check("[任务管理器] 配置 JSON 不写计算属性（SortModeText / FilePath / Store）",
            !json.Contains("SortModeText") && !json.Contains("FilePath") && !json.Contains("\"Store\""));
    }

    // ---------------------------------------------------------------- 按需管理员权限（UAC）

    private static void CheckElevationHelpers(CheckFn check)
    {
        check("[任务管理器] 提权重启参数：--ylt-restart-pid 与 --ylt-tool 拼装",
            AppElevation.BuildRestartArguments(4321, "task-manager") == "--ylt-restart-pid 4321 --ylt-tool task-manager"
            && AppElevation.BuildRestartArguments(4321, null) == "--ylt-restart-pid 4321"
            && AppElevation.BuildRestartArguments(4321, "   ") == "--ylt-restart-pid 4321",
            AppElevation.BuildRestartArguments(4321, "task-manager"));

        var kept = AppElevation.StripToolSwitch(
            new[] { "--ylt-tool", "task-manager", @"C:\pics\a.png" }, out var toolId);
        check("[任务管理器] 提权重启参数：--ylt-tool 被摘掉、文件参数原样保留",
            toolId == "task-manager" && kept.SequenceEqual(new[] { @"C:\pics\a.png" }),
            string.Join(" | ", kept));

        var keptRestart = AppElevation.StripToolSwitch(
            new[] { "--ylt-restart-pid", "4321", "--ylt-tool", "task-manager" }, out var reopenTool);
        check("[任务管理器] 提权重启参数：摘 --ylt-tool 时不动 --ylt-restart-pid（旧进程等待仍生效）",
            reopenTool == "task-manager"
            && keptRestart.SequenceEqual(new[] { "--ylt-restart-pid", "4321" }),
            string.Join(" | ", keptRestart));

        var noValue = AppElevation.StripToolSwitch(new[] { "--ylt-tool" }, out var missing);
        check("[任务管理器] 提权重启参数：--ylt-tool 缺值时忽略（不会当成文件参数）、不抛异常",
            missing is null && noValue.Length == 0);

        check("[任务管理器] 管理员权限判定与底层保持一致（普通 / 管理员环境都不抛异常）",
            AppElevation.IsCurrentProcessElevated == ProcessTerminator.IsCurrentProcessElevated(),
            AppElevation.IsCurrentProcessElevated ? "已提权" : "普通权限");

        // 注入的退出回调：只验证回调被调用，不真的退出自检进程
        var exited = false;
        var previous = AppElevation.ExitApplication;
        try
        {
            AppElevation.ExitApplication = () => exited = true;
            AppElevation.RequestExit();
        }
        finally
        {
            AppElevation.ExitApplication = previous;
        }
        check("[任务管理器] 提权重启：RequestExit 走主程序注入的退出回调（不会误触发「最小化到托盘」）", exited);
    }

    // ---------------------------------------------------------------- 真实只读采样（安全）

    private static void CheckReadOnlySampling(CheckFn check)
    {
        try
        {
            using var system = new SystemSampler();
            var snapshot = system.Sample();
            check("[任务管理器] 整机采样：内存总量 / 已用 / 百分比 / 运行时长 / 磁盘都在合理范围",
                snapshot.TotalMemoryBytes > 0
                && snapshot.AvailableMemoryBytes >= 0
                && snapshot.AvailableMemoryBytes <= snapshot.TotalMemoryBytes
                && snapshot.UsedMemoryBytes > 0
                && snapshot.MemoryPercent is >= 0 and <= 100
                && snapshot.CpuPercent is >= 0 and <= 100
                && snapshot.Uptime > TimeSpan.Zero
                && snapshot.Volumes.Count > 0
                && snapshot.Volumes.All(v => v.TotalBytes > 0 && v.UsedPercent is >= 0 and <= 100)
                && snapshot.Disks.All(d => d.Name.Length > 0
                                           && !string.Equals(d.Instance, "_Total", StringComparison.OrdinalIgnoreCase)
                                           && d.ReadBytesPerSecond >= 0 && d.WriteBytesPerSecond >= 0)
                && snapshot.HasPerDiskActivity == (snapshot.Disks.Count > 0)
                && !string.IsNullOrWhiteSpace(snapshot.HardwareText),
                $"CPU {snapshot.CpuText} · 内存 {snapshot.MemoryText} · 卷 {snapshot.Volumes.Count} · 磁盘 {snapshot.Disks.Count} 块 · {snapshot.HardwareText}");
        }
        catch (Exception ex)
        {
            check("[任务管理器] 整机采样：不应抛异常", false, ex.Message);
        }

        try
        {
            using var sampler = new ProcessSampler();
            var result = sampler.Sample(includePorts: false);
            var self = result.Processes.FirstOrDefault(p => p.Pid == Environment.ProcessId);
            check("[任务管理器] 进程采样：能枚举到自己（PID / 名称 / 内存 / CPU 合理）",
                result.TotalProcessCount >= 1 && self is not null
                && self!.Name.Length > 0 && self.WorkingSetBytes > 0
                && self.CpuPercent is >= 0 and <= 100,
                self is null ? "没找到自己" : $"{self.Name} CPU={self.CpuPercent:0.0} 内存={DisplayFormat.Bytes(self.WorkingSetBytes)}");

            check("[任务管理器] 进程总数 / 线程总数与列表长度一致",
                result.TotalProcessCount == result.Processes.Count
                && result.TotalThreadCount >= 0);

            var details = sampler.ReadDetails(Environment.ProcessId);
            check("[任务管理器] 进程详情：自己的 PID 能读到路径（读不到时给出错误说明而不是抛异常）",
                details.Pid == Environment.ProcessId
                && (!string.IsNullOrWhiteSpace(details.Path) || details.Error.Length > 0),
                details.Path.Length > 0 ? details.Path : details.Error);
        }
        catch (Exception ex)
        {
            check("[任务管理器] 进程采样：不应抛异常", false, ex.Message);
        }

        try
        {
            var ports = PortMapper.Scan();
            check("[任务管理器] 端口表采样：枚举成功、端口号 / PID 合理",
                ports.All(p => p.LocalPort is >= 0 and <= 65535 && p.Pid >= 0 && p.Protocol.Length > 0),
                $"{ports.Count} 条端口记录");
        }
        catch (Exception ex)
        {
            check("[任务管理器] 端口表采样：不应抛异常", false, ex.Message);
        }

        try
        {
            var items = new StartupManager().Scan();
            check("[任务管理器] 开机启动项扫描：只读、每项都有名字（不修改任何东西）",
                items.All(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Location)),
                $"{items.Count} 项");
        }
        catch (Exception ex)
        {
            check("[任务管理器] 开机启动项扫描：不应抛异常", false, ex.Message);
        }

        try
        {
            check("[任务管理器] ProcessTerminator：能判断自己存在、不存在 PID 返回 false",
                ProcessTerminator.ProcessExists(Environment.ProcessId)
                && !ProcessTerminator.ProcessExists(int.MaxValue));
            _ = ProcessTerminator.IsCurrentProcessElevated();
            _ = StartupManager.IsCurrentProcessElevated();
            check("[任务管理器] 权限判定不抛异常（普通 / 管理员环境都适用）", true);
        }
        catch (Exception ex)
        {
            check("[任务管理器] ProcessTerminator 基本判断：不应抛异常", false, ex.Message);
        }
    }

    /// <summary>
    /// 结束任务的真链路验证：自己启动一个长跑子进程（ping 60 次），再用三级方案的第①级结束它。
    /// 只碰自检自己创建的子进程，**不碰系统里任何其它进程**；最坏情况在 finally 里强制清理。
    /// </summary>
    private static void CheckTerminateOwnChild(CheckFn check)
    {
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (child is null)
            {
                check("[任务管理器] 结束任务：能结束自建子进程", false, "无法启动测试子进程");
                return;
            }

            Thread.Sleep(250);
            var pid = child.Id;
            var outcome = new ProcessTerminator().KillAsync(pid, child.ProcessName).GetAwaiter().GetResult();
            check("[任务管理器] 结束任务：第①级 Process.Kill() 结束自建子进程（成功即停）",
                outcome.Success
                && outcome.Attempts.Count == 1
                && outcome.Attempts[0].Stage == KillStage.ProcessKill
                && !ProcessTerminator.ProcessExists(pid),
                outcome.Summary);
        }
        catch (Exception ex)
        {
            check("[任务管理器] 结束任务：能结束自建子进程", false, ex.Message);
        }
        finally
        {
            try
            {
                if (child is { HasExited: false }) child.Kill();
            }
            catch
            {
                // 清理失败不影响自检结果
            }
            child?.Dispose();
        }
    }
}
