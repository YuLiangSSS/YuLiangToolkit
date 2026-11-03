using System.IO;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>嗅探到的资源类型（图片 / 音频 / 视频）。</summary>
public enum BrowserResourceKind
{
    Image,
    Audio,
    Video,
}

/// <summary>
/// 页面里嗅探到的一个资源（图片 / 音频 / 视频）。条目本身<b>不落盘</b>：
/// 属于「当前这次浏览」的临时结果，换页 / 重开浏览器就重新嗅。
/// </summary>
public sealed class BrowserResource : ObservableObject
{
    private string _mimeType = "";
    private int _width;
    private int _height;
    private long _sizeBytes;
    private string _title = "";

    /// <summary>绝对地址（http / https / file）。</summary>
    public string Url { get; set; } = "";

    /// <summary>图片 / 音频 / 视频。</summary>
    public BrowserResourceKind Kind { get; set; }

    /// <summary>响应的 MIME（CDP 嗅探才有；爬虫抓到的为空）。</summary>
    public string MimeType
    {
        get => _mimeType;
        set => Set(ref _mimeType, value ?? "");
    }

    /// <summary>图片 / 视频宽（未知 0）。</summary>
    public int Width
    {
        get => _width;
        set => Set(ref _width, Math.Max(0, value));
    }

    /// <summary>图片 / 视频高（未知 0）。</summary>
    public int Height
    {
        get => _height;
        set => Set(ref _height, Math.Max(0, value));
    }

    /// <summary>大小（字节，未知 0）。</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (Set(ref _sizeBytes, Math.Max(0, value))) OnPropertyChanged(nameof(SizeText));
        }
    }

    /// <summary>标题 / alt（可能为空）。</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value ?? "");
    }

    /// <summary>来源：<c>network</c>（CDP 网络嗅探）/ <c>page</c>（网页内爬虫）。</summary>
    public string Source { get; set; } = "network";

    /// <summary>CDP 的 requestId（保存原图 / 原视频时用它取响应体；爬虫抓到的为空）。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>嗅探时间。</summary>
    public DateTime FoundAt { get; set; }

    /// <summary>类型中文名。</summary>
    [JsonIgnore]
    public string KindText => Kind switch
    {
        BrowserResourceKind.Image => "图片",
        BrowserResourceKind.Audio => "音频",
        _ => "视频",
    };

    /// <summary>类型小图标。</summary>
    [JsonIgnore]
    public string KindGlyph => Kind switch
    {
        BrowserResourceKind.Image => "🖼️",
        BrowserResourceKind.Audio => "🎵",
        _ => "🎬",
    };

    /// <summary>大小文本（未知时为空）。</summary>
    [JsonIgnore]
    public string SizeText => SizeBytes > 0 ? FileSize.Format(SizeBytes) : "";

    /// <summary>尺寸文本，例如 <c>1920×1080</c>（未知为空）。</summary>
    [JsonIgnore]
    public string DimensionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";

    /// <summary>列表显示名：优先文件名，没有就用地址。</summary>
    [JsonIgnore]
    public string DisplayName => BrowserResources.GetDisplayName(Url);

    public override string ToString() => $"{Kind}: {DisplayName} <{Url}>";
}

