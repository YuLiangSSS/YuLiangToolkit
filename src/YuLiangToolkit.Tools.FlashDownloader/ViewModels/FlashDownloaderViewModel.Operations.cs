using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.FlashDownloader.Core;
using YuLiangToolkit.Tools.FlashDownloader.Engine;
using YuLiangToolkit.Tools.FlashDownloader.Web;

namespace YuLiangToolkit.Tools.FlashDownloader.ViewModels;

/// <summary>「Flash 下载器」的实际业务（界面以外的部分都在这个 partial 里）。</summary>
public sealed partial class FlashDownloaderViewModel
{
    // ================================================================ 列表

    /// <summary>重建游戏列表（<paramref name="announce"/> 为 true 时把结果写进状态栏与日志）。</summary>
    public void RefreshGames(bool announce)
    {
        var previous = SelectedGame?.Entry.FileOrFolderName;
        var entries = Library.List();

        Games.Clear();
        foreach (var entry in entries) Games.Add(new GameItem(entry));

        if (previous is not null)
            SelectedGame = Games.FirstOrDefault(g => g.Entry.FileOrFolderName == previous);

        OnPropertyChanged(nameof(GamesSummary));

        if (!announce) return;
        var legacy = Library.FindLegacyFolderNames();
        var suffix = legacy.Count > 0
            ? $"。由于版本更新，有 {legacy.Count} 个文件夹还是旧名字（例如 {legacy[0]} 应改成 {legacy[0]}s）"
            : "";
        StatusText = $"找到 {Games.Count} 个游戏{suffix}";
        Log(StatusText);
    }

    // ================================================================ 下载

