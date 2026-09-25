# YuLiangToolkit（凉雨工具集）

一个可扩展的 WPF 工具集应用：主页提供工具入口卡片，左侧导航自动列出所有已发现的工具。
界面采用深色主题 + 自定义标题栏（最小化/最大化/关闭按钮内置于界面，无系统窗口顶栏）。

## 主页：搜索 · 排序 · 标签

功能多了也不用翻找：

- **搜索**：直接打字即可（进主页自动聚焦搜索框，`Ctrl+F` 聚焦、`Esc` 清空）。
  按**名称、分类、标签、描述、Id** 匹配，大小写不敏感；空格分隔多个关键词时需**全部命中**（例如 `视频 mp3`）。
- **标签**：卡片上那行蓝色小字就是标签（分类 + 工具自带的 `Tags`）。主页有一排「快速筛选」标签，**点一下就筛**；搜不到时也有空状态提示。
- **排序**：默认顺序 / **最近使用**（刚打开过的排最前，卡片上会标“x 分钟前用过”）/ 名称 A→Z / 名称 Z→A / 按分类。
  排序偏好与使用时间记在 `Config\shell\config.json`，重启后保持（搜索结果本身不保留，免得下次以为功能丢了）。

## 快速开始

```powershell
# 一键还原 + 构建（离线可用，使用本机 NuGet 缓存）
.\build.ps1

# 运行（或直接在 Rider / Visual Studio 中打开解决方案按运行）
dotnet run --project YuLiangTookit.csproj

# 发布到 dist\YuLiangToolkit\（加 -SelfContained 可打成不依赖本机 .NET 的版本）
.\publish.ps1
```

要求：.NET 9 SDK（`global.json` 已锁定 9.0.317）。
「视频转音频」依赖 FFmpeg：应用会自动检测 PATH 中的 `ffmpeg.exe`（也可在工具内手动选择，或点「下载 FFmpeg」打开官方下载页）。

> 📒 **改代码前先看 [`代码记录.md`](代码记录.md)**：里面是所有“轮子”（工具契约、MVVM 基类、Data/Config 存储与配置框架、
> FFmpeg 封装、UI 样式清单）的用途 + 签名 + 一行用法，以及新增功能的完整步骤和踩坑记录。
> 有它就不需要把 `src/` 通读一遍。

## 项目结构

```
YuLiangTookit.csproj                     # 主程序（WPF 壳：自定义标题栏、主页、侧边栏导航、深色主题）
build.ps1 / publish.ps1                  # 一键构建 / 一键发布（发布到 dist\YuLiangToolkit\）
代码记录.md                               # 轮子速查 / 开发约定（先读这个）
Data/  Config/                           # 只有说明文件【运行时目录在 exe 旁边，见下】
src/
├── YuLiangToolkit.Core/                 # 核心库
│   ├── Tools/ITool.cs                   # 工具契约：实现它即可被自动发现（Tags 可选）
│   ├── Tools/ToolDiscovery.cs           # 反射式工具发现（主目录 + plugins 目录）
│   ├── Mvvm/                            # MVVM 基类 + OptionItem + ToolViewModelBase（功能 VM 基类）
│   ├── Storage/                         # AppPaths / ToolStorage（Data 门面）/ JsonFileStore / RuntimeReadme
│   ├── Configuration/                   # ToolConfig / ToolConfigStore / DynamicToolConfig（等价于“即时存选项”）
│   └── Services/                        # FFmpeg 定位/执行、全局设置（%AppData%）
├── YuLiangToolkit.Tools.VideoToAudio/   # 功能：视频转音频
├── YuLiangToolkit.Tools.BatchRename/    # 功能：批量重命名（规则清单引擎 + 两阶段改名 + 撤销）
├── YuLiangToolkit.Tools.FlashDownloader/# 功能：Flash 下载器（4399/7k7k/17yy + 本地服务器 + 自动补全资源）
└── YuLiangToolkit.Tools.Sample/         # 框架示例（新功能的复制模板，可整目录删除）
tests/YuLiangToolkit.FrameworkSelfCheck/ # 轮子自检控制台程序（339 项行为逐条验证）
```

## 数据与配置（Data / Config）

