namespace YuLiangToolkit.Core.Models;

/// <summary>
/// 全局设置（JSON 持久化到 <c>%AppData%\YuLiangToolkit\settings.json</c>）。
///
/// <para><b>只放跨功能共享的东西</b>（工具路径、许可证、代理…）。
/// 每个功能自己的选项（Checkbox / Textbox / 下拉框）请放到 <c>Config\&lt;功能名&gt;\config.json</c>，
/// 见 <see cref="YuLiangToolkit.Core.Configuration.ToolConfig"/>。</para>
/// </summary>
public class AppSettings
{
    /// <summary>用户手动指定的 ffmpeg.exe 路径（所有用到 FFmpeg 的功能共享）。</summary>
    public string? FFmpegPath { get; set; }
}
