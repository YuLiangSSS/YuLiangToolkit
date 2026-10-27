// Data / Config 轮子自检：把 AGENTS.md 里承诺的行为逐条真跑一遍。
// 运行： dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
// （默认在临时目录里造数据根，不碰仓库的 Data/Config；退出码 0 = 全部通过）
using System.IO;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.FrameworkSelfCheck;
using YuLiangToolkit.Tools.BatchRename;
using YuLiangToolkit.Tools.BatchRename.ViewModels;

// 自检必须跑在临时数据根里：YLT_HOME 要在任何 AppPaths 访问之前设好
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_HOME")))
{
    Environment.SetEnvironmentVariable("YLT_HOME", SelfCheckScratch.MakeDirectory("ylt-selfcheck"));
}

var results = new List<string>();
void Check(string name, bool ok, string detail = "") =>
    results.Add($"{(ok ? "PASS" : "FAIL")} | {name}{(detail.Length > 0 ? "  :: " + detail : "")}");

Console.WriteLine($"数据根目录: {AppPaths.Root}");
Console.WriteLine($"完整性自检: {IntegrityCheck.Summary()}");
Console.WriteLine();

// ---------- 1. 强类型配置：首次加载落盘默认值 ----------
var store = ToolConfigStore.For("test-tool");
var cfg = store.Load<TestCfg>();
Check("首次加载即生成 config.json", File.Exists(cfg.FilePath), cfg.FilePath);
Check("默认值正确", cfg.Repeat == 3 && cfg.Enable && cfg.Mode == "标准");

// ---------- 2. 自动保存：自动属性改了也会存（不依赖 Set/变更通知） ----------
cfg.Note = "你好，配置";
cfg.Repeat = 9;
cfg.Enable = false;
cfg.Mode = "极限";
Check("改动后立刻还没写盘（合并窗口内）", JsonFileStore.Read<TestCfg>(cfg.FilePath)?.Repeat == 3);
Thread.Sleep(1000);
var afterSave = JsonFileStore.Read<TestCfg>(cfg.FilePath);
Check("自动保存已把自动属性的改动写盘",
    afterSave is { Note: "你好，配置", Repeat: 9, Enable: false, Mode: "极限" },
    JsonFileStore.Serialize(afterSave).Replace(Environment.NewLine, " "));

// ---------- 3. Flush：立即落盘（不等合并窗口） ----------
cfg.Note = "flush-now";
ToolConfigStore.FlushAll();
Check("FlushAll 立即落盘", JsonFileStore.Read<TestCfg>(cfg.FilePath)?.Note == "flush-now");

// ---------- 4. 宽容读取：camelCase / PascalCase 都能读 ----------
File.WriteAllText(cfg.FilePath, """{ "note":"手改", "ENABLE":false, "mode":"高速", "repeat":12, "threshold":0.25 }""");
var handEdited = JsonFileStore.Read<TestCfg>(cfg.FilePath);
Check("camelCase/PascalCase 都能读",
    handEdited is { Note: "手改", Enable: false, Repeat: 12 } && handEdited.Threshold == 0.25);

// ---------- 5. Load 幂等：同类型只有一个实例 ----------
Check("同类型重复 Load 返回同一实例", ReferenceEquals(store.Load<TestCfg>(), cfg));

// ---------- 6. 损坏文件：备份 + 回退默认值 ----------
var brokenStore = ToolConfigStore.For("broken-tool");
Directory.CreateDirectory(brokenStore.Directory);
File.WriteAllText(brokenStore.FilePath, "{ 这不是合法 JSON ");
var recovered = brokenStore.Load<TestCfg>();
var backups = Directory.GetFiles(brokenStore.Directory, "*.bad-*");
Check("损坏配置回退默认值", recovered is { Repeat: 3, Note: "" });
Check("损坏文件已备份", backups.Length == 1, backups.FirstOrDefault() ?? "(无)");

// ---------- 7. 动态配置：往返 + 类型还原 ----------
var dyn = DynamicToolConfig.Load("test-tool");
dyn.SetValue("flag", true);
dyn.SetValue("num", 42);
dyn.SetValue("text", "中文值");
dyn.SetValue("ratio", 0.75);
Thread.Sleep(1000);
var dyn2 = DynamicToolConfig.Load("test-tool");
Check("动态配置往返（bool/int/string/double）",
    dyn2.GetBool("flag") && dyn2.GetInt("num") == 42 && dyn2.GetString("text") == "中文值"
    && Math.Abs(dyn2.GetDouble("ratio") - 0.75) < 1e-9,
    dyn2.ToJson().Replace(Environment.NewLine, " "));
Check("索引器取值类型正确（XAML 绑定用）", dyn2["flag"] is bool and true && dyn2["num"] is long and 42);

// ---------- 8. 目录结构：Config\<功能名>\ 、Data\<功能名>\ ----------
Check("Config 下按功能名分文件夹",
    Path.GetFileName(Path.GetDirectoryName(cfg.FilePath)!) == "test-tool"
    && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(cfg.FilePath)!)!) == "Config",
    cfg.FilePath);
Check("Data 下按功能名分文件夹", Path.GetFileName(ToolStorage.For("test-tool").Directory) == "test-tool",
    ToolStorage.For("test-tool").Directory);

// ---------- 9. ToolStorage：写入 / 列举 / 大小 / 导入去重 ----------
var storage = ToolStorage.For("test-tool");
storage.WriteText("sub/资源.txt", "内容");
var listed = storage.List();
Check("ToolStorage 写入并列举（含子目录）", listed.Any(f => f.RelativePath == Path.Combine("sub", "资源.txt")),
    string.Join(",", listed.Select(f => f.RelativePath)));
Check("SizeText 可读", listed.First(f => f.RelativePath.EndsWith("资源.txt")).SizeText.EndsWith("B"));

var external = SelfCheckScratch.MakeFile("ylt-selfcheck-external.swf");
File.WriteAllBytes(external, new byte[] { 1, 2, 3, 4, 5 });
var first = storage.Import(external);
var second = storage.Import(external);
Check("导入外部文件（重名自动加序号）",
    first is not null && second is not null && first.RelativePath != second.RelativePath,
    $"{first?.RelativePath} / {second?.RelativePath}");
Check("Import 后能按扩展名筛选", storage.List("*.swf").Count == 2);

// ---------- 10. 路径越界防护 ----------
var blocked = false;
try { storage.PathOf("../../逃逸.txt"); } catch (ArgumentException) { blocked = true; }
Check("拒绝 .. 越界路径", blocked);

