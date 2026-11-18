using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Diary;
using YuLiangToolkit.Tools.Diary.ViewModels;
using YuLiangToolkit.Tools.Diary.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「日记本」自检：工具元数据 / 正文计数与摘要 / 每篇一个 JSON 的读写往返（含损坏文件容错）/
/// 富文本 ⇄ HTML（STA）/ 搜索与排序 / 新建·删除·配置往返。
/// 全部纯逻辑，**不创建窗口、不碰真实 Data/Config**（编辑界面本身由截图冒烟验收）。
/// </summary>
internal static class DiaryChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckModel(assert);
        CheckText(assert);
        CheckStore(assert);
        CheckConfig(assert);
        CheckViewModel(assert);
        CheckRichText(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new DiaryTool();
        check("[日记本] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "diary" && tool.Name == "日记本" && tool.EmojiIcon == "📔"
            && tool.Category == "生活" && tool.Order == 24,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[日记本] 标签含日记 / 富文本 / 心情",
            tool.Tags.Contains("日记") && tool.Tags.Contains("富文本") && tool.Tags.Contains("心情"),
            string.Join(" / ", tool.Tags));

        check("[日记本] 描述写明支持富文本与普通文本两种写法",
            tool.Description.Contains("富文本") && tool.Description.Contains("普通文本"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 数据模型

    private static void CheckModel(CheckFn check)
    {
        var empty = new DiaryEntry { Title = "", CreatedAt = new DateTime(2026, 3, 5, 9, 8, 0) };
        check("[日记本] 空标题显示成「（无标题）」", empty.DisplayTitle == "（无标题）", empty.DisplayTitle);
        check("[日记本] 标题首尾空白会被去掉后显示", new DiaryEntry { Title = "  旅行  " }.DisplayTitle == "旅行");

        var plain = new DiaryEntry { Format = DiaryFormat.PlainText, Content = "你好 世界\n第二行" };
        check("[日记本] 普通文本字数按非空白字符算",
            plain.WordCount == 7 && plain.WordCountText == "7 字",
            $"{plain.WordCount} / {plain.WordCountText}");
        check("[日记本] 普通文本摘要压成一行", plain.Snippet == "你好 世界 第二行", plain.Snippet);

        var rich = new DiaryEntry { Format = DiaryFormat.RichText, Content = "<p><b>加粗</b> 文字</p>" };
        check("[日记本] 富文本字数按纯文字算（标签不计）",
            rich.WordCount == 4 && rich.Snippet == "加粗 文字",
            $"{rich.WordCount} / {rich.Snippet}");

        check("[日记本] 空内容会被判为空（含仅空段落 / 仅有图片不算空）",
            new DiaryEntry { Format = DiaryFormat.RichText, Content = "" }.IsEmptyContent
            && new DiaryEntry { Format = DiaryFormat.RichText, Content = "<p><br></p>" }.IsEmptyContent
            && !new DiaryEntry { Format = DiaryFormat.RichText, Content = "<p><img src=\"x.png\"></p>" }.IsEmptyContent);

        check("[日记本] 日期徽标（日 / 月 / 时间）格式化正确",
            empty.DateDay == "05" && empty.DateMonth == "03月" && empty.TimeText == "09:08",
            $"{empty.DateDay} / {empty.DateMonth} / {empty.TimeText}");
        check("[日记本] 完整日期带中文年月日与时间",
            empty.FullDateText.Contains("2026年3月5日") && empty.FullDateText.Contains("09:08"),
            empty.FullDateText);
    }

    // ---------------------------------------------------------------- 纯文本工具

    private static void CheckText(CheckFn check)
    {
        var plain = DiaryText.HtmlToPlainText("<h1>标题</h1><p>正文 <a href=\"#\">链接</a></p>");
        check("[日记本] HTML → 纯文本：块级标签转换行、去掉标签只留文字",
            plain.Contains("标题") && plain.Contains("正文") && plain.Contains("链接") && !plain.Contains("<"),
            plain.Replace("\n", "\\n"));

        var html = DiaryText.FromPlainText("a<b\nc");
        check("[日记本] 纯文本 → HTML：转义特殊字符、换行变 <br>",
            html.Contains("a&lt;b") && html.Contains("<br>") && html.StartsWith("<p>"),
            html);

        check("[日记本] 非空白字数统计忽略空格 / 制表符 / 换行",
            DiaryText.CountCharacters("a b\tc\n") == 3 && DiaryText.CountCharacters("") == 0
            && DiaryText.CountCharacters(null) == 0);

        var longText = new string('字', 100);
        var snippet = DiaryText.Snippet(longText, DiaryFormat.PlainText, 60);
        check("[日记本] 长文本摘要会截断并加省略号",
            snippet.Length == 61 && snippet.EndsWith('…'), snippet.Length.ToString());
    }

    // ---------------------------------------------------------------- 存储

    private static void CheckStore(CheckFn check)
    {
        var storage = ToolStorage.For("diary-selftest");
        storage.ClearAll();
        var store = new DiaryStore(storage);

        var older = new DiaryEntry
        {
            Id = "20260101-080000-aaaaaa", Title = "旧的一篇",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0), UpdatedAt = new DateTime(2026, 1, 1, 8, 0, 0),
            Format = DiaryFormat.PlainText, Content = "旧内容",
        };
        var newer = new DiaryEntry
        {
            Id = "20260202-090000-bbbbbb", Title = "新的一篇",
            CreatedAt = new DateTime(2026, 2, 2, 9, 0, 0), UpdatedAt = new DateTime(2026, 2, 2, 9, 0, 0),
            Format = DiaryFormat.RichText, Content = "<p>新内容</p>", Mood = "😊",
        };

        check("[日记本] 保存两篇日记成功", store.Save(older) && store.Save(newer));

        var files = Directory.GetFiles(store.EntriesDirectory, "*.json");
        check("[日记本] 每篇日记一个 JSON，落在 entries 目录里",
            files.Length == 2 && files.All(f => Path.GetFileName(Path.GetDirectoryName(f)!) == "entries"),
            string.Join(",", files.Select(Path.GetFileName)));

        var loaded = store.LoadAll();
        check("[日记本] 读回全部：按创建时间倒序（最新在前）",
            loaded.Count == 2 && loaded[0].Title == "新的一篇" && loaded[1].Title == "旧的一篇"
            && loaded[0].Mood == "😊",
            string.Join(" / ", loaded.Select(e => e.Title)));

        check("[日记本] 读回保留写法 / 正文等字段",
            loaded[0].Format == DiaryFormat.RichText && loaded[0].Content == "<p>新内容</p>"
            && loaded[1].Format == DiaryFormat.PlainText && loaded[1].Content == "旧内容");

        // 损坏文件不该让整次读取崩掉（只会丢这一篇）
        File.WriteAllText(Path.Combine(store.EntriesDirectory, "broken.json"), "{ 这不是合法 JSON ");
        var afterBroken = store.LoadAll();
        check("[日记本] 损坏的日记文件被跳过、其它篇照常读回",
            afterBroken.Count == 2, afterBroken.Count.ToString());

        check("[日记本] 删除一篇成功、之后只剩一篇",
            store.Delete(older.Id) && store.LoadAll().Count == 1);

        storage.ClearAll();
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new DiaryConfig();
        check("[日记本] 配置默认值：最新在前 / 富文本",
            config.SortNewestFirst && config.DefaultFormat == DiaryFormat.RichText && config.SortText == "最新在前",
            $"{config.SortNewestFirst} / {config.DefaultFormat}");

        var store = ToolConfigStore.For(DiaryConfig.ToolId, "selfcheck-config.json");
        try
        {
            var saved = store.Load<DiaryConfig>();
            saved.SortNewestFirst = false;
            saved.DefaultFormat = DiaryFormat.PlainText;

            var reloaded = ToolConfigStore.For(DiaryConfig.ToolId, "selfcheck-config.json").Load<DiaryConfig>();
            check("[日记本] 配置往返（排序 / 默认写法）",
                !reloaded.SortNewestFirst && reloaded.DefaultFormat == DiaryFormat.PlainText,
                $"{reloaded.SortNewestFirst} / {reloaded.DefaultFormat}");
        }
        finally
        {
            store.DeleteFile();
        }
    }

    // ---------------------------------------------------------------- ViewModel（无头）

    private static void CheckViewModel(CheckFn check)
    {
        // 把这组的数据目录清干净，避免与别的自检互相干扰
        ToolStorage.For(DiaryConfig.ToolId).ClearAll();

        var vm = new DiaryViewModel();

        check("[日记本] 打开时没有日记、选中为空",
            vm.Entries.Count == 0 && vm.SelectedEntry is null && vm.HasNoSelection,
            vm.Entries.Count.ToString());

        vm.NewEntry();
        check("[日记本] 新建一篇：进入列表、成为选中、写入磁盘",
            vm.Entries.Count == 1 && vm.SelectedEntry is not null && vm.HasSelection
            && Directory.GetFiles(vm.Store.EntriesDirectory, "*.json").Length == 1,
            vm.Entries.Count.ToString());

        vm.DraftTitle = "自检标题";
        check("[日记本] 改标题即时反映到那一篇", vm.SelectedEntry!.Title == "自检标题", vm.SelectedEntry.Title);

        vm.SelectedEntry.Content = DiaryText.FromPlainText("第一行\n第二行");
        vm.SelectedEntry.Format = DiaryFormat.RichText;
        vm.SaveCurrentEntry(explicitSave: true);
        check("[日记本] 保存后字数统计更新", vm.WordCountText == "6 字", vm.WordCountText);

        vm.Keyword = "自检标题";
        check("[日记本] 搜索标题命中", vm.Entries.Count == 1, vm.Entries.Count.ToString());

        vm.Keyword = "根本不存在的关键字zzz";
        check("[日记本] 搜不到时列表为空但知道「有日记」",
            vm.Entries.Count == 0 && vm.IsFilteredEmpty && vm.HasAnyEntry,
            $"count={vm.Entries.Count} filteredEmpty={vm.IsFilteredEmpty}");

        vm.Keyword = "";
        vm.SortNewestFirst = false;
        check("[日记本] 切换排序不丢日记（切换后仍能看到这一篇）", vm.Entries.Count == 1);

        vm.ConfirmDelete = _ => true;
        vm.DeleteSelected();
        check("[日记本] 删除选中篇后列表清空、磁盘文件也没了",
            vm.Entries.Count == 0 && vm.SelectedEntry is null
            && Directory.GetFiles(vm.Store.EntriesDirectory, "*.json").Length == 0,
            vm.Entries.Count.ToString());
    }

    // ---------------------------------------------------------------- 富文本 ⇄ HTML（STA）

    private static void CheckRichText(CheckFn check)
    {
        // FlowDocument 必须在 STA 线程上创建（WPF 文档对象有线程亲和性），单独起一个 STA 线程跑。
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunRichText(check); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            check("[日记本] 富文本 ⇄ HTML 自检运行（STA 线程）", false, failure.Message);
    }

    private static void RunRichText(CheckFn check)
    {
        var document = new FlowDocument();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("普通 ") { FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run("文字") { Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xDB)) });
        document.Blocks.Add(paragraph);

        var html = RichTextHtml.ToHtml(document);
        check("[日记本] 富文本 → HTML：加粗 / 颜色都能带出去",
            html.Contains("<b>") && html.Contains("color:#2F6FDB"),
            html);

        var roundTrip = RichTextHtml.FromHtml(html);
        var text = new TextRange(roundTrip.ContentStart, roundTrip.ContentEnd).Text;
        check("[日记本] HTML → 富文本：文字内容不丢",
            text.Contains("普通") && text.Contains("文字"), text.Trim());

        var stable = RichTextHtml.ToHtml(RichTextHtml.FromHtml("<p><b>加粗</b>的段落</p>"));
        check("[日记本] 富文本往返稳定（再来一次不会越转越乱）",
            stable.Contains("<b>加粗</b>") && stable.Contains("的段落"), stable);

        check("[日记本] 空 / null HTML 也能读成一份空文档",
            RichTextHtml.FromHtml("").Blocks.Count == 1 && RichTextHtml.FromHtml(null).Blocks.Count == 1);
    }
}
