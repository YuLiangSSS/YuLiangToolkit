namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 界面主题（「主页设置 → 界面风格」里五选一，存 <c>Config\shell\config.json</c> 的 <c>uiTheme</c>）。
///
/// <para>枚举**按 C# 原名以字符串存**（<c>"Default"</c> / <c>"LiquidGlass"</c> / <c>"FrostedGlass"</c> /
/// <c>"Acrylic"</c> / <c>"Daoji"</c>，见 <c>JsonFileStore</c> 的 <c>JsonStringEnumConverter</c>）——
/// 改枚举名等于改配置格式，别随便改；旧配置文件没有 <c>uiTheme</c> 字段时读出来就是 <see cref="Default"/>
/// （第一套 UI，行为不变）。</para>
///
/// <para>液态玻璃 / 毛玻璃 / 亚克力是**半透明界面**（<see cref="ThemeManager.HasTranslucentSurface"/>）；
/// 道系（墨金）是纯色面板 + 装饰背景层，也会显示背景图 / 远山 / 金色光晕。
/// 深浅色由「外观模式」（<see cref="ThemeMode"/>）统一控制，五套主题都支持。</para>
/// </summary>
public enum UiTheme
{
    /// <summary>默认（第一套 UI）：经典纯色界面（不显示背景图），深 / 浅色跟随外观模式。</summary>
    Default,

    /// <summary>液态玻璃（第二套 UI）：半透明玻璃面板 + 流动光晕背景，参考 Kerxs/glassium。</summary>
    LiquidGlass,

    /// <summary>毛玻璃（v0.43.0）：奶白磨砂面板 + 重模糊背景，安静冷色调，没有动画光晕。</summary>
    FrostedGlass,

    /// <summary>亚克力（v0.43.0）：深色亚克力面板 + 轻模糊 + 颗粒噪点，类似 Win11 Acrylic。</summary>
    Acrylic,

    /// <summary>道系（墨金，v0.89.0）：玄墨底 + 香槟金 + 远山剪影 + 金色光晕，参考道阶表。</summary>
    Daoji,
}
