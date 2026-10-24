using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>
/// 「osu! 等级计算器」表格里的一行：Level / Star Rating / OD / 键数 / 标题 / 难度名 / 艺术家 / 谱师 + 状态。
///
/// <para>只存展示文本；数值由 ViewModel 在后台**串行** <c>OsuBeatmapFile.TryLoad</c> 后填进来
/// （OsuParsers 解码器是 static 状态、非线程安全，不能并发读）。</para>
/// </summary>
public sealed class LevelRowItem : ObservableObject
{
    private string _levelText = "…";
    private string _starRatingText = "…";
    private string _odText = "…";
    private string _keyCountText = "…";
    private string _title = "";
    private string _difficulty = "";
    private string _artist = "";
    private string _creator = "";
    private string _statusText = "等待计算";
    private string? _error;
    private bool _isFailed;

    public LevelRowItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Folder = Path.GetDirectoryName(filePath) ?? "";
    }

    /// <summary>完整路径（去重 / 重新计算用）。</summary>
    public string FilePath { get; }

    /// <summary>文件名。</summary>
    public string FileName { get; }

    /// <summary>所在文件夹（「打开文件夹」用）。</summary>
    public string Folder { get; }

    /// <summary>等级（拟合公式换算；无法换算时显示 "-"）。</summary>
    public string LevelText
    {
        get => _levelText;
        set => Set(ref _levelText, value);
    }

    /// <summary>星数（Star Rating，两位小数）。</summary>
    public string StarRatingText
    {
        get => _starRatingText;
        set => Set(ref _starRatingText, value);
    }

    /// <summary>OD（OverallDifficulty）。</summary>
    public string OdText
    {
        get => _odText;
        set => Set(ref _odText, value);
    }

    /// <summary>键数（"4K"）。</summary>
    public string KeyCountText
    {
        get => _keyCountText;
        set => Set(ref _keyCountText, value);
    }

    /// <summary>标题。</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>难度名。</summary>
    public string Difficulty
    {
        get => _difficulty;
        set => Set(ref _difficulty, value);
    }

    /// <summary>艺术家。</summary>
    public string Artist
    {
        get => _artist;
        set => Set(ref _artist, value);
    }

    /// <summary>谱师。</summary>
    public string Creator
    {
        get => _creator;
        set => Set(ref _creator, value);
    }

    /// <summary>状态文字（已计算 / 失败原因）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>加载失败原因（ToolTip / 状态列）。</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(Tooltip));
        }
    }

    /// <summary>这一行是不是加载失败（XAML 用它标红）。</summary>
    public bool IsFailed
    {
        get => _isFailed;
        set => Set(ref _isFailed, value);
    }

    /// <summary>行的 ToolTip：失败时显示原因，否则显示完整路径。</summary>
    public string Tooltip => IsFailed ? $"{FilePath}\n{Error}" : FilePath;

    /// <summary>把计算结果写进行里；失败时清空数值列并标红。</summary>
    public void Apply(bool ok, string? error, double starRating, double level, double overallDifficulty,
        int keyCount, string title, string difficulty, string artist, string creator)
    {
        if (!ok)
        {
            IsFailed = true;
            StatusText = "✘ " + (error ?? "无法读取");
            Error = error ?? "无法读取";
            LevelText = "—";
            StarRatingText = "—";
            OdText = "—";
            KeyCountText = "—";
            Title = Path.GetFileName(FilePath);
            Difficulty = "";
            Artist = "";
            Creator = "";
            OnPropertyChanged(nameof(Tooltip));
            return;
        }

        IsFailed = false;
        Error = null;
        StatusText = "✔ 已计算";
        StarRatingText = starRating.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        LevelText = LevelCalculator.FormatLevel(level);
        OdText = overallDifficulty.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        KeyCountText = $"{keyCount}K";
        Title = title;
        Difficulty = difficulty;
        Artist = artist;
        Creator = creator;
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>重新计算前先恢复成“等待计算”的样子。</summary>
    public void Reset()
    {
        IsFailed = false;
        Error = null;
        StatusText = "等待计算";
        LevelText = "…";
        StarRatingText = "…";
        OdText = "…";
        KeyCountText = "…";
        Title = "";
        Difficulty = "";
        Artist = "";
        Creator = "";
        OnPropertyChanged(nameof(Tooltip));
    }
}
