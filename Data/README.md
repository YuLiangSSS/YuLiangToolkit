# Data —— 功能数据（说明文件，不是运行时目录）

> ⚠️ **真正的运行时 `Data` 在 exe 旁边**，不在仓库里：
> `bin\Debug\net9.0-windows10.0.19041.0\Data\`、`bin\Release\net9.0-windows10.0.19041.0\Data\`，或 `publish.ps1` 出来的 `dist\YuLiangToolkit\Data\`。
> 这个仓库文件夹只保留说明，方便你（和以后接手的人）知道约定长什么样。

## 目录约定（运行时那份）

```
<exe 所在目录>\Data\
├─ 怎么用这两个文件夹.md    ← 程序首次运行时自己写的说明
├─ <功能Id>\                ← 每个功能一个同名小文件夹（首次真正写入时才创建）
│  ├─ player.swf
│  └─ sub/dir/其它资源.bin  ← 子目录也支持
├─ flash-downloader\        ← Flash 下载器：games\（下载的游戏）、tools\（播放器与播放页模板）
├─ batch-rename\            ← 批量重命名：rename-history.json（改名流水账，撤销靠它，删了就不能撤销）
└─ sample-framework\        ← 示例工具（可整目录删除）
```

* **小文件夹名 = 工具 Id**（`ITool.Id`），例如 `video-to-audio`、`flash-downloader`。
* 文件夹**按需自动创建**：某个功能第一次写文件时才出现，不用手工先建。
* 想手工放文件？直接在 exe 旁边的 `Data\<功能Id>\` 里建同名文件夹放进去，工具用 `ToolStorage.List()` 就能列出来。

## 代码里怎么读写（不要自己拼路径）

```csharp
var data = ToolStorage.For("flash-downloader");   // → <exe 旁边>\Data\flash-downloader\
data.EnsureCreated();
var swf  = data.PathOf("player.swf");             // 组装路径（不检查是否存在）
data.WriteBytes("player.swf", bytes);             // 写入资源
data.Import(@"D:\下载\game.swf");                  // 把外部文件拷进来（重名自动加 (1)）
foreach (var f in data.List("*.swf"))             // 列文件（含 SizeText / ModifiedText）
    Console.WriteLine($"{f.Name} · {f.SizeText}");
data.OpenInExplorer();                            // 让用户在资源管理器里看到
```

* 相对路径禁止用 `..` 或绝对路径逃出功能自己的数据目录（会抛 `ArgumentException`）。
* 所有 IO 失败都不会抛异常，返回 `null` / `false` / 空列表，由界面自行提示。

## 路径是怎么定的

`YLT_HOME` 环境变量（自检 / 特殊部署用）→ **exe 所在目录**（默认）→ `%AppData%\YuLiangToolkit`（exe 目录不可写时兜底）。
实现见 `src/YuLiangToolkit.Core/Storage/AppPaths.cs`，速查表见 [AGENTS.md](../AGENTS.md)。

## 注意

* **`dotnet clean`、手删 `bin` 或 `dist`、重新克隆仓库都会删掉 exe 旁边的数据**（下载的游戏、配置都在里面），
  要长期保存请自己备份整个程序目录。
* 本目录已从主项目编译项里排除（见 `YuLiangTookit.csproj` 的 `DefaultItemExcludes`），
  往里丢 `.cs` / `.xaml` 也不会被编译。
* ⚠️ 不要在 csproj 里用 `<None CopyToOutputDirectory>` 往 exe 旁边拷这里的文件：
  那些文件会被记进 `*.FileListAbsolute.txt`，源文件一删，下次构建的 IncrementalClean 会把
  exe 旁边的同名文件删掉（**踩过，删掉过用户已下载的游戏**）。说明文件由程序自己写（`RuntimeReadme`）。
