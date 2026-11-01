namespace YuLiangToolkit.Tools.Live2D.Speech;

/// <summary>系统里可用的语音（在线软妹音 / Windows 自带 TTS 本地语音）。</summary>
public sealed record SpeechVoiceInfo(string Id, string DisplayName, string Language, string Gender, string? Detail = null)
{
    private string GenderText => Gender == "Female" ? "女声" : Gender == "Male" ? "男声" : Gender;

    /// <summary>界面上显示的名字：在线是 <c>晓伊 · 少女音（在线 · 软妹音）</c>，本地是 <c>Microsoft Huihui（zh-CN，女声）</c>。</summary>
    public string DisplayText => Detail is null
        ? $"{DisplayName}（{Language}，{GenderText}）"
        : $"{DisplayName}（{Detail}）";
}

/// <summary>一句合成好的语音：WAV 字节 + 时长 + 唇形包络（20ms 一帧，0~1）。</summary>
public sealed record SpeechUtterance(
    string Text,
    byte[] WaveBytes,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    double DurationSeconds,
    float[] Envelope)
{
    /// <summary>时长（毫秒）。</summary>
    public double DurationMs => DurationSeconds * 1000.0;
}
