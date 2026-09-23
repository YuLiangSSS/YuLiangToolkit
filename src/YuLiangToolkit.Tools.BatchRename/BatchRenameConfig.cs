using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.BatchRename;

/// <summary>文件列表的排序字段。</summary>
public enum FileSortMode
{
    /// <summary>不排序（保持添加/扫描顺序）。</summary>
    None,

    /// <summary>按文件名。</summary>
    Name,

    /// <summary>按完整路径。</summary>
    Path,

    /// <summary>按最后修改时间。</summary>
    Time,

    /// <summary>按文件大小。</summary>
    Size,
}

/// <summary>
/// 「批量重命名」的全部选项 → <c>Config\batch-rename\config.json</c>。
///
/// <para><b>核心是一张规则清单</b>（<see cref="Rules"/>）：<b>数组顺序 = 执行顺序</b>，
/// 同一种规则可以放多条（例如先「去空格」再「替换」再「替换」再「加序号」），
/// 顺序由界面上的拖动 / 上下移动决定。规则本身长什么样见 <see cref="RenameRule"/>。</para>
///
/// <para>这里的属性用普通自动属性即可：保存靠 <c>ToolConfigStore</c> 后台的内容比对，
/// 列表增删改（原地修改 <see cref="Rules"/>）同样会被发现并落盘
/// （和 <c>ShellConfig.LastUsed</c> 字典同一套路）。</para>
/// </summary>
public sealed class BatchRenameConfig : ToolConfig
{
    /// <summary>
    /// 规则清单。<b>列表顺序就是套用顺序</b>，逐条写进 config.json 的 <c>rules</c> 数组。
    /// 空列表 = 什么都不改（新建工具时就是空的，等你加规则）。
    /// </summary>
    public List<RenameRule> Rules { get; set; } = new();

    /// <summary>目标重名时自动加 <c> (1)</c>、<c> (2)</c> 避让，而不是拦着不让执行。</summary>
    public bool AutoResolveConflicts { get; set; }

    // ================= 使用偏好（不是命名规则，但一起存） =================

    /// <summary>上次添加/扫描的文件夹（方便「重新扫描」）。</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>扫描文件夹时是否包含子文件夹。</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>
    /// 只收这些扩展名，分号/逗号/空格分隔，例如 <c>jpg; png; .bmp</c>。留空 = 全部文件。
    /// 只影响「扫描文件夹 / 拖文件夹进来」，不影响手动添加的单个文件。
    /// </summary>
    public string ExtensionFilter { get; set; } = "";

    /// <summary>列表排序字段。</summary>
    public FileSortMode SortField { get; set; } = FileSortMode.Path;

    /// <summary>是否降序。</summary>
    public bool SortDescending { get; set; }
}
