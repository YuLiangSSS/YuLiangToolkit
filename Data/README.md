# Data —— 功能数据总目录

这个文件夹用来放**每个功能运行需要的资源文件**（例如 flash 下载器的 `swf`、播放器运行时、模板、
字体、缓存、导出结果……）。代码不用改，直接把文件丢进对应功能的小文件夹即可。

## 目录约定

```
Data/
├─ README.md               ← 本说明
├─ <功能Id>/               ← 每个功能一个同名小文件夹（首次真正写入时自动创建）
│  ├─ player.swf
│  └─ sub/dir/其它资源.bin  ← 子目录也支持
└─ sample-framework/       ← 示例工具的文件夹（可整目录删除）
```

* **小文件夹名 = 工具 Id**（`ITool.Id`），例如 `video-to-audio`、`flash-downloader`。
  在 Rider/VS 里按运行时的就是本目录；发布后是 exe 同级的 `Data\`。
* 文件夹**按需自动创建**：某个功能第一次写文件时才会出现它的文件夹，不用手工先建。
* 想手工放文件进去？直接建同名文件夹放进去即可，工具用 `ToolStorage.List()` 就能列出来。

## 代码里怎么读写（不要自己拼路径）

```csharp
var data = ToolStorage.For("flash-downloader");   // → Data\flash-downloader\
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

## 注意

* 这里的数据**不进版本库也没关系**：大文件（几十 MB 的 swf / 运行时）建议自行加到 `.gitignore`，
  例如新增一行 `Data/flash-downloader/`；小的示例数据则可以提交。
* 本目录已从主项目编译项里排除（见 `YuLiangTookit.csproj` 的 `DefaultItemExcludes`），
  所以往这里丢 `.cs` / `.xaml` 也不会被编译进去。
* 路径解析顺序（`YLT_HOME` 环境变量 → 开发态仓库根 → exe 同级 → `%AppData%`）见
  `src/YuLiangToolkit.Core/Storage/AppPaths.cs`，速查表见根目录 `代码记录.md`。
