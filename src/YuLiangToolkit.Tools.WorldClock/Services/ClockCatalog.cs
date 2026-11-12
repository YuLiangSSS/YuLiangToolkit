namespace YuLiangToolkit.Tools.WorldClock.Services;

/// <summary>
/// 城市库里的一条：中文名 + 英文名（也用来搜索）+ Windows 时区 Id + 分组（大洲）。
///
/// <para>只存「怎么找时区」，时间本身永远现算（见 <see cref="ClockMath"/>）。</para>
/// </summary>
/// <param name="Name">中文名（卡片主标题）。</param>
/// <param name="EnglishName">英文名（表盘下方小字，也参与搜索）。</param>
/// <param name="TimeZoneId">Windows 时区 Id（本机逐个验证过，见 AGENTS.md 的验证纪律）。</param>
/// <param name="Region">分组：亚洲 / 欧洲 / 非洲 / 北美 / 南美 / 大洋洲。</param>
/// <param name="Aliases">常见别称（国家 / 地区名，空格分隔），只用于搜索，例如「伦敦」可以输了「英国」找到。</param>
public sealed record ClockCityOption(
    string Name, string EnglishName, string TimeZoneId, string Region, string Aliases = "");

/// <summary>
/// 本机时区相关的小工具（区分「本机」与「某个城市」的时区，避免到处写 <c>TimeZoneInfo.Local</c>）。
/// </summary>
public static class WorldClockTimeZones
{
    /// <summary>本机时区 Id（新建钟的默认值、自定义偏移的占位值）。</summary>
    public static string LocalId => TimeZoneInfo.Local.Id;

    /// <summary>本机时区的中文展示名（页头那句「本机时区 …」用）。</summary>
    public static string LocalDisplayName => TimeZoneInfo.Local.DisplayName;

    /// <summary>
    /// 本机时区的短名字（页头用）：把系统给的 <c>(UTC+08:00) 北京，重庆，香港特别行政区，乌鲁木齐</c>
    /// 压成 <c>北京 等（UTC+08:00）</c> —— 整串念下来太长，一屏也挤。
    /// </summary>
    public static string LocalShortName
    {
        get
        {
            var display = TimeZoneInfo.Local.DisplayName;
            if (string.IsNullOrWhiteSpace(display)) return TimeZoneInfo.Local.Id;

            var text = display.Trim();
            var offsetText = "";
            if (text.StartsWith('(') && text.Contains(')'))
            {
                var close = text.IndexOf(')');
                offsetText = text[1..close];
                text = text[(close + 1)..].Trim();
            }

            // 「北京，重庆，香港特别行政区，乌鲁木齐」→ 取第一个 + 「等」
            var parts = text.Split(new[] { '，', ',', '、' }, StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 1 ? parts[0].Trim() + " 等" : text;
            return offsetText.Length > 0 ? $"{name}（{offsetText}）" : name;
        }
    }

    /// <summary>本机时区当前的 UTC 偏移文本（含夏令时）。</summary>
    public static string LocalOffsetText(DateTime utcNow) => ClockMath.FormatOffset(ClockMath.LocalOffset(utcNow));
}

/// <summary>
/// 内置城市库（122 座常用城市）。**时区 Id 全部是本机真实存在的 Windows Id**，
/// 启动时不做探测；某个版本的系统缺了哪个，<see cref="ClockMath.Resolve"/> 会退化成 UTC 并在卡片上标出来，
/// 不会崩、也不会静默显示错时间。
///
/// <para>搜索匹配中文名 / 英文名 / 常见别称（「伦敦」「London」「英国」都能搜到），
/// 匹配逻辑在 <see cref="Search"/> 里，纯逻辑可自检。</para>
/// </summary>
public static class ClockCatalog
{
    /// <summary>全部城市（按大洲分组，组内按常用度排）。</summary>
    public static IReadOnlyList<ClockCityOption> All { get; } = Build();

    /// <summary>分组顺序（下拉 / 列表分组标题用）。</summary>
    public static IReadOnlyList<string> Regions { get; } = new[] { "亚洲", "欧洲", "非洲", "北美", "南美", "大洋洲" };

