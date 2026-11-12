using System.IO;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Shell;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「开机自启」的纯逻辑自检（v0.99.0，<c>shell</c> 分组）。
///
/// <para><b>安全边界</b>：这里<b>绝不调用</b> <see cref="StartupRegistration.Register"/> /
/// <see cref="StartupRegistration.Unregister"/> / <see cref="StartupRegistration.EnsureUpToDate"/> ——
/// 它们会往用户真实的 <c>HKCU\…\Run</c> 写启动项，跑一次自检就在用户机器上留下开机自启。
/// 只允许测纯函数 + 只读的 <see cref="StartupRegistration.ReadCommand"/> /
/// <see cref="StartupRegistration.IsRegistered"/> / <see cref="StartupRegistration.Describe"/>。</para>
///
/// <para>配置文件只在 <see cref="SelfCheckScratch"/> 临时目录里读写，不碰仓库 Data/Config。</para>
/// </summary>
internal static class StartupChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;   // 本组断言都带明细

        void Check(string name, bool ok, string detail = "") => check("[开机自启] " + name, ok, detail);

        CheckCommandString(Check);
        CheckExecutablePathResolution(Check);
        CheckConfig(Check);
        CheckReadOnlyState(Check);
    }

    // ---------------------------------------------------------------- 命令行拼装

    private static void CheckCommandString(CheckFn check)
    {
        check("值名 / 显示名固定在当前用户的 Run 键",
            StartupRegistration.ValueName == "YuLiangToolkit"
            && StartupRegistration.DisplayName == "凉雨工具集"
            && StartupRegistration.RunKeyPath == @"Software\Microsoft\Windows\CurrentVersion\Run",
            StartupRegistration.RunKeyPath);

        const string main = @"C:\Apps\凉雨 工具集\YuLiangTookit.exe";
        check("命令行就是带引号的 exe 路径（路径带空格也不会被拆开）",
            StartupRegistration.BuildCommand(main) == "\"" + main + "\""
            && StartupRegistration.BuildCommand("  \"" + main + "\"  ") == "\"" + main + "\"",
            StartupRegistration.BuildCommand(main));

        check("解析命令行：带引号（含参数 / 含空格）都能准确取出 exe 路径",
            StartupRegistration.ExtractExecutablePath("\"" + main + "\"") == main
            && StartupRegistration.ExtractExecutablePath("\"C:\\A B\\x.exe\" --open-as=text \"%1\"") == @"C:\A B\x.exe"
            && StartupRegistration.ExtractExecutablePath("  \"" + main + "\"  ") == main
            && StartupRegistration.ExtractExecutablePath("") == ""
            && StartupRegistration.ExtractExecutablePath(null) == "");

        check("解析命令行：不带引号时按空格切（本程序写入时一律带引号，所以不会踩到这个歧义）",
            StartupRegistration.ExtractExecutablePath(@"C:\Apps\x.exe") == @"C:\Apps\x.exe"
            && StartupRegistration.ExtractExecutablePath(@"C:\Apps\x.exe --mini") == @"C:\Apps\x.exe"
            && StartupRegistration.BuildCommand(@"C:\A B\x.exe").StartsWith('"'));

        check("比对登记路径：大小写不敏感、忽略参数、右侧带空格也不被截断、空值一律不算匹配",
            StartupRegistration.IsSameExecutable("\"" + main.ToUpperInvariant() + "\"", main)
            && StartupRegistration.IsSameExecutable("\"" + main + "\" --mini", main)
            && !StartupRegistration.IsSameExecutable(@"C:\Other\x.exe", main)
            && !StartupRegistration.IsSameExecutable(null, main)
            && !StartupRegistration.IsSameExecutable("", main));
    }

    // ---------------------------------------------------------------- 主程序路径

    private static void CheckExecutablePathResolution(CheckFn check)
    {
        // 「打开方式」的入口副本启动时也要还原成主 exe，否则开机自启会指向副本
        var directory = @"C:\Apps\凉雨 工具集";
        var main = Path.Combine(directory, "YuLiangTookit.exe");
        var textOpener = Path.Combine(directory, "YuLiangTookit.open-text.exe");

        check("登记路径：入口副本（.open-text.exe）还原成主程序 exe",
            StartupRegistration.ResolveExecutablePath(textOpener) == main
            && StartupRegistration.ResolveExecutablePath(main) == main,
            StartupRegistration.ResolveExecutablePath(textOpener));

        check("登记路径：空值退回当前进程的 exe（不会拼出空路径）",
            StartupRegistration.ResolveExecutablePath(null).Length > 0
            && StartupRegistration.ResolveExecutablePath("   ").Length > 0,
            StartupRegistration.ResolveExecutablePath(null));
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var defaults = new ShellConfig();
        check("默认关闭（老用户升级后不会凭空开机自启）",
            !defaults.StartWithWindows
            && JsonFileStore.Serialize(defaults).Contains("\"startWithWindows\": false"),
            JsonFileStore.Serialize(defaults).Replace(Environment.NewLine, " "));

        var directory = SelfCheckScratch.MakeDirectory("startup-config");
        var path = Path.Combine(directory, "config.json");

        // 旧配置没有这个字段：读出来必须是 false（不能靠“属性初始值”碰运气）
        File.WriteAllText(path, "{ \"closeAction\": \"MinimizeToTray\" }");
        var legacy = JsonFileStore.Read<ShellConfig>(path);
        check("旧配置没有 startWithWindows 键 → 读回 false（升级后行为不变）",
            legacy is { StartWithWindows: false, CloseAction: ShellCloseAction.MinimizeToTray },
            JsonFileStore.Serialize(legacy).Replace(Environment.NewLine, " "));

        JsonFileStore.Write(path, new ShellConfig { StartWithWindows = true });
        check("打开后落盘并读回（配置键 startWithWindows = true）",
            JsonFileStore.Read<ShellConfig>(path)?.StartWithWindows == true
            && File.ReadAllText(path).Contains("\"startWithWindows\": true"),
            File.ReadAllText(path).Replace(Environment.NewLine, " "));
    }

    // ---------------------------------------------------------------- 只读状态

    private static void CheckReadOnlyState(CheckFn check)
    {
        // 只读断言：本机可能有 / 没有这一条登记，两种都要能描述清楚，且不改注册表
        string? command = null;
        bool registered = false;
        try
        {
            command = StartupRegistration.ReadCommand();
            registered = StartupRegistration.IsRegistered();
        }
        catch (Exception ex)
        {
            check("读取登记状态不抛异常", false, ex.Message);
            return;
        }

        check("读取登记状态：不抛异常，且「有命令行」与「已登记」结论一致",
            registered == !string.IsNullOrWhiteSpace(command),
            command ?? "（本机没有开机自启登记）");

        var state = StartupRegistration.Describe(command is null ? null : Environment.ProcessPath);
        check("Describe：状态字段与只读结论一致",
            state.Registered == registered
            && (!state.Registered || state.Command.Length > 0)
            && state.Message.Length > 0,
            state.Message);

        check("Describe：没登记时说明里写「未开启」，登记了写「已开启」",
            registered ? state.Message.Contains("已开启") : state.Message.Contains("未开启"),
            state.Message);
    }
}