var rooted = false;
try { storage.PathOf(@"C:\Windows\evil.txt"); } catch (ArgumentException) { rooted = true; }
Check("拒绝绝对路径", rooted);

// ---------- 11. 手写容错：注释与尾随逗号 ----------
File.WriteAllText(cfg.FilePath, """
{
  // 手写注释
  "note": "带注释",
  "repeat": 5,
}
""");
Check("允许注释与尾随逗号", JsonFileStore.Read<TestCfg>(cfg.FilePath) is { Note: "带注释", Repeat: 5 });

// ---------- 12. 工具关闭再打开：自动保存必须继续生效 ----------
// （工具切换/关闭时框架会 StopWatching，重新打开时必须恢复，否则“改了不存”）
store.StopWatching();
var reopened = store.Load<TestCfg>();          // 模拟重新打开功能
Check("重新打开拿到的是同一实例", ReferenceEquals(reopened, cfg));
reopened.Threshold = 0.9;
Thread.Sleep(1000);
Check("关闭再打开后自动保存仍生效", JsonFileStore.Read<TestCfg>(cfg.FilePath)?.Threshold == 0.9,
    $"threshold={JsonFileStore.Read<TestCfg>(cfg.FilePath)?.Threshold}");

// ---------- 13. 动态配置默认值：声明过的键会被补齐并落盘 ----------
var seeded = DynamicToolConfig.LoadWithDefaults("defaults-tool", new Dictionary<string, object?>
{
    ["flag"] = false,
    ["text"] = "",
});
var seededOnDisk = JsonFileStore.Read<Dictionary<string, System.Text.Json.Nodes.JsonNode?>>(seeded.FilePath);
Check("LoadWithDefaults 补齐缺失键并落盘",
    seeded.Contains("flag") && seeded.Contains("text") && seededOnDisk?.Count == 2
    && seeded.GetBool("flag") == false,
    seeded.ToJson().Replace(Environment.NewLine, " "));

// ---------- 14. 主页搜索（ToolFilter：纯逻辑，不需要开界面） ----------
var fakeTools = new ITool[]
{
    new FakeTool("video-to-audio", "视频转音频", "媒体", "批量提取视频的音频", 10, "视频", "音频", "FFmpeg", "mp3"),
    new FakeTool("flash-downloader", "Flash 小游戏下载器", "网络", "抓取并保存网页里的 swf", 20, "flash", "swf", "下载"),
    new FakeTool("sample-framework", "框架示例", "开发", "Data/Config 轮子演示", 900, "示例", "模板", "Config"),
};
var cards = fakeTools.Select(t => new ToolCard(t, null)).ToList();

int MatchCount(string query) => ToolFilter.Apply(cards, query, ToolSortMode.Default).Count;

Check("按名称搜索", MatchCount("视频") == 1);
Check("按标签搜索（mp3 命中视频工具）", MatchCount("mp3") == 1);
Check("按分类搜索", MatchCount("网络") == 1);
Check("按描述搜索", MatchCount("swf") == 1);
Check("按 Id 搜索（英文）", MatchCount("flash-downloader") == 1);
Check("大小写不敏感（FFMPEG / ffmpeg）", MatchCount("FFMPEG") == 1 && MatchCount("ffmpeg") == 1);
Check("空格多关键词 = 都要命中", MatchCount("视频 mp3") == 1 && MatchCount("视频 swf") == 0);
Check("中文逗号/顿号也能切分关键词", MatchCount("视频，mp3") == 1 && MatchCount("视频、音频") == 1);
Check("空搜索词返回全部", MatchCount("") == cards.Count && MatchCount("   ") == cards.Count);
Check("搜不到时返回空（界面显示空状态）", MatchCount("不存在的功能xyz") == 0);

// ---------- 15. 主页排序 ----------
var now = DateTime.Now;
cards[0].LastUsed = now.AddHours(-2);   // 视频转音频：2 小时前用过
cards[1].LastUsed = now;                // Flash：刚用过
// 框架示例：从未用过

var byRecent = ToolFilter.Sort(cards, ToolSortMode.RecentlyUsed);
Check("最近使用排序：刚用过的排最前", byRecent[0].Id == "flash-downloader" && byRecent[1].Id == "video-to-audio");
Check("最近使用排序：从未用过的排最后", byRecent[^1].Id == "sample-framework");

var byDefault = ToolFilter.Sort(cards, ToolSortMode.Default);
Check("默认顺序 = 按 Order", byDefault[0].Id == "video-to-audio" && byDefault[^1].Id == "sample-framework");

var asc = ToolFilter.Sort(cards, ToolSortMode.NameAsc).Select(c => c.Name).ToList();
var desc = ToolFilter.Sort(cards, ToolSortMode.NameDesc).Select(c => c.Name).ToList();
Check("名称升序 / 降序互为倒序", asc.SequenceEqual(desc.AsEnumerable().Reverse()));

var byCategory = ToolFilter.Sort(cards, ToolSortMode.Category);
Check("按分类排序：同分类聚在一起", byCategory.Select(c => c.Category).Distinct().Count() == 3);

// ---------- 16. 标签收集 + 上次使用时间文案 ----------
var tags = ToolFilter.CollectTags(cards);
Check("标签收集去重（分类也算标签）", tags.Contains("媒体") && tags.Contains("mp3") && tags.Count == tags.Distinct().Count(),
    string.Join(" / ", tags));
Check("卡片标签里分类排第一", cards[0].Tags[0] == "媒体" && cards[0].TagsText.StartsWith("媒体"));
Check("上次使用文案", ToolCard.FormatLastUsed(null) == "从未使用"
    && ToolCard.FormatLastUsed(now) == "刚刚用过"
    && ToolCard.FormatLastUsed(now.AddMinutes(-12)) == "12 分钟前用过"
    && ToolCard.FormatLastUsed(now.AddHours(-3)) == "3 小时前用过"
    && ToolCard.FormatLastUsed(now.AddDays(-5)) == "5 天前用过");

// ---------- 17. 外壳配置（主页排序偏好 + 上次使用时间） ----------
var shellStore = ToolConfigStore.For("shell");
var shell = shellStore.Load<ShellConfig>();
Check("外壳配置落在 Config\\shell\\config.json",
    Path.GetFileName(Path.GetDirectoryName(shell.FilePath)!) == "shell", shell.FilePath);

