# 踩坑清单（按需读）

> 入口是根 [AGENTS.md](../../AGENTS.md)：硬性规则与开工 / 收尾流程都在那里，本页只放读代码不容易看出的坑。
> 这里只记「读代码不容易看出来、又必须遵守」的坑：排错 / 改危险代码时按需查看，别整篇塞进上下文。

## 各功能专属坑

### 批量重命名
- 两阶段改名（先 `.ylt-renaming-<guid>.tmp`）：A↔B、只改大小写、链式改名都靠它；失败 / 取消全部改回。
- 冲突检测迭代到稳定（`MarkConflicts`）：只有状态 `Ok` 的会让位，`Unchanged` 不算。
- ⚠ 不要在 `CollectionChanged` 里 `ScrollIntoView`（OLE 回调抛异常 = 闪退）；规则行根 `StackPanel` 要 `Background="Transparent"`。

### Flash 下载器
- ⚠ `HttpListener` 的 `AbsolutePath` 是百分号编码（中文目录）：`FlashWebServer.Resolve` 先 `UrlDecode`；自己拼 URL 别重复转义。
- 缺文件时同步带 Referer 下载、404 删本地占位；同文件并发只下一次（`_inFlight`）。

### 联机聊天室
- ⚠ 绑 `::` 必须显式 `DualMode`（`TcpListener` 不会替你开），否则 IPv4 全连不进。
- 拒收超限文件前必须把对方灌来的字节读干净，否则后面的包全乱。

### osu! 下图器
- 先下 `.osz.part` 再改名：断线留半个文件会被「已存在」永久跳过。
- 日期与 Key 数走本地筛选（API 不支持）；结果表头与数据行列定义必须一字不差。

### 视频 / 音乐播放器
- ⚠ `MediaElement` 的 `LoadedBehavior` / `UnloadedBehavior` 都要设 `Manual`，否则自播自闭、跟 VM 命令打架。
- ⚠ 整窗全屏临时改 `WM_GETMINMAXINFO` 再最大化，并 `SetWindowRgn(hwnd, IntPtr.Zero)` 清工作区 Region，否则底部黑带。
- 藏任务栏用 `TaskbarHider`：400ms 看门狗、`Hide()` 幂等、不能 `Thread.Sleep`；`YLT_TASKBAR_TRACE` 错误码 5 = 完整性级别低。
- 切走还要干活：`IBackgroundTool.KeepAliveOnLeave` + 外壳存视图实例；`Unloaded` 时不能 `Close()` 播放器。

### 外壳：分类 · 粒子 · 主页设置
- 老工具 `Category` 别自动当分类目录：树只认 `CategoryPath`（默认空）。
- 类别页只平铺「直接工具 + 子类别卡片」，别递归平铺；主页搜索对全部工具扁平过滤。
- ⚠ `ParticleOverlay` 订阅 `CompositionTarget.Rendering`（静态）+ Window 鼠标事件，必须成对挂摘，否则泄漏 + 关窗后还在跑。
- 粒子发射点用 `GetCursorPos` + `PointFromScreen`：`DragMove` 期间 `GetPosition` 返回 (0,0)，粒子会从左上角冒。
- 粒子别每帧 new 控件 / 画刷：`DrawingContext.DrawEllipse` + 冻结画刷，否则 UI 线程卡。
- 光标特效分「自动效果」（`autoEffects`）/「点击效果」（`clickEffects`）两套 **独立多选** 的 Id 数组；
  特效 Id 就是配置格式（清单在 `Core/Effects/CursorEffects.cs`），加特效 = 清单加一条 + `ParticleOverlay` 加渲染分支，
  设置页按清单自动渲染、不用改 XAML。
- `ParticleOverlay` 只在「自动拖尾在生成」或「有一次性特效在播」时订阅 `CompositionTarget.Rendering`，
  播完立刻退订；别改成常驻 60fps（鼠标散光只在鼠标移动时重绘一次）。

### osu!mania 三件套
- ⚠ OsuParsers 解码器 static 状态、非线程安全：`OsuBeatmapFile` 里 `IoLock` 加锁 + 批量串行，别用 `Parallel.ForEachAsync`。
- ⚠ `Save()` 全量重排：未知段落 / 未知键 / 部分注释会丢，别承诺原样保留。
- 输出写原谱面目录、按元数据命名；别覆盖原文件（同名才覆盖），日志必须显示输出路径。
- 星数别拿 osu!lazer 官方值对答案：Level 公式按 `StarRating.cs` 拟合，差 ~1.2 星；等级系数只按凉雨 8K 谱面拟合，别把 Level 外推到 4K / 7K。

