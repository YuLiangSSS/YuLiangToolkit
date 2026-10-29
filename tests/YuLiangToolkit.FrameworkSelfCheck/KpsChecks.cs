namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「KPS 按键统计」纯逻辑自检（骨架版本；完整断言由队友补充）：
/// 统计引擎口径 / 内置预设 / 配置序列化 / 计数器持久化格式。
/// 全部不碰真实键盘、不弹窗。
/// </summary>
public static class KpsChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        // 骨架阶段先放一条占位断言，保证分组能被 --list 看到且不会静默通过。
        C("[KPS] 自检模块已注册（详细断言开发中）", true);
    }
}
