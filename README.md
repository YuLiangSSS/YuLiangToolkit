# YuLiangToolkit（凉雨工具集|自用小工具）99.99% AI Coding

一个可扩展的 WPF 工具集应用：主页是工具卡片墙（**搜索 / 排序 / 标签**），左侧导航自动列出所有已发现的工具。
深色主题 + 自定义标题栏（最小化 / 最大化 / 关闭按钮画在界面里，没有系统窗口顶栏）。

## 工具一览

每个工具都有**单独一页**详细说明（用法、设置项、数据放在哪、注意事项）—— **点表里的工具名就能进去**：

| 工具 | 能干什么 |
| --- | --- |
| [**🎥 视频转音频**](docs/tools/video-to-audio.md) | 抽视频里的音轨：有损 MP3 / 无损 FLAC / 原样提取，批量 + 进度 + 取消 |
| [**🎬 视频 / 音乐播放器**](docs/tools/media-player.md) | 本地播放器：播放列表、单曲 / 随机、**沉浸式全屏（鼠标一动浮出控制条）**、**后台播放（切页不断，左下角迷你条）**、可调步长的快进快退 |
| [**⚡ Flash 下载器**](docs/tools/flash-downloader.md) | 4399 / 7k7k / 17yy / ArmorGames / Kongregate 的小游戏下载到本地离线玩 |
| [**💬 联机聊天室**](docs/tools/chat-room.md) | 主机开端口、客户端连进来，文字 / 图片 / 文件随便传，带单次流量上限 |
| [**🏷️ 批量重命名**](docs/tools/batch-rename.md) | 规则清单 + 实时预览 + 两阶段改名 + 可撤销 |
| [**🎧 osu! 下图器**](docs/tools/map-downloader.md) | 搜谱面集、批量下 `.osz`（含视频 / 无视频） |
| [**🧪 框架示例**](docs/tools/sample-framework.md) | 开发用模板（Data / Config 轮子演示，可整目录删除） |

> 全部文档的索引（含通用说明）在 [docs/](docs/README.md)。

> 主页的搜索 / 排序 / 标签、自定义标题栏、以及**数据存在哪（Data / Config 目录约定）**：
> 见 [docs/shell.md](docs/shell.md)。

## 快速开始

```powershell
# 一键还原 + 构建（离线可用，用本机 NuGet 缓存）
.\build.ps1

# 运行（或直接在 Rider / Visual Studio 里打开解决方案按运行）
dotnet run --project YuLiangTookit.csproj

# 发布到 dist\YuLiangToolkit\（加 -SelfContained 可打成不依赖本机 .NET 的版本）
.\publish.ps1
```

要求：.NET 9 SDK（`global.json` 已锁定 9.0.317）。
只有「视频转音频」依赖 FFmpeg（应用会自动检测 `PATH` 里的 `ffmpeg.exe`，也可以在工具里手动指定）——
见 [视频转音频](docs/tools/video-to-audio.md)。

## 文档

- 📘 **[docs/](docs/README.md)** —— 每个工具一页的详细说明（就是 GitHub Wiki 那种用法，但它跟着代码一起走）；
- 🧰 **[AGENTS.md](AGENTS.md)** —— 改代码前先看它：所有「轮子」（工具契约、MVVM 基类、Data/Config 配置框架、
  FFmpeg 封装、UI 样式清单）的用途 + 签名 + 一行用法，加上新增功能的完整步骤和踩过的坑；
- ✅ **自检**：654 项行为逐条真跑（退出码 0 = 全通过），改了行为就该顺手跑一次：

  ```powershell
  dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
  ```

## 数据与配置

**运行时目录就在 exe 旁边**（`bin\Debug\net9.0-windows\` 或发布出来的目录），首次运行自动生成：

- `Data\<功能Id>\` —— 功能自己的东西（下载的游戏、聊天记录、播放列表快照…）；
- `Config\<功能Id>\config.json` —— 那个功能所有选项的值，**改一下就自动保存，没有「保存」按钮**。

因为数据跟着 exe 走，**整个程序目录拷走即可迁移 / 发布**；反过来 `dotnet clean`、手删 `bin`/`dist`
会连数据一起删掉。细节见 [docs/shell.md](docs/shell.md#数据与配置都在-exe-旁边)。

## 项目结构

```
YuLiangTookit.csproj                     # 主程序（WPF 壳：自定义标题栏、主页、侧边栏导航、深色主题）
build.ps1 / publish.ps1                  # 一键构建 / 一键发布（发布到 dist\YuLiangToolkit\）
AGENTS.md                               # 轮子速查 / 开发约定（改代码先读这个）
README.md                               # 本文件：工具一览 + 快速开始
docs/                                   # 每个工具一页的详细说明（见 docs/README.md）
Data/  Config/                           # 只有说明文件【运行时目录在 exe 旁边】
src/
├── YuLiangToolkit.Core/                 # 核心库：工具契约 / MVVM 基类 / 存储 / 配置 / FFmpeg 封装
├── YuLiangToolkit.Tools.VideoToAudio/   # 功能：视频转音频
├── YuLiangToolkit.Tools.MediaPlayer/    # 功能：视频 / 音乐播放器
├── YuLiangToolkit.Tools.BatchRename/    # 功能：批量重命名（规则清单引擎 + 两阶段改名 + 撤销）
├── YuLiangToolkit.Tools.FlashDownloader/# 功能：Flash 下载器（五站点解析 + 本地服务器 + 自动补全资源）
├── YuLiangToolkit.Tools.ChatRoom/       # 功能：联机聊天室（TCP 直连 / 内网穿透 + 传文件 + 流量上限）
├── YuLiangToolkit.Tools.MapDownloader/  # 功能：osu! 下图器（搜谱面集 + 批量下 .osz）
└── YuLiangToolkit.Tools.Sample/         # 框架示例（新功能的复制模板，可整目录删除）
tests/YuLiangToolkit.FrameworkSelfCheck/ # 轮子自检控制台程序（654 项行为逐条验证）
```

## 如何添加一个新工具

实现 `ITool`（提供 `Id / Name / Description / EmojiIcon / Category / Order` 与 `CreateView()`）就会被
`ToolDiscovery` 在启动时自动发现；选项继承 `ToolConfig`，ViewModel 继承 `ToolViewModelBase`，
Data / Config 的路径与持久化全由框架接管。也可以把编译好的 dll 放进程序目录的 `plugins\`（外部插件，不用改主程序）。

最省事的做法是复制 `src\YuLiangToolkit.Tools.Sample` 当模板，完整步骤见
[AGENTS.md 第 4 节](AGENTS.md) 与 [框架示例](docs/tools/sample-framework.md)。