/// <summary>
/// 资源嗅探的纯逻辑（自检 <c>browser</c> 组直接断言）：按 MIME / 扩展名判断类型、去重合并、文件名。
/// 负责真正抓取的是 BrowserView：CDP 的 <c>Network.responseReceived</c>（网络嗅探）
/// + 网页内 JS 爬虫（DOM / performance 兜底）。
/// </summary>
public static class BrowserResources
{
    /// <summary>每个标签最多保留多少条（防止长视频网站刷出几万条分段把内存吃掉）。</summary>
    public const int MaxEntries = 500;

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".svg", ".ico" };

    private static readonly string[] AudioExtensions =
        { ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac", ".opus", ".wma" };

    private static readonly string[] VideoExtensions =
        { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".flv", ".m4v", ".m3u8", ".mpd", ".ts" };

    /// <summary>
    /// 按 MIME + 地址扩展名判断资源类型；不是图片 / 音频 / 视频返回 <c>null</c>。
    /// MIME 优先（CDP 会给准确 mime），没有 mime 时看扩展名。
    /// </summary>
    public static BrowserResourceKind? DetectKind(string? mimeType, string? url)
    {
        var mime = mimeType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        if (mime.StartsWith("image/", StringComparison.Ordinal)) return BrowserResourceKind.Image;
        if (mime.StartsWith("audio/", StringComparison.Ordinal)) return BrowserResourceKind.Audio;
        if (mime.StartsWith("video/", StringComparison.Ordinal)) return BrowserResourceKind.Video;
        // HLS / DASH 播放列表：mime 常见 application/vnd.apple.mpegurl / application/dash+xml
        if (mime is "application/vnd.apple.mpegurl" or "application/x-mpegurl"
            or "audio/mpegurl" or "audio/x-mpegurl" or "application/dash+xml")
        {
            return BrowserResourceKind.Video;
        }

        var extension = GetExtension(url);
        if (ImageExtensions.Contains(extension, StringComparer.Ordinal)) return BrowserResourceKind.Image;
        if (AudioExtensions.Contains(extension, StringComparer.Ordinal)) return BrowserResourceKind.Audio;
        if (VideoExtensions.Contains(extension, StringComparer.Ordinal)) return BrowserResourceKind.Video;
        return null;
    }

    /// <summary>
    /// 是不是可以嗅探 / 保存的地址：只接受 <c>http:</c> / <c>https:</c> / <c>file:</c>。
    /// <c>data:</c>（网页内联图标 / base64 图片，往往巨大且没意义）、<c>blob:</c>、<c>about:</c> 一律跳过。
    /// </summary>
    public static bool IsSupportedUrl(string? url)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" or "file";
    }

    /// <summary>取地址路径的小写扩展名（含点）；没有 / 解析失败返回空串。</summary>
    public static string GetExtension(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var path = url.Trim();
        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) path = path[..cut];
        try
        {
            return Path.GetExtension(path).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>列表显示名：优先 URL 里的文件名（URL 解码），没有就用主机 + 路径，再不行用原地址。</summary>
    public static string GetDisplayName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url.Trim();

        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
        return string.IsNullOrWhiteSpace(uri.Host) ? url.Trim() : uri.Host + path;
    }

    /// <summary>
    /// 合并一条嗅探结果：同地址只保留一条，但用新信息补全（mime / 大小 / requestId / 尺寸 / 标题）。
    /// 返回是否是新条目；新条目插到最前面，超过上限从末尾裁掉。
    /// </summary>
    public static bool Merge(IList<BrowserResource> list, BrowserResource? incoming, int max = MaxEntries)
    {
        if (incoming is null) return false;
        var url = incoming.Url?.Trim() ?? "";
        if (url.Length == 0 || !IsSupportedUrl(url)) return false;
        if (url.Contains("favicon.ico", StringComparison.OrdinalIgnoreCase)) return false;
        incoming.Url = url;

        var detected = DetectKind(incoming.MimeType, url);
        if (detected is not null) incoming.Kind = detected.Value;

        for (var i = 0; i < list.Count; i++)
        {
            var existing = list[i];
            if (!string.Equals(existing.Url, url, StringComparison.Ordinal)) continue;

            if (!string.IsNullOrWhiteSpace(incoming.MimeType)) existing.MimeType = incoming.MimeType;
            if (incoming.SizeBytes > 0) existing.SizeBytes = incoming.SizeBytes;
            if (incoming.Width > 0) existing.Width = incoming.Width;
            if (incoming.Height > 0) existing.Height = incoming.Height;
            if (!string.IsNullOrWhiteSpace(incoming.Title)) existing.Title = incoming.Title;
            if (!string.IsNullOrWhiteSpace(incoming.RequestId)) existing.RequestId = incoming.RequestId;
            if (incoming.Source == "network") existing.Source = "network";
            return false;
        }

        list.Insert(0, incoming);
        if (list.Count > Math.Max(1, max)) list.RemoveAt(list.Count - 1);
        return true;
    }

    /// <summary>
    /// 网页内爬虫脚本：收集 &lt;img&gt; / &lt;picture&gt; / &lt;video&gt; / &lt;audio&gt; / CSS 背景图 /
    /// 指向媒体文件的链接 / performance 已加载资源。返回 <c>[{url,kind,width,height,title}]</c>。
    /// 用来补「打开嗅探面板时页面已经加载完、CDP 没来得及记录」的情况。
    /// </summary>
    public const string CrawlScript = """
        (() => {
          const out = new Map();
          const extKind = (u) => {
            if (/\.(png|jpe?g|gif|webp|avif|bmp|svg|ico)([?#]|$)/i.test(u)) return 'image';
            if (/\.(mp4|webm|mkv|mov|avi|flv|m4v|m3u8|mpd|ts)([?#]|$)/i.test(u)) return 'video';
            if (/\.(mp3|wav|ogg|m4a|flac|aac|opus|wma)([?#]|$)/i.test(u)) return 'audio';
            return '';
          };
          const add = (raw, kind, width, height, title) => {
            if (!raw) return;
            let url;
            try { url = new URL(raw, location.href).href; } catch { return; }
            if (!/^(https?|file):/i.test(url)) return;
            const k = kind || extKind(url);
            if (!k) return;
            const old = out.get(url);
            if (old) {
              if (!old.width && width) old.width = width;
              if (!old.height && height) old.height = height;
              if (!old.title && title) old.title = title;
              return;
            }
            out.set(url, { url: url, kind: k, width: width | 0, height: height | 0, title: title || '' });
          };
          document.querySelectorAll('img').forEach(el => {
            add(el.currentSrc || el.src || el.getAttribute('data-src') || el.getAttribute('data-original'),
                'image', el.naturalWidth, el.naturalHeight, el.alt);
            const ss = el.getAttribute('srcset');
            if (ss) add(ss.split(',')[0].trim().split(/\s+/)[0], 'image', el.naturalWidth, el.naturalHeight, el.alt);
          });
          document.querySelectorAll('picture source').forEach(el => {
            const ss = el.getAttribute('srcset');
            if (ss) add(ss.split(',')[0].trim().split(/\s+/)[0], 'image');
          });
          document.querySelectorAll('video, audio').forEach(el => {
            const kind = el.tagName.toLowerCase() === 'video' ? 'video' : 'audio';
            add(el.currentSrc || el.src, kind, el.videoWidth, el.videoHeight, el.title);
            el.querySelectorAll('source').forEach(s => add(s.src, kind));
          });
          document.querySelectorAll('a[href]').forEach(el => {
            const k = extKind(el.href);
            if (k) add(el.href, k);
          });
          try {
            document.querySelectorAll('*').forEach(el => {
              const bg = getComputedStyle(el).backgroundImage;
              if (!bg || bg === 'none') return;
              const matches = bg.match(/url\(["']?(.*?)["']?\)/g);
              if (matches) matches.forEach(x => add(x.replace(/^url\(["']?|["']?\)$/g, ''), 'image'));
            });
          } catch (e) { }
          try {
            performance.getEntriesByType('resource').forEach(e => {
              const k = extKind(e.name);
              if (k) add(e.name, k);
            });
          } catch (e) { }
          return Array.from(out.values());
        })()
        """;
}
