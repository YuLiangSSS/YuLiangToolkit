using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Archive;

/// <summary>
/// 「压缩包工具」的配置：<c>Config\archive\config.json</c>，改一下就自动落盘（见 <see cref="ToolConfig"/>）。
/// 只存“下次打开还能用”的偏好，不存密码（密码只在本次运行内使用，避免明文落盘）。
/// </summary>
public sealed class ArchiveConfig : ToolConfig
{
    // ---------- 解压 ----------

    /// <summary>解压到哪里：空 = 压缩包旁边；非空 = 固定目录。</summary>
    public string ExtractDirectory { get; set; } = "";

    /// <summary>为每个压缩包单独建一个子文件夹（默认开，避免解出一堆文件糊在原目录）。</summary>
    public bool SubfolderPerArchive { get; set; } = true;

    /// <summary>同名文件处理方式（默认「自动改名」，不覆盖用户数据）。</summary>
    public OverwriteMode Overwrite { get; set; } = OverwriteMode.Rename;

    /// <summary>保留压缩包里的文件修改时间。</summary>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>上次添加压缩包时所在的目录（下次打开对话框直接定位）。</summary>
    public string LastAddDirectory { get; set; } = "";

    /// <summary>上次选择的目标目录（“浏览”对话框的起始位置）。</summary>
    public string LastExtractDirectory { get; set; } = "";

    // ---------- 压缩 ----------

    /// <summary>创建压缩包的格式：ZIP 或 7Z（RAR 因版权原因不支持创建）。</summary>
    public ArchiveFormat CreateFormat { get; set; } = ArchiveFormat.SevenZip;

    public CompressionPreset CompressionLevel { get; set; } = CompressionPreset.Normal;

    /// <summary>压缩包输出到哪个目录：空 = 第一个源文件旁边。</summary>
    public string OutputDirectory { get; set; } = "";

    public string LastOutputDirectory { get; set; } = "";

    /// <summary>任务完成后自动打开输出位置（资源管理器）。</summary>
    public bool OpenFolderAfterDone { get; set; } = true;
}
