namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>开机启动项的来源类型。</summary>
public enum StartupEntryKind
{
    /// <summary>注册表 <c>…\CurrentVersion\Run</c> / <c>RunOnce</c> 下的值。</summary>
    RegistryRun,

    /// <summary>开始菜单「启动」文件夹里的快捷方式 / 程序。</summary>
    StartupFolder,
}

/// <summary>
/// 一条开机启动项。数据由 <c>System.StartupManager</c> 扫描得到；
/// 「已启用 / 已禁用」来自 Windows 的 <c>StartupApproved</c> 记录（任务管理器里那个开关就是它）。
/// </summary>
public sealed class StartupEntry
{
    public StartupEntryKind Kind { get; init; }

    /// <summary>显示名（注册表值名 / 启动文件夹里的文件名）。</summary>
    public string Name { get; init; } = "";

    /// <summary>原始启动命令（注册表值内容 / 快捷方式路径）。</summary>
    public string Command { get; init; } = "";

    /// <summary>作用范围：当前用户 / 所有用户 / 所有用户（32 位）。</summary>
    public string Scope { get; init; } = "";

    /// <summary>来源位置的完整路径（注册表键路径 / 启动文件夹路径）。</summary>
    public string Location { get; init; } = "";

    /// <summary>注册表值名（启动文件夹项为 null）。</summary>
    public string? ValueName { get; init; }

    /// <summary>从命令里解析出的可执行文件路径（解析不出就是空串）。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>注册表项的根键：<c>HKCU</c> / <c>HKLM</c>；启动文件夹项为空串。</summary>
    public string HiveName { get; init; } = "";

    /// <summary>注册表视图：<c>Default</c> / <c>Registry64</c> / <c>Registry32</c>（决定 32 / 64 位分支）。</summary>
    public string RegistryViewName { get; init; } = "";

    /// <summary>StartupApproved 键相对于根键的路径（写「启用 / 禁用」标记用）。</summary>
    public string ApprovedKeyPath { get; init; } = "";

    /// <summary>StartupApproved 里的值名（注册表 Run 项 = 值名；启动文件夹项 = 文件名）。</summary>
    public string? ApprovedValueName { get; init; }

    /// <summary>启动文件夹项的完整文件路径（删除时用）；注册表项为空串。</summary>
    public string FolderPath { get; init; } = "";

    /// <summary>是否启用（Windows 的 StartupApproved 记录；没有记录视为启用）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>当前进程是否有权限改这一项（HKLM / 公共启动文件夹需要管理员）。</summary>
    public bool CanModify { get; init; } = true;

    public string KindText => Kind == StartupEntryKind.RegistryRun ? "注册表" : "启动文件夹";

    /// <summary>来源摘要：<c>注册表 · 当前用户</c>。</summary>
    public string SourceText => $"{KindText} · {Scope}";

    public string StateText => Enabled ? "已启用" : "已禁用";

    /// <summary>命令太长时列表里只显示前面一段（完整命令看详情 / 提示）。</summary>
    public string CommandSummary
    {
        get
        {
            var text = Command.Trim();
            if (text.Length == 0) return "";
            return text.Length <= 72 ? text : text[..71] + "…";
        }
    }

    /// <summary>解析出来的程序路径；解析不出时退回原始命令首段。</summary>
    public string TargetText => FilePath.Length > 0 ? FilePath : CommandSummary;

    /// <summary>操作失败时用来定位「到底是哪一项」。</summary>
    public override string ToString() => $"{Name}（{SourceText}）";
}