### 棋牌（五子棋 / 象棋）
- 主机是唯一权威：客户端只从 `state` 重建，任意时刻状态都能整体重建房间与棋局。
- ⚠ `stateFactory` 在网络线程调：只读加锁模型 + `_lastGameSnapshot`，别碰游戏 VM / UI。
- ⚠ 发完 bye 等 80ms 再关连接，否则 TCP RST 可能被主机判成网络断开。

### 图片连续阅读器
- ⚠ 阅读偏移自己记（`_readingOffset`），别每帧反读 `ScrollViewer`：`ScrollToXxx` 排队到布局阶段才生效。
- 横向右→左：图片物理倒序、阅读偏移 = `ScrollableWidth − HorizontalOffset`（与物理偏移互为反数）；别用 `FlowDirection`。
- 缩放后两轴都可能能滚：图片宽高必须都显式给，否则另一轴拖不动。
- 全屏（F11）不自己操作窗口：`Window.GetWindow(this) as IFullscreenHost` 交给外壳藏壳 / 铺满屏幕（任务栏由外壳藏）。
  视图 `Unloaded` 顺序必须是「先 `vm.ExitFullscreen()`（事件还订阅着，外壳能还原）→ 再摘事件」，
  反过来会留下一个“藏了壳”的窗口（播放器踩过的坑，两个阅读器复制同一套）。
- 全屏工具条是浮层：行高绑 VM 的 `ToolbarRowHeight`（全屏 = 0），Border 用 DataTrigger 换到 Row1 + `Panel.ZIndex` 置顶；
  ⚠ `ControlsVisible` 改变时必须同时通知 `IsToolbarVisible`，否则「鼠标停 2.5 秒自动隐藏」不生效（工具条显隐绑的是后者）。

### PDF 阅读器
- ⚠ PDF 渲染用系统自带的 `Windows.Data.Pdf`（WinRT，不引第三方包）：**主程序 / 自检目标框架因此升到
  `net9.0-windows10.0.19041.0`**（工具库仍是 `net9.0-windows`），构建输出会多 `Microsoft.Windows.SDK.NET.dll` /
  `WinRT.Runtime.dll`。改 TFM 时主程序、自检、PdfReader 三处要一起改，否则 ProjectReference 不兼容。
- ⚠ `Windows.Data.Pdf.PdfDocument` **没有 Close/Dispose**（WinRT 对象靠 GC），只有 `PdfPage` 是 `IDisposable`；
  别写 `using var doc = ...`（编不过）。
- ⚠ `IAsyncAction` / `DataReaderLoadOperation` 不是 `Task`，**不能 `.ConfigureAwait(false)`**；直接 `await`。
- ⚠ 双页 `SpreadHost` 刚从 Collapsed 变 Visible 时 `ActualWidth` 还是 0：必须等 `SpreadViewport.SizeChanged`
  补一次排版，否则双页模式只有页码没有页（踩过）。连续滚动那套的 ScrollViewer 一开始就可见，没这个问题。
- 只渲染可见页（前后各一屏缓冲、串行渲染、滚出范围把 `Image.Source` 置空）：几百上千页的 PDF 也不会把内存吃满。
- 换窗口大小 / 换阅读方式时用「**阅读偏移最前面那一页**」定位，别用视口中心页 —— 横向连续一屏能并排两三页，
  用中心页会在重排时把整条长带往后跳一页（第一页跑到左边看不见）。
- 调试开关：`YLT_PDF_DEMO=1` 生成演示 PDF（`Data\pdf-reader\demo.pdf`）、`YLT_PDF_OPEN=<路径>` 直接打开、
  `YLT_PDF_MODE=vertical|horizontal|spread` 临时切方式（**不写配置**）、`YLT_PDF_PAGE=n` 定位到第 n 页、
  `YLT_PDF_SCROLL=0~1` 加载后滚到指定比例（截图核对版式用）。