**这两个目录就在 exe 旁边**（`bin\Debug\net9.0-windows\`、`bin\Release\…`、`publish.ps1` 出来的 `dist\YuLiangToolkit\`），
首次运行 exe 时自动生成，并各带一份「怎么用这两个文件夹.md」说明：

* `<exe 旁边>/Data/<功能Id>/`：功能用到的资源文件（例如 Flash 下载器下载的游戏在
  `Data/flash-downloader/games/`，播放器与播放页模板在 `Data/flash-downloader/tools/`）。
  代码里用 `ToolStorage.For("flash-downloader")` 读写，别自己拼路径；文件夹首次真正写入时自动创建。
* `<exe 旁边>/Config/<功能Id>/config.json`：该功能每个选项（Checkbox / Textbox / 下拉框 / 滑块…）的值。
  在功能 ViewModel 里 `Cfg = LoadConfig<XxxConfig>()`，XAML 直接 `{Binding Cfg.Xxx}`，**改一下就自动存，不需要保存按钮**。
* 找目录的顺序：`YLT_HOME` 环境变量 → **exe 同级** → `%AppData%\YuLiangToolkit`（只在 exe 目录不可写时兜底）。
  状态栏最右侧一直显示当前实际用的目录。详见 `代码记录.md` 第 3 节。
* 因为数据跟着 exe 走，**整个程序目录拷走即可迁移/发布**；反过来 `dotnet clean`、手删 `bin`/`dist` 会连数据一起删掉，
  重要内容自己备份。
* 仓库根的 `Data/`、`Config/` 现在只剩说明文件（不再是运行时目录）。

## 如何添加一个新工具（可扩展性）

```powershell
# 1) 复制示例模板
Copy-Item -Recurse src\YuLiangToolkit.Tools.Sample src\YuLiangToolkit.Tools.MyTool
# 2) 改 csproj 的 AssemblyName / RootNamespace，再改 4 个文件（Tool / Config / ViewModel / View）
# 3) 接入解决方案
dotnet sln YuLiangToolkit.sln add src\YuLiangToolkit.Tools.MyTool\YuLiangToolkit.Tools.MyTool.csproj
#    并在 YuLiangTookit.csproj 里加一条 ProjectReference
.\build.ps1
```

要点：实现 `ITool`（提供 `Id/Name/Description/EmojiIcon/Category/Order` 和 `CreateView()`）即可被
`ToolDiscovery` 在启动时自动发现；选项继承 `ToolConfig`，ViewModel 继承 `ToolViewModelBase`，
Data/Config 的路径与持久化全部由框架接管。也可以把编译好的 dll 放进程序目录的 `plugins/` 文件夹
（外部插件，无需改主程序）。完整步骤见 `代码记录.md` 第 4 节。

## 第一个工具：视频转音频

- 选择源文件夹，支持子文件夹递归扫描；默认只扫 `.mp4`，可扩展为常见视频格式；
- 三种转换模式（后端 FFmpeg）：
  - **有损 MP3**：`libmp3lame`，可选码率 128/192/256/320 kbps；
  - **无损 FLAC**：`-c:a flac`（compression_level 8）；
  - **无损提取原始音轨**：`-c:a copy` 不重新编码，用 `ffprobe` 探测音轨编码后自动选择容器（m4a/opus/ogg/flac/wav 等）；
- 批量转换带进度条、逐文件状态、实时日志、取消（杀掉 ffmpeg 进程树）；
- 输出重名自动加序号 `(1)` `(2)`；
- 选项（模式/码率/并行数/文件夹/是否递归…）保存在 `Config\video-to-audio\config.json`，改一下就记录；
  从老版本升级时会自动把 `%AppData%\YuLiangToolkit\settings.json` 里的旧选项迁移过来（一次性）。

## 第二个工具：批量重命名

给一批文件套一组改名规则，**改之前先看预览，改完还能一键撤销**。

- **怎么把文件加进来**：`添加文件…`（多选）/ `扫描文件夹…`（可递归子文件夹、按扩展名过滤：`jpg; png; .bmp`），
  或者直接把文件 / 文件夹**拖进窗口**。列表里每行前面的勾选框用来临时排除某几个文件。
- **命名规则是一张清单，不是写死的顺序**：
  - 上面选一个类型 → 点「**+ 添加规则**」→ 规则出现在清单里，点标题行展开填参数；
  - **同一种规则可以加多条**（例如两条「查找替换」接力、先「编号」再加「前缀」）；
  - **顺序自己定**：按住每行左边的 **☰** 上下拖动就能挪位置（也可以点 ↑ ↓），
    **清单顺序 = 执行顺序**，换顺序预览立刻跟着变；
  - 不想要的规则去掉勾选就临时停用，点 **✕** 删除，「清空规则」一键清空。

  | 规则类型 | 能做什么 |
  | --- | --- |
  | 查找替换 | 普通文本（可不区分大小写、可只替换第一处）或**正则**（替换里能用 `$1` 引用捕获组） |
  | 清理 | 去首尾空格 / 连续空格合并成一个 / 删掉指定字符（如 `_-`） |
  | 截取 / 删除 | 从头删 N 个字符 / 从尾删 N 个字符 / 删掉两个标记之间的内容（可只删标记、保留内容） |
  | 大小写 | 全小写 / 全大写 / 每个单词首字母大写 / 仅首字母大写 |
  | 加前缀 / 后缀 | 加前缀、加后缀 |
  | 自动编号 | 起始 / 步长（可为负）/ 位数补零 / 放在前、后或直接当整个名字，**按列表顺序编号** |
  | 扩展名 | 保持 / 转小写 / 转大写 / 统一换成指定扩展名 / 去掉 |

  每条规则还能选**作用于**哪一部分：`主名`（默认，不动扩展名）/ `扩展名` / `整个文件名`。
- **列表可排序、可上下移动**（序号跟着列表走）；排序方式与上次用的文件夹会记住。
- **冲突会拦下来**：两个文件算成同一个名字、或目标位置已有别的文件 → 标红为「冲突」并跳过，
  不会悄悄覆盖你的东西；想让它自动加 ` (1)`、` (2)` 也有开关。
- **执行是两阶段的**：先把所有要改的文件改成同目录临时名，再改成最终名字。所以
  `A→B`、`B→A` 互换、只改大小写（`photo.jpg` → `Photo.jpg`）、`A→B→C` 链式改名都不会打架；
  中途失败的那一条会被改回原名，不会留下半个临时文件。
- **撤销**：每次改名都写一份流水账到 `Data\batch-rename\rename-history.json`（自动只保留最近 20 批），
  所以**关掉程序再打开照样能撤销**；列表里列出每批改名，可以挑其中一批单独撤销。
- 整套规则存在 `Config\batch-rename\config.json` 的 `rules` 数组里，**可以直接用记事本改**（改完重开功能即生效）。

## 第三个工具：Flash 下载器

把 4399 / 7k7k / 17yy 上的小游戏下载到本地，离线也能玩。用的是「Flash 独立播放器」或「本地网页服务器」两条路。

- **下载**：把游戏页网址（如 `https://www.4399.com/flash/6853.htm`）粘进「游戏 URL」→ 点「下载」或「下载并游玩」。
  下载完成后，单个 swf 存成 `游戏名_a6853.swf`；网页游戏（h5 / Unity）存成 `游戏名_a12346h/` 文件夹。
