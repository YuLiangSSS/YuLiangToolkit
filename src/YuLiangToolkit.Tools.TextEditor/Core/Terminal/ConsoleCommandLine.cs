using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.TextEditor.Core.Terminal;

/// <summary>控制台类型（跟 Zed 一样有「标签页」的概念，这里只有 CMD / PowerShell 两种）。</summary>
public enum ConsoleShellKind
{
    /// <summary>Windows 命令提示符（<c>cmd.exe</c>）。</summary>
    Cmd,

    /// <summary>PowerShell（优先 <c>pwsh.exe</c>，没有就用系统自带的 <c>powershell.exe</c>）。</summary>
    PowerShell,
}

/// <summary>
/// 内置控制台的命令行拼装 / 工作目录换算（纯逻辑，自检直接断言）：
/// 不依赖 UI / 进程，只负责「这条命令该用哪个 exe、带什么参数、输出怎么解码」这类规则。
///
/// <para><b>不是完整终端</b>：我们按「一次执行一条命令、流式回收输出」的方式跑进程（不是 ConPTY 交互终端），
/// 所以 <c>vim</c>、<c>python</c> 交互 REPL 这类需要终端模拟的程序不适用；编译、git、脚本、一次性命令都能用。</para>
/// </summary>
public static class ConsoleCommandLine
{
    /// <summary>PowerShell 输出按 UTF-8 解码（命令前会先设 Console 编码）。</summary>
    public static readonly Encoding OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    static ConsoleCommandLine()
    {
        // CMD 默认输出走系统 OEM 代码页（中文系统是 936），.NET Core 默认不带这些代码页
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 不同 shell 的标准输出编码：<b>CMD 用系统 OEM 代码页</b>（重定向时它仍按控制台输出代码页写字节，
    /// 用 UTF-8 解会乱码）；<b>PowerShell 用 UTF-8</b>（见 <see cref="PrepareCommandText"/> 里先把 Console 编码改掉）。
    /// </summary>
    public static Encoding OutputEncodingFor(ConsoleShellKind kind)
    {
        if (kind != ConsoleShellKind.Cmd) return OutputEncoding;

        try
        {
            var codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    /// <summary>控制台标题（标签页上显示）。</summary>
    public static string KindLabel(ConsoleShellKind kind)
        => kind == ConsoleShellKind.Cmd ? "CMD" : "PowerShell";

    /// <summary>控制台短提示词：CMD 是 <c>C:\path&gt;</c>，PowerShell 是 <c>PS C:\path&gt;</c>。</summary>
    public static string BuildPrompt(ConsoleShellKind kind, string workingDirectory)
        => kind == ConsoleShellKind.Cmd ? $"{workingDirectory}>" : $"PS {workingDirectory}>";

    /// <summary>找到对应的可执行文件：PowerShell 优先 <c>pwsh.exe</c>（PowerShell 7），找不到用系统 <c>powershell.exe</c>。</summary>
    public static string ResolveExecutable(ConsoleShellKind kind)
        => kind == ConsoleShellKind.Cmd ? "cmd.exe" : ResolvePowerShellExecutable();

    /// <summary>进程参数：CMD 用 <c>/d /s /c 命令</c>，PowerShell 用 <c>-NoLogo -NoProfile -NonInteractive -Command 命令</c>。</summary>
    public static IReadOnlyList<string> BuildArguments(ConsoleShellKind kind, string command)
    {
        command ??= "";
        return kind == ConsoleShellKind.Cmd
            ? new[] { "/d", "/s", "/c", command }
            : new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command };
    }

    /// <summary>
    /// 真正交给 shell 的命令文本：<b>CMD 原样</b>（它的输出按 OEM 代码页解码）；
    /// <b>PowerShell 先垫一句「把控制台输出编码改成 UTF-8」</b>，这样中文路径 / 中文输出不乱码。
    /// </summary>
    public static string PrepareCommandText(ConsoleShellKind kind, string command)
    {
        command ??= "";
        return kind == ConsoleShellKind.Cmd
            ? command
            : "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8; " + command;
    }

    /// <summary>把工作目录规整成绝对路径；不存在 / 没权限 / 空串时退回当前用户目录（永远返回一个可用的目录）。</summary>
    public static string NormalizeWorkingDirectory(string? candidate)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var full = Path.GetFullPath(candidate.Trim());
                if (Directory.Exists(full)) return full;
            }
        }
        catch
        {
            // 非法路径 → 走下面的用户目录兜底
        }

        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return Environment.CurrentDirectory;
        }
    }

    /// <summary>
    /// 识别 <c>cd</c> / <c>chdir</c> 命令并在我们的控制台里直接换目录（不启动进程）：
    /// 支持 <c>cd ..</c>、<c>cd 子目录</c>、<c>cd "带 空格"</c>、<c>cd /d X:\path</c>、<c>cd ~</c>。
    /// 返回 <c>false</c> = 不是 cd 命令 / 目标目录不存在（交给 shell 自己报错）。
    /// </summary>
    public static bool TryResolveChangeDirectory(string? command, string currentDirectory, out string newDirectory)
    {
        newDirectory = "";
        if (string.IsNullOrWhiteSpace(command)) return false;

        var match = Regex.Match(command.Trim(), @"^(?:cd|chdir)\b\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        var target = match.Groups[1].Value.Trim();
        target = Regex.Replace(target, @"^/d\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (target.Length >= 2 && target[0] == '"' && target[^1] == '"')
            target = target[1..^1].Trim();

        if (target is "~" or "~/")
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 空参数（单纯 cd）：停留在原地，当作已处理，避免弹一整屏帮助
        if (target.Length == 0)
        {
            newDirectory = NormalizeWorkingDirectory(currentDirectory);
            return true;
        }

        try
        {
            var baseDirectory = NormalizeWorkingDirectory(currentDirectory);
            var full = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(baseDirectory, target));
            if (!Directory.Exists(full)) return false;
            newDirectory = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是不是「清屏」命令（CMD 的 <c>cls</c> / PowerShell 的 <c>clear</c>，顺手认一下中文习惯）。</summary>
    public static bool IsClearCommand(string? command)
    {
        var trimmed = command?.Trim();
        return string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "清屏", StringComparison.Ordinal);
    }

    // ---------------- 内部 ----------------

    private static string ResolvePowerShellExecutable()
    {
        // 优先 PowerShell 7（pwsh.exe），没有就用 Windows 自带的 powershell.exe
        try
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVariable))
            {
                foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch
                    {
                        // 某个 PATH 项非法就跳过
                    }
                }
            }
        }
        catch
        {
            // 环境变量读不到就用系统 PowerShell
        }

        return "powershell.exe";
    }
}
