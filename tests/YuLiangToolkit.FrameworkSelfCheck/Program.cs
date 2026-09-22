// Data / Config 轮子自检：把 代码记录.md 里承诺的行为逐条真跑一遍。
// 运行： dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
// （默认在临时目录里造数据根，不碰仓库的 Data/Config；退出码 0 = 全部通过）
using System.IO;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

// 自检必须跑在临时数据根里：YLT_HOME 要在任何 AppPaths 访问之前设好
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_HOME")))
{
    var tempRoot = Path.Combine(Path.GetTempPath(), $"ylt-selfcheck-{DateTime.Now:yyyyMMdd-HHmmss}");
    Environment.SetEnvironmentVariable("YLT_HOME", tempRoot);
}

var results = new List<string>();
void Check(string name, bool ok, string detail = "") =>
    results.Add($"{(ok ? "PASS" : "FAIL")} | {name}{(detail.Length > 0 ? "  :: " + detail : "")}");

Console.WriteLine($"数据根目录: {AppPaths.Root}");
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

var external = Path.Combine(Path.GetTempPath(), "ylt-selfcheck-external.swf");
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
