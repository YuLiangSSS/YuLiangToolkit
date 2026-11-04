using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MediaEditor.ViewModels;
using YuLiangToolkit.Tools.MediaEditor.Views;

namespace YuLiangToolkit.Tools.MediaEditor;

/// <summary>
/// 「剪辑工具」：简易视频 / GIF 剪辑（裁剪 + 多片段保留拼接 + 生成低清预览）。
/// <para>不做逐帧实时预览；用时间轴缩略图定位、生成低清预览片段确认效果。</para>
/// </summary>
public class MediaEditorTool : ITool
{
    public string Id => "media-editor";

    public string Name => "剪辑工具";

    public string Description
        => "简易视频 / GIF 剪辑：生成时间轴缩略图，设起点 / 终点，多个保留片段可排序拼接，"
           + "导出 MP4 / MKV / MOV / AVI / WebM 或 GIF；不能实时预览，但可以一键生成低清预览片段看效果。";

    public string EmojiIcon => "✂️";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "剪辑", "视频剪辑", "GIF剪辑", "裁剪", "截取", "拼接", "多片段", "预览", "FFmpeg", "媒体",
    };

    /// <summary>排在录屏后面。</summary>
    public int Order => 12;

    public FrameworkElement CreateView()
        => new MediaEditorView { DataContext = new MediaEditorViewModel() };
}