    private async Task DownloadAsync(bool thenPlay)
    {
        var url = (GameUrl ?? "").Trim();
        if (url.Length == 0)
        {
            StatusText = "请输入游戏 URL，例如 https://www.4399.com/flash/6853.htm";
            return;
        }

        // 同一个游戏不重复下载（按后缀认）
        var (appendix, _) = GamePageParser.GetUrlInfo(url);
        if (appendix.Length > 0)
        {
            var existing = Games.FirstOrDefault(g => string.Equals(g.Entry.Appendix, appendix, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                SelectedGame = existing;
                StatusText = $"这个游戏已经下载过了：{existing.Entry.DisplayText}";
                Log(StatusText);
                return;
            }
        }

        Cfg.LastGameUrl = url;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "正在下载游戏…";
        try
        {
            var result = await _downloader.ResolveAndDownloadAsync(Library, url, Log, _cts.Token);
            if (result.Meta is not null) InfoText = $"找到的 SWF 路径：{result.Meta.ContentUrl}";

            if (!result.Ok)
            {
                StatusText = "下载失败";
                Log($"[失败] {result.Error}");
                ShowError("下载失败", result.Error);
                return;
            }

            RefreshGames(false);
            SelectedGame = Games.FirstOrDefault(g => g.Entry.FileOrFolderName == result.FileOrFolderName);
            StatusText = $"下载完成：{result.FileOrFolderName}";
            Log(StatusText);

            if (thenPlay) PlaySelected();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            Log("下载已取消");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    // ================================================================ 游玩

    private void PlaySelected()
    {
        var item = SelectedGame;
        if (item is null)
        {
            StatusText = "请先选择一个游戏";
            return;
        }

        var entry = item.Entry;
        if (entry.IsFolder)
        {
            if (entry.FolderExt == ".swf")
            {
                RunWithFlashPlayer(entry.GameFullPath);
            }
            else if (entry.FolderExt == ".htm")
            {
                PlayHtmlGame(entry);
            }
            else
            {
                StatusText = $"为安全考虑只能打开 .swf 和 .htm，这个文件夹标记的扩展名是 {entry.FolderExt}，不支持打开";
                Log(StatusText);
            }
            return;
        }

        RunWithFlashPlayer(entry.FullPath);
    }

    /// <summary>用 Flash 独立播放器打开一个 swf。</summary>
    private bool RunWithFlashPlayer(string swfFullPath)
    {
        if (!File.Exists(swfFullPath))
        {
            StatusText = $"找不到文件：{swfFullPath}";
            Log(StatusText);
            return false;
        }

        if (!HasFlashPlayer)
        {
            LocateFlashPlayer(false);
            if (!HasFlashPlayer)
            {
                var answer = Confirm(
                    "没找到 FlashPlayer.exe（Flash 独立播放器）。\n\n"
                    + "要现在自己去下一个吗？（Ruffle 的 ruffle.exe 也可以，放在同一个文件夹里就行）\n"
                    + "选「确定」用系统默认方式打开，选「取消」先不打开。",
                    "缺少 Flash 播放器");
                if (answer) OpenWithShell(swfFullPath);
                return false;
            }
        }

        try
        {
            var info = new ProcessStartInfo { FileName = FlashPlayerPath, UseShellExecute = false };
            info.ArgumentList.Add(swfFullPath);
            Process.Start(info);
            StatusText = $"已用 Flash 播放器打开 {Path.GetFileName(swfFullPath)}";
            Log(StatusText);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"无法运行游戏：{ex.Message}";
            Log(StatusText);
            OpenWithShell(swfFullPath);
            return false;
        }
    }

    /// <summary>网页游戏：优先用本地服务器 + 浏览器打开播放页，服务器没开就退回直接双击 htm。</summary>
    private void PlayHtmlGame(GameEntry entry)
    {
        var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
        if (!entry.HasGameFile)
        {
            StatusText = $"找不到游戏网页：{entry.GameFullPath}";
            Log(StatusText);
            return;
        }

        if (Cfg.UseLocalServerForHtm)
        {
            if (!Server.IsRunning && !StartServer())
            {
                Log("本地服务器没起来，改成直接用文件打开（有些游戏可能不正常）");
                OpenWithShell(entry.GameFullPath);
                return;
            }

            var width = ini.Width > 0 ? ini.Width : 800;
            var height = ini.Height > 0 ? ini.Height : 600;
            var url = $"{Server.BaseUrl}frame.htm?game={Uri.EscapeDataString(entry.GameName)}&width={width}&height={height}";
            OpenUrl(url);
            StatusText = $"已在浏览器打开：{url}";
            Log(StatusText + "（玩的时候别关掉本工具，否则服务器一停网页就加载不了文件了）");
            return;
        }

        OpenWithShell(entry.GameFullPath);
        StatusText = $"已打开 {entry.GameFullPath}";
        Log(StatusText);
    }

    // ================================================================ 删除

    private void DeleteSelected()
    {
        var item = SelectedGame;
        if (item is null) return;

        var entry = item.Entry;
        var extra = entry.IsFolder ? "（连同文件夹里的全部文件）" : "";
        if (!Confirm($"确定要删除 {entry.DisplayText} 吗？{extra}\n\n{entry.FullPath}", "删除确认")) return;

        var result = Library.Delete(entry);
        StatusText = result.Message;
        Log(result.Message);
        SelectedGame = null;
        RefreshGames(false);
    }

    private void DeleteAll()
    {
        if (Games.Count == 0)
        {
            StatusText = "没有可删除的游戏";
            return;
        }
        if (!Confirm($"确定要删除这 {Games.Count} 个游戏吗？文件会被真的删掉。", "全部删除")) return;

        var failed = 0;
        foreach (var item in Games.ToList())
        {
            var result = Library.Delete(item.Entry);
            if (!result.Ok) { failed++; Log(result.Message); }
        }
        StatusText = failed == 0 ? "已删除所有游戏" : $"删除完成，{failed} 个失败";
        Log(StatusText);
        SelectedGame = null;
        RefreshGames(false);
    }

    // ================================================================ 装入文件夹

    private async Task MoveIntoFolderAsync()
    {
        var item = SelectedGame;
        if (item is null || !item.Entry.CanMoveIntoFolder)
        {
            StatusText = "请先选中一个散放的 swf 文件（文件夹已经装好了）";
            return;
        }

        var entry = item.Entry;
        IsBusy = true;
        StatusText = "正在装入文件夹…";
        try
        {
            // 顺便重新解析一次，把网络路径与宽高写进 SWFNetPath.ini
            GameMeta? meta = null;
            if (entry.Appendix.Length > 0)
            {
                var resolved = await _downloader.ResolveByAppendixAsync(entry.Appendix);
                if (resolved.Ok) meta = resolved.Meta;
                else Log($"自动识别失败（ini 里会留空）：{resolved.Error}");
            }
            else
            {
                Log("文件名里没有站点后缀（如 _a12345），无法自动识别网络路径，ini 里会留空");
            }

            var result = Library.MoveIntoFolder(entry, meta);
            StatusText = result.Message;
            Log(result.Message);
            if (!result.Ok) ShowError("装入文件夹失败", result.Message);
            RefreshGames(false);

            var folderName = entry.GameName + "s";
            SelectedGame = Games.FirstOrDefault(g => g.Entry.FileOrFolderName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase));
        }
        finally { IsBusy = false; }
    }

    // ================================================================ 改为 htm 打开

    private async Task ConvertToHtmAsync()
    {
        var item = SelectedGame;
        if (item is null || !item.Entry.CanConvertToHtm)
        {
            StatusText = "只能对 xxx_?123s 这样的 swf 文件夹操作";
            return;
        }

        var entry = item.Entry;
        if (!Confirm(
                $"确定要把 {entry.GameName} 改成用网页打开吗？\n\n"
                + "这会失去「双击就能玩」的优势（只是断网也能玩而已）：\n"
                + "你仍然需要给浏览器装 Flash 插件，并忍受插件的不稳定。\n"
                + "只有不得不用浏览器环境时才这么做。",
                "改为 htm 打开"))
            return;

        IsBusy = true;
        StatusText = "正在转换成 htm…";
        try
        {
            var ini = SwfNetPathIni.FromFile(entry.IniFullPath, entry.FolderExt);
            GameMeta? meta = null;
            if (entry.Appendix.Length > 0)
            {
                var resolved = await _downloader.ResolveByAppendixAsync(entry.Appendix);
                if (resolved.Ok) meta = resolved.Meta;
            }

            // 尽量以 ini 里记录的信息为准（用户可能手工改过），其次用刚解析到的
            var netPath = ini.NetPath.Length > 0 ? ini.NetPath : meta?.ContentUrl ?? "";
            var isNewPage = ini.IsNewPage == SwfNetPathIni.NewPageUnknown
                ? (meta?.IsNewPage == true)
                : ini.IsNewPage == SwfNetPathIni.NewPageTrue;
            var newPageUrl = ini.NewPageUrl.Length > 0 ? ini.NewPageUrl : meta?.NewPageUrl ?? "";
            var width = ini.Width > 0 ? ini.Width : meta?.Width ?? -1;
            var height = ini.Height > 0 ? ini.Height : meta?.Height ?? -1;

            if (isNewPage && newPageUrl.Length == 0)
            {
                StatusText = "既不能在 SWFNetPath.ini 里找到新页面地址，也没能从网站获取，无法转换";
                Log(StatusText);
                ShowError("改为 htm 打开失败", StatusText);
                return;
            }

            string innerSwfName;
            string mainPageNetPath;
            if (isNewPage)
            {
                innerSwfName = GamePageParser.GetUrlFileName(netPath.Length > 0 ? netPath : newPageUrl);
                if (innerSwfName.Length == 0) innerSwfName = "mainSwfGame.swf";
                mainPageNetPath = newPageUrl;
            }
            else
            {
                innerSwfName = "mainSwfGame.swf";
                mainPageNetPath = netPath;
            }

            var renamed = Library.RenameFolderToHtm(entry,
                new GamesLibrary.HtmConversion(mainPageNetPath, innerSwfName, width, height, isNewPage));
            if (!renamed.Ok)
            {
                StatusText = renamed.Message;
                Log(renamed.Message);
                ShowError("改为 htm 打开失败", renamed.Message);
                return;
            }

            var newFolderName = renamed.Message;
            byte[] page;
            if (isNewPage)
            {
                Log($"正在下载游戏主页：{newPageUrl}");
                var fetched = await _http.GetBytesAsync(newPageUrl, entry.Appendix.Length > 0
                    ? GamePageParser.AppendixToUrl(entry.Appendix)?.Url ?? ""
                    : "", CancellationToken.None);
                if (!fetched.Ok || fetched.Content is null)
                {
                    StatusText = $"下载游戏主页失败：{fetched.Message}";
                    Log(StatusText);
                    ShowError("改为 htm 打开", StatusText);
                    return;
                }
                var text = GamePageParser.DecodeWebContent(fetched.Content, fetched.ContentType).Text;
                var replaced = text.Contains("4399.com", StringComparison.OrdinalIgnoreCase)
                    ? GamePageParser.Replace4399Page(text, Cfg.Replace4399H5Api)
                    : GamePageParser.Replace7k7k17yyPage(text);
                page = System.Text.Encoding.UTF8.GetBytes(replaced);
            }
            else
            {
                // 无新页面：用内置的加载页指向本地 swf
                page = System.Text.Encoding.UTF8.GetBytes(
                    FlashWebAssets.BuildFlashPage(innerSwfName, entry.GameName));
            }

            var written = Library.WriteMainPage(newFolderName, entry.GameName, page);
            if (!written.Ok)
            {
                StatusText = written.Message;
                Log(StatusText);
                ShowError("改为 htm 打开", written.Message);
                return;
            }

            StatusText = "转换成 htm 成功";
            Log(StatusText + $"：{newFolderName}");
            RefreshGames(false);
            SelectedGame = Games.FirstOrDefault(g => g.Entry.FileOrFolderName == newFolderName);
        }
        catch (Exception ex)
        {
            StatusText = $"转换成 htm 失败：{ex.Message}";
            Log(StatusText);
        }
        finally { IsBusy = false; }
    }

    // ================================================================ 在资源管理器中显示

    private void RevealSelected()
    {
        var entry = SelectedGame?.Entry;
        if (entry is null) return;
        try
        {
            // /select 对文件和文件夹都能用（文件夹会在它的父目录里被选中）
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log($"打开资源管理器失败：{ex.Message}");
        }
    }

    // ================================================================ 目录 / 服务器 / 播放器

    private void OpenGamesFolder()
    {
        Library.EnsureCreated();
        OpenWithShell(Library.Root);
    }

    private void ChooseGamesFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择游戏存放目录",
            InitialDirectory = Directory.Exists(GamesDirectory) ? GamesDirectory : Storage.Directory,
        };
        if (dialog.ShowDialog() != true) return;

        // 只改内存里的配置（400ms 内自动落盘），目录立刻生效
        Cfg.GamesFolder = dialog.FolderName;
        GamesDirectory = dialog.FolderName;
        Library = new GamesLibrary(GamesDirectory);
        var previous = Server.Session;
        if (previous is not null) Server.EndSession();
        _autoDownloadEnabled = false;
        OnPropertyChanged(nameof(AutoDownloadEnabled));
        OnPropertyChanged(nameof(AutoDownloadButtonText));
        RefreshGames(false);
        StatusText = $"游戏目录已改为 {GamesDirectory}";
        Log(StatusText);
    }

