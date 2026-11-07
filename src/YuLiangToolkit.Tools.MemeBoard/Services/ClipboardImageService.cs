using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.MemeBoard.Services;

/// <summary>
/// 剪贴板图片读写。
///
/// <para><b>线程</b>：WPF 剪贴板 API 必须在 <b>STA / UI 线程</b>调用（ViewModel 里通过
/// <c>Ui(...)</c> / <c>UiAsync(...)</c> 切过去），本类只做「失败重试 + 编码」。</para>
///
/// <para><b>为什么要重试</b>：剪贴板是系统全局资源，别的程序（QQ、输入法、截图工具）可能正好
/// 占着它，<c>Clipboard.SetImage</c> 会抛 <see cref="COMException"/>；实测等几十毫秒再试就能成功，
/// 所以统一重试 10 次 × 50ms。</para>
///
/// <para><b>GIF 说明</b>：剪贴板与 <see cref="PngBitmapEncoder"/> 都只保存静态图；收藏的 GIF
/// 复制到 QQ 时只有第一帧（这是系统剪贴板的限制，不是本工具的 bug）。</para>
/// </summary>
public static class ClipboardImageService
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 50;

    /// <summary>
    /// 把图片放到剪贴板（必须在 UI 线程调用）。失败最多重试 10 次，全部失败返回 <c>false</c>。
    /// </summary>
    public static bool TrySetImage(BitmapSource? image)
    {
        if (image is null) return false;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (COMException)
            {
                // 剪贴板被别的程序占用：等一下再试
            }
            catch (ExternalException)
            {
                // 同 COMException（ExternalException 是它的基类分支）
            }

            if (attempt < MaxAttempts - 1) Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    /// <summary>
    /// 读剪贴板里的图片（必须在 UI 线程调用）；剪贴板里没有图片 / 读失败返回 <c>null</c>。
    /// </summary>
    public static BitmapSource? TryGetImage()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsImage()) return null;
                var image = Clipboard.GetImage();
                if (image is null) return null;
                if (image.CanFreeze) image.Freeze();
                return image;
            }
            catch (COMException)
            {
                // 剪贴板被占用，重试
            }
            catch (ExternalException)
            {
                // 同上
            }

            if (attempt < MaxAttempts - 1) Thread.Sleep(RetryDelayMs);
        }
        return null;
    }

    /// <summary>
    /// 在 UI 线程把一张图片文件解码后放进剪贴板。
    ///
    /// <para><b>为什么必须在这里解码</b>：实测在后台线程解码 + Freeze 出来的
    /// <see cref="BitmapSource"/>，即使在本进程读得到、<c>Clipboard.ContainsImage()</c> 也为 true，
    /// 但跨进程（QQ）粘贴时是坏数据（CLIPBRD_E_BAD_DATA），QQ 收不到图。
    /// 改成 UI 线程新建 <see cref="BitmapImage"/> 再 <c>Clipboard.SetImage</c>，QQ 就能正常粘贴。</para>
    /// </summary>
    /// <returns>成功返回 true；失败时 <paramref name="error"/> 写明原因。</returns>
    public static bool TrySetImageFromFile(string? fullPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            error = "图片文件不存在";
            return false;
        }

        BitmapSource? image;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;       // 整帧读进内存，不锁文件
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            if (bitmap.CanFreeze) bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception ex)
        {
            error = "图片解码失败：" + ex.Message;
            return false;
        }

        if (TrySetImage(image)) return true;
        error = "剪贴板被其它程序占用";
        return false;
    }

    /// <summary>
    /// 把位图编码成 PNG 字节（可直接写进数据文件夹）；失败返回 <c>null</c>。
    /// </summary>
    public static byte[]? EncodePng(BitmapSource? image)
    {
        if (image is null) return null;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
