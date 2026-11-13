using System.IO;
using System.Runtime.InteropServices;

namespace YuLiangToolkit.Core.Media;

/// <summary>
/// 一个可选的提示音来源（存进配置的是 <see cref="Id"/> 字符串）。
/// </summary>
/// <param name="Id">配置里存的值：<c>none</c> / <c>system:xxx</c> / <c>file:C:\a\b.wav</c>。</param>
/// <param name="Name">界面上显示的名字。</param>
public sealed record SoundChoice(string Id, string Name);

/// <summary>
/// 轻量音效播放：WinMM <c>PlaySoundW</c>（异步、任意线程可调、不阻塞调用方）。
///
/// <para><b>为什么不用 WPF 的 MediaPlayer</b>：<c>MediaPlayer</c> 必须活在 UI 线程、要等
/// <c>MediaOpened</c> 才出声，拿来播几百毫秒的「叮」太重；<c>PlaySound</c> 是即时的。</para>
///
/// <para><b>只吃 wav</b>：<c>PlaySound</c> 支持的格式固定（wav / 系统别名），
/// mp3 之类必须先用别的库解码 —— 界面上会写明这一点。</para>
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    /// <summary>WinMM 的 <c>SND_ASYNC</c>：立刻返回、后台播。</summary>
    private const uint SndAsync = 0x0001;

    /// <summary><c>SND_NODEFAULT</c>：文件不存在时不要偷偷播系统默认音。</summary>
    private const uint SndNoDefault = 0x0002;

    /// <summary><c>SND_FILENAME</c>：<c>pszSound</c> 是一个文件名。</summary>
    private const uint SndFilename = 0x00020000;

    /// <summary><c>SND_ALIAS</c>：<c>pszSound</c> 是系统声音别名（如 <c>SystemAsterisk</c>）。</summary>
    private const uint SndAlias = 0x00010000;

    /// <summary><c>SND_NOSTOP</c>：别打断正在播的声音。</summary>
    private const uint SndNoStop = 0x0010;

    private bool _disposed;

    /// <summary>「不播放」的选项值。</summary>
    public const string NoneId = "none";

    /// <summary>系统提示音的 Id 前缀（<c>system:SystemAsterisk</c>）。</summary>
    public const string SystemPrefix = "system:";

    /// <summary>自定义文件的 Id 前缀（<c>file:C:\a\b.wav</c>）。</summary>
    public const string FilePrefix = "file:";

    /// <summary>
    /// 可选的内置系统音（别名 → 中文名）。挑的都是各版本 Windows 都有的那几个，
    /// 不会出现「换台机器就没声」。
    /// </summary>
    public static IReadOnlyList<SoundChoice> SystemSounds { get; } = new[]
    {
        new SoundChoice(SystemPrefix + "SystemNotification", "默认通知"),
        new SoundChoice(SystemPrefix + "SystemAsterisk", "提示（星号）"),
        new SoundChoice(SystemPrefix + "SystemExclamation", "提醒（感叹号）"),
        new SoundChoice(SystemPrefix + "SystemHand", "错误（停止）"),
        new SoundChoice(SystemPrefix + "SystemQuestion", "疑问"),
    };

    /// <summary>下拉框用的完整清单：不播放 + 系统音若干。</summary>
    public static IReadOnlyList<SoundChoice> BuiltInChoices { get; } = BuildChoices();

    private static SoundChoice[] BuildChoices()
    {
        var list = new List<SoundChoice> { new(NoneId, "不播放") };
        list.AddRange(SystemSounds);
        return list.ToArray();
    }

    /// <summary>播放一个声音选项（不认识 / 不播放 / 文件不存在都安全返回 false）。</summary>
    public static bool TryPlay(string? choiceId)
    {
        if (string.IsNullOrWhiteSpace(choiceId)) return false;
        var id = choiceId.Trim();
        if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            if (id.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var alias = id[SystemPrefix.Length..].Trim();
                if (alias.Length == 0) return false;
                return PlaySound(alias, IntPtr.Zero, SndAsync | SndAlias | SndNoDefault | SndNoStop);
            }

            if (id.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = id[FilePrefix.Length..].Trim();
                return path.Length > 0 && File.Exists(path)
                       && PlaySound(path, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault | SndNoStop);
            }

            // 兜底：直接当文件名 / 别名再试一次（手改配置写成裸用户名时不至于静默）
            return PlaySound(id, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault | SndNoStop)
                   || PlaySound(id, IntPtr.Zero, SndAsync | SndAlias | SndNoDefault | SndNoStop);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>停止当前正在播的声音（不影响别的程序）。</summary>
    public static void Stop() => PlaySound("", IntPtr.Zero, 0);

    /// <summary>把文件路径包成配置值（<c>file:C:\a\b.wav</c>）。</summary>
    public static string FromFile(string path) => FilePrefix + (path ?? "");

    /// <summary>从一个配置值里取出文件路径（不是文件来源时返回 null）。</summary>
    public static string? TryGetFilePath(string? choiceId)
        => choiceId is not null && choiceId.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            ? choiceId[FilePrefix.Length..].Trim()
            : null;

    /// <summary>配置值的界面显示名（用于日志 / 提示）。</summary>
    public static string Describe(string? choiceId)
    {
        if (string.IsNullOrWhiteSpace(choiceId)) return "不播放";
        var id = choiceId.Trim();
        if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase)) return "不播放";

        foreach (var choice in SystemSounds)
        {
            if (string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase)) return choice.Name;
        }

        var path = TryGetFilePath(id);
        if (path is not null) return File.Exists(path) ? "自定义：" + Path.GetFileName(path) : "自定义文件不存在：" + Path.GetFileName(path);

        return id;
    }

    /// <summary>这个配置值当前能不能真的发出声（界面据此标红）。</summary>
    public static bool IsPlayable(string? choiceId)
    {
        if (string.IsNullOrWhiteSpace(choiceId)) return false;
        var id = choiceId.Trim();
        if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase)) return false;
        if (id.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase)) return true;

        var path = TryGetFilePath(id);
        return path is not null ? File.Exists(path) : File.Exists(id);
    }

    /// <summary>释放（停掉当前声音）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { /* 退出流程忽略 */ }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
