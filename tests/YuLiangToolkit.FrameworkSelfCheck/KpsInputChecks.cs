namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「KPS 按键统计」输入层自检（骨架版本；完整断言由队友补充）：
/// 热键字符串解析 / 键名映射 / Raw Input 注册与真实 SendInput 注入。
/// </summary>
public static class KpsInputChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        C("[KPS输入] 自检模块已注册（详细断言开发中）", true);
    }
}
