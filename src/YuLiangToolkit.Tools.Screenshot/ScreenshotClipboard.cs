using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>剪贴板写入小封装：优先 PNG 自定义格式（保留透明 / 质量），再退回标准位图。</summary>
public static class ScreenshotClipboard
{
    /// <summary>
    /// 把图片写入剪贴板。剪贴板被其它程序占用时重试几次；
    /// 返回是否成功，失败原因由调用方提示。
    /// </summary>
    public static bool TrySetImage(BitmapSource image, out string error)
    {
        error = "";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                var data = new DataObject();
                var png = ScreenCaptureService.EncodePng(image);
                data.SetData("PNG", new MemoryStream(png));
                data.SetImage(image);
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException)
            {
                if (attempt < 5) Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        error = "剪贴板正被其它程序占用，请稍后再试";
        return false;
    }

    /// <summary>把文本写入剪贴板（颜色值 / 路径）。</summary>
    public static bool TrySetText(string text, out string error)
    {
        error = "";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                if (attempt < 5) Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        error = "剪贴板正被其它程序占用，请稍后再试";
        return false;
    }
}
