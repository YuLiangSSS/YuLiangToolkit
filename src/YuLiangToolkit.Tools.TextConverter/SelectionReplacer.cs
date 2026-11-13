using System.Runtime.InteropServices;
using System.Windows;
using YuLiangToolkit.Core.Input;

namespace YuLiangToolkit.Tools.TextConverter;

/// <summary>一次「用快捷键转换选区」的结果。</summary>
/// <param name="Success">是否转换并替换成功。</param>
/// <param name="Message">给用户看的说明（失败时说清为什么）。</param>
/// <param name="Before">转换前的文本（成功时有值）。</param>
/// <param name="After">转换后的文本（成功时有值）。</param>
public readonly record struct SelectionConvertResult(bool Success, string Message, string Before = "", string After = "")
{
    /// <summary>成功的快捷构造。</summary>
    public static SelectionConvertResult Ok(string before, string after)
        => new(true, "已转换", before, after);

    /// <summary>失败的快捷构造。</summary>
    public static SelectionConvertResult Fail(string message) => new(false, message);
}

/// <summary>
/// 「选中文本 → 转换 → 替换回原处」这套动作的实现（**要碰剪贴板与键盘，所以不可无头自检**）。
///
/// <para><b>流程</b>（按用户确认的方案）：</para>
/// <list type="number">
///   <item>记下当前剪贴板内容（文字部分）；</item>
///   <item>给前台程序发 Ctrl+C，把选区复制到剪贴板；</item>
///   <item>读剪贴板 —— 还是原内容 / 空 → 判定「没有选中文本」，提示用户；</item>
///   <item>转换；</item>
///   <item>把结果放进剪贴板，给前台程序发 Ctrl+V 替换掉选区；</item>
///   <item><b>还原剪贴板</b>成最初的内容（不弄丢用户复制的东西）。</item>
/// </list>
///
/// <para><b>为什么要在 UI 线程 / 不在快捷键钩子回调里直接做</b>：Ctrl+C 之后要等前台程序把数据塞进剪贴板，
/// 需要几十毫秒的间隔；而这个间隔必须让消息循环转起来（用 <c>await Task.Delay</c>），
/// 所以在钩子回调里 <c>async void</c> 地排一个任务，且要在 <see cref="Application.Current"/> 的 Dispatcher 上跑。</para>
/// </summary>
public static class SelectionReplacer
{
    /// <summary>等前台程序把数据写进剪贴板的时间（毫秒）。太短会读到旧内容。</summary>
    public const int CopyDelayMs = 90;

    /// <summary>Ctrl+V 之后等粘贴完成再还原剪贴板的时间（毫秒）。太短会还原成旧内容、粘贴失败。</summary>
    public const int PasteDelayMs = 160;

    /// <summary>
    /// 转换当前前台程序的选中文本。
    /// </summary>
    /// <param name="conversionId">转换 Id（见 <see cref="TextTransformer.All"/>）。</param>
    /// <param name="restoreClipboard">转换完是否把剪贴板还原成原内容。</param>
    public static async Task<SelectionConvertResult> ConvertSelectionAsync(string conversionId, bool restoreClipboard = true)
    {
        if (TextTransformer.Find(conversionId) is null) return SelectionConvertResult.Fail("这种转换不认识");

        // ① 记下原剪贴板（只处理文字；图片等原样不动 —— 那种情况就不还原了）
        var originalText = TryGetClipboardText(out var hadTextBefore);

        // ② 发 Ctrl+C
        if (!KeySender.CopySelection()) return SelectionConvertResult.Fail("没能发送复制指令（可能被安全软件拦了）");

        await Task.Delay(CopyDelayMs).ConfigureAwait(true);

        // ③ 读剪贴板
        var copied = TryGetClipboardText(out var hasText);
        if (!hasText || string.IsNullOrEmpty(copied))
        {
            RestoreClipboard(originalText, hadTextBefore, restoreClipboard);
            return SelectionConvertResult.Fail("没有选中文本：请先选中要转换的文字再按快捷键");
        }

        // 复制没成功（剪贴板还是原内容）→ 也当作没选中
        if (hadTextBefore && string.Equals(copied, originalText, StringComparison.Ordinal))
        {
            RestoreClipboard(originalText, hadTextBefore, restoreClipboard);
            return SelectionConvertResult.Fail("没有选中文本：请先选中要转换的文字再按快捷键");
        }

        // ④ 转换
        var (converted, error) = TextTransformer.Apply(copied, conversionId);
        if (error.Length > 0)
        {
            RestoreClipboard(originalText, hadTextBefore, restoreClipboard);
            return SelectionConvertResult.Fail(error);
        }

        if (string.Equals(converted, copied, StringComparison.Ordinal))
        {
            RestoreClipboard(originalText, hadTextBefore, restoreClipboard);
            return SelectionConvertResult.Fail("转换后没有变化（这段文字不适合这种转换）");
        }

        // ⑤ 放回剪贴板并粘贴
        if (!TrySetClipboardText(converted))
        {
            RestoreClipboard(originalText, hadTextBefore, restoreClipboard);
            return SelectionConvertResult.Fail("剪贴板被别的程序占用，写不进去");
        }

        if (!KeySender.Paste())
        {
            RestoreClipboard(originalText, hadTextBefore, restoreClipboard);
            return SelectionConvertResult.Fail("没能发送粘贴指令（可能被安全软件拦了）");
        }

        await Task.Delay(PasteDelayMs).ConfigureAwait(true);

        // ⑥ 还原剪贴板
        RestoreClipboard(originalText, hadTextBefore, restoreClipboard);

        return SelectionConvertResult.Ok(copied, converted);
    }

    /// <summary>只把转换结果放进剪贴板（不自动粘贴；给「复制模式」用）。</summary>
    public static SelectionConvertResult ConvertToClipboard(string text, string conversionId)
    {
        var (converted, error) = TextTransformer.Apply(text, conversionId);
        if (error.Length > 0) return SelectionConvertResult.Fail(error);
        if (!TrySetClipboardText(converted)) return SelectionConvertResult.Fail("剪贴板被别的程序占用，写不进去");

        return SelectionConvertResult.Ok(text, converted);
    }

    /// <summary>把剪贴板还原回去（原来有文字就写回文字；原来没有就清空）。</summary>
    private static void RestoreClipboard(string? originalText, bool hadTextBefore, bool restore)
    {
        if (!restore) return;

        try
        {
            if (hadTextBefore && originalText is not null) Clipboard.SetText(originalText);
            else Clipboard.Clear();
        }
        catch
        {
            // 剪贴板被占用：还原失败不影响转换结果，忽略
        }
    }

    /// <summary>读剪贴板文字（被占用 / 没有文字都安全返回）。</summary>
    private static string? TryGetClipboardText(out bool hasText)
    {
        hasText = false;

        try
        {
            if (!Clipboard.ContainsText()) return null;

            var text = Clipboard.GetText();
            hasText = true;
            return text;
        }
        catch (COMException)
        {
            // 剪贴板正被别的程序占用：等一下再试一次
            try
            {
                Thread.Sleep(20);
                if (!Clipboard.ContainsText()) return null;

                var text = Clipboard.GetText();
                hasText = true;
                return text;
            }
            catch
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写剪贴板文字（被占用时重试几次）。</summary>
    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
