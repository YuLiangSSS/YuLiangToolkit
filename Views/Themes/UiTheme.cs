namespace YuLiangToolkit.App.Themes;

/// <summary>
/// 界面主题（「主页设置 → 界面风格」里二选一，存 <c>Config\shell\config.json</c> 的 <c>uiTheme</c>）。
///
/// <para>枚举**按 C# 原名以字符串存**（<c>"Default"</c> / <c>"LiquidGlass"</c>，见 <c>JsonFileStore</c> 的
/// <c>JsonStringEnumConverter</c>）—— 改枚举名等于改配置文件格式，别随便改；
/// 旧配置文件没有 <c>uiTheme</c> 字段时读出来就是 <see cref="Default"/>（第一套 UI，行为不变）。</para>
/// </summary>
public enum UiTheme
{
    /// <summary>默认（第一套 UI）：经典深色界面。</summary>
    Default,

    /// <summary>液态玻璃（第二套 UI）：半透明玻璃面板 + 流动光晕背景，参考 Kerxs/glassium。</summary>
    LiquidGlass,
}
