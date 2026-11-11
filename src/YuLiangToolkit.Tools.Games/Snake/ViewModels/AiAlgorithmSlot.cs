using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Games.Snake.ViewModels;

/// <summary>
/// 一个 AI 的算法选择（界面上一行「电脑 N：算法」下拉）。
/// <para>数量跟随「AI 数量」设置；每个 AI 可以单独选 1~10 号算法。</para>
/// </summary>
public sealed class AiAlgorithmSlot : ObservableObject
{
    private int _level;

    /// <summary>创建。</summary>
    /// <param name="index">第几个 AI（0 起）。</param>
    /// <param name="level">算法等级（1~10）。</param>
    public AiAlgorithmSlot(int index, int level)
    {
        Index = index;
        _level = Math.Clamp(level, 1, 10);
    }

    /// <summary>第几个 AI（0 起）。</summary>
    public int Index { get; }

    /// <summary>标签。</summary>
    public string Label => $"电脑 {Index + 1}";

    /// <summary>算法等级。</summary>
    public int Level
    {
        get => _level;
        set
        {
            if (!Set(ref _level, Math.Clamp(value, 1, 10))) return;
            OnPropertyChanged(nameof(SelectedLevel));
            OnPropertyChanged(nameof(Description));
        }
    }

    /// <summary>算法说明。</summary>
    public string Description => SnakeAi.LevelDescription(_level);

    /// <summary>下拉选项（1~10 号算法）。</summary>
    public IReadOnlyList<OptionItem<double>> LevelOptions { get; } = OptionItem.From(
        Enumerable.Range(1, 10).Select(i => ((double)i, SnakeAi.LevelName(i))).ToArray());

    /// <summary>下拉选中项。</summary>
    public OptionItem<double> SelectedLevel
    {
        get => OptionItem<double>.Find(LevelOptions, _level) ?? LevelOptions[0];
        set { if (value is not null) Level = (int)value.Value; }
    }
}