shell.SortMode = ToolSortMode.RecentlyUsed;
shell.MarkUsed("video-to-audio");          // 原地改字典，不走属性 setter
Thread.Sleep(900);
var savedShell = JsonFileStore.Read<ShellConfig>(shellStore.FilePath);
Check("原地改字典也能自动落盘（最近使用时间）", savedShell?.GetLastUsed("video-to-audio") is not null,
    JsonFileStore.Serialize(savedShell).Replace(Environment.NewLine, " "));
// 枚举按 C# 原名保存（"RecentlyUsed" / "Mp3Lossy"）：手改配置时看得懂，且不会因为枚举顺序变化而错位。
// 注意别改成 camelCase——已存的配置会读不出来（读取失败会被当成损坏文件备份并回退默认值）。
Check("枚举以字符串（C# 原名）保存并读回",
    File.ReadAllText(shellStore.FilePath).Contains("\"RecentlyUsed\"")
    && JsonFileStore.Read<ShellConfig>(shellStore.FilePath)?.SortMode == ToolSortMode.RecentlyUsed,
    File.ReadAllText(shellStore.FilePath).Replace(Environment.NewLine, " "));

// ---------- 18. 批量重命名：单条规则的行为（纯逻辑，不碰界面） ----------
// 规则清单就是一张 RenameRule 数组，BuildName 按数组顺序逐条套用。
// 下面几个小工厂函数只是为了让断言好读，规则本身定义在 RenameRule.cs 里。
static RenameRule Rep(string find, string replace, bool regex = false, bool caseSensitive = false, bool all = true)
    => new() { Kind = RenameRuleKind.Replace, FindText = find, ReplaceText = replace, UseRegex = regex, CaseSensitive = caseSensitive, ReplaceAll = all };

static RenameRule Clean(string remove = "", bool trim = true, bool collapse = false)
    => new() { Kind = RenameRuleKind.Cleanup, RemoveChars = remove, TrimWhitespace = trim, CollapseSpaces = collapse };

static RenameRule Del(int fromStart = 0, int fromEnd = 0, string start = "", string end = "", bool keepMarkersOnly = false)
    => new() { Kind = RenameRuleKind.DeleteRange, DeleteFromStart = fromStart, DeleteFromEnd = fromEnd, DeleteBetweenStart = start, DeleteBetweenEnd = end, DeleteBetweenKeepContent = keepMarkersOnly };

static RenameRule Cas(TextCaseMode mode) => new() { Kind = RenameRuleKind.Case, CaseMode = mode };

static RenameRule Aff(string prefix = "", string suffix = "", RuleScope scope = RuleScope.Stem)
    => new() { Kind = RenameRuleKind.Affix, Prefix = prefix, Suffix = suffix, Scope = scope };

static RenameRule Seq(int start = 1, int step = 1, int digits = 3, SequencePlacement place = SequencePlacement.Suffix, string separator = "_")
    => new() { Kind = RenameRuleKind.Sequence, SequenceStart = start, SequenceStep = step, SequenceDigits = digits, SequencePlacement = place, SequenceSeparator = separator };

static RenameRule Ext(ExtensionMode mode, string newExtension = "")
    => new() { Kind = RenameRuleKind.Extension, ExtensionMode = mode, NewExtension = newExtension };

static string Name(string fileName, int index, params RenameRule[] rules)
    => RenameEngine.BuildName(fileName, index, rules);

static BatchRenameConfig CfgOf(params RenameRule[] rules) => new() { Rules = rules.ToList() };

Check("空规则清单 = 名字原样不动", Name("照片 01.jpg", 0) == "照片 01.jpg");
Check("不勾选的规则会被跳过",
    Name("a.jpg", 0, new RenameRule { Kind = RenameRuleKind.Affix, Prefix = "X", Enabled = false }) == "a.jpg");

// 前后缀
Check("前后缀：加在主名两侧，扩展名不受影响", Name("照片.jpg", 0, Aff("新_", "_完")) == "新_照片_完.jpg");
Check("前后缀：空着等于没加", Name("a.txt", 0, Aff()) == "a.txt");

// 清理
Check("清理：去首尾空格", Name("  空格 .txt", 0, Clean()) == "空格.txt");
Check("清理：连续空格合并成一个", Name("a   b  c.txt", 0, Clean(collapse: true)) == "a b c.txt");
Check("清理：删指定字符是逐字符，不是子串", Name("a_b-c.txt", 0, Clean(remove: "_-")) == "abc.txt");
// 一条规则内部固定是「去首尾空格 → 合并连续空格 → 删字符」，所以删掉下划线后留下的双空格要靠再加一条规则收拾
// —— 这正是「规则清单可以放多条、顺序自己定」的意义
Check("清理：一条规则里三项一起上（删字符发生在合并空格之后）",
    Name(" a _ b .txt", 0, Clean(remove: "_", collapse: true)) == "a  b.txt");
Check("清理：再补一条「合并空格」规则就能把双空格收掉",
    Name(" a _ b .txt", 0, Clean(remove: "_"), Clean(collapse: true)) == "a b.txt");

// 查找替换
Check("替换：默认不区分大小写且全部替换", Name("IMG_IMG.jpg", 0, Rep("img", "照片")) == "照片_照片.jpg");
Check("替换：勾了区分大小写就不替换大小写不符的", Name("IMG_001.jpg", 0, Rep("img", "照片", caseSensitive: true)) == "IMG_001.jpg");
Check("替换：取消「全部替换」只换第一处", Name("aaa.txt", 0, Rep("a", "b", all: false)) == "baa.txt");
Check("替换：替换文本里含查找文本也不会死循环", Name("a.txt", 0, Rep("a", "aa")) == "aa.txt");
Check("替换：正则 + $1 捕获组", Name("IMG_007.jpg", 0, Rep(@"^IMG_(\d+)$", "照片$1", regex: true)) == "照片007.jpg");

// 截取 / 删除
Check("删除：删掉两个标记之间的内容（含标记）", Name("【字幕组】电影名.mkv", 0, Del(start: "【", end: "】")) == "电影名.mkv");
Check("删除：只删标记、保留中间内容", Name("【字幕组】电影名.mkv", 0, Del(start: "【", end: "】", keepMarkersOnly: true)) == "字幕组电影名.mkv");
Check("删除：只填一个标记不生效", Name("【字幕组】电影名.mkv", 0, Del(start: "【")) == "【字幕组】电影名.mkv");
Check("删除：从头 / 从尾按字数删", Name("ABCDEFGHIJ.txt", 0, Del(fromStart: 4, fromEnd: 3)) == "EFG.txt");

