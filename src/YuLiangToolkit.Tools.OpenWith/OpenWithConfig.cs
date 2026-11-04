using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.OpenWith;

/// <summary>
/// 「打开方式」的配置（<c>Config\open-with\config.json</c>，改一下自动落盘）。
/// <para>登记状态本身不存这里 —— 那是系统注册表的真实状态，每次打开页面都重新读一次，
/// 免得配置文件说“已启用”而注册表其实被别的工具改掉了。</para>
/// </summary>
public sealed class OpenWithConfig : ToolConfig
{
    /// <summary>功能 Id：同时是 <c>Config\open-with</c> / <c>Data\open-with</c> 的文件夹名。</summary>
    public const string ToolId = "open-with";

    /// <summary>「模拟打开」输入框里上次填的路径（只是省得每次重新选）。</summary>
    public string LastSimulatedPath { get; set; } = "";
}
