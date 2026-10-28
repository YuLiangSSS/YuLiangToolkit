# YuLiangToolkit（凉雨工具集|自用小工具）99.99% AI Coding

一个可扩展的 WPF 工具集应用：主页是工具卡片墙（**搜索 / 排序 / 标签**），左侧导航自动列出所有已发现的工具。
功能多了会按类别收进**类别页**（支持任意层级分类，顶部是可点击的面包屑 `主页 › 音游工具 › 工具名`）；
右上角「⚙ 主页设置」里可以开关**鼠标粒子效果**、自定义**粒子颜色（1~2 种颜色随机渐变）**，还能选**点 × 时「关闭应用 / 最小化到托盘」**；程序在**任务栏右侧有托盘小图标**（单击恢复窗口，右键菜单：显示主窗口 / 主页设置 / 退出）；顶部工具条还有**浏览器式「← 后退 / → 前进」**，跳错页面可以快速回退。
深色主题 + 自定义标题栏（最小化 / 最大化 / 关闭按钮画在界面里，没有系统窗口顶栏；**最大化时按住标题栏往外拖，
会先还原成原来的窗口大小再跟着鼠标走** —— 和普通 Windows 窗口一样）。

## 工具一览

每个工具都有**单独一页**详细说明（用法、设置项、数据放在哪、注意事项）—— **点表里的工具名就能进去**：

| 工具 | 能干什么 |
| --- | --- |
| [**🎥 视频转音频**](docs/tools/video-to-audio.md) | 抽视频里的音轨：有损 MP3 / 无损 FLAC / 原样提取，批量 + 进度 + 取消 |
| [**🎬 视频 / 音乐播放器**](docs/tools/media-player.md) | 本地播放器：播放列表、单曲 / 随机、**沉浸式全屏（鼠标一动浮出控制条）**、**后台播放（切页不断，左下角迷你条）**、可调步长的快进快退 |
| [**🖼️ 图片连续阅读器**](docs/tools/picture-viewer.md) | 把一个文件夹的图片首尾相接拼成一条长带（无缝、无间距），滚轮连续看条漫 / 长图 / 漫画，可切纵向 / 横向 / 日式从右到左，也可切**双页翻页 / 单页浏览**；支持 **25% ~ 800% 缩放**、放大后**自由拖拽**、可开关的**拖动惯性（连续甩会越滑越快）**；可**关联图片后缀，双击图片直接打开并定位到那张**（默认**复用同一个窗口**，也能在设置里切成**多开**）；**点工具条上的文件夹路径可在资源管理器打开所在目录**；还能调**三层图片边框（RGBA 颜色 + 调色板）**并**一键恢复默认** |
| [**⚡ Flash 下载器**](docs/tools/flash-downloader.md) | 4399 / 7k7k / 17yy / ArmorGames / Kongregate 的小游戏下载到本地离线玩 |
| [**💬 联机聊天室**](docs/tools/chat-room.md) | 主机开端口、客户端连进来，文字 / 图片 / 文件随便传，带单次流量上限 |
| [**🏷️ 批量重命名**](docs/tools/batch-rename.md) | 规则清单 + 实时预览 + 两阶段改名 + 可撤销 |
| [**🎧 osu! 下图器**](docs/tools/map-downloader.md) | 搜谱面集、批量下 `.osz`（含视频 / 无视频） |
| [**🎹 osu! Key 转换器**](docs/tools/osu-key-converter.md) | 把低键数 mania 谱面「复制列 / 插空白列」加成高键数（2~10 可选），支持批量 |
| [**🍜 osu! LN 转换器**](docs/tools/osu-ln-transformer.md) | 按 14 档强度把普通键改成 LN（长条），可选保留原 LN / 修尾巴误差 |
| [**⭐ osu! 等级计算器**](docs/tools/osu-level-calculator.md) | 拖入 mania 谱面算等级（Star Rating → 等级），批量表格 |
| [**⚫ 联机五子棋**](docs/tools/gomoku.md) | 开 TCP 房间和朋友联机下五子棋：可选禁手（三三 / 四四 / 长连）、悔棋、聊天、同机双人 |
| [**🀄 联机象棋**](docs/tools/xiangqi.md) | 开 TCP 房间联机下中国象棋：自由摆棋 + 一键默认开局、让子 / 让先、中文记谱 |
| [**🧪 框架示例**](docs/tools/sample-framework.md) | 开发用模板（Data / Config 轮子演示，可整目录删除） |

> 🎵 **音游工具**是主页上的一个分类：点「音游工具」类别卡片进类别页，再点工具卡片打开；
> 顶部的面包屑（`主页 › 音游工具 › 工具名`）每一层都能点回去。osu! 相关的四个工具都收在这个分类里。
>
> 🎲 **棋牌**也是主页上的一个分类：联机五子棋与联机象棋都收在里面，开一个房间（或「同机双人」）就能对弈。

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
见 [视频转音频](docs/tools/video-to-audio.md)。osu! 三个转换 / 计算工具用 `OsuParsers`（MIT）解析 `.osu`：
从源码构建时**首次需要联网还原一次** NuGet 包，之后走本机缓存（`build.ps1` 离线也能命中）。

