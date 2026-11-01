namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 音乐跟唱 / 律动的识别模式（配置里是中文文字，UI / Session 负责映射到本枚举）。
/// </summary>
public enum MusicVocalMode
{
    /// <summary>自动识别（按分析器的人声分类结果决定是否跟唱）。</summary>
    Auto = 0,

    /// <summary>强制按「带歌词」处理：不管分析结果，只要有声音就做嘴型。</summary>
    ForceVocal = 1,

    /// <summary>强制按「纯音乐」处理：不做嘴型，只做身体摇晃 / 点头。</summary>
    ForceInstrumental = 2,
}