    private void UseDefaultFolder()
    {
        Cfg.GamesFolder = "";
        GamesDirectory = Cfg.DefaultGamesDirectory;
        Library = new GamesLibrary(GamesDirectory);
        RefreshGames(false);
        StatusText = $"已恢复默认游戏目录：{GamesDirectory}";
        Log(StatusText);
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开失败：{ex.Message}");
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开网址失败：{ex.Message}（{url}）");
        }
    }

    /// <summary>启动本地服务器（端口被占用会自动往后找）。</summary>
    public bool StartServer()
    {
        var ok = Server.Start(Cfg.ServerStartPort);
        UpdateServerVisibility();
        if (!ok)
        {
            ServerStatus = "本地服务器：启动失败（没有可用端口）";
            StatusText = ServerStatus;
        }
        return ok;
    }

    private void StopServer()
    {
        Server.EndSession();
        AutoDownloadEnabled = false;
        Server.Stop();
        UpdateServerVisibility();
        StatusText = "本地服务器已停止";
    }

    private void UpdateServerVisibility()
    {
        if (Server.IsRunning)
        {
            ServerStatus = $"本地服务器：{Server.BaseUrl}（关掉本工具就失效）";
            OnPropertyChanged(nameof(ServerBaseUrlText));
        }
        else
        {
            ServerStatus = "本地服务器：未启动（网页游戏需要它）";
        }
    }

    public string ServerBaseUrlText => Server.IsRunning ? Server.BaseUrl : "";

    private void CopyServerUrl()
    {
        if (!Server.IsRunning) return;
        try
        {
            Clipboard.SetText(Server.BaseUrl);
            StatusText = $"已复制 {Server.BaseUrl}";
        }
        catch (Exception ex)
        {
            Log($"复制失败：{ex.Message}");
        }
    }

    /// <summary>找 Flash 播放器；<paramref name="announce"/> 为 true 时把结果写进状态栏。</summary>
    public bool LocateFlashPlayer(bool announce)
    {
        var found = FlashPlayerLocator.EnsureAvailable(Cfg.FlashPlayerPath, ToolsDirectory);
        FlashPlayerPath = found.Ok ? found.Path : "";
        if (found.Ok)
        {
            Cfg.FlashPlayerPath = found.Path;
            if (announce)
            {
                StatusText = $"已找到 Flash 播放器：{found.Path}";
                Log(StatusText + $"（来源：{found.Source}）");
            }
            return true;
        }

        if (announce)
        {
            StatusText = "没找到 FlashPlayer.exe";
            Log("没找到 FlashPlayer.exe。三个办法：① 把它（或 Ruffle 的 ruffle.exe）放进 " + ToolsDirectory
                + "；② 点「指定…」直接选一个；③ 若你之前把原工具连同播放器整理到了别处（例如「桌面\\Flash Games\\tool\\」），"
                + "「指定…」选到那个 FlashPlayer.exe 即可 —— 选一次就会记住，并自动拷进上面那个目录。");
        }
        return false;
    }

    private void ChooseFlashPlayer()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Flash 独立播放器",
            Filter = "Flash 播放器|FlashPlayer*.exe;flashplayer*.exe;ruffle*.exe|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        Cfg.FlashPlayerPath = dialog.FileName;
        LocateFlashPlayer(true);
    }

    private void OnConfigChanged()
    {
        OnPropertyChanged(nameof(GamesDirectorySourceText));
        UpdateServerVisibility();
    }

    // ================================================================ 首次运行的小礼物

    /// <summary>
    /// 首次运行时在游戏目录里放一份命名规则说明（纯文本，用户可删）。
    ///
    /// <para>⚠ 一定要等到「游戏目录」确定之后再调用：早期版本在构造函数最前面就调了它，
    /// 那时目录还是空字符串，说明文件被写到了仓库根目录（自检 / 截图时才发现的）。</para>
    /// </summary>
    private void SeedReadmeIfFirstRun()
    {
        if (!IsFirstRun || Library.Root.Length == 0) return;
        try
        {
            Library.EnsureCreated();
            var readme = Path.Combine(Library.Root, "怎么放游戏.txt");
            if (File.Exists(readme)) return;
            File.WriteAllText(readme, """
                这个文件夹里放下载好的小游戏，命名规则（去掉引号）：

                  单个 swf：   "游戏名_a12345.swf"
                  网页游戏：   文件夹 "游戏名_a12345h"，里面是 "游戏名_a12345.htm" + "SWFNetPath.ini"
                  播放器游戏： 文件夹 "游戏名_a12345s"，里面是 "游戏名_a12345.swf" + "SWFNetPath.ini"

                其中字母 a=4399、b=7k7k、c=17yy、z=其它；数字是游戏在站点里的编号。
                字母后面的 s / h 表示用 Flash 播放器 / 用网页打开，改错了工具会拒绝打开。

                用本工具的「下载」按钮会自动按这个规则命名，不需要你手工改。
                这个说明文件可以直接删掉。
                """, new UTF8Encoding(true)); // 带 BOM：Windows 记事本 / 老工具打开中文才不会乱码
        }
        catch { /* 只读环境忽略 */ }
    }

    /// <summary>工具被切走：停服务器、停后台任务、落盘配置。</summary>
    protected override void OnClosed()
    {
        try { _watchCts?.Cancel(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { Server.EndSession(); } catch { }
        try { Server.Stop(); } catch { }
        try { Server.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}
