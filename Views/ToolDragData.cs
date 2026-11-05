using System.Windows;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 主页工具卡片 → 左侧快捷功能栏的拖拽数据（只传工具 Id，不传实例）。
///
/// <para>和拖文件互不干扰：用私有格式，<see cref="System.Windows.DragEventArgs.Data"/>
/// 里只有这一项时才是快捷栏认的拖拽。主页卡片发起时用 Copy 光标；侧栏内部重排用 Move，
/// 但落点逻辑一样（<see cref="MainViewModel.InsertOrMoveQuickTool"/> 自己会去重 / 重排）。</para>
/// </summary>
internal static class ToolDragData
{
    /// <summary>私有拖拽格式名（避免和资源管理器拖文件 / 其它工具的内部拖拽撞车）。</summary>
    public const string Format = "YuLiangToolkit.QuickSidebarToolId";

    /// <summary>打包一个「工具 Id」拖拽数据。</summary>
    public static DataObject Create(string toolId) => new(Format, toolId);

    /// <summary>从拖拽事件里取工具 Id；不是本格式 / 内容为空时返回 null。</summary>
    public static string? GetToolId(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(Format)) return null;
        return e.Data.GetData(Format) as string is { Length: > 0 } id ? id : null;
    }
}
