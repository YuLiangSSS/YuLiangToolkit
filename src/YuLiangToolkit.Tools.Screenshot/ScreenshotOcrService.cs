using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>
/// 系统 OCR（Windows.Media.Ocr）：把截图区域编码成 PNG，交给系统已安装的 OCR 语言包识别。
/// 识别结果只复制文本，不改截图本身。没有可用语言包时返回空串，由界面提示。
/// </summary>
public static class ScreenshotOcrService
{
    /// <summary>识别一张图片里的文字；异常 / 无语言包时返回空串。</summary>
    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        try
        {
            var png = ScreenCaptureService.EncodePng(image);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return "";
            var result = await engine.RecognizeAsync(software);
            return result.Text?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
