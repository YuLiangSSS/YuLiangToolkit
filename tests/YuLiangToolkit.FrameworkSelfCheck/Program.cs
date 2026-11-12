// 项目自检：把项目约定里承诺的行为逐条真跑一遍。
// 运行（按分组跑，省时间也省输出 token）：
//   dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj -- theme
//   dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj -- core,shell
//   不带参数 / --all = 跑全部分组；加 -- --list 列出所有分组。
// （默认在临时目录里造数据根，不碰仓库的 Data/Config；退出码 0 = 本次跑的分组全部通过）
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.FrameworkSelfCheck;
using YuLiangToolkit.Tools.BatchRename;
using YuLiangToolkit.Tools.BatchRename.ViewModels;

// ---------- 自检分组：不带参数 = 全跑；`-- <组名...>` 只跑指定组（`-- --list` 查看） ----------
// 改哪个功能只跑哪个组；全量留给发布前 / 改 Core 公共轮子时跑。
var selfCheckGroups = new (string Id, string Label)[]
{
    ("core", "存储 / 配置 / MVVM / 滚动链 / 窗口拖拽还原"),
    ("shell", "外壳：搜索·排序·标签 / 分类导航 / 粒子 / 窗口 / 完整性"),
    ("batch-rename", "批量重命名"),
    ("flash-downloader", "Flash 下载器"),
    ("chat-room", "联机聊天室"),
    ("map-downloader", "osu! 下图器"),
    ("media-player", "视频 / 音乐播放器"),
    ("osu-mania", "osu!mania 三件套（含星数计算）"),
    ("osu-mania-editor", "osu!mania 谱面编辑器：播放时钟 / 网格吸附 / .osu 保真读写 / 大量 note 索引"),
    ("board-games", "棋牌：五子棋 / 象棋（规则 / 配置 / 轮换纯逻辑）"),
    ("board-net", "棋牌网络 / 协议：多客户端回环 / 断线重连 / 连接失败（真实 TCP）"),
    ("board-room", "棋牌房间 VM：席位 / 观战 / 自动轮换（最慢，偶发时序失败可重跑）"),
    ("maintenance", "缓存清理 / 重建配置（Cache / Config 维护）"),
    ("picture-viewer", "图片连续阅读器"),
    ("pdf-reader", "PDF 阅读器：滚动排版 / 双页配对 / 惯性 / 真实 PDF 渲染"),
    ("theme", "第二套 UI：液态玻璃主题"),
    ("kps", "KPS 按键统计：统计引擎 / 预设 / 热键 / 全局输入"),
    ("spug", "Spug 验证码测试：URL 校验 / 请求构造 / 响应解析 / 本地验证"),
    ("live2d", "Live2D 桌宠：内置 moc3 加载 / 参数部件 / 动作表情 / 物理 / 眼睛跟随 / 软件渲染"),
    ("screen-sticker", "屏幕贴纸：几何换算 / 层级顺序 / 最近使用 / 配置默认值（纯逻辑，不建窗口）"),
    ("browser", "网页浏览器：地址栏解析 / 主机识别 / 搜索模板 / 标签栏规则与会话恢复 / 配置默认值"),
    ("text-editor", "文本编辑器：编码探测 / 换行风格 / 查找替换 / 标签规则 / 高亮调色 / 配置默认值"),
    ("audio-tools", "音频工具：视频转音频 / 分段合成（自然排序 / concat 清单 / FFmpeg 参数）"),
    ("word-viewer", "Word 阅读器：docx 解析（段落 / 格式 / 表格 / 图片 / 大纲）/ 查找 / 最近列表"),
    ("excel-viewer", "Excel 阅读器：xlsx 解析（共享字符串 / 样式 / 合并 / 列宽 / 截断）/ 数字格式 / 最近列表"),
    ("ppt-viewer", "PPT 阅读器：pptx 解析（幻灯片顺序 / 版式继承 / 形状 / 文字 / 图片 / 表格 / 备注）/ 查找 / 缩放 / 放映"),
    ("context-menu", "右键菜单管理：注册表枚举 / 隐藏 / 恢复（内存假注册表）"),
    ("archive", "压缩包工具：格式识别 / 解压 / 创建 / 密码 / 路径穿越 / 覆盖策略"),
    ("open-with", "打开方式：三种模式 / 自动分流 / 注册表命令拼装（只读，不写注册表）"),
    ("screen-recorder", "录屏 / GIF：区域换算 / FFmpeg 参数 / 文件名 / 配置默认值"),
    ("media-editor", "剪辑工具：ffprobe 解析 / 片段数学 / 裁剪拼接滤镜 / GIF 参数"),
    ("software-updater", "软件升级器：winget 表格解析 / 包 Id 安全 / 命令拼装 / 批量勾选规则（不启动 winget）"),
    ("calculator", "计算器：表达式求值 / 优先级 / 省略乘号 / 阶乘 / 角度制 / 结果格式化 / 历史与配置"),
    ("random-number", "随机数生成器：十进制步长网格 / 对齐 / 无偏抽样 / 去重与排序 / 精确数轮子 / 配置默认值"),
    ("function-plot", "函数绘图：视口换算 / 缩放平移 / 好看刻度 / 自动 Y 范围 / 配置 / 采样集成"),
    ("mail", "邮箱代收：IMAP 预设 / 地址与 HTML 处理 / DPAPI 授权码加密 / 配置往返（纯逻辑，不联网）"),
    ("mail-sender", "邮件推送：富文本⇄HTML / 地址与附件校验 / 请求组装与请求头 / 错误翻译 / DPAPI Key（纯逻辑，不联网）"),
    ("launcher", "软件启动器：导入校验 / 默认显示名 / 失效判定 / 清空无效项 / 联动启动 / 配置往返（纯逻辑）"),
    ("meme-board", "表情包收藏夹：会话名归一 / 目标窗口解析 / 随机延迟夹取 / 图片扩展名 / 配置往返（纯逻辑，不碰 QQ）"),
    ("task-manager", "任务管理器：数字格式化 / 结束任务三级方案 / 开机启动解析与启停标记 / 保护名单 / 进程行 / 真实只读采样"),
    ("app-manager", "应用管理器：注册表过滤与去重 / 安装日期 / 大小与盘符 / 卸载命令行拆分 / 排序 / 配置（真实只读扫描）"),
    ("tetris", "游戏：俄罗斯方块（7-bag 出块 / SRS 旋转与墙踢 / 消行计分升级 / 暂存 / 幽灵 / 配置）"),
    ("board-new-games", "棋牌新游戏：斗地主（牌型 / 比大小 / 发牌 / 叫牌 / 结算）+ 军棋（布阵 / 走法 / 裁判 / 暗棋 / 胜负）"),
    ("ai-games", "电脑对手：五子棋 / 象棋 / 斗地主 / 军棋 / 俄罗斯方块的 AI 与四档难度 + 四国军棋规则"),
    ("go", "围棋：落子 / 提子 / 禁自杀 / 打劫 / 停一手 / 数子计分 / 悔棋认输 / 快照 / AI"),
    ("snake", "贪吃蛇：引擎（吃果 / 边缘 / 碰撞 / 重生 / 两种玩法）/ 评分权重 / 九档 AI"),
    ("ludo", "飞行棋：棋盘路线 / 起飞规则 / 精确到终点 / 踩子 / 飞行线踢子 / 回合 / 胜负"),
    ("checkers", "跳棋：六芒星棋盘 121 格 / 2·3·4·6 人座位规则 / 跳与走一格 / 占满对面角 / 让棋 / AI"),
    ("checkers-net", "跳棋·联机：127.0.0.1 真回环（选座 / 走棋 / 状态广播 / 退出座位）"),
};
var requestedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var runAllGroups = true;
foreach (var argument in args)
{
    if (argument is "--all" or "-a")
    {
        requestedGroups.Clear();
        runAllGroups = true;
        continue;
    }

    if (argument is "--list" or "-l" or "--help" or "-h")
    {
        Console.WriteLine("用法：dotnet run --project tests\\YuLiangToolkit.FrameworkSelfCheck\\YuLiangToolkit.FrameworkSelfCheck.csproj -- <组名...>");
        Console.WriteLine("  不带参数 / --all = 跑全部分组；多个组用逗号或空格分隔。可用分组：");
        foreach (var (id, label) in selfCheckGroups)
            Console.WriteLine($"  {id,-18} {label}");
        return 0;
    }

    if (argument.StartsWith("-", StringComparison.Ordinal))
        continue;

    runAllGroups = false;
    foreach (var part in argument.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        requestedGroups.Add(part.Trim());
}

// 组名写错直接报错，否则会「以为跑了、其实一个断言都没跑」
var unknownGroups = requestedGroups
    .Where(g => !selfCheckGroups.Any(x => x.Id.Equals(g, StringComparison.OrdinalIgnoreCase)))
    .ToList();
if (unknownGroups.Count > 0)
{
    Console.WriteLine($"未知自检分组：{string.Join(", ", unknownGroups)}");
    Console.WriteLine($"可用分组：{string.Join(", ", selfCheckGroups.Select(g => g.Id))}");
    return 2;
}

requestedGroups = new HashSet<string>(
    selfCheckGroups.Where(g => requestedGroups.Contains(g.Id)).Select(g => g.Id),
    StringComparer.OrdinalIgnoreCase);

// 自检必须跑在临时数据根里：YLT_HOME 要在任何 AppPaths 访问之前设好
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_HOME")))
{
    Environment.SetEnvironmentVariable("YLT_HOME", SelfCheckScratch.MakeDirectory("ylt-selfcheck"));
}

var results = new List<string>();
var skippedGroups = new List<string>();
var runCurrentGroup = true;

// 排错用：YLT_SELFCHECK_TRACE=1 时打印「进入 / 完成哪个分组」，便于定位「自检全部通过但进程不退出」
// 这类问题卡在哪一步（正常跑不输出，不影响输出量）。
var traceGroups = Environment.GetEnvironmentVariable("YLT_SELFCHECK_TRACE") == "1";
void TraceMark(string message)
{
    if (!traceGroups) return;
    Console.WriteLine($"[trace] {DateTime.Now:HH:mm:ss.fff} {message}");
    Console.Out.Flush();
}

void EnterGroup(string id)
{
    var run = runAllGroups || requestedGroups.Contains(id);
    if (!run && !skippedGroups.Contains(id)) skippedGroups.Add(id);
    runCurrentGroup = run;
    if (run) TraceMark("group-begin " + id);
}

bool RunGroup(string id)
{
    EnterGroup(id);
    return runCurrentGroup;
}

void Check(string name, bool ok, string detail = "")
{
    // 没选中的分组连结果都不收（省时间也省输出 token）
    if (!runCurrentGroup) return;
    results.Add($"{(ok ? "PASS" : "FAIL")} | {name}{(detail.Length > 0 ? "  :: " + detail : "")}");
}

// 外部检查模块统一入口：按分组决定跑不跑；模块内抛异常只记一条 FAIL，不中断别的分组
void RunGroupModule(string groupId, string errorLabel,
    Action<Action<string, bool, string>, Action<string, bool>> run, string namePrefix = "")
{
    if (!RunGroup(groupId)) return;
    try
    {
        run(
            (name, ok, detail) => Check(namePrefix + name, ok, detail),
            (name, ok) => Check(namePrefix + name, ok));
    }
    catch (Exception ex)
    {
        Check($"[{errorLabel}] 运行过程中抛出异常（{ex.GetType().Name}）", false,
            $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
    }
    finally
    {
        TraceMark("group-done " + groupId);
    }
}

Console.WriteLine($"数据根目录: {AppPaths.Root}");
Console.WriteLine($"完整性自检: {IntegrityCheck.Summary()}");
Console.WriteLine(runAllGroups
    ? $"自检范围: 全部 {selfCheckGroups.Length} 组"
    : $"自检范围: {string.Join(", ", requestedGroups)}（未选中的分组不会运行；--all 全跑）");
Console.WriteLine();

EnterGroup("core");

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

// ---------- 13.5 OOXML 包轮子（Word / Excel 阅读器共用，见 Core/Documents） ----------
// 真造一个最小 OOXML 包：正常部件 + 关系（内部图片 / 外部超链接 / 绝对路径）+ 坏 XML + DTD 实体，
// 断言读取、关系解析、安全解析、单位换算与最近文件列表规则。
var ooxmlPath = SelfCheckScratch.MakeFile("ooxml-probe.docx");
try
{
    static void AddTextEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    using (var archive = ZipFile.Open(ooxmlPath, ZipArchiveMode.Create))
    {
        AddTextEntry(archive, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""");
        AddTextEntry(archive, "word/document.xml",
            """<?xml version="1.0" encoding="UTF-8"?><document xmlns="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><body><p><r><t>你好 OOXML</t></r></p></body></document>""");
        AddTextEntry(archive, "word/_rels/document.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png" />
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/a?b=1" TargetMode="External" />
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="/xl/worksheets/sheet1.xml" />
            </Relationships>
            """);
        AddTextEntry(archive, "word/broken.xml", "<foo><bar></foo>");
        AddTextEntry(archive, "word/dtd.xml",
            """<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><foo>&xxe;</foo>""");
        using (var image = archive.CreateEntry("word/media/image1.png").Open())
            image.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // 用户机器上这个文件可能已经存在（同名探针），这里只写测试内容。
    }

    // 单位换算：写错会「图片变巨大 / 表格错位」，是最值得钉死的几行。
    Check("[文档] EMU / twip / 磅 → DIP 换算正确",
        OoxmlPackage.EmuToDip(914400) == 96
        && OoxmlPackage.TwipToDip(15) == 1
        && OoxmlPackage.PointToDip(72) == 96
        && OoxmlPackage.DipToEmu(96) == 914400
        && Math.Abs(OoxmlPackage.DipToPoint(96) - 72) < 1e-9,
        $"{OoxmlPackage.EmuToDip(914400)} / {OoxmlPackage.TwipToDip(15)} / {OoxmlPackage.PointToDip(72)}");

    Check("[文档] 部件名规范化：反斜杠 / 开头斜杠 / 空白",
        OoxmlPackage.NormalizePartName(@"\word\document.xml") == "word/document.xml"
        && OoxmlPackage.NormalizePartName("  /word/document.xml  ") == "word/document.xml"
        && OoxmlPackage.NormalizePartName("") == "",
        OoxmlPackage.NormalizePartName(@"\word\document.xml"));

    Check("[文档] 关系文件部件名与相对路径解析",
        OoxmlPackage.GetRelationshipPartName("word/document.xml") == "word/_rels/document.xml.rels"
        && OoxmlPackage.GetRelationshipPartName("_rels/.rels") == "_rels/_rels/.rels.rels"
        && OoxmlPackage.ResolvePartPath("word/document.xml", "media/image1.png") == "word/media/image1.png"
        && OoxmlPackage.ResolvePartPath("word/document.xml", "../media/a.png") == "media/a.png"
        && OoxmlPackage.ResolvePartPath("word/document.xml", "/word/x.xml") == "word/x.xml"
        && OoxmlPackage.ResolvePartPath("word/document.xml", "https://x/y") == "https://x/y",
        "word/document.xml → word/_rels/document.xml.rels");

    using (var package = OoxmlPackage.Open(ooxmlPath))
    {
        Check("[文档] 打开 OOXML 包：部件清单 / 存在判断 / 读 XML / 读图片字节",
            package.PartNames.Contains("word/document.xml")
            && package.Contains(@"word\document.xml")     // 反斜杠也认
            && package.ReadXml("word/document.xml").Root?.Name.LocalName == "document"
            && package.ReadBytes("word/media/image1.png").Length == 8,
            string.Join(",", package.PartNames));

        var relationships = package.ReadRelationships("word/document.xml");
        Check("[文档] 关系解析：内部图片 / 外部超链接 / 绝对路径",
            relationships.Count == 3
            && relationships.Single(r => r.Id == "rId1") is { IsExternal: false, Target: "word/media/image1.png" }
            && relationships.Single(r => r.Id == "rId2") is { IsExternal: true, Target: "https://example.com/a?b=1" }
            && relationships.Single(r => r.Id == "rId3") is { IsExternal: false, Target: "xl/worksheets/sheet1.xml" },
            string.Join(" | ", relationships.Select(r => $"{r.Id}:{r.Target}")));

        var missingThrew = false;
        try { package.ReadXml("word/不存在.xml"); }
        catch (OoxmlException ex) { missingThrew = ex.Message.Contains("不存在"); }
        Check("[文档] 缺部件时抛中文 OoxmlException", missingThrew);

        var brokenThrew = false;
        try { package.ReadXml("word/broken.xml"); }
        catch (OoxmlException) { brokenThrew = true; }
        Check("[文档] 坏 XML 抛 OoxmlException（不会带病返回）", brokenThrew);

        var dtdThrew = false;
        try { package.ReadXml("word/dtd.xml"); }
        catch (OoxmlException) { dtdThrew = true; }
        Check("[文档] 禁用 DTD / 外部实体（防 XXE）", dtdThrew);

        Check("[文档] TryReadXml：缺部件 / 坏 XML 返回 null（可选部件容错）",
            package.TryReadXml("word/不存在.xml") is null && package.TryReadXml("word/broken.xml") is null,
            "");
    }

    // 释放后再用必须抛 ObjectDisposedException（解析器里 using 写错会踩到）
    var disposedPackage = OoxmlPackage.Open(ooxmlPath);
    disposedPackage.Dispose();
    var disposedThrew = false;
    try { disposedPackage.Contains("word/document.xml"); }
    catch (ObjectDisposedException) { disposedThrew = true; }
    Check("[文档] 释放后再读抛 ObjectDisposedException", disposedThrew);

    // 不是 ZIP 的文件（用户误选了 .doc / .txt）要给出可读错误
    var fakeDocx = SelfCheckScratch.MakeFile("ooxml-broken.docx");
    try
    {
        File.WriteAllText(fakeDocx, "这不是 ZIP，只是普通文本");
        var corruptThrew = false;
        try { OoxmlPackage.Open(fakeDocx); }
        catch (OoxmlException ex) { corruptThrew = ex.Message.Contains("不是有效的 Office 文档"); }
        Check("[文档] 非 ZIP 文件抛可读的 OoxmlException", corruptThrew);
    }
    finally
    {
        try { File.Delete(fakeDocx); } catch { /* 清理失败忽略 */ }
    }

    // 最近文件列表（Word / Excel 阅读器共用）：去重 / 置顶 / 上限 / 清空 / 文件存在判断
    var recentProbe = SelfCheckScratch.MakeFile("recent-probe.docx");
    try
    {
        File.WriteAllText(recentProbe, "x");
        var recent = new List<RecentFileEntry>();
        var anotherPath = Path.Combine(SelfCheckScratch.Root, "another.xlsx");

        RecentFiles.Remember(recent, recentProbe, 3, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        RecentFiles.Remember(recent, anotherPath, 3);
        RecentFiles.Remember(recent, recentProbe.ToUpperInvariant(), 3);   // 同一路径换大小写 = 去重
        Check("[文档] 最近文件：同一路径去重并置顶，最多按上限保留",
            recent.Count == 2
            && string.Equals(recent[0].Path, recentProbe, StringComparison.OrdinalIgnoreCase)
            && recent[0].LastOpenedUtc.Kind == DateTimeKind.Utc,
            string.Join(",", recent.Select(r => r.Path)));

        Check("[文档] 最近文件：文件是否存在 / 上限夹取",
            RecentFiles.IsAvailable(recent[0])
            && !RecentFiles.IsAvailable(new RecentFileEntry { Path = Path.Combine(SelfCheckScratch.Root, "no-such.docx") })
            && RecentFiles.ClampLimit(-8) == 0 && RecentFiles.ClampLimit(9999) == RecentFiles.MaxLimit,
            $"{RecentFiles.IsAvailable(recent[0])} / {RecentFiles.ClampLimit(-8)} / {RecentFiles.ClampLimit(9999)}");

        RecentFiles.Trim(recent, 1);
        Check("[文档] 最近文件：裁剪按上限保留最新的", recent.Count == 1, recent.Count.ToString());

        RecentFiles.Remove(recent, recentProbe);
        RecentFiles.Remember(recent, anotherPath, 1);
        Check("[文档] 最近文件：删除指定项后其它记录还在",
            recent.Count == 1 && string.Equals(recent[0].Path, anotherPath, StringComparison.OrdinalIgnoreCase),
            string.Join(",", recent.Select(r => r.Path)));

        RecentFiles.Remember(recent, recentProbe, 0);
        Check("[文档] 最近文件：上限 0 = 不记录并清空已有列表", recent.Count == 0, recent.Count.ToString());
    }
    finally
    {
        try { File.Delete(recentProbe); } catch { /* 清理失败忽略 */ }
    }
}
finally
{
    try { File.Delete(ooxmlPath); } catch { /* 清理失败忽略 */ }
}

EnterGroup("shell");

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

// ---------- 17.1 关闭按钮行为（主页设置「关闭应用 / 最小化到托盘」，见 docs/dev/pitfalls.md） ----------
Check("关闭行为默认：点 × 关闭应用", new ShellConfig().CloseAction == ShellCloseAction.Exit);

shell.CloseAction = ShellCloseAction.MinimizeToTray;
Thread.Sleep(900);
var savedClose = JsonFileStore.Read<ShellConfig>(shellStore.FilePath);
Check("关闭行为改成「最小化到托盘」后自动落盘",
    savedClose?.CloseAction == ShellCloseAction.MinimizeToTray,
    File.ReadAllText(shellStore.FilePath).Replace(Environment.NewLine, " "));
Check("关闭行为按 C# 原名存（配置文件里是 \"MinimizeToTray\"）",
    File.ReadAllText(shellStore.FilePath).Contains("\"MinimizeToTray\""),
    File.ReadAllText(shellStore.FilePath).Replace(Environment.NewLine, " "));
Check("关闭行为读回一致（重启后保持）", savedClose?.CloseAction == ShellCloseAction.MinimizeToTray);

shell.CloseAction = ShellCloseAction.Exit;
Thread.Sleep(900);
Check("关闭行为改回「关闭应用」后落盘",
    JsonFileStore.Read<ShellConfig>(shellStore.FilePath)?.CloseAction == ShellCloseAction.Exit);

// ---------- 17.2 粒子颜色（主页设置「粒子颜色」，见 docs/dev/pitfalls.md） ----------
var defaultShell = new ShellConfig();
Check("粒子颜色默认：自定义关、两种颜色都启用且是默认值",
    !defaultShell.ParticleCustomColors
    && defaultShell.ParticleColor1Enabled && defaultShell.ParticleColor2Enabled
    && defaultShell.ParticleColor1 == "#F2EEFF" && defaultShell.ParticleColor2 == "#8FB4FF");

Check("颜色文本解析：#RRGGBB / 省略 # / 大小写都能认，坏值回退",
    ParticleColors.Parse("#4C8DFF", Colors.Black) == Color.FromRgb(0x4C, 0x8D, 0xFF)
    && ParticleColors.Parse("4c8dff", Colors.Black) == Color.FromRgb(0x4C, 0x8D, 0xFF)
    && ParticleColors.Parse("坏值", Colors.Black) == Colors.Black
    && ParticleColors.ToHex(Color.FromRgb(0x4C, 0x8D, 0xFF)) == "#4C8DFF");

shell.ParticleCustomColors = true;
shell.ParticleColor1Enabled = false;
shell.ParticleColor1 = "#FF5A5A";
shell.ParticleColor2 = "#9F8FFF";
Thread.Sleep(900);
var savedColors = JsonFileStore.Read<ShellConfig>(shellStore.FilePath);
Check("粒子颜色设置自动落盘并读回（颜色按 #RRGGBB 存）",
    savedColors is { ParticleCustomColors: true, ParticleColor1Enabled: false, ParticleColor2Enabled: true }
    && savedColors.ParticleColor1 == "#FF5A5A" && savedColors.ParticleColor2 == "#9F8FFF"
    && File.ReadAllText(shellStore.FilePath).Contains("\"#FF5A5A\""),
    JsonFileStore.Serialize(savedColors).Replace(Environment.NewLine, " "));

// ---------- 17.2.1 主页标签墙（v0.33.0：默认 3 行 + 横向滚动 / 可展开 / 设置里可整体隐藏） ----------
Check("主页标签墙默认显示（老配置升上来行为不变；配置键 showHomeTags）",
    new ShellConfig().ShowHomeTags
    && JsonFileStore.Serialize(new ShellConfig()).Contains("\"showHomeTags\": true"),
    JsonFileStore.Serialize(new ShellConfig()).Replace(Environment.NewLine, " "));

// 真读一份「旧配置」：没有 showHomeTags 键时必须回落到默认值 true（不能靠“属性初始值”碰运气）
var legacyTagsDir = SelfCheckScratch.MakeDirectory("shell-tags-legacy");
var legacyTagsPath = Path.Combine(legacyTagsDir, "config.json");
File.WriteAllText(legacyTagsPath, "{ \"sortMode\": \"NameAsc\" }");
var legacyTags = JsonFileStore.Read<ShellConfig>(legacyTagsPath);
Check("旧配置文件没有 showHomeTags 键 → 读回 true（升级后标签墙照常显示）",
    legacyTags is { ShowHomeTags: true, SortMode: ToolSortMode.NameAsc },
    JsonFileStore.Serialize(legacyTags).Replace(Environment.NewLine, " "));

shell.ShowHomeTags = false;
Thread.Sleep(900);
var savedTags = JsonFileStore.Read<ShellConfig>(shellStore.FilePath);
Check("主页设置里隐藏标签墙后自动落盘并读回（showHomeTags = false）",
    savedTags is { ShowHomeTags: false }
    && File.ReadAllText(shellStore.FilePath).Contains("\"showHomeTags\": false"),
    JsonFileStore.Serialize(savedTags).Replace(Environment.NewLine, " "));

shell.ShowHomeTags = true;
Thread.Sleep(900);
Check("重新打开标签墙后落盘（showHomeTags = true）",
    JsonFileStore.Read<ShellConfig>(shellStore.FilePath)?.ShowHomeTags == true);

// ---------- 17.2.2 左侧「功能列表」栏显隐（v0.66.0：主页设置 → 界面布局） ----------
Check("左侧功能列表栏默认显示（旧配置升上来行为不变；配置键 showSidebar）",
    new ShellConfig().ShowSidebar
    && JsonFileStore.Serialize(new ShellConfig()).Contains("\"showSidebar\": true"),
    JsonFileStore.Serialize(new ShellConfig()).Replace(Environment.NewLine, " "));

var legacySidebar = JsonFileStore.Read<ShellConfig>(legacyTagsPath);
Check("旧配置文件没有 showSidebar 键 → 读回 true（升级后侧边栏照常显示）",
    legacySidebar is { ShowSidebar: true },
    JsonFileStore.Serialize(legacySidebar).Replace(Environment.NewLine, " "));

shell.ShowSidebar = false;
Thread.Sleep(900);
var savedSidebar = JsonFileStore.Read<ShellConfig>(shellStore.FilePath);
Check("主页设置里隐藏侧边栏后自动落盘并读回（showSidebar = false）",
    savedSidebar is { ShowSidebar: false }
    && File.ReadAllText(shellStore.FilePath).Contains("\"showSidebar\": false"),
    JsonFileStore.Serialize(savedSidebar).Replace(Environment.NewLine, " "));

shell.ShowSidebar = true;
Thread.Sleep(900);
Check("重新显示侧边栏后落盘（showSidebar = true）",
    JsonFileStore.Read<ShellConfig>(shellStore.FilePath)?.ShowSidebar == true);

// ---------- 17.2.3 开机自启（v0.99.0：主页设置 → 开机自启，写入 HKCU Run 键） ----------
// 只测命令行拼装 / 路径还原 / 配置默认值与往返 + 只读的登记状态，**绝不写注册表**。
StartupChecks.Run(
    (name, ok, detail) => Check(name, ok, detail),
    (name, ok) => Check(name, ok));

// ---------- 17.3 最大化窗口拖标题栏 → 还原位置（纯逻辑，见 AGENTS.md） ----------
// 场景：1920x1032 的工作区里，1280x820 的窗口最大化后（实际 1920 宽），按住标题栏某个点往外拖。
var dragArea = new Rect(0, 0, 1920, 1032);

var dragCenter = WindowDragRestore.Compute(960, 20, 960, 20, 1920, 1280, 820, dragArea, 1024, 700);
Check("拖标题栏还原：抓正中间 → 窗口居中、贴工作区顶边",
    dragCenter.Left == 320 && dragCenter.Top == 0 && dragCenter.Width == 1280 && dragCenter.Height == 820,
    $"({dragCenter.Left},{dragCenter.Top}) {dragCenter.Width}x{dragCenter.Height}");

var dragRight = WindowDragRestore.Compute(1800, 20, 1800, 20, 1920, 1280, 820, dragArea, 1024, 700);
Check("拖标题栏还原：抓靠右 → 光标还在标题栏同一比例处", Math.Abs(dragRight.Left - 600) < 1e-9,
    $"left={dragRight.Left}");

var dragEdge = WindowDragRestore.Compute(1919, 20, 1800, 20, 1920, 1280, 820, dragArea, 1024, 700);
Check("拖标题栏还原：拖到屏幕右边缘 → 窗口被夹回工作区", dragEdge.Left == 640,
    $"left={dragEdge.Left}");

var dragAbove = WindowDragRestore.Compute(960, 5, 480, 20, 1920, 1280, 820, dragArea, 1024, 700);
Check("拖标题栏还原：窗口不会被推到工作区上面（标题栏始终可点）", dragAbove.Top == 0,
    $"top={dragAbove.Top}");

var dragHuge = WindowDragRestore.Compute(960, 20, 960, 20, 1920, 2400, 1400, dragArea, 1024, 700);
Check("拖标题栏还原：还原尺寸比工作区大 → 夹到工作区大小",
    dragHuge.Width == 1920 && dragHuge.Height == 1032 && dragHuge.Left == 0 && dragHuge.Top == 0,
    $"({dragHuge.Left},{dragHuge.Top}) {dragHuge.Width}x{dragHuge.Height}");

var dragMin = WindowDragRestore.Compute(960, 20, 960, 20, 1920, 800, 600, dragArea, 1024, 700);
Check("拖标题栏还原：还原尺寸比最小尺寸还小 → 用 Window 的最小尺寸",
    dragMin.Width == 1024 && dragMin.Height == 700, $"{dragMin.Width}x{dragMin.Height}");

var dragUnknown = WindowDragRestore.Compute(960, 20, 960, 20, 1920, 0, 0, dragArea, 1024, 700);
Check("拖标题栏还原：拿不到最大化前的尺寸 → 用工作区尺寸兜底",
    dragUnknown.Width == 1920 && dragUnknown.Height == 1032, $"{dragUnknown.Width}x{dragUnknown.Height}");

var dragOffsetArea = WindowDragRestore.Compute(960, 70, 960, 20, 1920, 1280, 820, new Rect(100, 50, 1900, 1000), 1024, 700);
Check("拖标题栏还原：工作区不在 (0,0)（任务栏在左边 / 上边）也按工作区算",
    dragOffsetArea.Left == 320 && dragOffsetArea.Top == 50, $"({dragOffsetArea.Left},{dragOffsetArea.Top})");

var dragNoWidth = WindowDragRestore.Compute(960, 20, 0, 20, 0, 1280, 820, dragArea, 1024, 700);
Check("拖标题栏还原：拿不到当前窗口宽度 → 按正中间算",
    dragNoWidth.Left == 320, $"left={dragNoWidth.Left}");

EnterGroup("batch-rename");

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
// 这一批里有真下载 / 真起 HTTP 服务器；模块内抛异常只记一条 FAIL，不中断别的分组
RunGroupModule("flash-downloader", "Flash下载器", FlashDownloaderChecks.Run, "[Flash下载器] ");

// ---------- 滚轮滚动链（内层列表滚到头之后交给外层页面） ----------
// 用真的 ScrollViewer 量一次布局再断言“该滚谁”，见 ScrollChainingChecks 里的说明。
RunGroupModule("core", "滚动链", ScrollChainingChecks.Run, "[滚动链] ");

// ---------- 联机聊天室（协议 / CRC / 流量上限 / 会话文件夹 / 回环真连一轮） ----------
// 会真的开 TCP 监听（只绑 127.0.0.1）并写一堆临时文件。
RunGroupModule("chat-room", "聊天室", ChatRoomChecks.Run, "[聊天室] ");

// ---------- osu! 下图器（查询串 / 命名 / 本地筛选 / 打自检自己起的假镜像真下载） ----------
RunGroupModule("map-downloader", "下图器", MapDownloaderChecks.Run);

// ---------- 视频 / 音乐播放器（扩展名归类 / 快进快退 / 扫文件夹 / 播放顺序 / 假播放器跑 VM） ----------
RunGroupModule("media-player", "播放器", MediaPlayerChecks.Run);

// ---------- 外壳分类导航（任意层级目录树 / 面包屑 / 类别计数） ----------
RunGroupModule("shell", "导航", NavigationChecks.Run, "[导航] ");

// ---------- 粒子效果（引擎的生成 / 寿命 / 衰减 / 开关 / 上限；纯逻辑无界面） ----------
RunGroupModule("shell", "粒子", ParticleChecks.Run);
RunGroupModule("shell", "光标特效", ShellEffectsChecks.Run);

// ---------- 渲染 GPU（v0.96.0：Windows 图形首选项字符串 / 配置兼容 / 显卡文案 / 重启参数；不写真实注册表） ----------
RunGroupModule("shell", "渲染GPU", GpuRenderChecks.Run);

// ---------- 主页标签墙（v0.33.0：3 行 + 横向滚动；FixedRowsPanel 的行内紧密排列 / 行高 / 逐行平衡，无头 STA 线程） ----------
RunGroupModule("shell", "标签墙", HomeTagWallChecks.Run, "[标签墙] ");

// ---------- 星数计算（数值基准回归） ----------
RunGroupModule("osu-mania", "星数", StarRatingChecks.Run);

// ---------- osu!mania 三件套（解析桥 / 转 K / 转 LN / 等级 / 批量，见 docs/dev/pitfalls.md） ----------
RunGroupModule("osu-mania", "osu工具", OsuManiaChecks.Run);

// ---------- osu!mania 谱面编辑器（播放时钟 / 网格吸附 / .osu 保真读写 / 大量 note 索引与裁剪） ----------
RunGroupModule("osu-mania-editor", "谱面编辑器", OsuManiaEditorChecks.Run);

// ---------- 棋牌：规则 / 配置 / 轮换纯逻辑（见 docs/dev/pitfalls.md） ----------
RunGroupModule("board-games", "棋牌", BoardGamesChecks.Run);

// ---------- 棋牌网络 / 协议：本机回环多客户端会话（真实 TCP） ----------
RunGroupModule("board-net", "棋牌网络", BoardGamesChecks.RunNet);

// ---------- 棋牌房间 VM：席位 / 观战 / 自动轮换（含真实回环网络，最慢） ----------
RunGroupModule("board-room", "棋牌房间", BoardGamesChecks.RunRoom);

// ---------- 缓存清理 / 重建配置（Cache 路径约定 / ToolCache / 汇总与清理 / 配置重建与只读预览） ----------
RunGroupModule("maintenance", "缓存/配置维护", MaintenanceChecks.Run, "[维护] ");

// ---------- 图片连续阅读器（滚动偏移数学 / 中键单点-按住状态机 / 页序 / 真实扫描 / 无头 VM） ----------
RunGroupModule("picture-viewer", "图片", PictureViewerChecks.Run);

// ---------- PDF 阅读器（上下 / 左右滚动排版 / 双页配对与翻页 / 拖动惯性 / 生成最小 PDF 真渲染一页） ----------
RunGroupModule("pdf-reader", "PDF", PdfReaderChecks.Run);

// PDF 文字层 / 书签目录 / 搜索：手写 PDF 解析（xref / 对象流 / Flate / ToUnicode / 页目录），纯无头断言。
RunGroupModule("pdf-reader", "PDF文字", PdfTextChecks.Run);

// PDF 文件关联：只断言纯逻辑（后缀判断 / 命令行拼装 / 常量），**绝不写注册表**。
RunGroupModule("pdf-reader", "PDF关联", PdfAssociationChecks.Run);

// ---------- 第二套 UI：液态玻璃主题（枚举 / 字典 key 对齐 / 默认主题颜色不变 / 配置往返） ----------
RunGroupModule("theme", "主题", ThemeChecks.Run);

// 「KPS 按键统计」：统计引擎（当前 KPS / 峰值 / 平均 / 折线采样）、内置预设与配置序列化、
// 热键字符串解析、Raw Input 注册 + 真实 SendInput 注入（无头跑，不弹窗）。
RunGroupModule("kps", "KPS统计", KpsChecks.Run, "[KPS] ");
RunGroupModule("kps", "KPS输入", KpsInputChecks.Run);

// 「Spug 验证码测试」：URL / 参数校验、短信 GET 拼接、邮箱 POST JSON、响应解析、本地验证、打码、配置；
// 发送器用假 HttpMessageHandler 真跑请求层，**不碰外网**（真联网留给人工用工具验收）。
RunGroupModule("spug", "Spug验证码", SpugChecks.Run);

// 「Live2D 桌宠」：内置 moc3 真加载（Cubism Core）/ 参数部件 / 动作表情解析 / 物理求值
// / 眼睛跟随数学 / 软件渲染器纯逻辑；无头跑，不弹窗、不碰真 Data。
// AI 聊天（DeepSeek）/ 好感度断言由 Live2DChecks.Run 末尾调用 Live2DAiChecks.Run(...)，共用 live2d 组。
// 养成（饥饿 / 金币 / 每日任务 / 投喂）断言由 Live2DChecks.Run 末尾调用 Live2DCareChecks.Run(...)，同属 live2d 组。
// 语音语气（SpeechTone / 分段韵律 / SSML / WAV 拼接）断言由 Live2DChecks.Run 末尾调用 Live2DTtsToneChecks.Run(...)，同属 live2d 组。
// 互动动作 / 台词预设 + 聊天窗自由度 + 试管满格断言由 Live2DChecks.Run 末尾调用 Live2DInteractionScenarioChecks.Run(...)，同属 live2d 组。
RunGroupModule("live2d", "Live2D桌宠", Live2DChecks.Run, "[Live2D] ");

// 「屏幕贴纸」：默认大小与图片比例 / 以鼠标为锚点缩放 / 角点缩放 / 夹回屏幕（多显示器非 0 原点）/
// 层级上移下移 / 最近使用去重与上限 / 配置默认值；全部纯逻辑，不创建窗口、不碰真 Data。
RunGroupModule("screen-sticker", "屏幕贴纸", ScreenStickerChecks.Run);

// 「网页浏览器」：地址栏输入解析（补协议 / localhost / IP / 搜索词）/ 主机识别 / 搜索模板 / 配置默认值；
// 全是纯逻辑，**不启动 WebView2、不联网**（网页渲染本身由截图冒烟验收）。
RunGroupModule("browser", "网页浏览器（多标签）", BrowserChecks.Run);

// 「文本编辑器」：编码探测 / 换行 / 查找替换 / 标签规则 / 脏标记链路 / 高亮调色 / 配置默认值；
// 全是纯逻辑，**不实例化 AvalonEdit 控件、不弹窗**（界面本身由截图冒烟验收）。
RunGroupModule("text-editor", "文本编辑器", TextEditorChecks.Run);

// 「音频工具」：分段视频合成的纯逻辑（分段识别 / 自然排序 / concat 清单 / FFmpeg 参数 / 输出命名 /
// 进度解析 / 配置包装）；**不启动 FFmpeg、不联网**（真合成由人工用本地 m3u8 / 分段文件夹验收）。
RunGroupModule("audio-tools", "音频工具", AudioToolsChecks.Run);

// 「Word 阅读器」：手写 docx 解析（段落 / 字符格式 / 表格 / 图片 / 大纲）/ 纯逻辑查找 /
// 配置与最近列表；**无头跑，不弹窗、不碰真 Data**（界面渲染由截图冒烟验收）。
RunGroupModule("word-viewer", "Word 阅读器", WordViewerChecks.Run);

// 「Excel 阅读器」：手写 xlsx 解析（共享字符串 / 样式 / 合并单元格 / 列宽 / 截断）/
// 数字格式与日期序列 / 地址换算 / 配置与最近列表；同样无头跑。
RunGroupModule("excel-viewer", "Excel 阅读器", ExcelViewerChecks.Run);

// 「PPT 阅读器」：手写 pptx 解析（幻灯片顺序 / 版式继承 / 形状 / 文字 / 图片 / 表格 / 备注）/
// 纯逻辑查找 / 缩放 / 配置与最近列表；**无头跑，不弹窗、不碰真 Data**（界面渲染由截图冒烟验收）。
RunGroupModule("ppt-viewer", "PPT 阅读器", PptViewerChecks.Run);

// 「PPT 文件关联」：ProgId / 后缀表 / 后缀判断 / 命令行拼装 / 状态说明；
// **只读，不写 HKCU 注册表**（真正的关联登记由用户点界面开关）。
RunGroupModule("ppt-viewer", "PPT 文件关联", PptAssociationChecks.Run);

// 「右键菜单管理」：枚举 / 隐藏 / 恢复 / 恢复全部 / 筛选 / CLSID 规范化。
// 全部走内存假注册表（FakeContextMenuRegistry），**绝不写用户真实注册表**，无头跑、可在任何机器重复。
RunGroupModule("context-menu", "右键菜单管理", ContextMenuChecks.Run, "[右键菜单] ");

// 「压缩包工具」：格式识别 / 自造包 Probe + Extract / Create 真创建 ZIP 与 7Z 再解压 /
// testdata 素材真解压（RAR5 / TAR.XZ / TAR.ZST）/ 密码 / 覆盖策略 / 路径穿越 / 取消 / 配置；
// 全部在临时目录里造数据，**不碰真实 Data/Config、不联网、不启外部 exe**。
RunGroupModule("archive", "压缩包工具", ArchiveChecks.Run, "[压缩包] ");

// 「打开方式」：三种模式 / 命令行解析 / 扩展名 → 工具分流 / 注册表命令拼装。
// 只测纯逻辑 + 只读的 IsRegistered / Describe，**绝不调用 Register / Unregister**（会写用户真实注册表）。
RunGroupModule("open-with", "打开方式", OpenWithChecks.Run);

// 「录屏 / GIF」：区域换算 / 参数归一化 / FFmpeg 采集与 GIF 两遍 / MP4 / 截图参数 / 文件名 / 计时 / 配置。
// 纯逻辑，**不启动 FFmpeg、不采集屏幕、不弹窗**；真实录制由人工冒烟验收。
RunGroupModule("screen-recorder", "录屏", ScreenRecorderChecks.Run);

// 「剪辑工具」：ffprobe JSON 解析 / 时间格式 / 片段数学 / 裁剪拼接滤镜 / GIF 参数 / 配置归一化。
// 纯逻辑，**不启动 FFmpeg、不碰真实文件**；真实剪辑 / 预览由人工冒烟验收。
RunGroupModule("media-editor", "剪辑", MediaEditorChecks.Run);

// 「软件升级器」：winget 中文 / 英文表格解析（CJK 显示列宽 / ANSI 回车帧 / 分隔线与统计行）、
// 包 Id 安全白名单、扫描与升级命令拼装、批量勾选规则（多选 / 全选 / 全不选 / 反选 / 表头三态）、
// 工具元数据与配置默认值。纯逻辑，**不启动 winget.exe、不联网、不碰真实注册表**；
// 真实扫描 / 升级由界面人工冒烟验收。
RunGroupModule("software-updater", "软件升级器", SoftwareUpdaterChecks.Run);

// 「计算器」：共享表达式引擎（解析 / 优先级 / 省略乘号 / 后缀阶乘 / 角度制 / 常量 / 结果格式化）、
// 计算器的输入构造与历史记录（去重 / 上限裁剪 / 清空）、配置默认值与夹取。
// 纯逻辑，不创建窗口、不碰剪贴板。
// 辈分计算模式（关系链规范化 / 南北称谓 / 长幼 / 反向称谓 / 兜底 / 配置）由 KinCalculatorChecks.Run 断言，
// 同样在本组内、由 CalculatorChecks.Run 末尾调用（不单列组，避免入口卡片变多）。
RunGroupModule("calculator", "计算器", CalculatorChecks.Run);

// 「随机数生成器」：十进制步长网格（任意大下限 / 任意细步长 / 上限对齐 / 除不尽与非法输入）、
// 无偏抽样（注入确定性随机源断言边界与去重）、排序与分隔符、精确数轮子（ExactDecimal 解析 / 四则 / 除尽判定）、
// 配置默认值与夹取。纯逻辑，不创建窗口、不碰剪贴板。
RunGroupModule("random-number", "随机数生成器", RandomNumberChecks.Run);

// 「函数绘图」：视口换算（世界 ↔ 屏幕往返 / 以光标为中心缩放 / 平移）、好看刻度（1·2·5×10^n）、
// 自动 Y 范围（空集合 / NaN / 上下界写反）、每条函数的配置校验，以及与 FunctionSampler 的采样集成
// （tan 多段 / 定义域收窄 / 越界断线）。纯逻辑，不创建窗口、不渲染。
RunGroupModule("function-plot", "函数绘图", FunctionPlotChecks.Run);

// 「邮箱代收」：服务商预设匹配 / 地址打码 / HTML→纯文本 / 附件名清洗 / 配置 JSON 往返 /
// DPAPI 授权码加解密 / 错误翻译。纯逻辑，**不联网**；真实 Gmail / QQ IMAP 登录由界面人工冒烟。
RunGroupModule("mail", "邮箱代收", MailChecks.Run);

// 「邮件推送」：富文本 ⇄ HTML（STA）/ HTML→纯文本 / 地址拆分与上限 / 附件 base64 体积与危险后缀 /
// 请求组装与校验 / 假 HttpMessageHandler 断言请求头（UA 必须带）与 JSON 字段 / 错误翻译 /
// DPAPI API Key / 配置往返 / 投递状态解析。纯逻辑，**不联网、不发任何真实邮件**。
RunGroupModule("mail-sender", "邮件推送", MailSenderChecks.Run);

// 「软件启动器」：导入校验（.exe / .lnk）/ 默认显示名（lnk 用快捷方式名、exe 优先文件说明）/
// 路径规范化与导入去重 / 自定义图标缓存路径安全 / 失效判定（文件丢失·快捷方式损坏·目标已卸载）/
// 「清空无效项」挑选项 / 配置 JSON 往返。假 probe 注入，**不启动 Shell、不启动任何程序**。
RunGroupModule("launcher", "软件启动器", LauncherChecks.Run);

// 「表情包收藏夹」：ActionMode 解析（未知值安全回退到只复制）/ 会话名归一与精确匹配（emoji 装饰 / 空白 / 大小写）/
// 目标 QQ 窗口解析（多窗口优先非最小化、空目标不猜）/ 随机发送延迟的夹取与边界 / 图片扩展名 / 剪贴板导入文件名 /
// 配置默认值与 JSON 往返。纯逻辑，**不启动 QQ、不碰剪贴板、不发送任何消息**；真实粘贴与自动发送由界面人工冒烟。
RunGroupModule("meme-board", "表情包收藏夹", MemeBoardChecks.Run);

// 「任务管理器」：DisplayFormat（字节 / 速度 / 百分比 / 运行时长 / 端口摘要 / TCP 状态 / 路径省略）、
// KillPlan（三级顺序 / taskkill 参数 / wmic 参数与进程名规范化 / 失败不继续）、
// StartupCommandParser（引号 / 无引号 / 环境变量 / rundll32）、StartupApprovedState（12 字节启停标记往返）、
// ProcessProtection（关键进程 / 桌面外壳）、ProcessInfo（原地合并 / 搜索命中 / 显示文本）、配置默认值与 JSON 往返；
// 另外真的只读采样本机（自己的进程 / 端口表 / 整机内存），**不结束任何进程、不写注册表**。
RunGroupModule("task-manager", "任务管理器", TaskManagerChecks.Run);

// 「应用管理器」：注册表卸载项过滤（系统组件 / 补丁 / 子项 / 空名）与同名去重优先级 /
// 安装日期解析（20260626 · 2026-06-26 · 越界 · 空）/ 大小与盘符格式化 / 驱动器标签 /
// 卸载命令行拆分（引号 / 无引号含空格 / msiexec）/ 排序比较（未知值沉底 · 正逆序 · 同值稳定）/
// 配置默认值与 JSON 往返 / 工具元数据；最后真的**只读**扫一遍本机注册表做自洽断言。
// ⚠ 全程不写注册表、不删文件、**绝不启动任何卸载 / 修改程序**（启动链路只测命令拆分，不真跑）。
RunGroupModule("app-manager", "应用管理器", AppManagerChecks.Run);

// 「游戏 → 俄罗斯方块」：工具 / 「游戏」类别注册、7 种方块 × 4 朝向的形状、SRS 墙踢表、
// 碰撞与边界、7-bag 出块、移动·软降·硬降、消行·计分·升级、暂存、幽灵落点、顶死判负、配置默认值与夹取。
// 纯逻辑，不创建窗口、不渲染、不碰真实 Data/Config。
RunGroupModule("tetris", "俄罗斯方块", TetrisChecks.Run);

// 「斗地主 / 军棋」的规则层：牌型识别与比大小、发牌、叫牌与结算；
// 军棋棋盘布局、走法（铁路 / 工兵拐弯 / 行营）、裁判（大吃小 / 炸弹 / 工兵排雷 / 夺旗）、布阵与胜负、暗棋过滤。
// 纯逻辑，不建窗口、不联网。
RunGroupModule("board-new-games", "斗地主/军棋", BoardGamesNewGameChecks.Run);

// 五个游戏的电脑对手（AI）+ 四档难度（入门 / 普通 / 困难 / 大师），以及四国军棋的棋盘与规则。
// 纯逻辑：AI 只算不渲染，四国军棋只跑规则，不建窗口、不联网。
RunGroupModule("ai-games", "电脑对手/四国军棋", AiAndFourJunqiChecks.Run);

// 围棋：规则（提子 / 禁自杀 / 打劫 / 停一手 / 中国规则数子）+ AI（四档难度）。
// 纯逻辑，不建窗口、不联网。
RunGroupModule("go", "围棋", GoChecks.Run);

// 贪吃蛇：两种对战玩法（共享果 / 大混战）、边缘规则、碰撞、重生虚化、评分权重、九档 AI。
// 纯逻辑，不建窗口、不联网。
RunGroupModule("snake", "贪吃蛇", SnakeChecks.Run);

// 飞行棋：15×15 十字棋盘、52 格外圈、四色、起飞规则（含 5 点后门）、精确到终点回退、
// 踩子与中间飞行线踢子、掷 6 再掷、胜负。纯逻辑，不建窗口、不联网。
RunGroupModule("ludo", "飞行棋", LudoChecks.Run);

// 跳棋：六芒星 121 格几何、2/3/4/6 人座位组合（第一个选位的人定规则）、
// 跳与连跳、可选走一格、占满对面角才胜、防卡出生点、让棋、AI。纯逻辑，不联网。
RunGroupModule("checkers", "跳棋", CheckersChecks.Run);

// 跳棋联机：本机 127.0.0.1 真回环（开房 / 连上 / 选座 / 走棋 / 状态广播 / 退出座位）+ 状态包往返。
// 只绑本机，不联外网。
RunGroupModule("checkers-net", "跳棋·联机", CheckersNetChecks.Run);

EnterGroup("shell");

// ---------- 完整性自检（全屏藏任务栏的前置条件） ----------
// 只读：读自己的令牌 + FindWindow，**不隐藏任何窗口**（见 docs/dev/pitfalls.md）。
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
var summary = runAllGroups
    ? (failed == 0
        ? $"全部通过（{results.Count} 项）· 分组：全部 {selfCheckGroups.Length} 组"
        : $"失败 {failed} / {results.Count} 项 · 分组：全部")
    : (failed == 0
        ? $"本次分组通过（{results.Count} 项）· 分组：{string.Join(" / ", requestedGroups)}（未运行 {skippedGroups.Count} 组，--all 可全跑）"
        : $"失败 {failed} / {results.Count} 项 · 分组：{string.Join(" / ", requestedGroups)}");
Console.WriteLine($"{summary} · 数据根：{AppPaths.Root}");

// 收尾：直接 TerminateProcess 带退出码终止进程。
// 原因：自检有多个分组会在各自的 STA 线程上创建 WPF 位图 / 视觉对象（图片、PDF、主题、Live2D 渲染…），
// 全量跑时 WPF 的合成线程会在进程收尾阶段一直空转，`return 退出码` 和 `Environment.Exit` 都会卡在
// CLR/WPF 的退出清理里 → 表现为「自检全部 PASS 但进程十来分钟不退出」，check.ps1 因此永远等不到
// 退出码（历史坑）。这里是测试程序，输出已经 Flush、各检查要写的文件也已落盘，直接带码终止最稳。
Console.Out.Flush();
if (!OperatingSystem.IsWindows()) Environment.Exit(failed == 0 ? 0 : 1);
NativeExit.TerminateProcess(NativeExit.GetCurrentProcess(), (uint)(failed == 0 ? 0 : 1));
return 0;   // 不可达：只为满足「有的分支 return 退出码」的编译要求

/// <summary>自检退出用的 Win32 调用（见文件末尾收尾处注释）。</summary>
internal static class NativeExit
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
}

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