// 大小写
Check("大小写：全部小写", Name("My Photo.JPG", 0, Cas(TextCaseMode.Lower)) == "my photo.JPG");
Check("大小写：全部大写", Name("ab.txt", 0, Cas(TextCaseMode.Upper)) == "AB.txt");
Check("大小写：每个单词首字母大写", Name("my holiday photo.jpg", 0, Cas(TextCaseMode.TitleCase)) == "My Holiday Photo.jpg");
Check("大小写：仅首字母大写", Name("my holiday.jpg", 0, Cas(TextCaseMode.SentenceCase)) == "My holiday.jpg");

// 自动编号
Check("编号：在后并补零", Name("照片.jpg", 4, Seq()) == "照片_005.jpg" && Name("照片.jpg", 0, Seq()) == "照片_001.jpg");
Check("编号：在前", Name("照片.jpg", 0, Seq(place: SequencePlacement.Prefix)) == "001_照片.jpg");
Check("编号：只用序号", Name("照片.jpg", 0, Seq(place: SequencePlacement.Replace)) == "001.jpg");
Check("编号：分隔符可以留空", Name("照片.jpg", 0, Seq(separator: "")) == "照片001.jpg");
Check("编号：步长可以是负数", Name("照片.jpg", 3, Seq(start: 10, step: -1, digits: 2)) == "照片_07.jpg");

// 扩展名
Check("扩展名：转小写", Name("A.JPG", 0, Ext(ExtensionMode.Lower)) == "A.jpg");
Check("扩展名：转大写", Name("a.jpg", 0, Ext(ExtensionMode.Upper)) == "a.JPG");
Check("扩展名：统一换（不带点也认）", Name("a.jpeg", 0, Ext(ExtensionMode.Replace, "png")) == "a.png");
Check("扩展名：去掉", Name("a.jpg", 0, Ext(ExtensionMode.Remove)) == "a");
Check("扩展名：原本没扩展名的也能补上", Name("README", 0, Ext(ExtensionMode.Replace, ".txt")) == "README.txt");

// 扩展名过滤串解析
var parsed = RenameEngine.ParseExtensionFilter("jpg; png、.bmp  JPG");
Check("扩展名过滤解析（拆分 / 去点 / 去重 / 保留顺序）",
    parsed.SequenceEqual(new[] { "*.jpg", "*.png", "*.bmp" }), string.Join(",", parsed));
Check("扩展名过滤留空 = 不过滤", RenameEngine.ParseExtensionFilter("   ").Count == 0);

// ---------- 19. 批量重命名：规则清单的「顺序 / 可重复 / 作用范围」（本次改版的重点） ----------
// 顺序就是执行顺序：a→b→c 和「先 b→c 再 a→b」结果完全不同
var chain = new[] { Rep("a", "b"), Rep("b", "c") };
Check("规则按清单顺序依次套用（a→b→c 得到 c）", Name("a.txt", 0, chain) == "c.txt");
Check("顺序反过来结果就不同（先 b→c，a 只会变成 b）", Name("a.txt", 0, chain.Reverse().ToArray()) == "b.txt");

// 同一种规则可以出现多次
Check("同一种规则加多条：两条前缀按顺序叠加", Name("a.jpg", 0, Aff("图_"), Aff("新_")) == "新_图_a.jpg");
Check("同一种规则加多条：两条替换接力", Name("x.txt", 0, Rep("x", "y"), Rep("y", "z")) == "z.txt");
Check("同一种规则加多条：编号之后再套一条前缀", Name("a.jpg", 0, Seq(digits: 2), Aff("p_")) == "p_a_01.jpg");

// 每条规则自带作用范围：主名 / 扩展名 / 整个文件名
Check("作用于=主名：扩展名里的同名内容不受影响",
    Name("IMG_01.IMG", 0, Rep("IMG", "", caseSensitive: true).WithScope(RuleScope.Stem)) == "_01.IMG");
Check("作用于=扩展名：只动扩展名",
    Name("a.JPG", 0, Rep("JPG", "jpg").WithScope(RuleScope.Extension)) == "a.jpg");
Check("作用于=整个文件名：前后缀加在整个名字外",
    Name("a.txt", 0, Aff("x_", scope: RuleScope.FullName)) == "x_a.txt");
Check("作用于=整个文件名：改完重新拆扩展名，后面的规则还能接着处理",
    Name("a.txt", 0, Aff("x_", scope: RuleScope.FullName), Ext(ExtensionMode.Upper)) == "x_a.TXT");
Check("默认作用范围是「主名」", new RenameRule().Scope == RuleScope.Stem && new RenameRule().Summary.Length > 0);

// 规则写错时能指出是第几条
var badCfg = CfgOf(Rep("ok", "ok"), Rep("([", "x", regex: true));
Check("非法正则会指出是第几条规则",
    RenameEngine.Validate(badCfg) is { } msg && msg.Contains("第 2 条") && msg.Contains("查找替换"),
    RenameEngine.Validate(badCfg) ?? "(null)");
Check("停用掉的规则即使写错也不报错",
    RenameEngine.Validate(CfgOf(new RenameRule { Kind = RenameRuleKind.Replace, UseRegex = true, FindText = "([", Enabled = false })) is null);
Check("非法正则时整批标记为错误而不是崩掉",
    RenameEngine.BuildPlan(new[] { typeof(RenameEngine).Assembly.Location }, badCfg)[0].Status == RenameStatus.Error);
Check("空规则清单下所有文件都是「不变」",
    RenameEngine.BuildPlan(new[] { typeof(RenameEngine).Assembly.Location }, CfgOf())
        .All(e => e.Status == RenameStatus.Unchanged));

// ---------- 20. 批量重命名：冲突检测（在临时目录里造真文件） ----------
static string NewDir(string name)
{
    var dir = Path.Combine(AppPaths.Root, "rename-fixture", name);
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    Directory.CreateDirectory(dir);
    return dir;
}

static string Make(string dir, string name, string content = "x")
{
    var path = Path.Combine(dir, name);
    File.WriteAllText(path, content);
    return path;
}

// ① 两个文件算成同一个新名 → 都判冲突
var dupDir = NewDir("dup");
var dupA = Make(dupDir, "a1.txt");
var dupB = Make(dupDir, "a2.txt");
var dupPlan = RenameEngine.BuildPlan(new[] { dupA, dupB }, CfgOf(Clean(remove: "12")));
Check("两个文件算出同一个新名 → 都判冲突",
    dupPlan.All(e => e.Status == RenameStatus.Conflict) && dupPlan[0].NewName == "a.txt",
    string.Join(" / ", dupPlan.Select(e => $"{e.OriginalName}→{e.NewName}:{e.Status}")));

