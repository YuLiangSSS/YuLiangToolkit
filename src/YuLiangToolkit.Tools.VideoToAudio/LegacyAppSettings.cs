namespace YuLiangToolkit.Tools.VideoToAudio;

/// <summary>
/// 老版本（选项还塞在 <c>%AppData%\YuLiangToolkit\settings.json</c> 里时）的字段快照，
/// <b>只用于一次性迁移</b>：首次运行时把老选项搬进
/// <c>Config\video-to-audio\config.json</c>，免得用户重设一遍。
///
/// <para>迁移完成后这个类就可以删掉了（连同 <see cref="VideoToAudioConfig.FromLegacySettings"/>）。</para>
/// </summary>
public sealed class LegacyAppSettings
{
    public string? LastInputFolder { get; set; }
    public string? LastOutputFolder { get; set; }
    public bool LastUseSourceFolder { get; set; } = true;
    public string LastMode { get; set; } = "Mp3Lossy";
    public string LastBitrate { get; set; } = "192";
    public bool LastIncludeSubfolders { get; set; } = true;
    public bool LastOnlyMp4 { get; set; } = true;
    public int Parallelism { get; set; }
    public int FlacCompressionLevel { get; set; } = 5;
}
