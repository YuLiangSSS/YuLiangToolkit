namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// 「打开方式」的三种模式。
/// <para><b>Auto</b> = 按文件类型打开（PDF → PDF 阅读器、图片 → 图片阅读器、压缩包 → 压缩包工具……），
/// 认不出的类型用文本编辑器兜底；<b>Text</b> = 一律用文本编辑器；<b>Archive</b> = 一律用压缩包工具。</para>
/// </summary>
public enum OpenWithMode
{
    /// <summary>按文件类型自动选择工具（默认，也是历史 PDF / 图片关联走的路）。</summary>
    Auto = 0,

    /// <summary>强制用「文本编辑器」打开。</summary>
    Text = 1,

    /// <summary>强制用「压缩包工具」打开。</summary>
    Archive = 2,
}

/// <summary>
/// 一个「打开方式」入口：Windows 里显示的名字 + 启动命令里用的模式参数 +（非 Auto 时）入口副本的扩展名后缀。
/// <para><b>为什么 Auto 没有副本</b>：它直接复用主程序 exe 那个 Applications 条目（和 PDF / 图片关联
/// 用的是同一个键），命令是历史兼容的 <c>"exe" "%1"</c>；Text / Archive 必须各自有一个**不同 exe 路径**
/// 的入口副本（Windows 的「打开方式」按 exe 路径去重，同一个 exe 的多个参数只会显示成一条）。</para>
/// </summary>
public sealed record OpenWithOpener(
    OpenWithMode Mode,
    string DisplayName,
    string Hint,
    string FileNameSuffix);

/// <summary>「打开方式」当前的登记状态（给界面显示用；全部只读，不改系统）。</summary>
public sealed record OpenWithRegistrationState(
    bool Registered,
    bool OpenersUpToDate,
    IReadOnlyList<string> OpenerPaths,
    string Message);

/// <summary>一次登记 / 取消登记 / 修复操作的结果。</summary>
public sealed record OpenWithRegistrationResult(bool Success, string Message);