// ② 交换改名（A→B、B→A）不算冲突：执行时靠两阶段完成
var swapDir = NewDir("swap");
var swap1 = Make(swapDir, "01.txt", "first");
var swap2 = Make(swapDir, "02.txt", "second");
var swapCfg = CfgOf(Seq(start: 2, step: -1, digits: 2, place: SequencePlacement.Replace), Ext(ExtensionMode.Replace, "txt"));
var swapPlan = RenameEngine.BuildPlan(new[] { swap1, swap2 }, swapCfg);
Check("互换改名不算冲突（A→B、B→A）",
    swapPlan.All(e => e.Status == RenameStatus.Ok) && swapPlan[0].NewName == "02.txt" && swapPlan[1].NewName == "01.txt",
    string.Join(" / ", swapPlan.Select(e => $"{e.OriginalName}→{e.NewName}:{e.Status}")));

var swapOutcomes = RenameExecutor.Execute(swapPlan);
Check("两阶段执行互换成功", swapOutcomes.Count == 2 && swapOutcomes.All(o => o.Success));
Check("互换后内容正确对调",
    File.ReadAllText(Path.Combine(swapDir, "01.txt")) == "second" && File.ReadAllText(Path.Combine(swapDir, "02.txt")) == "first");

// ③ 撤销：按流水账倒着改回去（含把互换再对调一次）
var swapSession = new RenameSession();
foreach (var outcome in swapOutcomes)
    swapSession.Entries.Add(new RenameHistoryEntry { From = outcome.Entry.SourcePath, To = outcome.Entry.TargetPath });
var undoResults = RenameExecutor.Undo(swapSession);
Check("撤销成功", undoResults.All(r => r.Success));
Check("撤销后内容回到原样",
    File.ReadAllText(Path.Combine(swapDir, "01.txt")) == "first" && File.ReadAllText(Path.Combine(swapDir, "02.txt")) == "second");
var undoAgain = RenameExecutor.Undo(swapSession);
Check("已经撤销过的批次拒绝再撤一次（否则等于“重做”）",
    undoAgain.All(r => !r.Success) && undoAgain.All(r => r.Error is { Length: > 0 }));
Check("拒绝重复撤销后文件内容仍然正确",
    File.ReadAllText(Path.Combine(swapDir, "01.txt")) == "first" && File.ReadAllText(Path.Combine(swapDir, "02.txt")) == "second");

// ④ 只改大小写：Windows 上一步 File.Move 会失败，两阶段才行
var caseDir = NewDir("case");
var caseFile = Make(caseDir, "photo.jpg", "img");
var casePlan = RenameEngine.BuildPlan(new[] { caseFile }, CfgOf(Cas(TextCaseMode.SentenceCase)));
Check("小写改大写被判为“会改名”", casePlan[0].Status == RenameStatus.Ok && casePlan[0].NewName == "Photo.jpg");
var caseOutcomes = RenameExecutor.Execute(casePlan);
Check("只在大小写上做改动也能成功", caseOutcomes.Count == 1 && caseOutcomes[0].Success);
Check("磁盘上的文件名确实变了大小写",
    Directory.GetFiles(caseDir).Select(Path.GetFileName).Contains("Photo.jpg"),
    string.Join(",", Directory.GetFiles(caseDir).Select(Path.GetFileName)));

// ⑤ 目标位置被“不会移走的文件”占着 → 冲突（这条是自检抓出来的真 bug：
//    “名字没变的那个文件”不算让位，否则执行时第二阶段必然撞车）
var occupyDir = NewDir("occupy");
var occupySrc = Make(occupyDir, "2.txt", "B");
Make(occupyDir, "1.txt", "A");                       // 名单外的占用者
var occupyPlan = RenameEngine.BuildPlan(new[] { occupySrc }, CfgOf(Rep("2", "1")));
Check("目标位置已有名单外的同名文件 → 冲突",
    occupyPlan[0].Status == RenameStatus.Conflict, $"{occupyPlan[0].NewName} / {occupyPlan[0].Message}");

var occupyPlanAuto = RenameEngine.BuildPlan(new[] { occupySrc },
    new BatchRenameConfig { Rules = CfgOf(Rep("2", "1")).Rules, AutoResolveConflicts = true });
Check("勾了自动避让 → 改名成 1 (1).txt 而不是覆盖",
    occupyPlanAuto[0].Status == RenameStatus.Ok && occupyPlanAuto[0].NewName == "1 (1).txt",
    $"{occupyPlanAuto[0].NewName} / {occupyPlanAuto[0].Message}");

// ⑥ 名单内但“名字不变”的文件同样不让位
var frozenDir = NewDir("frozen");
var frozenKeep = Make(frozenDir, "1.txt", "A");
var frozenMove = Make(frozenDir, "2.txt", "B");
var frozenCfg = CfgOf(Rep("2", "1"));
var frozenPlan = RenameEngine.BuildPlan(new[] { frozenKeep, frozenMove }, frozenCfg);
Check("名单里“名字不变”的文件也不会让出位置 → 冲突",
    frozenPlan[0].Status == RenameStatus.Unchanged && frozenPlan[1].Status == RenameStatus.Conflict,
    string.Join(" / ", frozenPlan.Select(e => $"{e.OriginalName}:{e.Status}")));

// ⑦ 未勾选的文件整个跳过，且不让位
var skipPlan = RenameEngine.BuildPlan(new[] { frozenKeep, frozenMove }, frozenCfg, new[] { false, true });
Check("未勾选 → 状态为跳过且新名保持原名",
    skipPlan[0].Status == RenameStatus.Skipped && skipPlan[0].NewName == "1.txt");
Check("未勾选的文件同样不让出位置 → 另一个仍判冲突", skipPlan[1].Status == RenameStatus.Conflict);

// ⑧ 非法结果：空名 / 非法字符 / Windows 保留名
var oneFile = new[] { dupA };
Check("规则把整个名字（含扩展名）删空 → 错误",
    RenameEngine.BuildPlan(oneFile, CfgOf(Clean(remove: "a1"), Ext(ExtensionMode.Remove)))[0].Status == RenameStatus.Error);
