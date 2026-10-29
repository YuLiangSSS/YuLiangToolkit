using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Kps.Models;

namespace YuLiangToolkit.Tools.Kps.ViewModels;

/// <summary>
/// KPS 工具的主界面 ViewModel（骨架版本；完整实现见后续提交）。
/// 负责把「全局输入 → 统计引擎 → 键位格子 / 折线图 / Overlay」串起来，并提供键位编辑、预设、外观设置等命令。
/// </summary>
public sealed class KpsViewModel : ToolViewModelBase
{
    public KpsViewModel() : base(KpsTool.ToolId)
    {
        Cfg = LoadConfig<KpsConfig>();
        LayoutRows = new List<KpsRowView>();
        StatusText = "就绪";
        Log($"数据目录：{Storage.Directory}");
        Log($"配置文件：{Cfg.FilePath}");
    }

    /// <summary>强类型配置（<c>Config\kps\config.json</c>，改一下自动保存）。</summary>
    public KpsConfig Cfg { get; }

    /// <summary>当前布局的界面行（主界面预览 + Overlay 共用）。</summary>
    public List<KpsRowView> LayoutRows { get; }

    /// <summary>当前 KPS 文本（骨架阶段固定 0）。</summary>
    public string KpsText => "0.0";
}
