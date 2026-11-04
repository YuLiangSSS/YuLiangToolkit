using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Archive.ViewModels;
using YuLiangToolkit.Tools.Archive.Views;

namespace YuLiangToolkit.Tools.Archive;

/// <summary>
/// 「压缩包工具」：解压 RAR / ZIP / 7Z / TAR / GZ / BZ2 / XZ / ZST 等常见压缩包（支持密码、GBK 文件名），
/// 也能把文件 / 文件夹压成 ZIP 或 7Z。RAR 因版权原因只能解、不能压。
/// </summary>
public class ArchiveTool : ITool
{
    public string Id => "archive";

    public string Name => "压缩包工具";

    public string Description =>
        "解压 RAR / ZIP / 7Z / TAR / GZ / BZ2 / XZ / ZST 等常见压缩包（支持密码、旧版 GBK 文件名），"
        + "也能把文件 / 文件夹压成 ZIP 或 7Z；RAR 因版权原因只能解不能压。";

    public string EmojiIcon => "📦";

    public string Category => "文件";

    public IReadOnlyList<string> Tags => new[]
    {
        "压缩", "解压", "解压缩", "zip", "7z", "rar", "tar", "gz", "bz2", "xz",
        "密码", "压缩包", "批量", "归档",
    };

    public int Order => 42;

    public FrameworkElement CreateView()
        => new ArchiveView { DataContext = new ArchiveViewModel() };
}