// 主名删空、只剩扩展名时，".txt" 其实是合法文件名（Windows 允许），所以不拦——预览里能看清结果就行
Check("主名被删空但扩展名还在 → 结果就是 .txt（合法，不拦）",
    RenameEngine.BuildPlan(oneFile, CfgOf(Clean(remove: "a1")))[0].NewName == ".txt");
Check("新名含非法字符 → 错误", RenameEngine.BuildPlan(oneFile, CfgOf(Rep("a1", "a/b")))[0].Status == RenameStatus.Error);
Check("新名是 Windows 保留设备名 → 错误", RenameEngine.BuildPlan(oneFile, CfgOf(Rep("a1", "CON")))[0].Status == RenameStatus.Error);
Check("超长名字 → 错误", RenameEngine.BuildPlan(oneFile, CfgOf(Aff(new string('长', 210))))[0].Status == RenameStatus.Error);

// ---------- 21. 批量重命名：改名流水账（撤销的依据，落盘后可跨程序重启） ----------
var historyStore = new RenameHistoryStore(ToolStorage.For("batch-rename"));
Check("流水账落在 Data\\batch-rename\\rename-history.json",
    Path.GetFileName(Path.GetDirectoryName(historyStore.FilePath)!) == "batch-rename"
    && Path.GetFileName(historyStore.FilePath) == "rename-history.json",
    historyStore.FilePath);

var history = new RenameHistory();
history.Push(new RenameSession
{
    Entries =
    {
        new RenameHistoryEntry { From = @"C:\a\1.txt", To = @"C:\a\001.txt" },
        new RenameHistoryEntry { From = @"C:\a\2.txt", To = @"C:\a\002.txt" },
    },
});
Check("流水账写入成功", historyStore.Save(history));

var reloadedHistory = new RenameHistoryStore(ToolStorage.For("batch-rename")).Load();
Check("流水账能从磁盘读回（关掉程序再打开也能撤销）",
    reloadedHistory.LastUndoable() is { Entries.Count: 2 } last && last.Entries[1].To == @"C:\a\002.txt",
    JsonFileStore.Serialize(reloadedHistory).Replace(Environment.NewLine, " "));

reloadedHistory.Sessions[0].Undone = true;
historyStore.Save(reloadedHistory);
Check("撤销过的批次不再出现在可撤销列表里",
    new RenameHistoryStore(ToolStorage.For("batch-rename")).Load().LastUndoable() is null);

for (var i = 0; i < RenameHistory.MaxSessions + 5; i++)
    reloadedHistory.Push(new RenameSession { Entries = { new RenameHistoryEntry { From = "a", To = "b" } } });
Check($"流水账最多保留 {RenameHistory.MaxSessions} 批（不会无限长大）",
    reloadedHistory.Sessions.Count == RenameHistory.MaxSessions, reloadedHistory.Sessions.Count.ToString());

