using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Compressor.ViewModels;

namespace YuLiangToolkit.Tools.Compressor.Views;

/// <summary>压缩器页面代码后置：把拖入的路径交给 ViewModel。</summary>
public partial class CompressorView : UserControl
{
    public CompressorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CompressorViewModel viewModel) viewModel.OnPageLoaded();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not CompressorViewModel viewModel) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        viewModel.AddPaths(paths);
        e.Handled = true;
    }
}
