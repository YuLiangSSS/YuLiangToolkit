using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.MediaEditor.ViewModels;

namespace YuLiangToolkit.Tools.MediaEditor.Views;

/// <summary>
/// 剪辑工具页面：除了按钮选文件，也支持把视频 / GIF 文件直接拖进页面载入。
/// <para>落点是整个 <see cref="UserControl"/>（<c>AllowDrop="True"</c>）；拖一堆文件时只载入第一个能认的，
/// 不支持的类型 <c>DragOver</c> 直接显示禁止光标（<see cref="DragDropEffects.None"/>）。</para>
/// </summary>
public partial class MediaEditorView : UserControl
{
    public MediaEditorView()
    {
        InitializeComponent();
    }

    /// <summary>视图模型（由外壳通过 DataContext 注入；还没注入时拖放直接忽略）。</summary>
    private MediaEditorViewModel? ViewModel => DataContext as MediaEditorViewModel;

    /// <summary>拖动经过页面：只有「真实存在且后缀受支持」的文件才显示可放下（复制）光标。</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = TryGetDroppedInput(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    /// <summary>放手：把第一个能接受的文件交给 ViewModel，走和「选择视频 / GIF…」相同的载入链路。</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = TryGetDroppedInput(e);
        if (path is null || ViewModel is not { } viewModel) return;
        _ = viewModel.OpenDroppedFileAsync(path);
    }

    /// <summary>
    /// 拖放数据里第一个能接受的视频 / GIF（拖一堆文件时只取第一个能认的）。
    /// <para>只用静态的 <see cref="MediaEditorViewModel.CanAcceptDrop"/> 判断，<b>不依赖 ViewModel 是否已注入</b>，
    /// 这样鼠标还在拖动时就能正确显示可放下 / 禁止光标；ViewModel 是否为空放到 Drop 时再判断。</para>
    /// </summary>
    private static string? TryGetDroppedInput(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return null;
        return paths.FirstOrDefault(MediaEditorViewModel.CanAcceptDrop);
    }
}
