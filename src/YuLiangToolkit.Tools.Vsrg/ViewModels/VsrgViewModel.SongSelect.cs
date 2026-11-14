using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Vsrg.Core;

namespace YuLiangToolkit.Tools.Vsrg.ViewModels;

/// <summary>
/// 「VSRG」的选歌页命令：导入 / 删除 / 刷新 / 打开文件夹 / 开始游玩。
/// </summary>
public sealed partial class VsrgViewModel
{
    /// <summary>导入 .osz（点按钮选文件）。</summary>
    public AsyncRelayCommand ImportCommand { get; private set; } = null!;

    /// <summary>删除当前选中的谱面集。</summary>
    public RelayCommand DeleteSongCommand { get; private set; } = null!;

    /// <summary>重新扫描谱面集。</summary>
    public RelayCommand RefreshLibraryCommand { get; private set; } = null!;

    /// <summary>在资源管理器里打开 Mapset 根目录。</summary>
    public RelayCommand OpenMapsetFolderCommand { get; private set; } = null!;

    /// <summary>在资源管理器里定位当前谱面集。</summary>
    public RelayCommand RevealSongCommand { get; private set; } = null!;

    /// <summary>开始游玩。</summary>
    public RelayCommand StartPlayCommand { get; private set; } = null!;

    /// <summary>去设置页。</summary>
    public RelayCommand GoSettingsCommand { get; private set; } = null!;

    /// <summary>回选歌页。</summary>
    public RelayCommand GoSongSelectCommand { get; private set; } = null!;

    private void InitializeSongSelectCommands()
    {
        ImportCommand = new AsyncRelayCommand(async _ =>
        {
            var files = PickOszFiles();
            if (files.Count > 0) await ImportFilesAsync(files);
        }, _ => !IsImporting);

        DeleteSongCommand = new RelayCommand(_ => ConfirmDeleteSelectedSong(),
            _ => SelectedSong is not null && !IsImporting);

        RefreshLibraryCommand = new RelayCommand(_ =>
        {
            RefreshLibrary();
            ImportStatus = "";
        }, _ => !IsImporting);

        OpenMapsetFolderCommand = new RelayCommand(_ => OpenMapsetFolder(), _ => !IsImporting);

        RevealSongCommand = new RelayCommand(_ => RevealSelectedSong(), _ => SelectedSong is not null);

        StartPlayCommand = new RelayCommand(_ => StartPlay(), _ => CanStartPlay);

        GoSettingsCommand = new RelayCommand(_ => GoToSettings());
        GoSongSelectCommand = new RelayCommand(_ => GoToSongSelect());
    }

    /// <summary>选 .osz 文件（可以多选）。没有文件对话框可用时返回空列表。</summary>
    private static IReadOnlyList<string> PickOszFiles()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 osu! 谱面集（.osz）",
                Filter = "osu! 谱面集 (*.osz)|*.osz|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };

            var owner = System.Windows.Application.Current?.MainWindow;
            var ok = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return ok == true ? dialog.FileNames : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Vsrg] 打开文件对话框失败：{ex}");
            return Array.Empty<string>();
        }
    }

    /// <summary>删除当前谱面集（二次确认）。</summary>
    private void ConfirmDeleteSelectedSong()
    {
        if (SelectedSong is null) return;

        var answer = System.Windows.MessageBox.Show(
            $"删除谱面集「{SelectedSong.TitleLine}」？\n\n只删这一个文件夹（含里面的谱面 / 音频 / 背景图），删了就找不回来。",
            "删除谱面集",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        DeleteSelectedSong();
    }

    // ---------------------------------------------------------------- 页面切换

    /// <summary>去设置页（游玩中先去不了：界面上的按钮只在选歌页出现）。</summary>
    public void GoToSettings()
    {
        if (IsPlayRunning) return;
        Page = VsrgPage.Settings;
    }

    /// <summary>回选歌页。</summary>
    public void GoToSongSelect()
    {
        if (IsPlayRunning) return;
        Page = VsrgPage.SongSelect;
    }

    /// <summary>选一项之后刷新依赖它的命令可用状态。</summary>
    private void RefreshStartCommand() => CommandManager.InvalidateRequerySuggested();
}
