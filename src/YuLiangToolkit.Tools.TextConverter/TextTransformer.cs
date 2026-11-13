using System.Text;

namespace YuLiangToolkit.Tools.TextConverter;

/// <summary>一种文本转换。</summary>
/// <param name="Id">配置里存的稳定 Id（不要改，改了老配置就失效）。</param>
/// <param name="Name">界面上显示的名字。</param>
/// <param name="Group">分组（大小写 / 火星文 / Emoji / 形近字 / 编码 / 清理）。</param>
/// <param name="Description">一句话说明（悬停提示）。</param>
/// <param name="IsToggle">是不是「点一下还原」的开关型转换（火星文这类可逆的正则替换）。</param>
public sealed record TextConversion(
    string Id,
    string Name,
    string Group,
    string Description,
    bool IsToggle = false);

/// <summary>
/// 文本转换器（**纯逻辑**）：所有转换都是无副作用的纯函数，自检可以逐条断言。
///
/// <para>覆盖这些类别：</para>
/// <list type="bullet">
///   <item><b>大小写</b>：全大写 / 全小写 / 首字母大写 / 词首大写 / 反转大小写；</item>
///   <item><b>火星文 / 风格</b>：繁体、火星文、圈字、花体字母、小号大写字母、全角、倒置；</item>
///   <item><b>Emoji</b>：把字面里能对上表情的词换成表情（牛马 → 🐮🐴）。</item>
///   <item><b>形近字</b>：同音 / 形近替换；</item>
///   <item><b>编码</b>：Base64、URL 编码、摩斯电码；</item>
///   <item><b>清理</b>：去空格、去换行、去空行、去重、反转、每行加序号。</item>
/// </list>
/// </summary>
public static class TextTransformer
{
    // ================================================================== 注册表

    /// <summary>全部可用的转换（界面按 <see cref="TextConversion.Group"/> 分组展示）。</summary>
    public static IReadOnlyList<TextConversion> All { get; } = new[]
    {
        // ---------- 大小写 ----------
        new TextConversion("upper", "全大写", "大小写", "把字母都变成大写（ABC）"),
        new TextConversion("lower", "全小写", "大小写", "把字母都变成小写（abc）"),
        new TextConversion("title", "首字母大写", "大小写", "每个词的首字母大写（Hello World）"),
        new TextConversion("sentence", "句首大写", "大小写", "整段只把第一个字母大写"),
        new TextConversion("swapcase", "反转大小写", "大小写", "大写变小写、小写变大写（aB → Ab）"),

        // ---------- 风格 ----------
        new TextConversion("traditional", "转繁体", "风格", "把常见简体字换成繁体（可再点一次转回）", true),
        new TextConversion("mars", "火星文", "风格", "繁简混搭 + 常用火星字（可再点一次转回）", true),
        new TextConversion("circled", "圈字 ①②③", "风格", "数字与字母变成圆圈符号"),
        new TextConversion("fullwidth", "全角", "风格", "字母数字变成全角（ＡＢＣ１２３）", true),
        new TextConversion("smallcaps", "小号大写字母", "风格", "把字母换成 ᴀʙᴄ 这类小型大写"),
        new TextConversion("script", "花体字母", "风格", "把字母换成 𝓪𝓫𝓬 这类手写体"),
        new TextConversion("upsidedown", "倒置文字", "风格", "把文字上下颠倒（ǝpᴉsdn）", true),

        // ---------- Emoji ----------
        new TextConversion("emoji", "转 Emoji", "Emoji", "把能对上表情的词换成表情（牛马 → 🐮🐴）"),
        new TextConversion("emoji-space", "Emoji（逐字）", "Emoji", "每个能对上的字都换成表情，对不上的保留"),

        // ---------- 形近字 ----------
        new TextConversion("homophone", "同音字", "形近字", "把常见字换成同音字（牛 → 妞）"),

        // ---------- 编码 ----------
        new TextConversion("base64", "Base64 编码", "编码", "编码成 Base64"),
        new TextConversion("base64-decode", "Base64 解码", "编码", "把 Base64 解回文本"),
        new TextConversion("url", "URL 编码", "编码", "把字符转成 %XX"),
        new TextConversion("url-decode", "URL 解码", "编码", "把 %XX 解回字符"),
        new TextConversion("morse", "摩斯电码", "编码", "把字母数字转成 ·− 序列"),

        // ---------- 清理 ----------
        new TextConversion("trim", "去首尾空格", "清理", "去掉每行开头和结尾的空白"),
        new TextConversion("no-space", "去掉所有空格", "清理", "删掉所有空格与制表符"),
        new TextConversion("no-newline", "合并成一行", "清理", "把换行换成空格"),
        new TextConversion("no-empty-line", "去掉空行", "清理", "删掉所有空白行"),
        new TextConversion("reverse", "反转文字", "清理", "倒着排（按字符）"),
        new TextConversion("dedupe-chars", "字符去重", "清理", "同一个字只留第一次出现"),
    };

