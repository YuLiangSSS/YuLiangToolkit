using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.Compressor;
using YuLiangToolkit.Tools.Compressor.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>「压缩器」自检：工具元数据 / 文件类型识别 / 预设 / FFmpeg 参数 / 输出命名 / ffprobe 解析。</summary>
internal static class CompressorChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;
        CheckFn c = (name, ok, detail) => check(name, ok, detail);
        CheckMetadata(c);
        CheckFiles(c);
        CheckPresets(c);
        CheckArguments(c);
        CheckOutput(c);
        CheckProbe(c);
        CheckImageCompression(c);
    }

    private static void CheckMetadata(CheckFn c)
    {
        var tool = new CompressorTool();
        c("[压缩器] 工具元数据：Id / 名称 / 分类 / 排序",
            tool.Id == "compressor" && tool.Name == "压缩器" && tool.Category == "文件" && tool.Order == 15,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");
        c("[压缩器] 分类路径为「文件」，Emoji 为 🗜️",
            tool.CategoryPath.Count == 1 && tool.CategoryPath[0] == "文件" && tool.EmojiIcon == "🗜️",
            $"{string.Join("/", tool.CategoryPath)} / {tool.EmojiIcon}");
        c("[压缩器] 标签覆盖图片 / 音频 / 视频 / FFmpeg",
            new[] { "图片压缩", "音频压缩", "视频压缩", "FFmpeg" }.All(tool.Tags.Contains),
            string.Join(" / ", tool.Tags));
    }

    private static void CheckFiles(CheckFn c)
    {
        c("[压缩器] 图片类型识别",
            CompressorFiles.DetectKind("a.PNG") == CompressorFileKind.Image
            && CompressorFiles.DetectKind("b.webp") == CompressorFileKind.Image
            && CompressorFiles.DetectKind("c.gif") == CompressorFileKind.Image);
        c("[压缩器] 音频类型识别",
            CompressorFiles.DetectKind("a.flac") == CompressorFileKind.Audio
            && CompressorFiles.DetectKind("b.M4A") == CompressorFileKind.Audio
            && CompressorFiles.DetectKind("c.wav") == CompressorFileKind.Audio);
        c("[压缩器] 视频类型识别 / 未知扩展名",
            CompressorFiles.DetectKind("a.MKV") == CompressorFileKind.Video
            && CompressorFiles.DetectKind("b.mp4") == CompressorFileKind.Video
            && !CompressorFiles.IsSupported("readme.txt"));
    }

    private static void CheckPresets(CheckFn c)
    {
        c("[压缩器] 六档预设与质量递减",
            CompressorProfiles.All.Count == 6
            && CompressorProfiles.Get(CompressorProfiles.Light).ImageQuality
               > CompressorProfiles.Get(CompressorProfiles.Extreme).ImageQuality
            && CompressorProfiles.Get(CompressorProfiles.Ultra).ImageQuality
               > CompressorProfiles.Get(CompressorProfiles.TotalLoss).ImageQuality
            && CompressorProfiles.Get("bad").Key == CompressorProfiles.Standard,
            string.Join(" / ", CompressorProfiles.All.Select(p => p.Name)));
        c("[压缩器] 预设音频码率 / 视频 CRF 有效",
            CompressorProfiles.All.All(p => p.AudioBitrateKbps > 0 && p.VideoCrf is >= 0 and <= 51),
            string.Join(" / ", CompressorProfiles.All.Select(p => $"{p.AudioBitrateKbps}k·CRF{p.VideoCrf}")));
    }

    private static void CheckArguments(CheckFn c)
    {
        var preset = CompressorProfiles.Get(CompressorProfiles.Standard);
        var audio = CompressorArguments.BuildAudio("in.flac", "out.mp3", preset, "mp3");
        var video = CompressorArguments.BuildVideo("in.mkv", "out.mp4", preset);
        var ultraVideo = CompressorArguments.BuildVideo("in.mkv", "out.mp4", CompressorProfiles.Get(CompressorProfiles.Ultra));
        c("[压缩器] 音频 FFmpeg 参数包含 MP3 编码与码率",
            audio.Contains("libmp3lame") && audio.Contains($"{preset.AudioBitrateKbps}k")
            && audio.Contains("-vn") && audio.Contains("-progress"),
            string.Join(" ", audio));
        c("[压缩器] 视频 FFmpeg 参数包含 H.264 / CRF / AAC",
            video.Contains("libx264") && video.Contains("-crf") && video.Contains(preset.VideoCrf.ToString())
            && video.Contains("aac") && video.Contains("+faststart"),
            string.Join(" ", video));
        c("[压缩器] 整活档视频带缩放滤镜",
            ultraVideo.Contains("-vf") && ultraVideo.Contains("scale=-2:720"),
            string.Join(" ", ultraVideo));
        c("[压缩器] FFmpeg 进度行解析",
            CompressorArguments.ParseProgressSeconds("out_time_us=2500000") == 2.5
            && CompressorArguments.ParseProgressSeconds("progress=end") is null,
            "out_time_us=2500000");
    }

    private static void CheckOutput(CheckFn c)
    {
        var dir = SelfCheckScratch.MakeDirectory("compressor-output");
        var first = CompressorFiles.BuildOutputPath(Path.Combine(dir, "示例.mp4"), dir, "_压缩", ".mp4", overwrite: false);
        File.WriteAllText(first, "x");
        var second = CompressorFiles.BuildOutputPath(Path.Combine(dir, "示例.mp4"), dir, "_压缩", ".mp4", overwrite: false);
        c("[压缩器] 输出命名带后缀并自动避让重名",
            Path.GetFileName(first) == "示例_压缩.mp4" && Path.GetFileName(second) == "示例_压缩-1.mp4",
            $"{Path.GetFileName(first)} / {Path.GetFileName(second)}");
        c("[压缩器] 图片 / 音频 / 视频输出后缀正确",
            CompressorFiles.OutputExtension(CompressorFileKind.Image, "jpg", "mp3", "mp4") == ".jpg"
            && CompressorFiles.OutputExtension(CompressorFileKind.Audio, "jpg", "m4a", "mp4") == ".m4a"
            && CompressorFiles.OutputExtension(CompressorFileKind.Video, "png", "ogg", "mp4") == ".mp4",
            "jpg / m4a / mp4");
    }

    private static void CheckProbe(CheckFn c)
    {
        const string json = """
        {"streams":[
          {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"duration":"12.5"},
          {"codec_type":"audio","codec_name":"aac"}
        ],"format":{"duration":"12.5"}}
        """;
        var info = CompressorProbe.ParseProbeJson(json);
        c("[压缩器] ffprobe JSON 解析分辨率 / 时长 / 音轨",
            info is not null && info.Width == 1920 && info.Height == 1080
            && info.DurationSeconds == 12.5 && info.HasAudio && info.VideoCodec == "h264",
            info?.Summary ?? "null");
    }

    private static void CheckImageCompression(CheckFn c)
    {
        var done = false;
        var detail = "";
        var thread = new Thread(() =>
        {
            try
            {
                var dir = SelfCheckScratch.MakeDirectory("compressor-image");
                var input = Path.Combine(dir, "input.png");
                var output = Path.Combine(dir, "output.jpg");
                var bitmap = new WriteableBitmap(32, 24, 96, 96, PixelFormats.Bgra32, null);
                var pixels = new byte[32 * 24 * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = 220; pixels[i + 1] = 80; pixels[i + 2] = 30; pixels[i + 3] = 255;
                }
                bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 32, 24), pixels, 32 * 4, 0);
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(input)) encoder.Save(stream);

                CompressorImage.Compress(input, output, 70, "jpg");
                var small = Path.Combine(dir, "output-small.jpg");
                CompressorImage.Compress(input, small, 45, "jpg", 16);
                using var checkStream = File.OpenRead(small);
                var checkDecoder = BitmapDecoder.Create(checkStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                done = File.Exists(output) && new FileInfo(output).Length > 0
                       && checkDecoder.Frames[0].PixelWidth == 16 && checkDecoder.Frames[0].PixelHeight == 12;
                detail = done ? $"{new FileInfo(output).Length} bytes / small 16×12" : "output missing";
            }
            catch (Exception ex)
            {
                detail = ex.Message;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(8000);
        c("[压缩器] 图片压缩（WPF PNG → JPG）", done, detail);
    }
}
