namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 界面主题（「主页设置 → 界面风格」里四选一，存 <c>Config\shell\config.json</c> 的 <c>uiTheme</c>）。
///
/// <para>枚举**按 C# 原名以字符串存**（<c>"Default"</c> / <c>"LiquidGlass"</c> / <c>"FrostedGlass"</c> /
/// <c>"Acrylic"</c>，见 <c>JsonFileStore</c> 的 <c>JsonStringEnumConverter</c>）—— 改枚举名等于改配置格式，别随便改；
/// 旧配置文件没有 <c>uiTheme</c> 字段时读出来就是 <see cref="Default"/>（第一套 UI，行为不变）。</para>
///
/// <para>除 <see cref="Default"/> 外的三套都是**半透明界面**（<see cref="ThemeManager.HasTranslucentSurface"/>），
/// 设置里上传的背景图会在它们后面透出来（见 <c>Views/GlassBackdrop.xaml</c>）。</para>
/// </summary>
public enum UiTheme
{
    /// <summary>默认（第一套 UI）：经典深色界面（不显示背景图）。</summary>
    Default,

    /// <summary>液态玻璃（第二套 UI）：半透明玻璃面板 + 流动光晕背景，参考 Kerxs/glassium。</summary>
    LiquidGlass,

    /// <summary>毛玻璃（v0.43.0）：奶白磨砂面板 + 重模糊背景，安静冷色调，没有动画光晕。</summary>
    FrostedGlass,

    /// <summary>亚克力（v0.43.0）：深色亚克力面板 + 轻模糊 + 颗粒噪点，类似 Win11 Acrylic。</summary>
    Acrylic,
}
