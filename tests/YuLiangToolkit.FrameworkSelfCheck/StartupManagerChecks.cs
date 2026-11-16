using YuLiangToolkit.Tools.StartupManagerTool;
using YuLiangToolkit.Tools.TaskManager.Models;
using YuLiangToolkit.Tools.TaskManager.Native;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「开机启动管理」（从任务管理器拆出的独立工具）自检：工具元数据 / 启动项显示文本 /
/// 真实**只读**扫描（不写注册表、绝不删除任何启动项）。
/// </summary>
internal static class StartupManagerChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckStartupEntryDisplay(assert);
        CheckStartupManagerReadOnly(assert);
    }

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new StartupManagerTool();
        check("[开机启动管理] 工具 Id / 名称 / 分类 / 排序符合约定，且实现了 ITool",
            tool.Id == "startup-manager" && tool.Name == "开机启动管理"
            && tool.Category == "系统" && tool.Order == 48
            && tool is YuLiangToolkit.Core.Tools.ITool && tool.EmojiIcon.Length > 0,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[开机启动管理] 归在「系统」类别下（CategoryPath 与 Category 一致）",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "系统",
            string.Join(" / ", tool.CategoryPath));

        check("[开机启动管理] 标签含「开机启动」「启动项」，描述写明启用 / 禁用 / 删除 / 管理员",
            tool.Tags.Contains("开机启动") && tool.Tags.Contains("启动项")
            && tool.Description.Contains("启用") && tool.Description.Contains("禁用")
            && tool.Description.Contains("删除") && tool.Description.Contains("管理员"),
            tool.Description);
    }

    private static void CheckStartupEntryDisplay(CheckFn check)
    {
        var registry = new StartupEntry
        {
            Kind = StartupEntryKind.RegistryRun,
            Name = "OneDrive",
            Command = "\"C:\\Users\\Me\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe\" /background --minimized",
            Scope = "当前用户",
            Location = @"Software\Microsoft\Windows\CurrentVersion\Run",
            ValueName = "OneDrive",
            Enabled = false,
        };
        check("[开机启动管理] 启动项：来源 / 状态文本",
            registry.KindText == "注册表" && registry.SourceText == "注册表 · 当前用户"
            && registry.StateText == "已禁用");

        check("[开机启动管理] 启动项：命令摘要过长时截断",
            registry.CommandSummary.Length <= 72 && registry.CommandSummary.EndsWith("…"),
            registry.CommandSummary);

        var folder = new StartupEntry
        {
            Kind = StartupEntryKind.StartupFolder,
            Name = "微信.lnk",
            Command = @"C:\Users\Me\Desktop\微信.lnk",
            Scope = "当前用户",
            Enabled = true,
        };
        check("[开机启动管理] 启动项：启动文件夹来源 / 启用状态",
            folder.KindText == "启动文件夹" && folder.SourceText == "启动文件夹 · 当前用户"
            && folder.StateText == "已启用");
    }

    private static void CheckStartupManagerReadOnly(CheckFn check)
    {
        try
        {
            var items = new StartupManager().Scan();
            check("[开机启动管理] 开机启动项扫描：只读、每项都有名字与来源（不修改任何东西）",
                items.All(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Location)),
                $"{items.Count} 项");
        }
        catch (Exception ex)
        {
            check("[开机启动管理] 开机启动项扫描：不应抛异常", false, ex.Message);
        }

        check("[开机启动管理] 权限判定不抛异常（普通 / 管理员环境都适用）",
            new Func<bool>(() => { _ = StartupManager.IsCurrentProcessElevated(); return true; })());
    }
}
