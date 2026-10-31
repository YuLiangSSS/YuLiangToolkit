using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.Themes;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「四套 UI（默认 / 液态玻璃 / 毛玻璃 / 亚克力）」的纯逻辑自检（v0.25.0 起，v0.43.0 扩到四套主题）。
///
/// <para>钉住四类东西（核心断言不需要开窗口；冷启动时会摸一下离屏窗口注册 pack URI，见 WarmUpWpf）：</para>
/// <list type="bullet">
///   <item><b>主题枚举 / 名字 / 字典路径</b>：<see cref="UiTheme"/> 枚举名就是配置文件里存的字符串
///   （<c>"Default"</c> / <c>"LiquidGlass"</c> / <c>"FrostedGlass"</c> / <c>"Acrylic"</c>），改名字 = 改配置格式；
///   <c>YLT_THEME</c> 的宽松解析；<see cref="ThemeManager.SourceFor"/> 指向的 pack URI 必须真的能加载；</item>
///   <item><b>四套字典的 key 必须完全一致</b>：缺一个 key 在运行时只会静默退回控件默认值（文字变黑之类），
///   界面测试很难发现，所以这里逐项对比 key 集合 + <see cref="ThemeManager.RequiredTokenKeys"/> 清单；</item>
///   <item><b>默认主题 = v0.24.0 原值</b>：关键颜色逐个断言（窗口底色 / 卡片 / 文字 / 强调色 / 高光透明），
///   保证切回默认后第一套 UI 外观与以前分毫不差；三套玻璃主题检查半透明 / 亮色文字 / 背景图 token；</item>
///   <item><b>配置往返</b>：<see cref="ShellConfig.UiTheme"/> 按枚举名存字符串、读回一致，
///   旧配置文件没有 <c>uiTheme</c> 字段时按默认主题处理；背景图字段（v0.43.0）也能往返，
///   文件名安全校验（<see cref="ShellBackgroundImage.IsSafeFileName"/>）挡住 <c>..</c> / 绝对路径。</item>
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
        CheckBackgroundImageHelper(Check);
        CheckApplyState(Check);
    }

    // ---------------------------------------------------------------- 枚举 / 名字 / 路径

    private static void CheckEnumAndNames(CheckFn check)
    {
        check("枚举默认值是默认主题", default(UiTheme) == UiTheme.Default);
        check("枚举里是四套 UI",
            Enum.GetValues<UiTheme>().Length == 4
            && Enum.GetNames<UiTheme>().SequenceEqual(new[] { "Default", "LiquidGlass", "FrostedGlass", "Acrylic" }),
            string.Join(", ", Enum.GetNames<UiTheme>()));

        check("默认主题名字", ThemeManager.DisplayName(UiTheme.Default) == "默认");
        check("液态玻璃名字", ThemeManager.DisplayName(UiTheme.LiquidGlass) == "液态玻璃");
        check("毛玻璃名字", ThemeManager.DisplayName(UiTheme.FrostedGlass) == "毛玻璃");
        check("亚克力名字", ThemeManager.DisplayName(UiTheme.Acrylic) == "亚克力");
        check("四套主题说明都不为空且各不相同",
            new[] { UiTheme.Default, UiTheme.LiquidGlass, UiTheme.FrostedGlass, UiTheme.Acrylic }
                .Select(ThemeManager.Describe)
                .All(text => text.Length > 0)
            && new[] { UiTheme.Default, UiTheme.LiquidGlass, UiTheme.FrostedGlass, UiTheme.Acrylic }
                .Select(ThemeManager.Describe).Distinct().Count() == 4);

        check("半透明判定：默认主题不透明、其余三套半透明",
            !ThemeManager.HasTranslucentSurface(UiTheme.Default)
            && ThemeManager.HasTranslucentSurface(UiTheme.LiquidGlass)
            && ThemeManager.HasTranslucentSurface(UiTheme.FrostedGlass)
            && ThemeManager.HasTranslucentSurface(UiTheme.Acrylic));

        check("默认主题字典路径指向 DefaultTheme.xaml",
            ThemeManager.SourceFor(UiTheme.Default).OriginalString.EndsWith("Views/Themes/DefaultTheme.xaml"),
            ThemeManager.SourceFor(UiTheme.Default).OriginalString);
        check("液态玻璃字典路径指向 LiquidGlassTheme.xaml",
            ThemeManager.SourceFor(UiTheme.LiquidGlass).OriginalString.EndsWith("Views/Themes/LiquidGlassTheme.xaml"),
            ThemeManager.SourceFor(UiTheme.LiquidGlass).OriginalString);
        check("毛玻璃字典路径指向 FrostedGlassTheme.xaml",
            ThemeManager.SourceFor(UiTheme.FrostedGlass).OriginalString.EndsWith("Views/Themes/FrostedGlassTheme.xaml"),
            ThemeManager.SourceFor(UiTheme.FrostedGlass).OriginalString);
        check("亚克力字典路径指向 AcrylicTheme.xaml",
            ThemeManager.SourceFor(UiTheme.Acrylic).OriginalString.EndsWith("Views/Themes/AcrylicTheme.xaml"),
            ThemeManager.SourceFor(UiTheme.Acrylic).OriginalString);
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
        check("解析 frosted / frosted-glass / 毛玻璃 / 磨砂",
            Parsed("frosted", UiTheme.FrostedGlass) && Parsed("FrostedGlass", UiTheme.FrostedGlass)
            && Parsed("frosted-glass", UiTheme.FrostedGlass) && Parsed("毛玻璃", UiTheme.FrostedGlass)
            && Parsed("磨砂玻璃", UiTheme.FrostedGlass));
        check("解析 acrylic / Acrylic / 亚克力",
            Parsed("acrylic", UiTheme.Acrylic) && Parsed("Acrylic", UiTheme.Acrylic)
            && Parsed("ACRYLIC", UiTheme.Acrylic) && Parsed("亚克力", UiTheme.Acrylic));
        check("解析大小写 / 下划线 / 空格都不敏感",
            Parsed("LIQUID_GLASS", UiTheme.LiquidGlass) && Parsed(" liquid glass ", UiTheme.LiquidGlass)
            && Parsed("FROSTED_GLASS", UiTheme.FrostedGlass) && Parsed("DEFAULT", UiTheme.Default));
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
        check("必需 token 清单覆盖四套 UI 的核心项",
            ThemeManager.RequiredTokenKeys.Contains("WindowBackgroundBrush")
            && ThemeManager.RequiredTokenKeys.Contains("CardBackgroundBrush")
            && ThemeManager.RequiredTokenKeys.Contains("BorderBrush")
            && ThemeManager.RequiredTokenKeys.Contains("GlassHighlightBrush")
            && ThemeManager.RequiredTokenKeys.Contains("BackdropBaseBrush")
            && ThemeManager.RequiredTokenKeys.Contains("BackdropImageScrimBrush")
            && ThemeManager.RequiredTokenKeys.Contains("BackdropNoiseBrush")
            && ThemeManager.RequiredRadiusKeys.Contains("CardCornerRadius"),
            $"画刷 {ThemeManager.RequiredTokenKeys.Count} 个 + 圆角 {ThemeManager.RequiredRadiusKeys.Count} 个");
    }

    // ---------------------------------------------------------------- 四套字典

    private static void CheckDictionaries(CheckFn check)
    {
        WarmUpWpf();   // 只跑 -- theme 时前面没有 WPF 检查，pack:// 方案还没注册，见方法注释

        // 无 Application 的控制台进程里也能加载：pack URI 带程序集名（/YuLiangTookit;component/…）
        var themes = new[] { UiTheme.Default, UiTheme.LiquidGlass, UiTheme.FrostedGlass, UiTheme.Acrylic };
        var dictionaries = new Dictionary<UiTheme, ResourceDictionary>();
        var errors = new Dictionary<UiTheme, string>();

        foreach (var theme in themes)
        {
            try { dictionaries[theme] = ThemeManager.LoadThemeDictionary(theme); }
            catch (Exception ex) { errors[theme] = $"{ex.GetType().Name}: {ex.Message}"; }
        }

        foreach (var theme in themes)
        {
            var name = ThemeManager.DisplayName(theme);
            dictionaries.TryGetValue(theme, out var dictionary);
            check($"{name}主题字典能加载", dictionary is not null,
                dictionary is not null ? $"{dictionary.Count} 个 key" : errors.GetValueOrDefault(theme, ""));
        }

        if (dictionaries.Count != themes.Length)
        {
            check("四套主题字典 key 对比（有字典加载失败，跳过）", false,
                string.Join(" / ", errors.Select(pair => $"{ThemeManager.DisplayName(pair.Key)}：{pair.Value}")));
            return;
        }

        check("四套主题字典都被 ThemeManager 认出来",
            themes.All(theme => ThemeManager.IsThemeDictionary(dictionaries[theme])));

        var def = dictionaries[UiTheme.Default];
        var glass = dictionaries[UiTheme.LiquidGlass];
        var required = ThemeManager.RequiredTokenKeys.Concat(ThemeManager.RequiredRadiusKeys)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        List<string> KeysOf(UiTheme theme) => dictionaries[theme].Keys.Cast<object>()
            .Select(k => k.ToString() ?? "").OrderBy(k => k, StringComparer.Ordinal).ToList();

        var defKeys = KeysOf(UiTheme.Default);
        check("字典 key = 声明的必需清单（不多不少）", defKeys.SequenceEqual(required),
            "缺：" + OnlyIn(required, defKeys) + "；多：" + OnlyIn(defKeys, required));

        foreach (var theme in themes.Skip(1))
        {
            var keys = KeysOf(theme);
            check($"{ThemeManager.DisplayName(theme)}字典 key 与默认主题完全一致",
                keys.SequenceEqual(defKeys),
                OnlyIn(defKeys, keys) + OnlyIn(keys, defKeys));
        }

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
        check("默认主题：不显示背景图（压暗层 / 颗粒层都透明）",
            ColorOfAnyBrush(def, "BackdropImageScrimBrush").A == 0
            && ColorOfAnyBrush(def, "BackdropNoiseBrush").A == 0);

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

        // ---- 毛玻璃 / 亚克力（v0.43.0）：半透明面板 + 亮色文字 + 背景图暗化层 ----
        foreach (var theme in new[] { UiTheme.FrostedGlass, UiTheme.Acrylic })
        {
            var name = ThemeManager.DisplayName(theme);
            var dictionary = dictionaries[theme];
            check($"{name}：卡片底色是半透明（能透出后面的背景图）",
                dictionary["CardBackgroundBrush"] is GradientBrush card && HasTranslucentStop(card),
                dictionary["CardBackgroundBrush"]?.GetType().Name ?? "(缺失)");
            check($"{name}：文字是亮色（背景图上可读）",
                ColorOf(dictionary, "TextPrimaryBrush").A == 0xFF
                && ColorOf(dictionary, "TextPrimaryBrush").R > 0xD0
                && ColorOf(dictionary, "TextPrimaryBrush").G > 0xD0
                && ColorOf(dictionary, "TextPrimaryBrush").B > 0xD0);
            check($"{name}：背景图压暗层可见（保证白字可读）",
                ColorOfAnyBrush(dictionary, "BackdropImageScrimBrush").A > 0);
            check($"{name}：背景底色层是渐变（背景图没铺满 / 没设置时的舞台）",
                dictionary["BackdropBaseBrush"] is GradientBrush baseBrush && baseBrush.GradientStops.Count >= 2,
                dictionary["BackdropBaseBrush"]?.GetType().Name ?? "(缺失)");
        }

        check("亚克力：背景有颗粒噪点层（DrawingBrush 平铺，不是透明）",
            dictionaries[UiTheme.Acrylic]["BackdropNoiseBrush"] is DrawingBrush);
        check("毛玻璃：不要颗粒噪点（Transparent）",
            ColorOfAnyBrush(dictionaries[UiTheme.FrostedGlass], "BackdropNoiseBrush").A == 0);
    }

    /// <summary>任何 Brush（SolidColor / Gradient / Drawing）的“可见性”：取第一个非零 alpha，认不出返回全透明。</summary>
    private static Color ColorOfAnyBrush(ResourceDictionary dictionary, string key)
    {
        switch (dictionary[key])
        {
            case SolidColorBrush solid:
                return solid.Color;
            case GradientBrush gradient:
                var stop = gradient.GradientStops.FirstOrDefault(s => s.Color.A > 0);
                return stop?.Color ?? Colors.Transparent;
            case DrawingBrush:
                return Color.FromArgb(0x01, 0, 0, 0);   // DrawingBrush（噪点）只要存在就当作“可见”
            default:
                return Colors.Transparent;
        }
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

        // ---- v0.43.0：毛玻璃 / 亚克力按枚举名存字符串；背景图字段能往返 ----
        JsonFileStore.Write(path, new ShellConfig { UiTheme = UiTheme.FrostedGlass });
        check("uiTheme 按枚举名存成字符串 \"FrostedGlass\"",
            File.ReadAllText(path).Contains("\"FrostedGlass\""));
        check("uiTheme 读回来是毛玻璃",
            JsonFileStore.Read<ShellConfig>(path)?.UiTheme == UiTheme.FrostedGlass);

        JsonFileStore.Write(path, new ShellConfig { UiTheme = UiTheme.Acrylic });
        check("uiTheme 按枚举名存成字符串 \"Acrylic\"",
            File.ReadAllText(path).Contains("\"Acrylic\""));
        check("uiTheme 读回来是亚克力",
            JsonFileStore.Read<ShellConfig>(path)?.UiTheme == UiTheme.Acrylic);

        JsonFileStore.Write(path, new ShellConfig { BackgroundImage = "bg-20250101-120000-000.png", BackgroundImageFitWhole = true });
        var backgroundJson = File.ReadAllText(path);
        var backgroundRoundTrip = JsonFileStore.Read<ShellConfig>(path);
        check("背景图字段按文件名往返（backgroundImage / backgroundImageFitWhole）",
            backgroundJson.Contains("\"backgroundImage\": \"bg-20250101-120000-000.png\"")
            && backgroundRoundTrip?.BackgroundImage == "bg-20250101-120000-000.png"
            && backgroundRoundTrip.BackgroundImageFitWhole,
            backgroundJson.Replace(Environment.NewLine, " "));

        File.WriteAllText(path, "{ \"uiTheme\": \"FrostedGlass\" }");
        check("旧配置（没有背景图字段）读出来 = 没有背景图、铺满窗口",
            JsonFileStore.Read<ShellConfig>(path) is { BackgroundImage: null, BackgroundImageFitWhole: false });
    }

    // ---------------------------------------------------------------- 背景图文件名安全（v0.43.0）

    /// <summary>
    /// 背景图配置里只接受**纯文件名**：别人手改 JSON 写 <c>..\..\x</c> / 绝对路径也不能读到 / 删到数据目录外。
    /// 只测纯函数（<see cref="ShellBackgroundImage.IsSafeFileName"/> / <see cref="ShellBackgroundImage.Resolve"/>），
    /// 不在自检里真的复制图片（避免往运行时 Data 目录写测试文件）。
    /// </summary>
    private static void CheckBackgroundImageHelper(CheckFn check)
    {
        check("背景图：普通文件名合法",
            ShellBackgroundImage.IsSafeFileName("bg-20250101-120000-000.png")
            && ShellBackgroundImage.IsSafeFileName("wallpaper.jpg"));

        check("背景图：空 / 带目录 / 绝对路径 / .. 都不合法",
            !ShellBackgroundImage.IsSafeFileName(null)
            && !ShellBackgroundImage.IsSafeFileName("")
            && !ShellBackgroundImage.IsSafeFileName("   ")
            && !ShellBackgroundImage.IsSafeFileName("sub/bg.png")
            && !ShellBackgroundImage.IsSafeFileName("sub\\bg.png")
            && !ShellBackgroundImage.IsSafeFileName(@"C:\Windows\bg.png")
            && !ShellBackgroundImage.IsSafeFileName("../bg.png")
            && !ShellBackgroundImage.IsSafeFileName("..\\bg.png")
            && !ShellBackgroundImage.IsSafeFileName("."));

        check("背景图：非法 / 不存在的文件解析为 null",
            ShellBackgroundImage.Resolve(null) is null
            && ShellBackgroundImage.Resolve("../bg.png") is null
            && ShellBackgroundImage.Resolve("不存在-背景图.png") is null);

        check("背景图文件都在 Data\\shell\\background\\ 下",
            ShellBackgroundImage.Directory.EndsWith(
                Path.Combine("Data", "shell", "background"), StringComparison.OrdinalIgnoreCase),
            ShellBackgroundImage.Directory);

        // 真的复制 / 删除一次（check.ps1 会把自检数据根隔离到 .artifacts 下；文件名带时间戳，不会覆盖真实图片）
        var scratch = SelfCheckScratch.MakeDirectory("background-image-check");
        var source = Path.Combine(scratch, "source.png");
        File.WriteAllBytes(source, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });   // Save 只复制字节，不解码
        string? saved = null;
        try { saved = ShellBackgroundImage.Save(source); }
        catch (Exception ex) { check("背景图：选图能复制进数据目录", false, ex.Message); }

        if (saved is not null)
        {
            check("背景图：选图会复制进 Data\\shell\\background（文件名带 bg- 前缀）",
                saved.StartsWith("bg-", StringComparison.Ordinal) && ShellBackgroundImage.Exists(saved), saved);
            check("背景图：能显示文件大小", ShellBackgroundImage.SizeText(saved).Length > 0,
                ShellBackgroundImage.SizeText(saved));
            ShellBackgroundImage.Delete(saved);
            check("背景图：清除后副本被删掉", !ShellBackgroundImage.Exists(saved), saved);
        }
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
