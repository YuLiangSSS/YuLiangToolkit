using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Launcher.Models;

namespace YuLiangToolkit.Tools.Launcher.Services;

/// <summary>
/// 启动器的图标加载 / 自定义图标缓存。
///
/// <para><b>两种来源</b>：</para>
/// <list type="number">
///   <item><b>程序自己的图标</b>：<c>.exe</c> / <c>.lnk</c> 用 <c>SHGetFileInfo</c> 取系统大图标
///         （对 .lnk 会拿到 Windows 解析后的目标图标，跟资源管理器里看到的一致）。
///         先尝试 <c>PrivateExtractIcons</c> 取 256×256，失败再退回 <c>SHGetFileInfo</c> 兜底；</item>
///   <item><b>自定义图标文件</b>：支持 <c>.ico / .exe / .dll / .png / .jpg / .jpeg / .bmp</c>；
///         导入时统一编码成 PNG 存进 <c>Data\launcher\icons\&lt;安全 Id&gt;.png</c>，
///         这样原图标文件删掉 / 移动后启动器里的图标照样在。</item>
/// </list>
///
/// <para><b>线程</b>：Shell / GDI 调用与 WPF 位图解码都通过 <see cref="StaRunner"/> 在 STA 线程上执行；
/// 返回的 <see cref="BitmapSource"/> 一律 <c>Freeze()</c>，所以可以安全地从后台线程交给界面绑定。</para>
///
/// <para><b>不抛异常</b>：任何 API 失败（文件不在 / 不是图标 / Shell 拒绝 / 没有写权限）都返回
/// <c>null</c> / <c>false</c>，界面自己显示占位图标或提示。</para>
/// </summary>
public sealed class IconLoader
{
    /// <summary>自定义图标统一按最长边 256 像素保存 / 提取（界面最大只用 64，留足高分屏余量）。</summary>
    private const int MaxIconSize = 256;

    private const int ShgfiIcon = 0x00000100;
    private const int ShgfiLargeIcon = 0x00000000;
    private const int ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    private readonly ToolStorage _storage;

    /// <summary>图标缓存：键 <c>custom:相对路径</c> / <c>shell:完整路径</c>。
    /// <para>⚠ <see cref="ConcurrentDictionary{TKey,TValue}"/> 不允许 null 值，所以只缓存成功结果；
    /// 失败（文件不在 / 读不出图标）不写缓存，下次刷新会再试一次。</para></summary>
    private readonly ConcurrentDictionary<string, BitmapSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>创建图标加载器。</summary>
    /// <param name="storage">该功能的数据文件夹（启动器是 <c>Data\launcher</c>），自定义图标写在这里。</param>
    public IconLoader(ToolStorage storage)
        => _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    // ---------------------------------------------------------------- 对界面暴露的入口

