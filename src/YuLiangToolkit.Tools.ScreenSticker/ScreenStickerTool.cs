using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ScreenSticker.ViewModels;
using YuLiangToolkit.Tools.ScreenSticker.Views;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 「屏幕贴纸」工具入口：把任意图片贴到桌面上，每张贴纸一个透明无边框窗口，
/// 可以自由拖动 / 滚轮缩放 / 调层级 / 单独强制置顶；工具页负责选图、最近使用与统一管理。
///
/// <para><b>启动时自动恢复</b>：配置里 <see cref="ScreenStickerConfig.AutoRestoreOnStartup"/> 开着时，
/// 本工具被 <see cref="ToolDiscovery"/> 发现（启动早期、MainWindow 还没建完）只登记一次延迟回调，
/// 等 Dispatcher 空闲后再加载配置 / 放回贴纸 —— 构造函数里不做任何耗时操作、也不建窗口。</para>
///
/// <para><b>截图调试</b>：<c>YLT_STICKER_DEMO=&lt;图片路径&gt;</c> 启动时贴一张图，
/// 配合 <c>YLT_STICKER_SHOT=&lt;png&gt;</c> 截图后退出（见 <see cref="Views.StickerWindow"/>）。</para>
/// </summary>
public sealed class ScreenStickerTool : ITool, IToolCacheSource
{
    /// <summary>功能 Id（= Data / Config 下的小文件夹名，也是自检组名）。</summary>
    public const string ToolId = ScreenStickerConfig.ToolId;

    public ScreenStickerTool()
    {
        // ToolDiscovery 会在启动早期（MainWindow 还没建）new 出所有工具：这里绝不能同步建窗口。
        if (Application.Current is null) return;   // 自检 / 无 UI 进程：直接 no-op

        var demoPath = Environment.GetEnvironmentVariable("YLT_STICKER_DEMO");
        var hasDemo = !string.IsNullOrWhiteSpace(demoPath);

        var autoRestore = false;
        try
        {
            // 只有配置文件已经存在才去读：没开过这个工具的用户不该因为自动发现就多出一个 Config 文件。
            var store = ToolConfigStore.For(ToolId);
            if (store.Exists)
            {
                var config = store.Load<ScreenStickerConfig>();
                autoRestore = config.AutoRestoreOnStartup && (config.Stickers?.Count ?? 0) > 0;
            }
        }
        catch
        {
            // 配置读不出来就不自动恢复（不影响工具正常打开）
        }

        if (!autoRestore && !hasDemo) return;

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                if (hasDemo)
                {
                    // 截图冒烟用：先贴 demo 那张（不恢复旧贴纸，避免旧窗口抢第一张截图）。
                    StickerSession.Current.AddSticker(demoPath, topmost: true);
                    return;
                }

                // 正常启动恢复；只开了 YLT_STICKER_SHOT 没给 demo 时，恢复后由第一张窗口截图退出。
                StickerSession.Current.RestoreFromConfig();
            }
            catch
            {
                // 自动恢复失败不影响程序启动（用户还可以在工具页手动点「恢复上次贴纸」）
            }
        }));
    }

    public string Id => ToolId;

    public string Name => "屏幕贴纸";

    public string Description =>
        "把图片贴到桌面上：多选打开、每张可自由拖动 / 滚轮缩放 / 调整层级 / 单独强制置顶；带最近使用列表与贴纸管理";

    public string EmojiIcon => "📌";

    public string Category => "桌面";

    public IReadOnlyList<string> CategoryPath => new[] { "桌面" };

    public IReadOnlyList<string> Tags => new[]
    {
        "贴纸", "桌面", "图片", "置顶", "悬浮", "截图", "参考图", "看图", "最近使用", "缩放", "拖动",
    };

    public int Order => 162;

    public FrameworkElement CreateView()
        => new ScreenStickerView { DataContext = new ScreenStickerViewModel() };

    /// <summary>
    /// 「主页设置 → 清理缓存」里的缓存项：只清「最近使用」记录，不动桌面上的贴纸和设置。
    /// 记录为空时返回空数组，设置页就不会列出这一项。
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        var count = ToolConfigPeek.Read<ScreenStickerConfig>(ToolId)?.RecentEntries?.Count ?? 0;
        if (count == 0) return Array.Empty<ToolCacheItem>();

        return new[]
        {
            new ToolCacheItem
            {
                Id = "recent",
                Name = "最近使用的图片",
                Description = "存在 Config\\screen-sticker\\config.json 里；只清最近使用列表，不动桌面上的贴纸与其它设置",
                Detail = $"{count} 条",
                Clear = () => ToolConfigPeek.Mutate<ScreenStickerConfig>(ToolId, config => config.ClearRecent()),
            },
        };
    }
}
