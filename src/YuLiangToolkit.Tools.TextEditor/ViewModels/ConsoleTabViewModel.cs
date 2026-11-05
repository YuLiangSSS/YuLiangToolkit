using System.Text;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.TextEditor.Core.Terminal;

namespace YuLiangToolkit.Tools.TextEditor.ViewModels;

/// <summary>
/// 一个控制台标签页（CMD / PowerShell）：自己的输出缓冲、输入行、命令历史、工作目录与运行状态。
/// <para>进程执行不在这里（见 <see cref="Core.Terminal.ConsoleProcessRunner"/>），
/// ViewModel 通过 <see cref="TextEditorViewModel.RunConsoleAsync"/> 驱动，输出经 UI 线程追加到这里。</para>
/// </summary>
public sealed class ConsoleTabViewModel : ObservableObject
{
    /// <summary>输出最多保留多少字符（超过就从头部丢掉旧内容，避免长时间运行把内存吃满）。</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>超过上限后保留的字符数（丢到只剩 150K，不用每次删一点）。</summary>
    private const int TrimmedOutputChars = 150_000;

    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = new();
    private int _historyIndex;
    private string _input = "";
    private string _outputText = "";
    private bool _isRunning;
    private string _workingDirectory;

    public ConsoleTabViewModel(ConsoleShellKind kind, int index, string? workingDirectory)
    {
        Kind = kind;
        Index = index;
        Title = $"{ConsoleCommandLine.KindLabel(kind)} {index}";
        _workingDirectory = ConsoleCommandLine.NormalizeWorkingDirectory(workingDirectory);
    }

    /// <summary>CMD / PowerShell。</summary>
    public ConsoleShellKind Kind { get; }

    /// <summary>标签页上显示的名字（<c>CMD 1</c> / <c>PowerShell 2</c>）。</summary>
    public string Title { get; }

    /// <summary>同类型控制台的序号（从 1 开始，新建时由 ViewModel 分配）。</summary>
    public int Index { get; }

    /// <summary>当前工作目录（<c>cd</c> 会在我们这边直接改它，不启动进程）。</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (Set(ref _workingDirectory, ConsoleCommandLine.NormalizeWorkingDirectory(value)))
                OnPropertyChanged(nameof(Prompt));
        }
    }

    /// <summary>输入框前面的提示词（<c>C:\x&gt;</c> / <c>PS C:\x&gt;</c>）。</summary>
    public string Prompt => ConsoleCommandLine.BuildPrompt(Kind, WorkingDirectory);

    /// <summary>控制台输出（只读文本框直接绑定；追加时自动裁掉过老的头部）。</summary>
    public string OutputText
    {
        get => _outputText;
        private set => Set(ref _outputText, value);
    }

    /// <summary>用户正在输入的一行命令（绑定输入框）。</summary>
    public string Input
    {
        get => _input;
        set => Set(ref _input, value ?? "");
    }

    /// <summary>当前标签是不是正在跑命令（跑的时候禁用运行按钮、显示停止）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    /// <summary>正在运行的命令的取消源（停止按钮 / 关闭工具时 cancel）。</summary>
    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>命令历史（最多 <see cref="MaxHistory"/> 条，连续重复不重复记）。</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>命令历史上限。</summary>
    public const int MaxHistory = 100;

    /// <summary>追加输出（多行也行）；超长自动保留尾部。</summary>
    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        _buffer.Append(text);
        if (_buffer.Length > MaxOutputChars)
            _buffer.Remove(0, _buffer.Length - TrimmedOutputChars);

        OutputText = _buffer.ToString();
    }

    /// <summary>清空输出（<c>cls</c> / <c>clear</c> / 工具栏清空）。</summary>
    public void Clear()
    {
        _buffer.Clear();
        OutputText = "";
    }

    /// <summary>记一条命令到历史（连续相同的只记一次；超过上限丢最旧的）。</summary>
    public void RecordHistory(string? command)
    {
        var trimmed = command?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        if (_history.Count == 0 || !string.Equals(_history[^1], trimmed, StringComparison.Ordinal))
            _history.Add(trimmed);

        if (_history.Count > MaxHistory)
            _history.RemoveRange(0, _history.Count - MaxHistory);

        _historyIndex = _history.Count;
    }

    /// <summary>
    /// 上 / 下翻命令历史：<paramref name="direction"/> = -1 上一条、+1 下一条。
    /// 返回要填进输入框的文字（翻到头时返回空串）；没有历史返回 <c>null</c>。
    /// </summary>
    public string? MoveHistory(int direction)
    {
        if (_history.Count == 0) return null;

        if (direction < 0) _historyIndex = Math.Max(0, _historyIndex - 1);
        else _historyIndex = Math.Min(_history.Count, _historyIndex + 1);

        return _historyIndex >= _history.Count ? "" : _history[_historyIndex];
    }

    /// <summary>重置历史游标（输入框被手动改过 / 执行完命令后）。</summary>
    public void ResetHistoryCursor() => _historyIndex = _history.Count;
}