- **找游戏**：列表上方有**搜索框**（按名字 / 站点后缀 / `4399`、`网页` 这类词搜，空格分隔多个关键词需同时命中，
  `Ctrl+F` 聚焦、`Esc` 清空）和**排序**（按上次游玩 / 名字 / 大小 / 站点，可切正序逆序；
  默认「按上次游玩」——刚玩过的排最上面）。游戏多了也不用来回翻。
- **游玩**：
  - swf → 用 `FlashPlayer.exe`（Ruffle 的 `ruffle.exe` 也行）直接打开；工具会自动找到它并拷进 `Data\flash-downloader\tools\`。
  - 网页游戏 → 启动内置的本地 HTML 服务器，用浏览器打开播放页（有背景、重玩 / 放大 / 缩小 / 全屏、四套配色）。
    **注意：玩网页游戏时不能关掉本工具**，否则 `http://localhost:1055/` 就断了。
- **多文件游戏**（只下主 swf 玩不了的那种）：
  1. 选中游戏 → 「**装入文件夹**」（把散放的 swf 收进 `游戏名_?123s/`，并写好 `SWFNetPath.ini`）；
  2. 「**关联文件下载器**」里一行一个相对路径（如 `assets/level1.swf`）→ 「批量下载」。
     路径对不对可以拿 [ProcessMonitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon) 看
     Flash 播放器加载失败的那些文件，或者直接用下面的第 3 步。
  3. 更省事的办法：点「**自动下载所需文件**」，然后在游戏里点「重玩」/ 刷新网页 ——
     游戏向本地服务器要文件，服务器发现本地没有就**先去网上下下来再答复**，缺什么补什么。
