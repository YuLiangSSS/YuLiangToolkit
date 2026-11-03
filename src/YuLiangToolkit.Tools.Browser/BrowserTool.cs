using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Browser.ViewModels;
using YuLiangToolkit.Tools.Browser.Views;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 「网页浏览器」功能入口：用微软官方 <b>WebView2</b> 控件承载 Chromium 内核（和 Edge 同源），
/// 浏览器外壳（<b>多标签页</b> / 标签栏上方或左侧 / 地址栏 / 前进后退 / 刷新 / 主页 / 页内右键刷新 /
/// 历史记录 / 下载 / 页面缩放 / 主页与启动恢复设置）由本工具自己实现。
///
/// <para>后续想加的功能（书签 / 代理……）都在这个工程里扩展，不需要动外壳。
/// 地址解析（<see cref="BrowserUrl"/>）、标签栏规则（<see cref="BrowserTabBar"/>）、
/// 缩放（<see cref="BrowserZoom"/>）、历史（<see cref="BrowserHistory"/>）、
/// 下载记录（<see cref="BrowserDownloads"/>）与配置（<see cref="BrowserConfig"/>）
/// 都是纯逻辑 / 普通类，自检 <c>browser</c> 组直接断言。</para>
/// </summary>
public class BrowserTool : ITool
{
    public string Id => BrowserConfig.ToolId;

    public string Name => "网页浏览器";

    public string Description
        => "Chromium 内核（WebView2）多标签浏览器：上方 / 左侧两种标签栏一键切换；"
           + "地址栏输入网址或搜索词，前进 / 后退 / 刷新 / 主页按钮，网页里右键也能刷新；"
           + "历史记录与下载侧栏、页面缩放（Ctrl+滚轮）、默认主页与「启动时恢复上次标签页」设置；"
           + "恢复标签只恢复标签，点开才加载；右键图片可另存为 / 复制图片";

    public string EmojiIcon => "🌐";

    public string Category => "网络";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "浏览器", "网页", "上网", "标签页", "多标签", "Chromium", "WebView2", "地址栏", "刷新", "右键",
        "历史记录", "下载", "缩放", "主页", "另存为", "右键",
    };

    /// <summary>越小越靠前（视频转音频 10，播放器 15）。</summary>
    public int Order => 12;

    public FrameworkElement CreateView()
        => new BrowserView { DataContext = new BrowserViewModel() };
}
