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

### osu!mania 三件套
- ⚠ OsuParsers 解码器 static 状态、非线程安全：`OsuBeatmapFile` 里 `IoLock` 加锁 + 批量串行，别用 `Parallel.ForEachAsync`。
- ⚠ `Save()` 全量重排：未知段落 / 未知键 / 部分注释会丢，别承诺原样保留。
- 输出写原谱面目录、按元数据命名；别覆盖原文件（同名才覆盖），日志必须显示输出路径。
- 星数别拿 osu!lazer 官方值对答案：Level 公式按 `StarRating.cs` 拟合，差 ~1.2 星。

### 棋牌（五子棋 / 象棋）
- 主机是唯一权威：客户端只从 `state` 重建，任意时刻状态都能整体重建房间与棋局。
- ⚠ `stateFactory` 在网络线程调：只读加锁模型 + `_lastGameSnapshot`，别碰游戏 VM / UI。
- ⚠ 发完 bye 等 80ms 再关连接，否则 TCP RST 可能被主机判成网络断开。

### 图片连续阅读器
- ⚠ 阅读偏移自己记（`_readingOffset`），别每帧反读 `ScrollViewer`：`ScrollToXxx` 排队到布局阶段才生效。
- 横向右→左：图片物理倒序、阅读偏移 = `ScrollableWidth − HorizontalOffset`（与物理偏移互为反数）；别用 `FlowDirection`。
- 缩放后两轴都可能能滚：图片宽高必须都显式给，否则另一轴拖不动。

### 液态玻璃主题
- ⚠ 改 token 三处一起改：两套字典 + `ThemeManager.RequiredTokenKeys`（几何 key 在 `RequiredRadiusKeys`）；少一个静默退回控件默认值。
- 启动在 `new MainWindow()` 之前 `ThemeManager.Apply`；⚠ Storyboard 的 `TargetName` 必须同一命名域（写错启动崩）。

## 别做 · 先问用户

- `scripts/` 调试脚本会弹窗抢焦点、真动鼠标：跑前确认用户没在忙。
- 截图 / 界面验证必须 `Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait`（GUI 程序，直接跑不等它结束）。
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

### 构建与工具链
- 主项目在仓库根，`DefaultItemExcludes` 排除 `src\`、`Data\`、`Config\`、`tests\` 等；新增顶层目录同步加。
- 构建 `.\build.ps1`（`DOTNET_CLI_HOME` → `.dotnet-cli-home`）；发布 `.\publish.ps1` → `dist\YuLiangToolkit\`。
- 目标框架 `net9.0-windows`；库项目也要 `UseWPF=true`（Core 用 `Application.Current.Dispatcher`）。
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