    /// <summary>
    /// 取一个启动项当前应该显示的图标：有自定义图标优先用自定义，否则取程序 / 快捷方式的系统图标。
    /// </summary>
    /// <param name="item">配置项；为 null 返回 null。</param>
    /// <param name="forceReload">true = 跳过缓存重新读（刚换过自定义图标时用）。</param>
    public BitmapSource? LoadForItem(LauncherItemConfig? item, bool forceReload = false)
    {
        if (item is null) return null;
        try
        {
            if (item.HasCustomIcon)
            {
                var custom = LoadCustomIcon(item.CustomIconPath, forceReload);
                if (custom is not null) return custom;
                // 自定义图标文件丢了（被手动删掉）：静默回退到程序自己的图标。
            }
            return LoadShellIcon(item.Path, forceReload);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取一个 <c>.exe</c> / <c>.lnk</c> 的系统大图标；文件不存在返回 null。</summary>
    public BitmapSource? LoadShellIcon(string? path, bool forceReload = false)
    {
        try
        {
            var normalized = LauncherRules.NormalizePath(path);
            if (normalized.Length == 0) return null;

            var key = "shell:" + normalized;
            if (!forceReload && _cache.TryGetValue(key, out var cached)) return cached;

            var icon = StaRunner.Run(() => ExtractShellIconCore(normalized));
            if (icon is not null) _cache[key] = icon;
            return icon;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取已经存在数据文件夹里的自定义图标（相对路径）；路径不安全 / 文件不在返回 null。</summary>
    public BitmapSource? LoadCustomIcon(string? relativePath, bool forceReload = false)
    {
        try
        {
            if (!LauncherRules.IsSafeRelativePath(relativePath)) return null;

            var relative = relativePath!.Trim();
            var key = "custom:" + relative;
            if (!forceReload && _cache.TryGetValue(key, out var cached)) return cached;

            var fullPath = _storage.PathOf(relative);
            var icon = StaRunner.Run(() => DecodeImageCore(fullPath));
            if (icon is not null) _cache[key] = icon;
            return icon;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把用户选中的图标文件转成 PNG 存进 <c>Data\launcher\icons\&lt;安全 Id&gt;.png</c>，
    /// 成功返回落盘后的相对路径（可直接写进 <see cref="LauncherItemConfig.CustomIconPath"/>）。
    /// </summary>
    /// <param name="sourceFile">用户选中的图标文件（.ico / .exe / .dll / .png / .jpg / .jpeg / .bmp）。</param>
    /// <param name="itemId">启动项 Id（决定文件名，安全字符以外会被替换成 <c>-</c>）。</param>
    public string? SaveCustomIcon(string? sourceFile, string? itemId)
    {
        try
        {
            var normalized = LauncherRules.NormalizePath(sourceFile);
            if (normalized.Length == 0) return null;

            // 把「从文件取图标」与「编码成 PNG」分成两步：前者必须 STA，后者纯内存操作。
            var icon = StaRunner.Run(() => LoadSourceIconCore(normalized));
            if (icon is null) return null;

            var relative = LauncherRules.IconRelativePath(itemId);
            if (!LauncherRules.IsSafeRelativePath(relative)) return null;

            var png = EncodePng(icon);
            if (png is null || png.Length == 0) return null;
            if (_storage.WriteBytes(relative, png) is null) return null;

            // 同一个启动项换图标时文件名不变：清掉旧缓存，下次一定读新文件。
            _cache.TryRemove("custom:" + relative, out _);
            return relative;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除某个自定义图标文件（不存在也算成功）；路径不安全返回 false。</summary>
    public bool DeleteCustomIcon(string? relativePath)
    {
        try
        {
            if (!LauncherRules.IsSafeRelativePath(relativePath)) return false;
            _cache.TryRemove("custom:" + relativePath, out _);
            return _storage.Delete(relativePath!);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清空图标缓存（图标文件被外部替换时用；目前「刷新状态」会连带清一次）。</summary>
    public void ClearCache() => _cache.Clear();

    // ---------------------------------------------------------------- 取图标（STA 线程上执行）

    /// <summary>从 <c>.exe / .dll / .ico</c> 或 <c>.lnk</c> 取系统图标：先试 256px，失败退回 SHGetFileInfo。</summary>
    private static BitmapSource? ExtractShellIconCore(string path)
    {
        try
        {
            var extension = LauncherRules.ExtensionOf(path).ToLowerInvariant();
            if (extension is ".exe" or ".dll" or ".ico")
            {
                var large = ExtractLargeIconWithPrivateExtractIcons(path, MaxIconSize);
                if (large is not null) return large;
            }
            return ExtractSystemIconWithShGetFileInfo(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>自定义图标源文件 → <see cref="BitmapSource"/>（.exe/.dll 取系统图标，图片直接解码）。</summary>
    private static BitmapSource? LoadSourceIconCore(string path)
    {
        try
        {
            var extension = LauncherRules.ExtensionOf(path).ToLowerInvariant();
            return extension switch
            {
                ".exe" or ".dll" => ExtractShellIconCore(path),
                ".ico" => DecodeImageCore(path) ?? ExtractShellIconCore(path),
                ".png" or ".jpg" or ".jpeg" or ".bmp" => DecodeImageCore(path),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 <c>SHGetFileInfo(SHGFI_ICON | SHGFI_LARGEICON)</c> 取系统图标（任何文件类型都能拿到一个）。</summary>
    private static BitmapSource? ExtractSystemIconWithShGetFileInfo(string path)
    {
        var info = new SHFILEINFO { szDisplayName = "", szTypeName = "" };
        var flags = ShgfiIcon | ShgfiLargeIcon;
        if (!File.Exists(path) && !Directory.Exists(path)) flags |= ShgfiUseFileAttributes;

        try
        {
            var result = SHGetFileInfo(
                path,
                FileAttributeNormal,
                ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                (uint)flags);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                return ToBitmapSource(info.hIcon);
            }
            finally
            {
                DestroyIcon(info.hIcon); // 必须销毁，否则每次扫描都会泄漏一个图标句柄
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 <c>PrivateExtractIcons</c> 取 256px 大图标（.lnk 不支持，调用方会兜底）。</summary>
    private static BitmapSource? ExtractLargeIconWithPrivateExtractIcons(string path, int size)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var icons = new IntPtr[1];
            var iconIds = new uint[1];
            var count = PrivateExtractIcons(path, 0, size, size, icons, iconIds, 1, 0);
            if (count == 0 || icons[0] == IntPtr.Zero) return null;

            try
            {
                return ToBitmapSource(icons[0]);
            }
            finally
            {
                DestroyIcon(icons[0]);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>HICON → 已冻结的 WPF 位图（可跨线程）。</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        var source = Imaging.CreateBitmapSourceFromHIcon(
            hIcon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    /// <summary>解码 ico / png / jpg / bmp：多帧（.ico）取最大的一帧，并缩到 256 像素以内。</summary>
    private static BitmapSource? DecodeImageCore(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            // OnLoad：读完立刻放开文件句柄，用户换图标后不会因为“文件被占用”删不掉旧文件。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            BitmapSource? best = null;
            foreach (var frame in decoder.Frames)
            {
                var frameArea = (long)frame.PixelWidth * frame.PixelHeight;
                var bestArea = best is null ? -1 : (long)best.PixelWidth * best.PixelHeight;
                if (frameArea > bestArea) best = frame;
            }
            if (best is null) return null;

            var frozen = best.Clone();
            frozen.Freeze();
            return Downscale(frozen, MaxIconSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把大图（例如 4K 的 png 图标）缩到最长边 <paramref name="maxSize"/> 像素。</summary>
    private static BitmapSource Downscale(BitmapSource source, int maxSize)
    {
        try
        {
            if (source.PixelWidth <= maxSize && source.PixelHeight <= maxSize) return source;

            var scale = Math.Min(
                (double)maxSize / Math.Max(1, source.PixelWidth),
                (double)maxSize / Math.Max(1, source.PixelHeight));
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            return transformed;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>把位图编码成 PNG 字节；失败返回 null。</summary>
    private static byte[]? EncodePng(BitmapSource source)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string lpszFile,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
