namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// 从开机启动命令里解析「可执行文件路径」（纯函数，自检可直接断言）。
///
/// <para>注册表 Run 的值形如：</para>
/// <code>
/// "C:\Program Files\App\app.exe" --autostart
/// C:\Apps\Tool.exe /silent
/// %ProgramFiles%\App\app.exe
/// rundll32.exe C:\Apps\hook.dll,Entry
/// </code>
/// <para>这里不追求 100% 还原命令行，只要能把「程序在哪」尽量猜对；解析不出就返回空串，
/// 界面会退回显示原始命令。</para>
/// </summary>
public static class StartupCommandParser
{
    private const string ExeSuffix = ".exe";

    /// <summary>从启动命令里解析可执行文件路径；解析不出返回空串。</summary>
    public static string ExtractExecutable(string? command)
    {
        var text = (command ?? "").Trim();
        if (text.Length == 0) return "";

        var token = text[0] == '"' ? ReadQuoted(text) : ReadFirstToken(text);
        if (token.Length == 0) return "";

        token = ExpandEnvironment(token);
        token = token.Trim().Trim('"');
        return token;
    }

    /// <summary>把 <c>%SystemRoot%</c> 这类环境变量展开（失败时原样返回）。</summary>
    public static string ExpandEnvironment(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>命令太长时压成一行摘要（注册表里偶尔会有带换行的畸形值）。</summary>
    public static string CleanDisplay(string? command, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var text = string.Join(' ', command.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        text = text.Trim();
        if (max < 8) max = 8;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private static string ReadQuoted(string text)
    {
        var end = text.IndexOf('"', 1);
        return end > 1 ? text[1..end] : text.Trim('"');
    }

    private static string ReadFirstToken(string text)
    {
        var space = text.IndexOfAny(new[] { ' ', '\t' });
        var firstToken = space < 0 ? text : text[..space];

        // ① 第一个词本身就是 exe：rundll32.exe / Tool.exe / notepad.exe --x
        if (firstToken.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase)) return firstToken;

        // ② 没加引号但带目录的路径：一直吃到 ".exe" 为止
        //    （"C:\Program Files\App\app.exe" --x 这种中间有空格的路径）
        //    ⚠ 不能只看 '/'：「cmd /c start app.exe」里的 /c 是开关，不是目录分隔符。
        //    所以反斜杠直接算路径，正斜杠要配合盘符（C:/Apps/Tool.exe）才算。
        var index = text.IndexOf(ExeSuffix, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var end = index + ExeSuffix.Length;
            if (end >= text.Length || char.IsWhiteSpace(text[end]) || text[end] is ',' or '"')
            {
                var candidate = text[..end];
                var looksLikePath = candidate.Contains('\\')
                                    || (candidate.Contains('/') && candidate.Contains(':'));
                if (looksLikePath) return candidate;
            }
        }

        // ③ 普通命令（cmd /c start app.exe）：只取第一个词
        return firstToken;
    }
}