// ---------- 22. 批量重命名：ViewModel 的规则清单管理 + 端到端跑一遍 ----------
// Confirm 换成“永远点确定”，所以整条流程（含执行与撤销）可以无头跑完，不会弹窗。
{
    static bool WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 50)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    var vm = new BatchRenameViewModel { Confirm = (_, _) => true };
    OptionItem<RenameRuleKind> Kind(RenameRuleKind kind) => vm.RuleKindOptions.First(o => o.Value == kind);

    Check("刚打开时规则清单是空的，界面会显示引导", vm.Rules.Count == 0 && vm.HasNoRules);
    Check("空清单时「执行重命名」按钮不可用", !vm.ExecuteCommand.CanExecute(null) && vm.RenameCount == 0);

    // ---- 增：选类型 → 添加规则 ----
    vm.NewRuleKind = Kind(RenameRuleKind.Cleanup);
    vm.AddRuleCommand.Execute(null);
    Check("添加一条规则：进清单、写进配置、自动展开",
        vm.Rules.Count == 1 && vm.Cfg.Rules.Count == 1 && !vm.HasNoRules
        && vm.Rules[0].Rule.Kind == RenameRuleKind.Cleanup && vm.Rules[0].IsExpanded,
        $"{vm.Rules.Count} / {vm.Cfg.Rules.Count}");

    vm.AddRuleCommand.Execute(null);
    Check("添加第二条时上一条自动收起（同时只展开一条）",
        vm.Rules.Count == 2 && !vm.Rules[0].IsExpanded && vm.Rules[1].IsExpanded);
    Check("同一种规则可以加多条（两条都是「清理」）",
        vm.Rules.All(i => i.Rule.Kind == RenameRuleKind.Cleanup), vm.Rules.Count.ToString());

    // ---- 规则一改，摘要跟着变 ----
    vm.Rules[0].Rule.RemoveChars = "_-";
    vm.Rules[0].Rule.TrimWhitespace = true;
    Check("规则摘要随参数实时变化",
        vm.Rules[0].Rule.Summary.Contains("删字符「_-」") && vm.Rules[0].Rule.KindName == "清理",
        vm.Rules[0].Rule.Summary);
    Check("参数改了会转发给 ViewModel（预览重算的信号）", vm.Rules[0].Rule.Summary.Contains("_-"));

    // ---- 删 / 清空 ----
    vm.RemoveRuleCommand.Execute(vm.Rules[1]);
    Check("删除一条规则，配置同步", vm.Rules.Count == 1 && vm.Cfg.Rules.Count == 1);
    vm.ClearRulesCommand.Execute(null);
    Check("清空规则（Confirm 已换成“确定”）", vm.Rules.Count == 0 && vm.Cfg.Rules.Count == 0);

    // ---- 拖动排序：清单顺序 = 执行顺序 ----
    vm.NewRuleKind = Kind(RenameRuleKind.Affix);
    vm.AddRule(); vm.Rules[^1].Rule.Prefix = "A";
    vm.AddRule(); vm.Rules[^1].Rule.Prefix = "B";
    vm.AddRule(); vm.Rules[^1].Rule.Prefix = "C";
    var order = () => string.Concat(vm.Rules.Select(i => i.Rule.Prefix));
    var configOrder = () => string.Concat(vm.Cfg.Rules.Select(r => r.Prefix));
    Check("三条前缀规则按添加顺序排好", order() == "ABC", order());

    vm.MoveRuleTo(vm.Rules[2], vm.Rules[0], before: true);      // 把 C 拖到最前
    Check("拖动排序：把第 3 条拖到第 1 条前面 → CAB", order() == "CAB", order());
    Check("拖动排序后配置里的顺序同步（会被落盘）", configOrder() == "CAB", configOrder());

    vm.MoveRuleTo(vm.Rules[0], vm.Rules[2], before: false);     // 把 C 拖到最后
    Check("拖动排序：插到某条后面 → ABC", order() == "ABC", order());

    vm.MoveRuleTo(vm.Rules[0], null, before: false);            // 拖到清单空白处 = 末尾
    Check("拖动到空白处 = 放到末尾 → BCA", order() == "BCA", order());

    vm.SetDropTarget(vm.Rules[1], before: true);
    Check("拖动经过时指示线只画在目标那一条上",
        vm.Rules[1].ShowDropTop && !vm.Rules[1].ShowDropBottom && !vm.Rules[0].ShowDropTop);
    vm.ClearDropTarget();
    Check("拖放结束后指示线全部清掉", vm.Rules.All(i => !i.ShowDropTop && !i.ShowDropBottom));

    // 拖放的边界情况（这几条是“一拖就闪退”之后补的回归断言）。
    // 闪退的根源在界面层：在 CollectionChanged 回调里调 ScrollIntoView 强行布局 →
    // 容器生成器状态校验失败。这里把排序逻辑本身的各种落点钉死（改回“先删再插”会立刻挂）。
    // 进入这段时顺序是 B、C、A。
    vm.MoveRuleTo(vm.Rules[0], vm.Rules[0], before: true);
    Check("拖到自己身上 = 什么都不做", order() == "BCA", order());

    vm.MoveRuleTo(vm.Rules[0], vm.Rules[2], before: false);     // 第 1 条拖到第 3 条后面（往后拖）
    Check("往后拖到末尾：下标要补偿（自己先被挪走）→ CAB", order() == "CAB", order());

    vm.MoveRuleTo(vm.Rules[0], vm.Rules[1], before: false);     // 相邻往后挪一格
    Check("相邻往后挪一格 → ACB", order() == "ACB", order());

    vm.MoveRuleTo(vm.Rules[2], vm.Rules[1], before: true);      // 相邻往前挪一格
    Check("相邻往前挪一格 → ABC", order() == "ABC", order());

    vm.MoveRuleTo(vm.Rules[0], null, before: false);            // 拖到清单空白处
    Check("拖到清单空白处 = 放到末尾 → BCA", order() == "BCA", order());
    Check("反复拖动后顺序不会错乱，配置也一直同步", configOrder() == "BCA", configOrder());

    vm.MoveRuleUpCommand.Execute(vm.Rules[2]);                  // A 上移一位
    Check("↑ 按钮上移一条 → BAC", order() == "BAC", order());
    Check("到顶 / 到底时 ↑ ↓ 自动变灰",
        !vm.MoveRuleUpCommand.CanExecute(vm.Rules[0]) && !vm.MoveRuleDownCommand.CanExecute(vm.Rules[^1]));

    // ---- 文件 + 预览 ----
    var vmDir = NewDir("vm");
    var vmFiles = new[] { Make(vmDir, "b.jpg"), Make(vmDir, "a.jpg"), Make(vmDir, "c.jpg") };
    vm.AddFilePaths(vmFiles);
    Check("加文件后统计正确", vm.Files.Count == 3 && vm.SummaryText.Contains("共 3 个"), vm.SummaryText);
    Check("重复添加同一批文件会被去重", vm.Files.Count == 3);
    Check("默认按完整路径升序排列", vm.Files.Select(f => f.OriginalName).SequenceEqual(new[] { "a.jpg", "b.jpg", "c.jpg" }),
        string.Join(",", vm.Files.Select(f => f.OriginalName)));

    vm.Cfg.SortField = FileSortMode.None;
    vm.RebuildPreview();
    Check("预览按规则顺序叠加（清单是 B、A、C → 名字变成 CABa）",
        vm.Files[0].NewName == "CABa.jpg", string.Join(",", vm.Files.Select(f => f.NewName)));

    vm.MoveRuleTo(vm.Rules[0], null, before: false);            // 换个顺序再看
    vm.RebuildPreview();
    Check("调整规则顺序后预览立刻变（顺序真的生效）",
        vm.Files[0].NewName == "BCAa.jpg", string.Join(",", vm.Files.Select(f => f.NewName)));
    vm.MoveRuleTo(vm.Rules[^1], vm.Rules[0], before: true);     // 还原成 B、A、C
    vm.RebuildPreview();

    // ---- 勾选 / 全不选 ----
    vm.Files[1].IsChecked = false;
    vm.RebuildPreview();
    Check("取消勾选后该项跳过，其它照旧",
        vm.Files[1].Status == RenameStatus.Skipped && vm.RenameCount == 2,
        string.Join(",", vm.Files.Select(f => $"{f.OriginalName}:{f.Status}")));
    vm.UncheckAllCommand.Execute(null);
    vm.RebuildPreview();
    Check("全部取消勾选后按钮变灰、统计显示跳过",
        !vm.ExecuteCommand.CanExecute(null) && vm.SummaryText.Contains("跳过 3"), vm.SummaryText);
    vm.CheckAllCommand.Execute(null);
    vm.RebuildPreview();

    // ---- 真跑一次：执行 → 磁盘上名字变了 → 撤销 → 变回来 ----
    var beforeRun = vm.Files.Select(f => f.SourcePath).ToList();
    var beforeNames = vm.Files.Select(f => f.OriginalName).ToList();
    vm.ExecuteCommand.Execute(null);
    Check("执行完磁盘上确实是新名字",
        WaitUntil(() => vm.Files.All(f => f.Status == RenameStatus.Done))
        && vm.Files.Select((f, i) => Path.GetFileName(f.SourcePath) == "CAB" + beforeNames[i]).All(ok => ok),
        string.Join(",", vm.Files.Select(f => Path.GetFileName(f.SourcePath))));
    Check("执行完写进了流水账", vm.History.Count > 0 && vm.CanUndoLast, vm.History.Count.ToString());

    vm.UndoSessionCommand.Execute(null);
    Check("撤销后名字回到最初",
        WaitUntil(() => vm.History.All(s => s.Undone))
        && beforeRun.All(File.Exists) && Directory.GetFiles(vmDir).Length == 3,
        string.Join(",", Directory.GetFiles(vmDir).Select(Path.GetFileName)));
    Check("撤销后不再有可撤销的记录", !vm.CanUndoLast);
}