- ⚠ 文字搜索 / 书签目录没有系统 API：`Text/Extraction/` 是**手写的 PDF 解析器**（传统 xref 表 / xref 流 / 对象流 /
  Flate / ToUnicode / 页树 / 大纲），`Windows.Data.Pdf` 只负责渲染。解析器**永不向外抛异常**（失败 → `PdfTextLayer.Empty(中文原因)`），
  搜索 / 目录只是增强功能，提取失败绝不能影响翻页；改解析器先把 `PdfTextChecks` 跑绿（里面自带手写 PDF 生成器）。
- ⚠ 双页排版不是「左右各半个固定槽位」：两页先按可用高度等比、再整体适配「两页 + 间距」的宽度并居中 ——
  改回固定槽位的话，高度受限时页在槽里居中会留一大片空白（用户报过「两页隔太远」）。间距 0 是合法值，
  别用 `HasUsableLength(0) == false` 把它当「没给」退回默认。
- ⚠ 双页放大后拖动是**平移**（`_spreadPanX/Y` + `PdfSpreadLayout.ClampPan`），连续模式缩放后多出来的是**跨轴偏移**；
  两套偏移都自己记，惯性用 `PdfInertia` 的双轴接口，别从 ScrollViewer / RenderTransform 反读。
- ⚠ 文字层在后台 `Task.Run` 提取：切 PDF / 关页面要 `Cancel` 旧的 `CancellationTokenSource` 并比对 `_textLayerCts`，
  否则旧文档的结果可能覆盖新文档；完成回 UI 一律走 `Ui(...)`。
- 文件关联接线：`App.xaml.cs` 启动参数 + 第二实例 `HandleSecondInstance` 两处都要认 `.pdf`，
  `NormalizeLaunchArguments` 也要把 PDF 路径转成绝对路径；单实例开关是程序级的（读图片阅读器配置），PDF 没有单独开关。
- 调试开关（截图用）：`YLT_PDF_ZOOM=200`（**会写配置**，截图前删 config）、`YLT_PDF_SEARCH=关键词`、
  `YLT_PDF_OUTLINE=1`、`YLT_PDF_SETTINGS=1`。
- 渲染宽度上限 2000 px / 单页像素 800 万（缩放清晰度与内存的折中）；`PdfReaderChecks` 的「渲染宽度夹取」断言跟着这两个常量走。
- 全屏沉浸阅读（F11）和图片阅读器同一套：`IFullscreenHost` + 工具条浮层 + 鼠标 2.5 秒自动隐藏 + 指针隐藏；
  ⚠ 视图 `Unloaded` 同样是「先 `ExitFullscreen()`（事件还订阅着）→ 再摘事件」。PDF 工具条是两行，全屏时仍浮在顶部；
  全屏时底部操作提示条收起、页码 / 进度条保留。
- 调试 / 截图：`YLT_PICTURE_FULLSCREEN=1` / `YLT_PDF_FULLSCREEN=1` 全屏启动；`YLT_PICTURE_CONTROLS=0` /
  `YLT_PDF_CONTROLS=0` 强制收起工具条拍“沉浸”帧 —— 截图时鼠标可能正好在窗口里触发 `PreviewMouseMove` 又把工具条叫出来，
  所以视图里有个 `_forceHideControlsForScreenshot` 标志，置位后忽略鼠标活动。
- 记住上次阅读位置（`config.json` 的 `readingPositions`）：界面每帧都可调 `NoteReadingPosition`，VM 里 1.2s `DispatcherTimer`
  + 2 秒节流落盘；但**退出程序时 `View.Unloaded` 不一定触发**（实测截图 / 关窗路径没跑到），所以另外在 `Window.Closing`
  里 `FlushReadingPosition(saveNow: true)` 兜底 —— 只靠配置自动落盘会丢最后一秒的位置。恢复优先级：`YLT_PDF_PAGE` > 上次位置 > 第一页；
  **模式没变才按 `ReadingRatio` 恢复**，换了阅读方式按页号恢复。
- 调试 / 截图新增：`YLT_PDF_JUMP=4` 打开后直接走一次「跳页输入框」跳到第 4 页；`YLT_PDF_PAGE=4` 会覆盖恢复位置。