## 文档

- 📘 **[docs/](docs/README.md)** —— 每个工具一页的详细说明（就是 GitHub Wiki 那种用法，但它跟着代码一起走）；
- 🧰 **[AGENTS.md](AGENTS.md)** —— 改代码前先看它：所有「轮子」（工具契约、MVVM 基类、Data/Config 配置框架、
  FFmpeg 封装、UI 样式清单）的用途 + 签名 + 一行用法，加上新增功能的完整步骤和踩过的坑；
- ✅ **自检**：1600 项行为逐条真跑（退出码 0 = 全通过），改了行为就该顺手跑一次：

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
YuLiangTookit.csproj                     # 主程序（WPF 壳：自定义标题栏、主页/类别页/面包屑、侧边栏导航、深色主题）
build.ps1 / publish.ps1                  # 一键构建 / 一键发布（发布到 dist\YuLiangToolkit\）
AGENTS.md                               # 轮子速查 / 开发约定（改代码先读这个）
SingleInstance.cs                       # 单实例唤醒（Mutex + 命名管道：再启动一次只唤醒已开窗口）
README.md                               # 本文件：工具一览 + 快速开始
LICENSE                                 # Unlicense（随便用，不用署名）
THIRD-PARTY-NOTICES.md                  # 第三方组件的 MIT 许可（OsuParsers / ppy 数学工具）
docs/                                   # 每个工具一页的详细说明（见 docs/README.md）
Data/  Config/                           # 只有说明文件【运行时目录在 exe 旁边】
src/
├── YuLiangToolkit.Core/                 # 核心库：工具契约 / 多级分类目录树 / MVVM 基类 / 存储 / 配置 / FFmpeg 封装 / 粒子 / 通用 TCP 房间
├── YuLiangToolkit.Tools.VideoToAudio/   # 功能：视频转音频
├── YuLiangToolkit.Tools.MediaPlayer/    # 功能：视频 / 音乐播放器
├── YuLiangToolkit.Tools.PictureViewer/  # 功能：图片连续阅读器（连续长带 / 双页翻页 / 单页 + 缩放 / 惯性 / 双击打开 / 图片边框）
├── YuLiangToolkit.Tools.BatchRename/    # 功能：批量重命名（规则清单引擎 + 两阶段改名 + 撤销）
├── YuLiangToolkit.Tools.FlashDownloader/# 功能：Flash 下载器（五站点解析 + 本地服务器 + 自动补全资源）
├── YuLiangToolkit.Tools.ChatRoom/       # 功能：联机聊天室（TCP 直连 / 内网穿透 + 传文件 + 流量上限）
├── YuLiangToolkit.Tools.MapDownloader/  # 功能：osu! 下图器（搜谱面集 + 批量下 .osz）
├── YuLiangToolkit.Tools.OsuMania/       # 功能：音游工具（osu! Key 转换器 / LN 转换器 / 等级计算器）
├── YuLiangToolkit.Tools.BoardGames/     # 功能：棋牌（联机五子棋 / 联机象棋 + 房间联机底座）
└── YuLiangToolkit.Tools.Sample/         # 框架示例（新功能的复制模板，可整目录删除）
tests/YuLiangToolkit.FrameworkSelfCheck/ # 轮子自检控制台程序（1600 项行为逐条验证）
```

## 如何添加一个新工具

实现 `ITool`（提供 `Id / Name / Description / EmojiIcon / Category / Order` 与 `CreateView()`）就会被
`ToolDiscovery` 在启动时自动发现；想收进类别页就再加一个 `CategoryPath`（例如 `new[] { "音游工具" }`，
支持任意层级，不写就放在主页根层）。选项继承 `ToolConfig`，ViewModel 继承 `ToolViewModelBase`，
Data / Config 的路径与持久化全由框架接管。也可以把编译好的 dll 放进程序目录的 `plugins\`（外部插件，不用改主程序）。

最省事的做法是复制 `src\YuLiangToolkit.Tools.Sample` 当模板，完整步骤见
[AGENTS.md 第 4 节](AGENTS.md) 与 [框架示例](docs/tools/sample-framework.md)。

## 许可证

本项目采用 **Unlicense**（见 [LICENSE](LICENSE)）：

> **随便用、随便改、随便 fork、商用、二次发布、打包分发都可以，不需要署名，也不用保留版权声明。**
> 唯一请求：别把作者标成别人。

仓库里有少量第三方内容仍遵循它们自己的许可（`OsuParsers` 与 ppy osu!lazer 的数学工具，均为 MIT），
详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