    /// <summary>按 Id 找转换（找不到返回 null）。</summary>
    public static TextConversion? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Id, id.Trim(), StringComparison.Ordinal));

    // ================================================================== 执行

    /// <summary>
    /// 执行一次转换。<paramref name="id"/> 认不出来时原样返回（并标记失败原因）。
    /// </summary>
    /// <param name="text">选中的文本。</param>
    /// <param name="id">转换 Id（见 <see cref="All"/>）。</param>
    public static (string Result, string Error) Apply(string text, string? id)
    {
        if (Find(id) is not { } conversion) return (text, "这种转换不认识");
        if (string.IsNullOrEmpty(text)) return (text, "");

        try
        {
            var result = conversion.Id switch
            {
                "upper" => text.ToUpperInvariant(),
                "lower" => text.ToLowerInvariant(),
                "title" => ToTitleCase(text),
                "sentence" => ToSentenceCase(text),
                "swapcase" => SwapCase(text),

                "traditional" => MapTraditionalAuto(text),
                "mars" => MapMars(text),

                "circled" => MapCircled(text),
                "fullwidth" => MapFullwidth(text),
                "smallcaps" => MapSmallCaps(text),
                "script" => MapScript(text),
                "upsidedown" => MapUpsideDown(text),

                "emoji" => MapEmoji(text, everyChar: false),
                "emoji-space" => MapEmoji(text, everyChar: true),

                "homophone" => MapHomophone(text),

                "base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                "base64-decode" => DecodeBase64(text),
                "url" => Uri.EscapeDataString(text),
                "url-decode" => Uri.UnescapeDataString(text),
                "morse" => MapMorse(text),

                "trim" => TrimLines(text),
                "no-space" => text.Replace(" ", "").Replace("\t", ""),
                "no-newline" => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '),
                "no-empty-line" => RemoveEmptyLines(text),
                "reverse" => ReverseText(text),
                "dedupe-chars" => DedupeChars(text),

                _ => text,
            };

            return (result, "");
        }
        catch (Exception ex)
        {
            return (text, "转换失败：" + ex.Message);
        }
    }

    // ================================================================== 大小写

    private static string ToTitleCase(string text)
    {
        var builder = new StringBuilder(text.Length);
        var newWord = true;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                builder.Append(ch);
                newWord = true;
                continue;
            }

            builder.Append(newWord ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
            newWord = false;
        }

        return builder.ToString();
    }

    private static string ToSentenceCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        var firstLetterDone = false;

        foreach (var ch in text)
        {
            if (!firstLetterDone && char.IsLetter(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
                firstLetterDone = true;
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static string SwapCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(char.IsUpper(ch) ? char.ToLowerInvariant(ch)
                : char.IsLower(ch) ? char.ToUpperInvariant(ch)
                : ch);
        }

        return sb.ToString();
    }

    // ================================================================== 繁简 / 火星文

    /// <summary>
    /// 常见的繁简对照（**双向**替换：一次转过去、再点一次转回来）。
    /// 只收常用字，够用且不会把生僻字翻错。
    /// </summary>
    private static readonly (char Simple, char Traditional)[] TraditionalPairs =
    {
        ('爱', '愛'), ('办', '辦'), ('帮', '幫'), ('报', '報'), ('边', '邊'), ('变', '變'), ('别', '別'),
        ('参', '參'), ('产', '產'), ('长', '長'), ('场', '場'), ('车', '車'), ('成', '成'), ('迟', '遲'),
        ('处', '處'), ('传', '傳'), ('创', '創'), ('从', '從'), ('错', '錯'), ('达', '達'), ('带', '帶'),
        ('单', '單'), ('当', '當'), ('党', '黨'), ('导', '導'), ('到', '到'), ('得', '得'), ('灯', '燈'),
        ('敌', '敵'), ('点', '點'), ('电', '電'), ('调', '調'), ('东', '東'), ('动', '動'), ('对', '對'),
        ('儿', '兒'), ('发', '發'), ('饭', '飯'), ('飞', '飛'), ('费', '費'), ('风', '風'), ('个', '個'),
        ('给', '給'), ('关', '關'), ('观', '觀'), ('国', '國'), ('过', '過'), ('还', '還'), ('汉', '漢'),
        ('号', '號'), ('红', '紅'), ('后', '後'), ('华', '華'), ('话', '話'), ('欢', '歡'), ('会', '會'),
        ('几', '幾'), ('记', '記'), ('问', '問'), ('间', '間'), ('见', '見'), ('将', '將'), ('教', '教'),
        ('觉', '覺'), ('节', '節'), ('紧', '緊'), ('进', '進'), ('经', '經'), ('开', '開'), ('看', '看'),
        ('来', '來'), ('乐', '樂'), ('里', '裡'), ('两', '兩'), ('买', '買'), ('卖', '賣'), ('门', '門'),
        ('们', '們'), ('梦', '夢'), ('难', '難'), ('能', '能'), ('鸟', '鳥'), ('钱', '錢'), ('请', '請'),
        ('让', '讓'), ('热', '熱'), ('认', '認'), ('时', '時'), ('实', '實'), ('书', '書'), ('说', '說'),
        ('谁', '誰'), ('岁', '歲'), ('听', '聽'), ('头', '頭'), ('万', '萬'), ('为', '為'), ('卫', '衛'),
        ('问', '問'), ('无', '無'), ('习', '習'), ('现', '現'), ('写', '寫'), ('学', '學'), ('样', '樣'),
        ('药', '藥'), ('业', '業'), ('义', '義'), ('应', '應'), ('银', '銀'), ('语', '語'), ('园', '園'),
        ('远', '遠'), ('愿', '願'), ('运', '運'), ('这', '這'), ('只', '隻'), ('种', '種'), ('总', '總'),
    };

    private static readonly Dictionary<char, char> ToTraditionalMap =
        TraditionalPairs.GroupBy(p => p.Simple).ToDictionary(g => g.Key, g => g.First().Traditional);

    private static readonly Dictionary<char, char> ToSimplifiedMap =
        TraditionalPairs.GroupBy(p => p.Traditional).ToDictionary(g => g.Key, g => g.First().Simple);

    /// <summary>
    /// 繁简自动转换：文本里已经有繁体字就转回简体，否则转繁体。
    /// <para>这样一个按钮就能来回切，不用再给"转简体"单独占一项。</para>
    /// </summary>
    private static string MapTraditionalAuto(string text)
    {
        // 只要出现任意一个繁体字，就认为用户想转回简体
        var hasTraditional = text.Any(ch => ToSimplifiedMap.ContainsKey(ch));
        return MapTraditional(text, toTraditional: !hasTraditional);
    }

    /// <summary>
    /// 繁简转换。<paramref name="toTraditional"/> = true 转繁体，false 转简体。
    /// </summary>
    private static string MapTraditional(string text, bool toTraditional)
    {
        var map = toTraditional ? ToTraditionalMap : ToSimplifiedMap;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(map.TryGetValue(ch, out var mapped) ? mapped : ch);
        return sb.ToString();
    }

    /// <summary>火星文：常见字换成"火星"写法（偏旁/形近字混搭）。</summary>
    private static readonly Dictionary<char, char> MarsMap = new()
    {
        ['我'] = '莪', ['你'] = '伱', ['他'] = '彵', ['爱'] = '噯', ['的'] = '嘚', ['了'] = 'ㄋ',
        ['是'] = '湜', ['不'] = '吥', ['在'] = '茬', ['有'] = '洧', ['就'] = '僦', ['都'] = '嘟',
        ['很'] = '痕', ['这'] = '適', ['那'] = '哪', ['什'] = '甚', ['么'] = '庅', ['吗'] = '嗎',
        ['吧'] = 'の', ['啊'] = '銨', ['哦'] = '噢', ['嗯'] = '摁', ['会'] = '檜', ['说'] = '説',
        ['看'] = '瞰', ['好'] = '恏', ['想'] = '想', ['笑'] = '誚', ['哭'] = '庫', ['傻'] = '儍',
        ['笨'] = '苯', ['天'] = '兲', ['真'] = '眞', ['人'] = '亽', ['心'] = '訫', ['情'] = '埥',
    };

    /// <summary>火星文（可逆：再点一次会把能对上的还原成简体）。</summary>
    private static string MapMars(string text)
    {
        // 先尝试「还原」：如果全是火星字，就还原（让这个转换可以点两下切回去）
        var revert = new Dictionary<char, char>();
        foreach (var (simple, mars) in MarsMap) revert[mars] = simple;

        var canRevert = text.Any(ch => revert.ContainsKey(ch));
        var map = canRevert ? revert : MarsMap;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(map.TryGetValue(ch, out var mapped) ? mapped : ch);
        return sb.ToString();
    }

    // ================================================================== 花式字母

    /// <summary>
    /// 圆圈字符：数字 0-9 与字母 a-z 的 ①②③ / ⓐⓑⓒ 形式（可逆）。
    /// </summary>
    private static string MapCircled(string text)
    {
        const string digits = "0123456789";
        const string circledDigits = "⓪①②③④⑤⑥⑦⑧⑨";     // 0~9（1~9 用 ①~⑨）
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string circledLower = "ⓐⓑⓒⓓⓔⓕⓖⓗⓘⓙⓚⓛⓜⓝⓞⓟⓠⓡⓢⓣⓤⓥⓦⓧⓨⓩ";
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string circledUpper = "ⒶⒷⒸⒹⒺⒻⒼⒽⒾⒿⓀⓁⓂⓃⓄⓅⓆⓇⓈⓉⓊⓋⓌⓍⓎⓏ";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var index = digits.IndexOf(ch);
            if (index >= 0) { sb.Append(circledDigits[index]); continue; }

            index = lower.IndexOf(ch);
            if (index >= 0) { sb.Append(circledLower[index]); continue; }

            index = upper.IndexOf(ch);
            if (index >= 0) { sb.Append(circledUpper[index]); continue; }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>全角（可逆：全角字符再转一次会回到半角）。</summary>
    private static string MapFullwidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // 全角 ASCII 区（U+FF01~U+FF5E）与半角 ASCII（U+0021~U+007E）互相映射
            if (ch is >= '!' and <= '~') sb.Append((char)(ch + 0xFEE0));
            else if (ch is >= '\uFF01' and <= '\uFF5E') sb.Append((char)(ch - 0xFEE0));
            else if (ch == ' ') sb.Append('\u3000');
            else if (ch == '\u3000') sb.Append(' ');
            else sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>小号大写字母（ᴀʙᴄ）。</summary>
    private static string MapSmallCaps(string text)
    {
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string smallCaps = "ᴀʙᴄᴅᴇꜰɢʜɪᴊᴋʟᴍɴᴏᴘqʀꜱᴛᴜᴠᴡxʏᴢ";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var index = lower.IndexOf(char.ToLowerInvariant(ch));
            sb.Append(index >= 0 ? smallCaps[index] : ch);
        }

        return sb.ToString();
    }

    /// <summary>花体字母（𝓪𝓫𝓬）。</summary>
    private static string MapScript(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z') sb.Append(char.ConvertFromUtf32(0x1D4EA + (ch - 'a')));
            else if (ch is >= 'A' and <= 'Z') sb.Append(char.ConvertFromUtf32(0x1D4D0 + (ch - 'A')));
            else sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>倒置文字（可逆：再转一次会转回来）。</summary>
    private static readonly Dictionary<char, char> UpsideDownMap = new()
    {
        ['a'] = 'ɐ', ['b'] = 'q', ['c'] = 'ɔ', ['d'] = 'p', ['e'] = 'ǝ', ['f'] = 'ɟ', ['g'] = 'ƃ',
        ['h'] = 'ɥ', ['i'] = 'ᴉ', ['j'] = 'ɾ', ['k'] = 'ʞ', ['l'] = 'l', ['m'] = 'ɯ', ['n'] = 'u',
        ['o'] = 'o', ['p'] = 'd', ['q'] = 'b', ['r'] = 'ɹ', ['s'] = 's', ['t'] = 'ʇ', ['u'] = 'n',
        ['v'] = 'ʌ', ['w'] = 'ʍ', ['x'] = 'x', ['y'] = 'ʎ', ['z'] = 'z',
        ['!'] = '¡', ['?'] = '¿', ['0'] = '0', ['1'] = 'Ɩ', ['2'] = 'ᄅ', ['3'] = 'Ɛ', ['4'] = 'ㄣ',
        ['5'] = 'ϛ', ['6'] = '9', ['7'] = 'ㄥ', ['8'] = '8', ['9'] = '6',
        ['，'] = '，', ['。'] = '。',
    };

    private static string MapUpsideDown(string text)
    {
        // 反向映射（用于还原）
        var reversed = new Dictionary<char, char>();
        foreach (var (from, to) in UpsideDownMap)
        {
            if (!reversed.ContainsKey(to)) reversed[to] = from;
        }

        // 已经倒置过？先还原（顺带把顺序倒回来）
        var canRevert = text.Any(ch => reversed.ContainsKey(ch) && !UpsideDownMap.ContainsKey(ch));
        var map = canRevert ? reversed : UpsideDownMap;

        // 倒置前后的顺序都是反的：转过去要反转、转回来也要反转（否则 abc → cba 而不是 abc）
        var chars = text.ToCharArray();
        Array.Reverse(chars);

        var sb = new StringBuilder(chars.Length);
        foreach (var ch in chars)
        {
            var lower = char.ToLowerInvariant(ch);
            sb.Append(map.TryGetValue(lower, out var mapped) ? mapped : ch);
        }

        return sb.ToString();
    }

    // ================================================================== Emoji

    /// <summary>
    /// 字 → 表情。**只收「词 / 字能对上表情」的常见项**，做得太激进会把正常话变成一堆看不懂的图标。
    /// <para>按「最长匹配优先」匹配：先试两个字的词（牛马、开心），再试单字。</para>
    /// </summary>
    private static readonly Dictionary<string, string> EmojiWords = new()
    {
        // 动物（叠字 + 牛羊这类最容易玩梗的）
        ["牛"] = "🐮", ["马"] = "🐴", ["猪"] = "🐷", ["狗"] = "🐶", ["猫"] = "🐱", ["鸡"] = "🐔",
        ["鱼"] = "🐟", ["兔"] = "🐰", ["虎"] = "🐯", ["龙"] = "🐲", ["蛇"] = "🐍", ["羊"] = "🐑",
        ["猴"] = "🐵", ["鸟"] = "🐦", ["熊"] = "🐻", ["鼠"] = "🐭", ["鸭"] = "🦆", ["鹅"] = "🦢",
        ["虫"] = "🐛", ["蜂"] = "🐝", ["蟹"] = "🦀", ["虾"] = "🦐", ["鲸"] = "🐳", ["鲨"] = "🦈",

        // 天气 / 自然
        ["太阳"] = "☀️", ["月亮"] = "🌙", ["星星"] = "⭐", ["云"] = "☁️", ["雨"] = "🌧️",
        ["雪"] = "❄️", ["风"] = "🌬️", ["火"] = "🔥", ["水"] = "💧", ["雷"] = "⚡",
        ["树"] = "🌳", ["花"] = "🌸", ["草"] = "🌿", ["山"] = "⛰️", ["海"] = "🌊",

        // 情绪 / 表情
        ["开心"] = "😄", ["高兴"] = "😄", ["笑"] = "😄", ["哭"] = "😭", ["难过"] = "😢",
        ["生气"] = "😡", ["怒"] = "😠", ["累"] = "😩", ["困"] = "😴", ["睡"] = "😴",
        ["爱"] = "❤️", ["喜欢"] = "😍", ["害羞"] = "😳", ["无语"] = "😑", ["尴尬"] = "😅",
        ["惊"] = "😲", ["怕"] = "😨", ["酷"] = "😎", ["加油"] = "💪", ["赞"] = "👍",
        ["好"] = "👌", ["不行"] = "🙅", ["ok"] = "👌", ["抱抱"] = "🤗", ["亲"] = "😘",

        // 物件 / 食物
        ["饭"] = "🍚", ["面"] = "🍜", ["苹果"] = "🍎", ["香蕉"] = "🍌", ["西瓜"] = "🍉",
        ["咖啡"] = "☕", ["茶"] = "🍵", ["酒"] = "🍺", ["蛋糕"] = "🎂", ["钱"] = "💰",
        ["手机"] = "📱", ["电脑"] = "💻", ["书"] = "📖", ["笔"] = "✏️", ["车"] = "🚗",
        ["飞机"] = "✈️", ["房子"] = "🏠", ["礼物"] = "🎁", ["音乐"] = "🎵", ["游戏"] = "🎮",
        ["球"] = "⚽", ["时间"] = "⏰", ["钟"] = "⏰", ["礼物"] = "🎁",

        // 手势 / 符号
        ["心"] = "❤️", ["对"] = "✔️", ["错"] = "❌", ["问"] = "❓", ["注意"] = "⚠️",
        ["新"] = "🆕", ["免费"] = "🆓", ["第一"] = "🥇", ["完美"] = "💯", ["厉害"] = "🔥",
    };

    /// <summary>
    /// Emoji 转换。
    /// <para><paramref name="everyChar"/> = true 时逐字匹配（「牛马」→ 🐮🐴）；
    /// false 时优先匹配整词（「开心」→ 😄，不会被拆成两个字）。</para>
    /// </summary>
    private static string MapEmoji(string text, bool everyChar)
    {
        // 词表按长度倒序，保证「最长匹配优先」。
        // ⚠ 逐字模式下**先把多字词全部剔除**：否则循环里 continue 掉多字词后，
        // 后面的单字词仍然可能匹配（"开心"会变成 "开" + 心 的 emoji 或残留）。
        var ordered = EmojiWords
            .Where(pair => !everyChar || pair.Key.Length == 1)
            .OrderByDescending(pair => pair.Key.Length)
            .ToList();

        var sb = new StringBuilder(text.Length * 2);
        var index = 0;

        while (index < text.Length)
        {
            var matched = false;

            foreach (var (word, emoji) in ordered)
            {
                if (index + word.Length > text.Length) continue;
                if (string.Compare(text, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;

                sb.Append(emoji);
                index += word.Length;
                matched = true;
                break;
            }

            if (!matched)
            {
                // 按「一个完整的字符（可能是代理对）」复制，不能按 char 切 ——
                // 否则原来文本里的 emoji 会被拆成半个、变成 ?
                var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
                sb.Append(text, index, length);
                index += length;
            }
        }

        return sb.ToString();
    }

    // ================================================================== 同音字

    /// <summary>常见字的同音替换（只收「换个同音字还看得懂」的那些）。</summary>
    private static readonly Dictionary<char, char> HomophoneMap = new()
    {
        ['牛'] = '妞', ['马'] = '骂', ['你'] = '尼', ['我'] = '窝', ['他'] = '它', ['好'] = '号',
        ['是'] = '事', ['的'] = '得', ['了'] = '乐', ['有'] = '友', ['在'] = '再', ['不'] = '布',
        ['会'] = '回', ['说'] = '说', ['人'] = '仁', ['天'] = '添', ['大'] = '达', ['小'] = '晓',
        ['上'] = '尚', ['下'] = '夏', ['中'] = '钟', ['多'] = '夺', ['少'] = '绍', ['来'] = '莱',
        ['去'] = '趣', ['看'] = '砍', ['想'] = '响', ['爱'] = '艾', ['笑'] = '校', ['哭'] = '库',
    };

    private static string MapHomophone(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(HomophoneMap.TryGetValue(ch, out var mapped) ? mapped : ch);
        return sb.ToString();
    }

    // ================================================================== 编码

    private static string DecodeBase64(string text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text.Trim());
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            // 把 .NET 的英文报错换成中文（界面直接显示这句）
            throw new FormatException("这不是合法的 Base64（可能少了几位、或混进了中文标点）");
        }
    }

    /// <summary>摩斯电码（字母 / 数字，其它字符原样保留）。</summary>
    private static readonly Dictionary<char, string> MorseMap = new()
    {
        ['a'] = ".-", ['b'] = "-...", ['c'] = "-.-.", ['d'] = "-..", ['e'] = ".", ['f'] = "..-.",
        ['g'] = "--.", ['h'] = "....", ['i'] = "..", ['j'] = ".---", ['k'] = "-.-", ['l'] = ".-..",
        ['m'] = "--", ['n'] = "-.", ['o'] = "---", ['p'] = ".--.", ['q'] = "--.-", ['r'] = ".-.",
        ['s'] = "...", ['t'] = "-", ['u'] = "..-", ['v'] = "...-", ['w'] = ".--", ['x'] = "-..-",
        ['y'] = "-.--", ['z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['.'] = ".-.-.-", [','] = "--..--", ['?'] = "..--..", ['!'] = "-.-.--", ['/'] = "-..-.",
        ['-'] = "-....-", ['@'] = ".--.-.",
    };

    private static string MapMorse(string text)
    {
        var parts = new List<string>();

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                parts.Add("/");   // 词间隔
                continue;
            }

            var lower = char.ToLowerInvariant(ch);
            parts.Add(MorseMap.TryGetValue(lower, out var code) ? code : "?");
        }

        // 连续 / 只留一个
        var builder = new StringBuilder();
        var lastWasSlash = false;

        foreach (var part in parts)
        {
            var isSlash = part == "/";
            if (isSlash && lastWasSlash) continue;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(part);
            lastWasSlash = isSlash;
        }

        return builder.ToString();
    }

    // ================================================================== 清理

    private static string TrimLines(string text)
        => string.Join(Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()));

    private static string RemoveEmptyLines(string text)
        => string.Join(Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Where(line => line.Trim().Length > 0));

    private static string ReverseText(string text)
    {
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static string DedupeChars(string text)
    {
        var seen = new HashSet<char>();
        var sb = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (seen.Add(ch)) sb.Append(ch);
        }

        return sb.ToString();
    }
}
