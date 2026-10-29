using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.Themes;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「第二套 UI：液态玻璃主题」的纯逻辑自检（v0.25.0）。
///
/// <para>钉住四类东西（核心断言不需要开窗口；冷启动时会摸一下离屏窗口注册 pack URI，见 WarmUpWpf）：</para>
/// <list type="bullet">
///   <item><b>主题枚举 / 名字 / 字典路径</b>：<see cref="UiTheme"/> 枚举名就是配置文件里存的字符串
///   （<c>"Default"</c> / <c>"LiquidGlass"</c>），改名字 = 改配置格式；<c>YLT_THEME</c> 的宽松解析；
///   <see cref="ThemeManager.SourceFor"/> 指向的 pack URI 必须真的能加载；</item>
///   <item><b>两套字典的 key 必须完全一致</b>：缺一个 key 在运行时只会静默退回控件默认值（文字变黑之类），
///   界面测试很难发现，所以这里逐项对比 key 集合 + <see cref="ThemeManager.RequiredTokenKeys"/> 清单；</item>
///   <item><b>默认主题 = v0.24.0 原值</b>：关键颜色逐个断言（窗口底色 / 卡片 / 文字 / 强调色 / 高光透明），
///   保证切回默认后第一套 UI 外观与以前分毫不差；</item>
///   <item><b>配置往返</b>：<see cref="ShellConfig.UiTheme"/> 按枚举名存字符串、读回一致，
///   旧配置文件没有 <c>uiTheme</c> 字段时按默认主题处理。</item>
/// </list>
/// </summary>
internal static class ThemeChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;   // 本组断言都带明细

        void Check(string name, bool ok, string detail = "") => check("[主题] " + name, ok, detail);

        CheckEnumAndNames(Check);
        CheckParsing(Check);
        CheckRequiredKeyList(Check);
        CheckDictionaries(Check);
        CheckConfigRoundTrip(Check);
        CheckApplyState(Check);
    }

    // ---------------------------------------------------------------- 枚举 / 名字 / 路径

    private static void CheckEnumAndNames(CheckFn check)
    {
        check("枚举默认值是默认主题", default(UiTheme) == UiTheme.Default);
        check("枚举里只有两套 UI",
            Enum.GetValues<UiTheme>().Length == 2
            && Enum.GetNames<UiTheme>().SequenceEqual(new[] { "Default", "LiquidGlass" }),
            string.Join(", ", Enum.GetNames<UiTheme>()));

        check("默认主题名字", ThemeManager.DisplayName(UiTheme.Default) == "默认");
        check("液态玻璃名字", ThemeManager.DisplayName(UiTheme.LiquidGlass) == "液态玻璃");
        check("两套主题说明都不为空且不一样",
            ThemeManager.Describe(UiTheme.Default).Length > 0
            && ThemeManager.Describe(UiTheme.LiquidGlass).Length > 0
            && ThemeManager.Describe(UiTheme.Default) != ThemeManager.Describe(UiTheme.LiquidGlass));

        check("默认主题字典路径指向 DefaultTheme.xaml",
            ThemeManager.SourceFor(UiTheme.Default).OriginalString.EndsWith("Views/Themes/DefaultTheme.xaml"),
            ThemeManager.SourceFor(UiTheme.Default).OriginalString);
        check("液态玻璃字典路径指向 LiquidGlassTheme.xaml",
            ThemeManager.SourceFor(UiTheme.LiquidGlass).OriginalString.EndsWith("Views/Themes/LiquidGlassTheme.xaml"),
            ThemeManager.SourceFor(UiTheme.LiquidGlass).OriginalString);
    }

    private static void CheckParsing(CheckFn check)
    {
        bool Parsed(string? raw, UiTheme? expected) => ThemeManager.ParseThemeName(raw) == expected;

        check("解析 default / Default / 0 / 默认",
            Parsed("default", UiTheme.Default) && Parsed("Default", UiTheme.Default)
            && Parsed(" 0 ", UiTheme.Default) && Parsed("默认", UiTheme.Default));
        check("解析 liquid-glass / LiquidGlass / glass / 液态玻璃",
            Parsed("liquid-glass", UiTheme.LiquidGlass) && Parsed("LiquidGlass", UiTheme.LiquidGlass)
            && Parsed("glass", UiTheme.LiquidGlass) && Parsed("液态玻璃", UiTheme.LiquidGlass));
        check("解析大小写 / 下划线 / 空格都不敏感",
            Parsed("LIQUID_GLASS", UiTheme.LiquidGlass) && Parsed(" liquid glass ", UiTheme.LiquidGlass)
            && Parsed("DEFAULT", UiTheme.Default));
        check("空值 / 认不出的值返回 null（调用方回退默认）",
            ThemeManager.ParseThemeName(null) is null
            && ThemeManager.ParseThemeName("") is null
            && ThemeManager.ParseThemeName("   ") is null
            && ThemeManager.ParseThemeName("玻璃拟态") is null);
    }

    private static void CheckRequiredKeyList(CheckFn check)
    {
        var all = ThemeManager.RequiredTokenKeys.Concat(ThemeManager.RequiredRadiusKeys).ToList();
        check("必需 token 清单没有重复项", all.Count == all.Distinct(StringComparer.Ordinal).Count(),
            $"共 {all.Count} 个");
        check("必需 token 清单覆盖两套 UI 的核心项",
            ThemeManager.RequiredTokenKeys.Contains("WindowBackgroundBrush")
            && ThemeManager.RequiredTokenKeys.Contains("CardBackgroundBrush")
            && ThemeManager.RequiredTokenKeys.Contains("BorderBrush")
            && ThemeManager.RequiredTokenKeys.Contains("GlassHighlightBrush")
            && ThemeManager.RequiredRadiusKeys.Contains("CardCornerRadius"),
            $"画刷 {ThemeManager.RequiredTokenKeys.Count} 个 + 圆角 {ThemeManager.RequiredRadiusKeys.Count} 个");
    }

    // ---------------------------------------------------------------- 两套字典

    private static void CheckDictionaries(CheckFn check)
    {
        WarmUpWpf();   // 只跑 -- theme 时前面没有 WPF 检查，pack:// 方案还没注册，见方法注释

        ResourceDictionary? def = null;
        ResourceDictionary? glass = null;
        var defError = "";
        var glassError = "";

        // 无 Application 的控制台进程里也能加载：pack URI 带程序集名（/YuLiangTookit;component/…）
        try { def = ThemeManager.LoadThemeDictionary(UiTheme.Default); }
        catch (Exception ex) { defError = $"{ex.GetType().Name}: {ex.Message}"; }
        try { glass = ThemeManager.LoadThemeDictionary(UiTheme.LiquidGlass); }
        catch (Exception ex) { glassError = $"{ex.GetType().Name}: {ex.Message}"; }

        check("默认主题字典能加载", def is not null, def is not null ? $"{def.Count} 个 key" : defError);
        check("液态玻璃主题字典能加载", glass is not null, glass is not null ? $"{glass.Count} 个 key" : glassError);

        if (def is null || glass is null)
        {
            check("两套主题字典 key 对比（字典加载失败，跳过）", false, defError + " / " + glassError);
            return;
        }

        check("主题字典被 ThemeManager 认出来",
            ThemeManager.IsThemeDictionary(def) && ThemeManager.IsThemeDictionary(glass));

        var defKeys = def.Keys.Cast<object>().Select(k => k.ToString() ?? "").OrderBy(k => k, StringComparer.Ordinal).ToList();
        var glassKeys = glass.Keys.Cast<object>().Select(k => k.ToString() ?? "").OrderBy(k => k, StringComparer.Ordinal).ToList();

        check("两套主题字典 key 完全一致", defKeys.SequenceEqual(glassKeys),
            OnlyIn(defKeys, glassKeys) + OnlyIn(glassKeys, defKeys));

        var required = ThemeManager.RequiredTokenKeys.Concat(ThemeManager.RequiredRadiusKeys)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        check("字典 key = 声明的必需清单（不多不少）", defKeys.SequenceEqual(required),
            "缺：" + OnlyIn(required, defKeys) + "；多：" + OnlyIn(defKeys, required));

        // ---- 默认主题必须 = v0.24.0 的原始颜色（切回默认时第一套 UI 不能有任何变化） ----
        check("默认主题：窗口底色仍是 #16181D", ColorOf(def, "WindowBackgroundBrush") == Rgb(0x16, 0x18, 0x1D),
            ColorOf(def, "WindowBackgroundBrush").ToString());
        check("默认主题：标题栏仍是 #1A1E25", ColorOf(def, "HeaderBackgroundBrush") == Rgb(0x1A, 0x1E, 0x25));
        check("默认主题：卡片仍是 #1F242C", ColorOf(def, "CardBackgroundBrush") == Rgb(0x1F, 0x24, 0x2C));
        check("默认主题：正文仍是 #E8EAF0", ColorOf(def, "TextPrimaryBrush") == Rgb(0xE8, 0xEA, 0xF0));
        check("默认主题：强调色仍是 #4C8DFF", ColorOf(def, "AccentBrush") == Rgb(0x4C, 0x8D, 0xFF));
        check("默认主题：高光 / 整窗玻璃边都是全透明（不改变第一套 UI）",
            ColorOf(def, "GlassHighlightBrush").A == 0 && ColorOf(def, "WindowEdgeBorderBrush").A == 0);
        check("默认主题：主按钮底色仍是 #4C8DFF", ColorOf(def, "PrimaryButtonBackgroundBrush") == Rgb(0x4C, 0x8D, 0xFF));

        // ---- 液态玻璃：半透明 / 渐变 / 大圆角 ----
        check("液态玻璃：卡片底色是半透明渐变（能看到后面的光晕）",
            glass["CardBackgroundBrush"] is GradientBrush cardBrush && HasTranslucentStop(cardBrush),
            glass["CardBackgroundBrush"]?.GetType().Name ?? "(缺失)");
        check("液态玻璃：描边是渐变（上亮下暗的玻璃亮边）",
            glass["BorderBrush"] is GradientBrush borderBrush && borderBrush.GradientStops.Count >= 3);
        check("液态玻璃：高光层是斜向渐变而不是透明",
            glass["GlassHighlightBrush"] is GradientBrush highlight && HasVisibleStop(highlight));
        check("液态玻璃：整窗玻璃边可见",
            (glass["WindowEdgeBorderBrush"] as SolidColorBrush)?.Color.A is > 0);
        check("液态玻璃：卡片圆角比默认主题大",
            RadiusOf(glass, "CardCornerRadius") > RadiusOf(def, "CardCornerRadius"),
            $"{RadiusOf(def, "CardCornerRadius")} → {RadiusOf(glass, "CardCornerRadius")}");
        check("液态玻璃：文字仍是亮色（深色光晕上可读）",
            ColorOf(glass, "TextPrimaryBrush").A == 0xFF
            && ColorOf(glass, "TextPrimaryBrush").R > 0xD0
            && ColorOf(glass, "TextPrimaryBrush").G > 0xD0
            && ColorOf(glass, "TextPrimaryBrush").B > 0xD0);
    }

    /// <summary>
    /// 冷启动兜底：只跑 <c>-- theme</c> 时，前面没有任何 WPF 检查，WPF 的 <c>pack://</c> 方案还没注册，
    /// <see cref="ResourceDictionary"/> 用相对 pack URI 会抛「URI prefix is not recognized」。
    /// 在 STA 线程里摸一个离屏窗口把 WPF 初始化起来（做法同 <see cref="ScrollChainingChecks"/>），不显示界面。
    /// </summary>
    private static void WarmUpWpf()
    {
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window
                {
                    Width = 40, Height = 40, ShowActivated = false, ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None, Left = -4000, Top = -4000,
                };
                window.Show();
                window.Close();
            }
            catch (Exception)
            {
                // 摸窗口失败也无妨：后面加载字典失败会走 defError 分支，把真实原因报出来
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static string OnlyIn(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var extra = left.Except(right, StringComparer.Ordinal).ToList();
        return extra.Count == 0 ? "" : "[" + string.Join(",", extra) + "]";
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color ColorOf(ResourceDictionary dictionary, string key)
        => dictionary[key] is SolidColorBrush brush ? brush.Color : Colors.Transparent;

    private static bool HasTranslucentStop(GradientBrush brush)
        => brush.GradientStops.Any(stop => stop.Color.A is > 0 and < 0xFF);

    private static bool HasVisibleStop(GradientBrush brush)
        => brush.GradientStops.Any(stop => stop.Color.A > 0);

    private static double RadiusOf(ResourceDictionary dictionary, string key)
        => dictionary[key] is CornerRadius radius ? radius.TopLeft : double.NaN;

    // ---------------------------------------------------------------- 配置往返

    private static void CheckConfigRoundTrip(CheckFn check)
    {
        check("ShellConfig 默认是默认主题", new ShellConfig().UiTheme == UiTheme.Default);

        var dir = SelfCheckScratch.MakeDirectory("theme-check");
        var path = Path.Combine(dir, "config.json");

        JsonFileStore.Write(path, new ShellConfig { UiTheme = UiTheme.LiquidGlass });
        var json = File.ReadAllText(path);
        check("uiTheme 按枚举名存成字符串 \"LiquidGlass\"",
            json.Contains("\"uiTheme\"") && json.Contains("\"LiquidGlass\""),
            json.Replace(Environment.NewLine, " "));
        check("uiTheme 读回来是液态玻璃",
            JsonFileStore.Read<ShellConfig>(path)?.UiTheme == UiTheme.LiquidGlass);

        File.WriteAllText(path, "{ \"sortMode\": \"NameAsc\" }");
        check("旧配置文件没有 uiTheme 时按默认主题（老用户升级后外观不变）",
            JsonFileStore.Read<ShellConfig>(path)?.UiTheme == UiTheme.Default);

        File.WriteAllText(path, "{ \"uiTheme\": \"Default\" }");
        check("uiTheme = Default 能正常读回", JsonFileStore.Read<ShellConfig>(path)?.UiTheme == UiTheme.Default);
    }

    // ---------------------------------------------------------------- 切换状态机

    private static void CheckApplyState(CheckFn check)
    {
        // 自检进程没有 Application：Apply 只记录状态、不装资源（不抛异常），
        // 真正的“换掉界面资源”由带界面的 App 在 OnStartup / 设置窗口里调用。
        var hadApplication = Application.Current is not null;
        check("自检进程里没有 Application（Apply 走无界面分支）", !hadApplication);

        var first = ThemeManager.Apply(UiTheme.LiquidGlass);
        check("无界面时 Apply 也能记录当前主题",
            ThemeManager.Current == UiTheme.LiquidGlass && !first,
            $"Current={ThemeManager.Current}, 返回值={first}");

        var second = ThemeManager.Apply(UiTheme.LiquidGlass);
        check("重复 Apply 同一主题是幂等的（返回 false）", !second);

        check("切回默认主题成功", ThemeManager.Apply(UiTheme.Default) || ThemeManager.Current == UiTheme.Default);
        check("最终停留在默认主题", ThemeManager.Current == UiTheme.Default);
    }
}
