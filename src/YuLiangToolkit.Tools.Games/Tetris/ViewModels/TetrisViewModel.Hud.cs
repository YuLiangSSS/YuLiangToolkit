using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的<b>HUD 自由布局</b>（partial 扩展）：每个组件（暂存 / 接下来 / 分数 / 等级 / 状态）
/// 都可以在<b>编辑模式</b>下拖到棋盘的任意位置，也能一键恢复默认。
///
/// <para>位置存成 0~1 的相对比例，窗口缩放后不会跑偏；存在 <c>Config\tetris\config.json</c>。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    private HudLayout _hudLayout = HudLayout.CreateDefault();
    private bool _hudEditMode;

    /// <summary>当前 HUD 布局（绑定给 HudCanvas）。</summary>
    public HudLayout HudLayout
    {
        get => _hudLayout;
        private set
        {
            _hudLayout = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是不是 HUD 编辑模式（可拖动）。</summary>
    public bool HudEditMode
    {
        get => _hudEditMode;
        set
        {
            if (!Set(ref _hudEditMode, value)) return;
            OnPropertyChanged(nameof(HudEditButtonText));
            OnPropertyChanged(nameof(HudEditHint));
        }
    }

    /// <summary>编辑按钮文字。</summary>
    public string HudEditButtonText => _hudEditMode ? "完成编辑" : "编辑 HUD";

    /// <summary>编辑模式提示。</summary>
    public string HudEditHint => _hudEditMode
        ? "拖动各组件摆到想要的位置，松手即保存；点「完成编辑」退出"
        : "点「编辑 HUD」后可以拖动暂存 / 接下来 / 分数等组件的位置";

    /// <summary>预览块数（1~8，同时也是房间设置）。</summary>
    public int PreviewSlots
    {
        get => Cfg.PreviewSlots;
        set
        {
            Cfg.PreviewSlots = Math.Clamp(value, 1, 8);
            _engine.PreviewSlots = Cfg.PreviewSlots;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextPieces));
            RefreshScore();
        }
    }

    /// <summary>暂存格数量（1~5；多个时按 C 轮流换出）。</summary>
    public int HoldSlotCount
    {
        get => Cfg.HoldSlots;
        set
        {
            Cfg.HoldSlots = Math.Clamp(value, 1, 5);
            _engine.HoldSlots = Cfg.HoldSlots;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HeldPieces));
        }
    }

    /// <summary>切换 HUD 编辑模式。</summary>
    public RelayCommand ToggleHudEditCommand { get; private set; } = null!;

    /// <summary>恢复默认 HUD 布局。</summary>
    public RelayCommand ResetHudLayoutCommand { get; private set; } = null!;

    /// <summary>初始化（主构造函数调用）。</summary>
    private void InitializeHud()
    {
        _hudLayout = HudLayout.Deserialize(Cfg.HudLayout);
        _engine.HoldSlots = Cfg.HoldSlots;
        _engine.PreviewSlots = Cfg.PreviewSlots;

        ToggleHudEditCommand = new RelayCommand(_ => HudEditMode = !HudEditMode);
        ResetHudLayoutCommand = new RelayCommand(_ =>
        {
            HudLayout = HudLayout.CreateDefault();
            SaveHudLayout();
            Log("HUD 布局已恢复默认");
        });
    }

    /// <summary>拖动结束后保存布局（界面调用）。</summary>
    public void OnHudMovedExternal(HudPart part, double x, double y) => OnHudMoved(part, x, y);

    /// <summary>拖动结束后保存布局。</summary>
    private void OnHudMoved(HudPart part, double x, double y)
    {
        _hudLayout.Set(part, x, y);
        OnPropertyChanged(nameof(HudLayout));
        SaveHudLayout();
        _ = part;
    }

    private void SaveHudLayout() => Cfg.HudLayout = _hudLayout.Serialize();
}