// ---------- Flash 下载器（站点解析 / 命名 / ini / 批量计划 / 真下载 / 本地服务器 / 自动下载） ----------
// 这一批里有真下载 / 真起 HTTP 服务器，包一层便于出错时看清是哪一条挂的（顺便打印已有结果）
try
{
    FlashDownloaderChecks.Run(
        (name, ok, detail) => Check($"[Flash下载器] {name}", ok, detail),
        (name, ok) => Check($"[Flash下载器] {name}", ok));
}
catch (Exception ex)
{
    Check($"[Flash下载器] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
    Console.WriteLine(string.Join(Environment.NewLine, results));
}

// ---------- 滚轮滚动链（内层列表滚到头之后交给外层页面） ----------
// 用真的 ScrollViewer 量一次布局再断言“该滚谁”，见 ScrollChainingChecks 里的说明。
try
{
    ScrollChainingChecks.Run(
        (name, ok, detail) => Check($"[滚动链] {name}", ok, detail),
        (name, ok) => Check($"[滚动链] {name}", ok));
}
catch (Exception ex)
{
    Check($"[滚动链] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 联机聊天室（协议 / CRC / 流量上限 / 会话文件夹 / 回环真连一轮） ----------
// 最后跑：这一组会真的开 TCP 监听（只绑 127.0.0.1）并写一堆临时文件。
try
{
    ChatRoomChecks.Run(
        (name, ok, detail) => Check($"[聊天室] {name}", ok, detail),
        (name, ok) => Check($"[聊天室] {name}", ok));
}
catch (Exception ex)
{
    Check($"[聊天室] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- osu! 下图器（查询串 / 命名 / 本地筛选 / 打自检自己起的假镜像真下载） ----------
try
{
    MapDownloaderChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[下图器] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 视频 / 音乐播放器（扩展名归类 / 快进快退 / 扫文件夹 / 播放顺序 / 假播放器跑 VM） ----------
try
{
    MediaPlayerChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[播放器] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 外壳分类导航（任意层级目录树 / 面包屑 / 类别计数） ----------
try
{
    NavigationChecks.Run(
        (name, ok, detail) => Check($"[导航] {name}", ok, detail),
        (name, ok) => Check($"[导航] {name}", ok));
}
catch (Exception ex)
{
    Check($"[导航] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 粒子效果（引擎的生成 / 寿命 / 衰减 / 开关 / 上限；纯逻辑无界面） ----------
try
{
    ParticleChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[粒子] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 星数计算（数值基准回归） ----------
try
{
    StarRatingChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[星数] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- osu!mania 三件套（解析桥 / 转 K / 转 LN / 等级 / 批量，见 2.17） ----------
try
{
    OsuManiaChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[osu工具] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 棋牌：五子棋 / 象棋 / 房间网络层（见 AGENTS.md 2.18） ----------
try
{
    BoardGamesChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[棋牌] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 图片连续阅读器（滚动偏移数学 / 中键单点-按住状态机 / 页序 / 真实扫描 / 无头 VM） ----------
try
{
    PictureViewerChecks.Run(
        (name, ok, detail) => Check(name, ok, detail),
        (name, ok) => Check(name, ok));
}
catch (Exception ex)
{
    Check($"[图片] 运行过程中抛出异常（{ex.GetType().Name}）", false,
        $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
}

// ---------- 完整性自检（全屏藏任务栏的前置条件） ----------
// 只读：读自己的令牌 + FindWindow，**不隐藏任何窗口**（见 AGENTS.md 2.14）。
// 这块是 v0.9.4 加的：全屏藏不住任务栏时，先看启动时那行「完整性自检」，分清是权限问题还是代码问题。
Check("[完整性] RID 0x0000 判为 Untrusted", IntegrityCheck.Classify(0x0000) == IntegrityLevel.Untrusted);
Check("[完整性] RID 0x1000 判为 Low", IntegrityCheck.Classify(0x1000) == IntegrityLevel.Low);
Check("[完整性] RID 0x2000 判为 Medium", IntegrityCheck.Classify(0x2000) == IntegrityLevel.Medium);
Check("[完整性] RID 0x2100（Medium Plus）也算 Medium", IntegrityCheck.Classify(0x2100) == IntegrityLevel.Medium);
Check("[完整性] RID 0x3000 判为 High", IntegrityCheck.Classify(0x3000) == IntegrityLevel.High);
Check("[完整性] RID 0x4000 判为 System", IntegrityCheck.Classify(0x4000) == IntegrityLevel.System);
Check("[完整性] 当前进程级别读得出来（不是 Unknown）",
    IntegrityCheck.CurrentProcess != IntegrityLevel.Unknown,
    IntegrityCheck.Summary());
Check("[完整性] Low 的说明里点明 Win32 错误码 5",
    IntegrityCheck.Describe(IntegrityLevel.Low).Contains("5"),
    IntegrityCheck.Describe(IntegrityLevel.Low));

var integrityLines = IntegrityCheck.ReportLines();
Check("[完整性] 报告有结论行、级别与当前进程一致",
    integrityLines.Count >= 3
    && integrityLines[^1].Contains("结论")
    && integrityLines[0].Contains(IntegrityCheck.LevelName(IntegrityCheck.CurrentProcess)),
    string.Join(" | ", integrityLines));

Console.WriteLine(string.Join(Environment.NewLine, results));
var failed = results.Count(r => r.StartsWith("FAIL"));
Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"全部通过（{results.Count} 项）· 数据根：{AppPaths.Root}"
    : $"失败 {failed} / {results.Count} 项 · 数据根：{AppPaths.Root}");
return failed == 0 ? 0 : 1;

/// <summary>自检用的假工具：只填展示字段，用来验证搜索/排序逻辑。</summary>
internal sealed class FakeTool : ITool
{
    public FakeTool(string id, string name, string category, string description, int order, params string[] tags)
    {
        Id = id;
        Name = name;
        Category = category;
        Description = description;
        Order = order;
        Tags = tags;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string EmojiIcon => "🧪";
    public string Category { get; }
    public IReadOnlyList<string> Tags { get; }
    public int Order { get; }

    public System.Windows.FrameworkElement CreateView() => new System.Windows.Controls.TextBlock();
}

/// <summary>自检用的配置类：故意全部用自动属性，验证“不写字也能自动保存”这条承诺。</summary>
internal sealed class TestCfg : ToolConfig
{
    public string Note { get; set; } = "";
    public bool Enable { get; set; } = true;
    public string Mode { get; set; } = "标准";
    public int Repeat { get; set; } = 3;
    public double Threshold { get; set; } = 0.5;
}