- **实在打不开本地 swf** 时，可以「**改为 htm 打开**」：文件夹改名成 `h`、换成网页加载页 + 本地服务器打开
  （要浏览器支持 Flash，只是断网也能玩而已，一般情况下不需要）。
- **自由下载工具**：给网址 + 上一级网址（防盗链用的 Referer）+ 文件名，直接存进游戏目录 ——
  不属于那三个站、或者自动解析不到的地址可以用它，按说明手工建好文件夹与 `SWFNetPath.ini` 就能用批量下载。
- **4399 h5 游戏的广告**：下载时会把页面里的官方 h5api 换成内置的替代脚本（广告变 0 秒）；
  需要登录 / 联机排行的大游戏本地玩不了，这是设计如此。
- 选项在 `Config\flash-downloader\config.json`（游戏目录 / 并发 / 超时 / 端口 / 播放器路径…），
  **游戏目录可以在界面上改成任意位置**（默认 `Data\flash-downloader\games\`）。

> Flash 早就停止维护了，本工具走的是「下载游戏本体 + 本地播放器/本地服务器」的路子；
> 遇到提示“安全沙箱错误”时，需要在 Flash 播放器里把自己的游戏文件夹加进「受信任位置」。

## 第四个工具：框架示例（开发用，可删）

`src\YuLiangToolkit.Tools.Sample` 把 Data / Config 两个轮子跑给你看：

- 左侧选项（TextBox / CheckBox / ComboBox / Slider）改一下，右侧实时显示 `config.json`、`options.json` 的变化；
- 下方演示 `Data\sample-framework\` 的列文件、导入外部文件、写入、删除、在资源管理器中显示；
- 还有一份通用交互演示（忙碌状态 / 进度 / 日志 / 取消）——新功能直接复制这个项目即可。

## 自定义标题栏说明

- `MainWindow.xaml` 使用 `WindowStyle=None` + `WindowChrome`（CaptionHeight=0，整窗为客户区），边缘缩放由 `ResizeBorderThickness` 提供；
- 窗口控制按钮（— □ ×）在界面内右上角，关闭按钮悬停变红；
  - 注意：CaptionHeight 不能设 >0 覆盖按钮区域，否则点击会被命中测试成标题栏拖拽而失效（HTCAPTION）；
- 标题栏拖拽移动与双击最大化由 `MainWindow.xaml.cs` 中的 `OnTitleBarMouseLeftDown` 手动实现（`DragMove` + 双击切换），点击按钮区域不会误触发拖拽；
- 最大化时通过 `WM_GETMINMAXINFO` 将窗口钳制到工作区，不会盖住任务栏；
- 「返回主页」位于顶栏下方的工具条，右侧显示当前工具名。

## 开发调试

- `YLT_NAV_TOOL=<工具Id>`：启动后直接打开指定工具（如 `video-to-audio`）；
- `YLT_SHOT=<png路径>`（可选 `YLT_SHOT_TOOL`）：渲染当前界面为 PNG 后退出，用于无头验证 UI
  （注意：截图里内容区顶部会有淡淡的重复标题残影，是 `RenderTargetBitmap` 截图时机的老毛病，不是界面 bug）；
- `YLT_HOME=<目录>`：覆盖数据根目录（Data/Config 的父目录），测试时用来隔离；
- 轮子自检（339 项行为，退出码 0 = 全通过；其中 Flash 下载器那部分会自己起一个本地假游戏站跑真下载，不访问外网）：

  ```powershell
  dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
  ```
