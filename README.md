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
```

要求：.NET 9 SDK（`global.json` 已锁定 9.0.317）。
首个工具「视频转音频」依赖 FFmpeg：应用会自动检测 PATH 中的 `ffmpeg.exe`（也可在工具内手动选择，或点「下载 FFmpeg」打开官方下载页）。

> 📒 **改代码前先看 [`代码记录.md`](代码记录.md)**：里面是所有“轮子”（工具契约、MVVM 基类、Data/Config 存储与配置框架、
> FFmpeg 封装、UI 样式清单）的用途 + 签名 + 一行用法，以及新增功能的完整步骤和踩坑记录。
> 有它就不需要把 `src/` 通读一遍。

## 项目结构

```
YuLiangTookit.csproj                     # 主程序（WPF 壳：自定义标题栏、主页、侧边栏导航、深色主题）
代码记录.md                               # 轮子速查 / 开发约定（先读这个）
Data/                                    # 功能数据：每个功能一个同名小文件夹（swf、模板、缓存…）
Config/                                  # 功能配置：每个功能一个同名小文件夹（选项即时落盘成 json）
src/
├── YuLiangToolkit.Core/                 # 核心库
│   ├── Tools/ITool.cs                   # 工具契约：实现它即可被自动发现
│   ├── Tools/ToolDiscovery.cs           # 反射式工具发现（主目录 + plugins 目录）
│   ├── Mvvm/                            # MVVM 基类 + OptionItem + ToolViewModelBase（功能 VM 基类）
│   ├── Storage/                         # AppPaths / ToolStorage（Data 门面）/ JsonFileStore
│   ├── Configuration/                   # ToolConfig / ToolConfigStore / DynamicToolConfig（等价于“即时存选项”）
│   └── Services/                        # FFmpeg 定位/执行、全局设置（%AppData%）
├── YuLiangToolkit.Tools.VideoToAudio/   # 功能：视频转音频
└── YuLiangToolkit.Tools.Sample/         # 框架示例（新功能的复制模板，可整目录删除）
tests/YuLiangToolkit.FrameworkSelfCheck/ # 轮子自检控制台程序（23 项行为逐条验证）
```

## 数据与配置（Data / Config）

* `Data/<功能Id>/`：功能用到的资源文件（例如以后做 flash 下载器，就把 swf 丢进 `Data/flash-downloader/`）。
  代码里用 `ToolStorage.For("flash-downloader")` 读写，别自己拼路径；文件夹首次真正写入时自动创建。
* `Config/<功能Id>/config.json`：该功能每个选项（Checkbox / Textbox / 下拉框 / 滑块…）的值。
  在功能 ViewModel 里 `Cfg = LoadConfig<XxxConfig>()`，XAML 直接 `{Binding Cfg.Xxx}`，**改一下就自动存，不需要保存按钮**。
* 开发态（IDE 里按运行）读写的**就是仓库里这两个文件夹**；发布后是 exe 同级的 `Data/`、`Config/`；
  exe 目录不可写时兜底到 `%AppData%\YuLiangToolkit`。详见 `代码记录.md` 第 3 节。

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

## 第二个工具：框架示例（开发用，可删）

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
- 轮子自检（23 项行为，退出码 0 = 全通过）：

  ```powershell
  dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
  ```
