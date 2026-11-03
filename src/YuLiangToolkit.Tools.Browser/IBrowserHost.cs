namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// View（<c>BrowserView.xaml.cs</c>）提供给 ViewModel 的浏览器操作口：
/// 地址栏命令只认这组方法 / 属性，ViewModel 不直接碰 WebView2 控件（方便自检和以后换内核）。
/// </summary>
public interface IBrowserHost
{
    /// <summary>是否还能后退。</summary>
    bool CanGoBack { get; }

    /// <summary>是否还能前进。</summary>
    bool CanGoForward { get; }

    /// <summary>内核是否已经启动完成（没启动时命令只提示、不报错）。</summary>
    bool IsReady { get; }

    /// <summary>导航到指定 URL（调用方保证已经规范化）。</summary>
    void Navigate(string url);

    /// <summary>后退。</summary>
    void GoBack();

    /// <summary>前进。</summary>
    void GoForward();

    /// <summary>刷新当前页。</summary>
    void Reload();

    /// <summary>停止加载。</summary>
    void Stop();

    /// <summary>
    /// 设置<b>激活标签</b>的页面缩放（会夹回 <see cref="BrowserZoom.Min"/> ~ <see cref="BrowserZoom.Max"/>）。
    /// WebView2 的 Ctrl + 鼠标滚轮原生缩放也走同一套显示 / 同步逻辑（由 View 回写状态）。
    /// </summary>
    void SetZoom(double factor);

    /// <summary>释放内核（工具被切换走 / 关闭时调用；可重复调用）。</summary>
    void Close();
}