### KPS 按键统计
- 监听 / 统计 / Overlay 在 `KpsSession`（进程单例）里，工具页只是设置面板：切走页面时 Overlay 还开着就继续跑；
  页面关 + Overlay 关才 `SuspendAll`。ViewModel 由会话持有并在重开时复用（`Activate` 里补 `NotifyOpened`），
  别在 `OnClosed` 里把会话/统计 Dispose 掉。
- ⚠ 同进程注册 Raw Input **不要加 `RIDEV_NOLEGACY`**：那会吞掉 WPF 自己的传统键盘消息（文本框打不了字）。
  DmNote 能加是因为键盘监听跑在独立 daemon 进程里。
- ⚠ Overlay 是独立置顶窗口：主窗口关闭时必须由会话的 `Shutdown` 关掉它，否则「最后一个窗口关闭」永远不成立、
  进程退不掉（已在 `MainWindow.Closed` / `Application.Exit` 兜底）。改窗口生命周期先想这条。
- 统计口径：峰值只在 50ms `Tick()` 采样更新、平均只算 `kps>0` 的采样；UI 定时器必须 **50ms Tick + 10Hz 刷新**，
  别随帧率调 `Tick()`（口径会变），也别每按键 LINQ 全量扫。
- 计数器持久化：会话计数在 `SuspendAll` / 退出时并入 `Config.SavedKeyCounters` 再 `Reset` 引擎；不要每按一键写配置。
- 自检的 Raw Input 断言要**过滤目标 VK**（`VK_F24`）：自检跑的时候用户/别的程序可能按别的键，只断言
  「收到 F24 / F24 只算一次」，否则会偶发失败（已踩过）。
- 调试开关：`YLT_KPS_DEMO=1` 造假统计（截图冒烟）、`YLT_KPS_SHOT_OVERLAY=<png>` 把 Overlay 渲染成 PNG 后退出，
  用法同 `YLT_SHOT`（见 AGENTS.md）。

### 液态玻璃主题
- ⚠ 改 token 三处一起改：两套字典 + `ThemeManager.RequiredTokenKeys`（几何 key 在 `RequiredRadiusKeys`）；少一个静默退回控件默认值。
- 启动在 `new MainWindow()` 之前 `ThemeManager.Apply`；⚠ Storyboard 的 `TargetName` 必须同一命名域（写错启动崩）。

## 别做 · 先问用户

- `scripts/` 调试脚本会弹窗抢焦点、真动鼠标：跑前确认用户没在忙。
- 截图 / 界面验证必须 `Start-Process .\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe -Wait`（GUI 程序，直接跑不等它结束）。
- 自检别调 `PlayEntry()`（会真启动 FlashPlayer，无头自检卡死）；只调 `MarkPlayed()`。
- 别从 URL 反推本地路径（曾把文件写到游戏目录外）。
- 别引第三方库：新轮子手写进 `Core/`（参考 `ScrollChaining`、`Crc32`、`ParticleEngine`）。

## 跨领域的坑

### 存储与配置
- 配置用自动属性、后台内容比对；要联动刷新才用 `Set(ref …)`。
- `LoadConfig<T>()` 同类型只返回一个实例；工具关闭再打开框架自动恢复后台保存。
- `Dyn[键]` 键不存在返回 null：绑 `IsChecked` 得 null、点一下变 false；用 `LoadDynamicConfigWithDefaults` 预置或 `TargetNullValue=False`。

### WPF 与界面
- ComboBox 绑 `OptionItem<T>` 必须 `DisplayMemberPath="Label"`，否则显示类型名。
- 索引器绑定 `{Binding Dyn[键]}` 依赖 `OnPropertyChanged("Item[]")`；占位提示压内容用 `EmptyToVis`。
- 标题栏 `CaptionHeight` 必须 0，否则按钮被命中成拖拽（HTCAPTION）；无边框最大化靠 `WM_GETMINMAXINFO` 钳制任务栏。
- 枚举避开 `SequencePosition`（撞系统同名类型，已改 `SequencePlacement`）与 `Path`、`Timer`、`Index`。
- `ListBox.ScrollIntoView` 会顺 `BringIntoView` 顶掉外层页面；滚内层找内部 `ScrollViewer`。
- 图标别用 `⏪ ⏩ ⏹ 🔁 🔂 🔀`（无码位 → 豆腐块）：用矢量 `Path`、`Segoe MDL2 Assets` 码位或写字。
- 全局 `ScrollBar` 样式（`App.xaml` 里无 `x:Key` 的那个）要同时管横竖：`Track` 的 `Orientation` / `Value` / `ViewportSize`
  会自己从模板父级继承（`Track.OnPreApplyTemplate`），但**横向**要多掰两处 —— Track 的 `IsDirectionReversed` 设 `False`
  （否则一进来滑块在最右边、往右拖反而往回滚），轨道翻页按钮换成 `PageLeft` / `PageRight`
  （`PageUp` / `PageDown` 会被外层 ScrollViewer 当竖向翻页，点横条没反应）。横向滑块还要把竖条用的
  `MinHeight=24` 放开。v0.33.0 主页标签墙的横向滚动条踩过，别删 Orientation 触发器里那几行。
