using System.Windows;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.TextConverter.ViewModels;
using YuLiangToolkit.Tools.TextConverter.Views;

namespace YuLiangToolkit.Tools.TextConverter;

/// <summary>
/// 「文本转换器」工具入口：页面上转换 + 「选中文本按快捷键就地转换」。
///
/// <para><b>快捷键的特点</b>：每条都是用户自己「绑定某个组合 → 某种转换」，
/// 所以实现的不是固定的 <see cref="IShortcutProvider"/> 列表，而是动态注册（见 VM 的 <c>SyncShortcuts</c>）。
/// 为了让主页设置里看不到它（用户是在本工具页自己管快捷键），它**不实现** <see cref="IShortcutProvider"/>。</para>
/// </summary>
public sealed class TextConverterTool : ITool
{
    private readonly TextConverterViewModel _viewModel;

    /// <summary>构造：建常驻 ViewModel（快捷键要在页面没打开时也生效）。</summary>
    public TextConverterTool()
    {
        _viewModel = new TextConverterViewModel(this);
        _viewModel.SyncShortcuts();
    }

    /// <summary>常驻 ViewModel。</summary>
    public TextConverterViewModel ViewModel => _viewModel;

    /// <summary>强类型配置。</summary>
    public TextConverterConfig Config => _viewModel.Cfg;

    public string Id => TextConverterConfig.ToolId;

    public string Name => "文本转换器";

    public string Description =>
        "把文字转成各种样子：大小写、繁体、火星文、花体、全角、倒置、Emoji（牛马 → 🐮🐴）、"
        + "同音字、Base64 / URL / 摩斯编码、去空格去重等；还能自己绑快捷键，选中任何程序里的文字按一下就就地转换。";

    public string EmojiIcon => "🔤";

    public string Category => "文档";

    /// <summary>归在「文档」类别下。</summary>
    public IReadOnlyList<string> CategoryPath => new[] { "文档" };

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "文本转换", "文本", "转换", "大小写", "火星文", "繁体", "Emoji", "表情",
        "Base64", "URL 编码", "摩斯电码", "全角", "快捷转换",
    };

    /// <summary>排在「文档」分类里其它工具之后。</summary>
    public int Order => 175;

    public FrameworkElement CreateView() => new TextConverterView { DataContext = _viewModel };
}