    /// <summary>首次运行填入的默认城市（中国用户最常见的一组）。</summary>
    public static IReadOnlyList<string> DefaultCityNames { get; } = new[] { "北京", "东京", "伦敦", "纽约" };

    private static ClockCityOption[] Build() => new[]
    {
        // ---------- 亚洲 ----------
        new ClockCityOption("北京", "Beijing", "China Standard Time", "亚洲", "中国 大陆"),
        new ClockCityOption("上海", "Shanghai", "China Standard Time", "亚洲", "中国 大陆"),
        new ClockCityOption("广州", "Guangzhou", "China Standard Time", "亚洲", "中国 大陆"),
        new ClockCityOption("深圳", "Shenzhen", "China Standard Time", "亚洲", "中国 大陆"),
        new ClockCityOption("成都", "Chengdu", "China Standard Time", "亚洲", "中国 大陆"),
        new ClockCityOption("香港", "Hong Kong", "China Standard Time", "亚洲", "中国 HK"),
        new ClockCityOption("澳门", "Macau", "China Standard Time", "亚洲", "中国 MO"),
        new ClockCityOption("台北", "Taipei", "Taipei Standard Time", "亚洲", "台湾 TW"),
        new ClockCityOption("东京", "Tokyo", "Tokyo Standard Time", "亚洲", "日本 JP"),
        new ClockCityOption("大阪", "Osaka", "Tokyo Standard Time", "亚洲", "日本 JP"),
        new ClockCityOption("首尔", "Seoul", "Korea Standard Time", "亚洲", "韩国 南韩 KR"),
        new ClockCityOption("平壤", "Pyongyang", "North Korea Standard Time", "亚洲", "朝鲜 KP"),
        new ClockCityOption("新加坡", "Singapore", "Singapore Standard Time", "亚洲", "SG"),
        new ClockCityOption("吉隆坡", "Kuala Lumpur", "Singapore Standard Time", "亚洲", "马来西亚 MY"),
        new ClockCityOption("马尼拉", "Manila", "Singapore Standard Time", "亚洲", "菲律宾 PH"),
        new ClockCityOption("雅加达", "Jakarta", "SE Asia Standard Time", "亚洲", "印尼 印度尼西亚 ID"),
        new ClockCityOption("曼谷", "Bangkok", "SE Asia Standard Time", "亚洲", "泰国 TH"),
        new ClockCityOption("河内", "Hanoi", "SE Asia Standard Time", "亚洲", "越南 VN"),
        new ClockCityOption("胡志明市", "Ho Chi Minh City", "SE Asia Standard Time", "亚洲", "越南 西贡 VN"),
        new ClockCityOption("仰光", "Yangon", "Myanmar Standard Time", "亚洲", "缅甸 MM"),
        new ClockCityOption("达卡", "Dhaka", "Bangladesh Standard Time", "亚洲", "孟加拉 BD"),
        new ClockCityOption("加德满都", "Kathmandu", "Nepal Standard Time", "亚洲", "尼泊尔 NP"),
        new ClockCityOption("新德里", "New Delhi", "India Standard Time", "亚洲", "印度 IN"),
        new ClockCityOption("孟买", "Mumbai", "India Standard Time", "亚洲", "印度 IN"),
        new ClockCityOption("班加罗尔", "Bangalore", "India Standard Time", "亚洲", "印度 IN"),
        new ClockCityOption("科伦坡", "Colombo", "Sri Lanka Standard Time", "亚洲", "斯里兰卡 LK"),
        new ClockCityOption("伊斯兰堡", "Islamabad", "Pakistan Standard Time", "亚洲", "巴基斯坦 PK"),
        new ClockCityOption("卡拉奇", "Karachi", "Pakistan Standard Time", "亚洲", "巴基斯坦 PK"),
        new ClockCityOption("喀布尔", "Kabul", "Afghanistan Standard Time", "亚洲", "阿富汗 AF"),
        new ClockCityOption("塔什干", "Tashkent", "West Asia Standard Time", "亚洲", "乌兹别克斯坦 UZ"),
        new ClockCityOption("阿斯塔纳", "Astana", "Central Asia Standard Time", "亚洲", "哈萨克斯坦 KZ"),
        new ClockCityOption("乌兰巴托", "Ulaanbaatar", "Ulaanbaatar Standard Time", "亚洲", "蒙古 MN"),
        new ClockCityOption("迪拜", "Dubai", "Arabian Standard Time", "亚洲", "阿联酋 AE"),
        new ClockCityOption("阿布扎比", "Abu Dhabi", "Arabian Standard Time", "亚洲", "阿联酋 AE"),
        new ClockCityOption("利雅得", "Riyadh", "Arab Standard Time", "亚洲", "沙特 SA"),
        new ClockCityOption("德黑兰", "Tehran", "Iran Standard Time", "亚洲", "伊朗 IR"),
        new ClockCityOption("巴格达", "Baghdad", "Arabic Standard Time", "亚洲", "伊拉克 IQ"),
        new ClockCityOption("耶路撒冷", "Jerusalem", "Israel Standard Time", "亚洲", "以色列 IL"),
        new ClockCityOption("贝鲁特", "Beirut", "Middle East Standard Time", "亚洲", "黎巴嫩 LB"),
        new ClockCityOption("伊斯坦布尔", "Istanbul", "Turkey Standard Time", "亚洲", "土耳其 TR"),
        new ClockCityOption("第比利斯", "Tbilisi", "Caucasus Standard Time", "亚洲", "格鲁吉亚 GE"),
        new ClockCityOption("巴库", "Baku", "Azerbaijan Standard Time", "亚洲", "阿塞拜疆 AZ"),
        new ClockCityOption("叶卡捷琳堡", "Yekaterinburg", "Ekaterinburg Standard Time", "亚洲", "俄罗斯 RU"),
        new ClockCityOption("新西伯利亚", "Novosibirsk", "N. Central Asia Standard Time", "亚洲", "俄罗斯 RU"),
        new ClockCityOption("克拉斯诺亚尔斯克", "Krasnoyarsk", "North Asia Standard Time", "亚洲", "俄罗斯 RU"),
        new ClockCityOption("伊尔库茨克", "Irkutsk", "North Asia East Standard Time", "亚洲", "俄罗斯 RU"),
        new ClockCityOption("雅库茨克", "Yakutsk", "Yakutsk Standard Time", "亚洲", "俄罗斯 RU"),
        new ClockCityOption("符拉迪沃斯托克", "Vladivostok", "Vladivostok Standard Time", "亚洲", "俄罗斯 海参崴 RU"),
        new ClockCityOption("马加丹", "Magadan", "Magadan Standard Time", "亚洲", "俄罗斯 RU"),
        new ClockCityOption("堪察加彼得罗巴甫洛夫斯克", "Petropavlovsk-Kamchatsky", "Kamchatka Standard Time", "亚洲", "俄罗斯 RU"),

        // ---------- 欧洲 ----------
        new ClockCityOption("莫斯科", "Moscow", "Russian Standard Time", "欧洲", "俄罗斯 RU"),
        new ClockCityOption("圣彼得堡", "Saint Petersburg", "Russian Standard Time", "欧洲", "俄罗斯 RU"),
        new ClockCityOption("明斯克", "Minsk", "Belarus Standard Time", "欧洲", "白俄罗斯 BY"),
        new ClockCityOption("基辅", "Kyiv", "FLE Standard Time", "欧洲", "乌克兰 UA"),
        new ClockCityOption("维尔纽斯", "Vilnius", "FLE Standard Time", "欧洲", "立陶宛 LT"),
        new ClockCityOption("塔林", "Tallinn", "FLE Standard Time", "欧洲", "爱沙尼亚 EE"),
        new ClockCityOption("布加勒斯特", "Bucharest", "GTB Standard Time", "欧洲", "罗马尼亚 RO"),
        new ClockCityOption("雅典", "Athens", "GTB Standard Time", "欧洲", "希腊 GR"),
        new ClockCityOption("赫尔辛基", "Helsinki", "FLE Standard Time", "欧洲", "芬兰 FI"),
        new ClockCityOption("华沙", "Warsaw", "Central European Standard Time", "欧洲", "波兰 PL"),
        new ClockCityOption("柏林", "Berlin", "W. Europe Standard Time", "欧洲", "德国 DE"),
        new ClockCityOption("慕尼黑", "Munich", "W. Europe Standard Time", "欧洲", "德国 DE"),
        new ClockCityOption("罗马", "Rome", "W. Europe Standard Time", "欧洲", "意大利 IT"),
        new ClockCityOption("巴黎", "Paris", "Romance Standard Time", "欧洲", "法国 FR"),
        new ClockCityOption("马德里", "Madrid", "Romance Standard Time", "欧洲", "西班牙 ES"),
        new ClockCityOption("阿姆斯特丹", "Amsterdam", "W. Europe Standard Time", "欧洲", "荷兰 NL"),
        new ClockCityOption("布鲁塞尔", "Brussels", "Romance Standard Time", "欧洲", "比利时 BE"),
        new ClockCityOption("苏黎世", "Zurich", "W. Europe Standard Time", "欧洲", "瑞士 CH"),
        new ClockCityOption("维也纳", "Vienna", "W. Europe Standard Time", "欧洲", "奥地利 AT"),
        new ClockCityOption("布拉格", "Prague", "Central Europe Standard Time", "欧洲", "捷克 CZ"),
        new ClockCityOption("布达佩斯", "Budapest", "Central Europe Standard Time", "欧洲", "匈牙利 HU"),
        new ClockCityOption("斯德哥尔摩", "Stockholm", "W. Europe Standard Time", "欧洲", "瑞典 SE"),
        new ClockCityOption("奥斯陆", "Oslo", "W. Europe Standard Time", "欧洲", "挪威 NO"),
        new ClockCityOption("哥本哈根", "Copenhagen", "W. Europe Standard Time", "欧洲", "丹麦 DK"),
        new ClockCityOption("伦敦", "London", "GMT Standard Time", "欧洲", "英国 英格兰 UK GB"),
        new ClockCityOption("都柏林", "Dublin", "GMT Standard Time", "欧洲", "爱尔兰 IE"),
        new ClockCityOption("里斯本", "Lisbon", "GMT Standard Time", "欧洲", "葡萄牙 PT 葡萄牙"),
        new ClockCityOption("雷克雅未克", "Reykjavik", "Greenwich Standard Time", "欧洲", "冰岛 IS"),

        // ---------- 非洲 ----------
        new ClockCityOption("开罗", "Cairo", "Egypt Standard Time", "非洲", "埃及 EG"),
        new ClockCityOption("约翰内斯堡", "Johannesburg", "South Africa Standard Time", "非洲", "南非 ZA"),
        new ClockCityOption("开普敦", "Cape Town", "South Africa Standard Time", "非洲", "南非 ZA"),
        new ClockCityOption("内罗毕", "Nairobi", "E. Africa Standard Time", "非洲", "肯尼亚 KE"),
        new ClockCityOption("亚的斯亚贝巴", "Addis Ababa", "E. Africa Standard Time", "非洲", "埃塞俄比亚 ET"),
        new ClockCityOption("拉各斯", "Lagos", "W. Central Africa Standard Time", "非洲", "尼日利亚 NG"),
        new ClockCityOption("卡萨布兰卡", "Casablanca", "Morocco Standard Time", "非洲", "摩洛哥 MA"),
        new ClockCityOption("达喀尔", "Dakar", "Greenwich Standard Time", "非洲", "塞内加尔 SN"),

        // ---------- 北美 ----------
        new ClockCityOption("纽约", "New York", "Eastern Standard Time", "北美", "美国 美东 US"),
        new ClockCityOption("华盛顿", "Washington, D.C.", "Eastern Standard Time", "北美", "美国 US"),
        new ClockCityOption("波士顿", "Boston", "Eastern Standard Time", "北美", "美国 US"),
        new ClockCityOption("迈阿密", "Miami", "Eastern Standard Time", "北美", "美国 US"),
        new ClockCityOption("多伦多", "Toronto", "Eastern Standard Time", "北美", "加拿大 CA"),
        new ClockCityOption("芝加哥", "Chicago", "Central Standard Time", "北美", "美国 US"),
        new ClockCityOption("休斯敦", "Houston", "Central Standard Time", "北美", "美国 US"),
        new ClockCityOption("达拉斯", "Dallas", "Central Standard Time", "北美", "美国 US"),
        new ClockCityOption("墨西哥城", "Mexico City", "Central Standard Time (Mexico)", "北美", "墨西哥 MX"),
        new ClockCityOption("丹佛", "Denver", "Mountain Standard Time", "北美", "美国 US"),
        new ClockCityOption("菲尼克斯", "Phoenix", "US Mountain Standard Time", "北美", "美国 凤凰城 US"),
        new ClockCityOption("洛杉矶", "Los Angeles", "Pacific Standard Time", "北美", "美国 美西 US"),
        new ClockCityOption("旧金山", "San Francisco", "Pacific Standard Time", "北美", "美国 三藩市 US"),
        new ClockCityOption("西雅图", "Seattle", "Pacific Standard Time", "北美", "美国 US"),
        new ClockCityOption("温哥华", "Vancouver", "Pacific Standard Time", "北美", "加拿大 CA"),
        new ClockCityOption("安克雷奇", "Anchorage", "Alaskan Standard Time", "北美", "美国 阿拉斯加 US"),
        new ClockCityOption("火奴鲁鲁", "Honolulu", "Hawaiian Standard Time", "北美", "美国 夏威夷檀香山 US"),
        new ClockCityOption("哈瓦那", "Havana", "Cuba Standard Time", "北美", "古巴 CU"),

        // ---------- 南美 ----------
        new ClockCityOption("圣保罗", "São Paulo", "E. South America Standard Time", "南美", "巴西 BR"),
        new ClockCityOption("里约热内卢", "Rio de Janeiro", "E. South America Standard Time", "南美", "巴西 BR"),
        new ClockCityOption("布宜诺斯艾利斯", "Buenos Aires", "Argentina Standard Time", "南美", "阿根廷 AR"),
        new ClockCityOption("蒙得维的亚", "Montevideo", "Montevideo Standard Time", "南美", "乌拉圭 UY"),
        new ClockCityOption("圣地亚哥", "Santiago", "Pacific SA Standard Time", "南美", "智利 CL"),
        new ClockCityOption("利马", "Lima", "SA Pacific Standard Time", "南美", "秘鲁 PE"),
        new ClockCityOption("波哥大", "Bogotá", "SA Pacific Standard Time", "南美", "哥伦比亚 CO"),
        new ClockCityOption("加拉加斯", "Caracas", "Venezuela Standard Time", "南美", "委内瑞拉 VE"),

        // ---------- 大洋洲 ----------
        new ClockCityOption("悉尼", "Sydney", "AUS Eastern Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("墨尔本", "Melbourne", "AUS Eastern Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("布里斯班", "Brisbane", "E. Australia Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("阿德莱德", "Adelaide", "Cen. Australia Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("达尔文", "Darwin", "AUS Central Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("珀斯", "Perth", "W. Australia Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("霍巴特", "Hobart", "Tasmania Standard Time", "大洋洲", "澳大利亚 澳洲 AU"),
        new ClockCityOption("奥克兰", "Auckland", "New Zealand Standard Time", "大洋洲", "新西兰 NZ"),
        new ClockCityOption("惠灵顿", "Wellington", "New Zealand Standard Time", "大洋洲", "新西兰 NZ"),
        new ClockCityOption("苏瓦", "Suva", "Fiji Standard Time", "大洋洲", "斐济 FJ"),
    };

    // ---------------------------------------------------------------- 搜索

    /// <summary>
    /// 按关键词过滤城市库：匹配中文名 / 英文名 / 时区 Id / 大洲名，忽略大小写。
    /// 空关键词返回全部（保持库里的顺序，常用城市在前）。
    /// </summary>
    public static IReadOnlyList<ClockCityOption> Search(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return All;
        var key = keyword.Trim();
        return All.Where(city =>
                Contains(city.Name, key)
                || Contains(city.EnglishName, key)
                || Contains(city.TimeZoneId, key)
                || Contains(city.Region, key)
                || Contains(city.Aliases, key))
            .ToList();
    }

    private static bool Contains(string? source, string keyword)
        => !string.IsNullOrEmpty(source) && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>按中文名精确取一条（首次运行默认城市用；找不到返回 null）。</summary>
    public static ClockCityOption? Find(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.Ordinal));

    /// <summary>某个大洲的城市（列表视图的分组标题用）。</summary>
    public static IReadOnlyList<ClockCityOption> InRegion(string region)
        => All.Where(c => string.Equals(c.Region, region, StringComparison.Ordinal)).ToList();
}