- 横向 `ScrollBar` 的**粗细别写死** `Height=10`：Aero2 主题样式会给横向条设
  `MinHeight = SystemParameters.HorizontalScrollBarHeight`（默认 17 DIP），`Height=10` 会被它顶回 17，
  看起来像"设了没生效"。要跟系统一致就显式写 `Height` / `MinHeight = {x:Static SystemParameters.HorizontalScrollBarHeight}`
  （v0.33.0 已按这个写）。
- 横向滚动容器别让内层 `ScrollViewer` 吃掉滚轮：它 `OnMouseWheel` 是无条件 `e.Handled = true` 的
  （见 `ScrollChaining`），普通滚轮要在 `PreviewMouseWheel` 里转给外层页面；`Shift + 滚轮` 想横滚也得自己算
  （WPF 原生 `ScrollViewer` 不支持 Shift + 滚轮，跟很多应用的习惯不一样）。

### 构建与工具链
- 主项目在仓库根，`DefaultItemExcludes` 排除 `src\`、`Data\`、`Config\`、`tests\` 等；新增顶层目录同步加。
- 构建 `.\build.ps1`（`DOTNET_CLI_HOME` → `.dotnet-cli-home`）；发布 `.\publish.ps1` → `dist\YuLiangToolkit\`。
- 目标框架：主程序 / 自检 `net9.0-windows10.0.19041.0`（PDF 阅读器要 Windows.Data.Pdf），工具库 `net9.0-windows`；库项目也要 `UseWPF=true`（Core 用 `Application.Current.Dispatcher`）。
- `Directory.Build.targets` 补 `System.IO` / `System.Text` 隐式 using；往数据目录放文件请程序首次运行自己写（`RuntimeReadme`）或发布脚本 `Copy-Item`。

### 截图与脚本
- `YLT_SHOT` 出的 PNG 顶部有重复标题残影：`RenderTargetBitmap` 时机老毛病，不是界面 bug。
- 截图放 `shots/`（png / txt 不入库）；开窗口的调试脚本放 `scripts/`。
- 含中文 `.ps1` 必须 UTF-8 with BOM，否则 5.1 按 ANSI 解析报语法错误。

### 网络与 Flash 站点解析
- 判越界别用 `Contains("..")`：先 `Path.GetFullPath` 再比前缀（`SafeCombine`）。
- .NET Core 默认无 GBK：静态构造注册 `CodePagesEncodingProvider.Instance`，否则 `GetEncoding("gbk")` 抛异常。
- `UrlJoin` 先判 `//` 再判绝对网址（`Uri` 会把它当 `file://`）。
- 4399 的 `_strGamePath` 可能相对或完整；7k7k 的 `gamePath` 有时只是外壳（17 KB），主 swf 在旁 → 走「装入文件夹」+「关联文件下载器」。
- ArmorGames / Kongregate 的 swf 不能按文件名规律拼：从 `<param name="movie">` / `<embed src>` / `iframeConfig` / `swf_location` 里读；Kongregate 跳三跳、要排掉 API 库 swf（`API_AS3_*.swf`）、`iframeConfig` 不是合法 JSON。
- 散放 swf 本体就是自己：`GameFullPath` 别拼一层；`GamesLibrary` / `Library` 别写字段初始化器（构造前空字符串 → 游戏目录空、说明文件写到仓库根）。
- 播放器无稳定发现源：路径随时失效；查找器要覆盖用户自整理目录（`桌面\Flash Games\tool\`），找不到提示指定路径。
