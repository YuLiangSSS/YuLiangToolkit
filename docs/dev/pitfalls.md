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
- 主区域 / 日志这类「上下两块」是可拖分隔条（见 [layout.md](layout.md)）：整页还在 `ScrollViewer` 里，截图一屏看不全 →
  `YLT_FLASH_SCROLL=bottom`（或像素值）可滚到指定位置再截图，核对区块底边对齐 / 比例。
- 中间「关联文件下载器」卡片要铺满整列（底边和左右两块对齐）：卡片 `MinHeight` 绑外层 ScrollViewer 的
  `ViewportHeight`，卡片内最后一行的自动下载日志放 `*` 行吃掉多余高度；内容比视口高时 MinHeight 不生效、照常内部滚动。

### 联机聊天室
- ⚠ 绑 `::` 必须显式 `DualMode`（`TcpListener` 不会替你开），否则 IPv4 全连不进。
- 拒收超限文件前必须把对方灌来的字节读干净，否则后面的包全乱。
- ⚠ **群通话媒体必须单独一条 TCP 连接**：文件传输会长时间占住写锁，混在聊天连接上声音会卡成 PPT；
  而且必须复用主机**同一个监听端口**（内网穿透通常只映射一个端口，另开端口在穿透环境连不上）。
- ⚠ 媒体连接用 `callHello`（`isMedia=true` + `welcome` 里的 `callToken`）校验身份，**不要新增协议版本号**：
  老版本会把未知包跳过、握手超时，于是不会把媒体连接误登记成聊天成员；令牌对不上一律拒连。
- ⚠ 发送必须走**队列 + 丢旧帧**（`CallSendQueue` 非阻塞入队、满了丢最旧、音频优先保），
  绝不能阻塞采集 / 转发线程，否则一个人网络慢就拖死所有人。
- 音频固定 **16 kHz 单声道 μ-law 40 ms 一包（640 B）**，视频 **MJPEG**，全部手写编解码，不引第三方库。
- **没有回声消除（AEC）**：文档必须提醒戴耳机，否则外放会啸叫 / 回音；界面不要假装能消回音。
- ⚠ WinRT 摄像头**不要**自己 `SetFormatAsync`（某些设备会出绿屏 / 错色），按源默认格式采集、
  由引擎自己降到目标尺寸（坑同 Live2D 的面部动捕）。
- WASAPI 共享模式要按**设备混音格式**处理（float32 / PCM16 / PCM24 / PCM32、可能多通道取前两路），
  别假设固定是 16 位立体声。
- ⚠ **收到的媒体包必须接到引擎**：`ICallTransport.PacketReceived += engine.HandlePacket`（在
  `ChatRoomSession.JoinCallAsync` 里接、`LeaveCallAsync` 里摘）。漏了就是「能发不能收」——
  两端统计 Received* 恒 0、听不到也看不到别人，自检里踩过。
- 截图 / 排错：`YLT_CHAT_CALL_DEMO=1` 塞 4 个假磁贴并自动把通话浮层滚进视野
  （配 `YLT_SHOT` + `YLT_SHOT_TOOL=chat-room`）；正常启动不设这个变量就完全看不到通话浮层。
- ⚠ **通话中切到别的工具页不会挂断**：外壳把页面塞进缓存，不会调 `OnClosed`，
  麦克风 / 摄像头会继续工作；只有缓存淘汰（切过很多工具）/ 退出程序才挂断。
  文档与通话面板提示里都要写清楚，别让用户以为切走就停了采集。
- **语音消息的 MP3 不引第三方**：用 WinRT `MediaTranscoder` + `MediaEncodingProfile.CreateMp3` 转码；
  零 NuGet、不调 FFmpeg。Windows **N 版**默认没装媒体功能包 → 系统里没有 MP3 编码器，
  必须返回中文原因并提示安装「媒体功能包」，别静默失败（自检对这条 fail-open 跳过）。
- **先录 WAV 再转 MP3**：WAV 头在 `WavWriter.Finish()` 里回填 RIFF / data 长度（**0 样本也必须合法**，
  44 字节头 + data 长 0），没 `Finish` 就 Dispose 也要尽量回填。临时文件放 `%TEMP%\ylt-chat-voice\`，
  发送完删掉待发 MP3；转码失败 / 取消时 WAV 和 MP3 两边都要清，别在用户临时目录里攒垃圾。
- **语音消息是普通聊天连接上的 `voice` 包，不是通话媒体连接**：载荷是**整段 MP3**（受 4 MB 单包上限约束；
  录音上限 120 秒保证不超）。它和 `callAudio`（媒体连接、40 ms 一包、μ-law 实时流）是两套东西，
  文档里必须写清楚，别让用户以为语音消息走通话带宽。
- 协议必须带 `durationSeconds`：气泡秒数、`chat-log.jsonl`（`Kind="voice"`）与历史回看还原都靠它；
  老版本收到不认识的 `voice` 包会跳过载荷、不会错位。
- ⚠ **按住说话要正确处理鼠标捕获**：`MouseDown` 里 `CaptureMouse()`、松开时 `ReleaseMouseCapture()`；
  **`LostMouseCapture` 在正常松开时也会触发**，必须用「正在松开」标志防竞态 —— 别让松开先被当成取消，
  否则表现为「每次松开都取消、一条语音也发不出去」。语音气泡 `HasPlainAudio` 要跟着
  `IsVoiceMessage` 关掉普通音频卡片，否则界面上同一段语音会显示两份。
- 真机自检 / 截图：`YLT_CHAT_VOICE_AUTOTEST=1`（配合自动开局）会在连上房间后自动录 1.5 秒并发一条语音，
  验证「录音 → MP3 → 发送 → 气泡」整条链路，不需要真用鼠标按住按钮；正常启动不设就不跑。
  本次真机实测：录 1.5 秒 → 会话 `audio\语音消息-*.mp3` 8.8 KB、`ffprobe` 显示 mp3 / 16 kHz / 单声道 / 48 kbps。
- ⚠ **语音进度条（可拖 / 反复听）**：`MediaElement` 是 `LoadedBehavior=Manual`，进度用 200 ms `DispatcherTimer`
  读 `Position` 刷 `VoicePositionSeconds`；用户点 / 拖 `Slider` 时把 `MediaElement.Position` 设过去。
  ⚠ Slider 是 `Value` 单向绑定，定时器回写也会触发 `ValueChanged` —— 必须用「位置差 < 180 ms 就跳过」
  防抖，否则每 200 ms 会把播放位置重新拽一次（听感是卡顿 / 跳回）。`MediaEnded` 要把 `Position` 归零并复位
  按钮（否则再点播放只响一声就结束）；同一时刻只播一条语音（换条听先 Stop 其它）。
  ⚠ 视图 `Unloaded` 时要暂停并复位（外壳缓存视图，别留一个看不见还在播、进度却没人更新的孤儿播放器）。
  真机验证：`device-probe` 传 `seek` 参数会录一段 MP3 用 WPF `MediaPlayer` 打开，拖到 1.0 s / 0.4 s 后
  读回 Position 分别为 1.01 / 0.42，证明底层可拖（`MediaElement` 内部同一套内核）。

### osu! 下图器
- 先下 `.osz.part` 再改名：断线留半个文件会被「已存在」永久跳过。
- 日期与 Key 数走本地筛选（API 不支持）；结果表头与数据行列定义必须一字不差。

### 视频 / 音乐播放器
- ⚠ 停止后再按播放**必须重新 `Load` 一遍**，不能只在旧 `MediaElement` 上 `Play()`：
  `Stop()` 之后不会再有 `MediaOpened` 事件，VM 里的 `HasVideo`（画面标记）就恢复不了 ——
  表现是「声音出来了、视频画面一直停在封面」。暂停则相反：暂停后继续播走 `Play()`，别重新 Load（会跳回 0）。
- ⚠ 「重新 Load」**必须先 `MediaElement.Close()` 再赋 `Source`**：同一个 `Uri` 再赋一次 `Source`，
  WPF 认为没变、**不会重新触发 `MediaOpened`** —— 那样 reload 也只是回到「有声音没画面」。
  `MediaElementPlayback.Load` 里 `Stop()` 之后紧跟 `Close()` 就是这个原因。
- ⚠ `MediaElement` 的 `LoadedBehavior` / `UnloadedBehavior` 都要设 `Manual`，否则自播自闭、跟 VM 命令打架。
- ⚠ 整窗全屏临时改 `WM_GETMINMAXINFO` 再最大化，并 `SetWindowRgn(hwnd, IntPtr.Zero)` 清工作区 Region，否则底部黑带。
- 藏任务栏用 `TaskbarHider`：400ms 看门狗、`Hide()` 幂等、不能 `Thread.Sleep`；`YLT_TASKBAR_TRACE` 错误码 5 = 完整性级别低。
- ⚠ 退出全屏回到「最大化」时**不能直接 `WindowState = Maximized`**：进入全屏前窗口本来就是最大化，
  赋同一个值 WPF 不会重新走 `WM_GETMINMAXINFO`，窗口会保留整块显示器的尺寸（任务栏还回来了却被窗口盖住，看起来像没退干净）。
  必须先 `WindowState = Normal` 再 `Maximized`（见 `MainWindow.ExitFullscreen`）；播放器 / 图片 / PDF 三个工具共用这条路径。
- 切走还要干活：`IBackgroundTool.KeepAliveOnLeave` + 外壳存视图实例；`Unloaded` 时不能 `Close()` 播放器。
- 工具页状态缓存（v0.46.0）：`MainViewModel.CloseCurrentTool` **不再直接 `NotifyClosed`**，而是把视图 +
  ViewModel 塞进 `_toolViewCache`（最多 12 个，LRU）；切回时原样复用。真的 Dispose 只有三条路径：
  缓存淘汰 / 「重建配置」`DropCachedToolView` / 程序退出 `App.Exit → ShutdownTools()`。
  ⚠ 新工具别在 `OnClosed` 里假设「切走一定被调用」；需要切走时停后台的，要在缓存淘汰 / 退出路径里也安全。
  ⚠ 视图 `Unloaded/Loaded` 会重跑：阅读器这种要把「读到哪」存在自己的字段或 VM 里，重新挂载时恢复
  （PDF 用 `vm.ConsumeRestorePosition()`；图片在 `OnUnloaded` 存 `_restoreReadingRatioOnLoad`）。
  ⚠ 新增工具页 / 换 ViewModel 时注意缓存按 `ToolViewModelBase.ToolId` 匹配，Id 不一致会被丢弃重建。
- ⚠ 后台视图**只有两条腿释放**：① 回到该工具、把它原样挂回界面（声音接着放）；② 换成别的后台视图（先 `StopBackgroundPlayback()` 停声再丢）。
  别在「切去别的工具」时顺手 `ClearKeptAlive()`：只丢引用不停 `MediaElement`，会留下没人能控制的孤儿播放器 ——
  狂切工具页就是「同一首歌两份」，过一会儿被 GC 收掉又突然断音、左下角小窗也跟着消失（v0.44.1 修）。
- ⚠ 重排列表后必须 `_queue.Reset()` 并把当前曲目重新 `SetCurrent(Items.IndexOf(当前))`，否则列表循环会按旧下标跳到错的歌、随机的一轮顺序作废。
- `playlist.json` 的路径列表按**添加顺序**保存（别按界面显示顺序），否则「默认顺序」回不到最初的添加顺序。
- ⚠ 迷你播放条长标题「永远只露前一段、右边固定被切」**不是字体 / 滚动速度问题，是 WPF 自动 layout clip**：
  `TextBlock` 直接当 `Grid` 子元素且 `HorizontalAlignment="Left"` 时，WPF 会按 `DesiredSize` 把它排得比槽位宽，
  再给 TextBlock 自己加一个 **layout clip**（裁到槽位宽）。滚动是用 `RenderTransform` 平移文字的，
  这个裁剪窗口会**跟着文字一起平移** → 右侧的字永远露不出来；改滚动速度 / 时长 / 加 overshoot 都没用
  （v0.55.2 之前两次“修复”都没找到这一点，越改越像“速度太快没看清”）。
  正确结构：**宽度固定、位置不动的裁剪容器**（`Grid ClipToBounds`）→ `Canvas`（用无限宽量、按 DesiredSize 排，
  不会被加 layout clip）→ `TextBlock`；标题按钮模板里的 `ContentPresenter` 必须是 `Stretch`（是 `Left` 的话
  裁剪容器宽度会变成 0 / 内容宽，滚动窗口算错）。排查用 `YLT_MINI_MEASURE=<文件>`（dump 真实宽度 / 祖先链 /
  layout clip / 裁剪窗口在窗口坐标里的位置）和 `YLT_MINI_SHIFT=<像素>`（把跑马灯固定在某个位移，截图可复现）；
  回归脚本 `scripts/e2e-mini-marquee.ps1`（真截图 + 像素检查“最后一个字是不是真的画出来了”）。
- 迷你条标题**速度必须统一**（v0.55.3）：`ScrollDuration` 只按 `溢出宽度 ÷ PixelsPerSecond` 算，**别给超长标题
  封顶加速**（v0.54.0 曾封顶 4 秒，结果一百多字的歌名滚得飞快、短标题反而正常，用户一眼就看出“长标题太快”）。
  现在统一 26 px/s（约 2 汉字 / 秒）；要调就改 `MiniPlayerPanel.PixelsPerSecond` 一处，自检会按它反算每像素耗时。
- 「每个文件夹记住上次听到哪儿」（v0.56.0，`MediaFolderResumeState` + `.Resume.cs`）：
  ① 进度每 200ms 变一次，**记录只写内存、落盘节流 10 秒**（暂停 / 停止 / 换歌 / 切走 / 退出时强制落盘）；
  ② 恢复只在 `ResumeLastPlayed` 打开时做；「打开文件夹」按**这次新加进来的文件**去匹配记录（不是按文件夹名猜），
     多文件夹取最近更新的一条；「打开文件」不参与；
  ③ `CurrentItem` 必须在 `CaptureFolderProgress()` **之后**再换，否则换歌前那首的进度就记到新歌头上了；
  ④ 「恢复后自动播放」不能在构造 VM 里直接 `PlayItem` —— 那时 View 还没 Loaded、播放器是 null；
     要置 `_autoResumePending`，等 `AttachPlayer` 接上再放（见 `.Resume.cs` 的 `TryAutoResumeOnPlayerAttached`）。
  ⑤ ⚠ **关应用时播放器可能在后台保活**：`MainViewModel.ShutdownTools` 对保活视图走的是
     `ClearKeptAlive(stopPlayback: true) → StopBackgroundPlayback()`，**不是 `OnClosed`**；
     而 `StopPlayback()` 会把位置归零，所以必须「先 `PersistPlaybackState()` 落盘、再 `StopPlayback(persist: false)`」——
     顺序反了就会把「上次听到哪儿」写成 0（v0.65.1 用户报「持久化没生效」的根因之一）。
     换歌 / 暂停 / 停止 / 视图 `Unloaded` 也都要落盘，不能只靠 `OnClosed`（视图进缓存切走时外壳只 Flush 配置、不调 NotifyClosed）。
     更根本的一层：`App.Exit` 里原来用 `MainWindow.DataContext as MainViewModel` 拿 ViewModel ——
     最后一扇窗口一关，WPF 就把 `Application.MainWindow` 置成 null，`ShutdownTools` 直接被跳过
     （真程序实测：进程 exit=0，但 `OnClosed` / 所有落盘一次都没跑）。启动时把 MainViewModel 存进静态字段、
     退出流程用它；回归脚本 `scripts/e2e-player-resume.ps1`（真 exe + 临时 YLT_HOME：恢复 0:15 → 放几秒 →
     关主窗口 → 断言 playlist.json 的进度变成 >15 秒、folder-resume.json 也写了、trace 里有 OnClosed）。
  ⑥ ⚠ 落盘进度不能直接取 `_lastPosition`：刚恢复还没 Load 时它是 0，得用 `_pendingSeek` / `_pendingResume`
     兜底（否则「打开工具 → 什么都不点 → 关掉」就把进度清零）；`_pendingResume` 还要记住属于哪首
     （`_pendingResumePath`），否则恢复 A 之后双击 B 会跳到 A 的进度。
     恢复时 `playlist.json` 的当前曲目可能是空的（老数据 / 后台退出），要用 `folder-resume.json` 里最近的一条兜底。
  ⑦ ⚠ `YLT_PLAYER_DEMO` 演示模式塞的是假路径：`SavePlaylistState` / `SaveFolderResumeState` 必须跳过，
     否则截图 / 调试会把用户真实的 playlist.json / folder-resume.json 覆盖成一堆不存在的文件
     （v0.65.1 前真的发生过，表现为「下次打开列表空 / 当前那首没了」）。
  截图验收：`YLT_PLAYER_OPEN_FOLDER=<文件夹>`（打开播放器页后自动读一次这个文件夹，配 `YLT_SHOT` 看
  「恢复选中 + 进度 / 自动播放」的效果；只认第一次 Loaded，正常使用不设）。
  排错用 `YLT_PLAYER_TRACE=<文件>`：把 MediaElement 的 Load / MediaOpened / MediaFailed、PlayItem 的
  `pendingSeek`、OnPlayerOpened 拿到的时长与目标位置写进文件（排查「恢复进度后没跳过去」先看它）。

### 外壳：分类 · 粒子 · 主页设置
- 老工具 `Category` 别自动当分类目录：树只认 `CategoryPath`（默认空）。
- 类别页只平铺「直接工具 + 子类别卡片」，别递归平铺；主页搜索对全部工具扁平过滤。
- ⚠ 左侧「功能列表」栏要能被「主页设置 → 显示左侧功能列表栏」隐藏、全屏时也要收成 0：
  `ColumnDefinition.MinWidth` 会盖过 `Width=0`，所以 `SidebarColumn.MinWidth` **不能写死在 XAML**，
  由 `MainWindow.ApplySidebarVisibility()` 动态设（显示 180 / 隐藏·全屏 0），并同时把分隔条那一列设 0、
  分隔条 `Collapsed`（`PaneSplitter` 对不可见状态会跳过套用记忆，否则刚藏起来又被记忆值撑回来，v0.66.0）。
  宽度记忆用 `PaneSplitter` + `Key="shell.sidebar"`（`Config\layout.json`，和工具页共用、「恢复默认布局」一起清）。
  主窗口的 DataContext 是**构造之后**才由 App 赋的：监听 `ShowSidebar` 要挂 `DataContextChanged`，
  别只在构造函数里拿一次（那时还是 null）。
- ⚠ `ParticleOverlay` 订阅 `CompositionTarget.Rendering`（静态）+ Window 鼠标事件，必须成对挂摘，否则泄漏 + 关窗后还在跑。
- 粒子发射点用 `GetCursorPos` + `PointFromScreen`：`DragMove` 期间 `GetPosition` 返回 (0,0)，粒子会从左上角冒。
- 粒子别每帧 new 控件 / 画刷：`DrawingContext.DrawEllipse` + 冻结画刷，否则 UI 线程卡。
- 光标特效分「自动效果」（`autoEffects`）/「点击效果」（`clickEffects`）两套 **独立多选** 的 Id 数组；
  特效 Id 就是配置格式（清单在 `Core/Effects/CursorEffects.cs`），加特效 = 清单加一条 + `ParticleOverlay` 加渲染分支，
  设置页按清单自动渲染、不用改 XAML。
- `ParticleOverlay` 只在「自动拖尾在生成」或「有一次性特效在播」时订阅 `CompositionTarget.Rendering`，
  播完立刻退订；别改成常驻 60fps（鼠标散光只在鼠标移动时重绘一次）。
- 特效可调参数（数量 / 大小 / 范围 / 各半径）的默认值与范围集中在 `Core/Effects/CursorEffectTuning.cs`：
  设置页滑块、配置校验、渲染层都读它，**别在 XAML / VM / 渲染器里另写一套数字**；
  新加参数时同步改 `ShellConfig` 字段、`MainViewModel` 滑块列表、`ParticleOverlay.ApplyTuning`，自检会兜住默认值 / 越界夹取。
  参数滑块挂在 `ShellEffectOption.Sliders` 上、由设置页嵌在对应勾选框下面（勾选才显示）——
  别再改回「卡片底部统一一组滑块 + 置灰」，那是 v0.37.0~0.37.2 的旧布局。
- 反色圆环（`invert-circle`）没有现成 API：用 `VisualBrush` 把内容根节点取景到 `RenderTargetBitmap` 再逐像素取反。
  ⚠ `VisualBrush` 取景会把整棵内容树软件渲染一遍（实测约 20~35ms，区域设小也省不下来），**不能每帧取**：
  只在鼠标按下 / 松开时各取一次（`InvertCircleRenderer`），动画本身只改蒙版半径；窗口缩放直接结束这一圈。
  ⚠ 覆盖层必须和取景根节点（`MainWindow.xaml` 的 `RootGrid`）是**兄弟节点**，否则取景会把覆盖层自己
  （粒子 / 上一帧的反色）也画进去；根节点透明，取景时还要补 `WindowBackgroundBrush` 底色。
- 反色圆环支持连点、多圈并存（各圈各自取景 + 时间线，最多 8 圈）；「两圈重叠变回正色」用
  `CombinedGeometry` 的 Xor / Exclude / Intersect 按覆盖次数奇偶画（每圈同时留正色 / 反色两份位图），
  别图省事改成简单叠图；关掉开关时才走简单叠图（后画的盖住先前的）。

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
- 滚轮惯性（`WheelInertiaEnabled`，和拖动惯性 `InertiaEnabled` 是**两个独立设置**）：滚轮处理里用
  「这一格**实际**走过的偏移差」判断方向（反向阅读 / Shift 横滚不会滑反），再用
  `PictureInertia.WheelVelocity(步长)` 给初速度，复用拖动惯性那套 `_inertiaTimer` + `OnInertiaTick`（16ms 衰减）；
  翻页 / 单张模式、Ctrl+滚轮缩放不启动。调试：`YLT_PICTURE_WHEEL_SIM=-120` 打开后模拟滚一格（走正常处理链路），
  可和 `wheelInertiaEnabled=false` 的截图对比。
- 全屏（F11）不自己操作窗口：`Window.GetWindow(this) as IFullscreenHost` 交给外壳藏壳 / 铺满屏幕（任务栏由外壳藏）。
  视图 `Unloaded` 顺序必须是「先 `vm.ExitFullscreen()`（事件还订阅着，外壳能还原）→ 再摘事件」，
  反过来会留下一个“藏了壳”的窗口（播放器踩过的坑，两个阅读器复制同一套）。
- 全屏工具条是浮层：行高绑 VM 的 `ToolbarRowHeight`（全屏 = 0），Border 用 DataTrigger 换到 Row1 + `Panel.ZIndex` 置顶；
  **隐藏 / 显示用 `RenderTransform` 上下平移（约 180ms 滑出 / 160ms 滑回），别再拿 `Visibility` 切换**——Collapsed 就做不了动画，
  也没法做「靠近顶部才滑出」的交互。全屏鼠标只有在**距顶 84px 热区**才滑出；离开顶部 / 工具条约 **1 秒**自动滑回；
  阅读区中间晃鼠标只让指针显示，不弹工具条。⚠ **设置 / 目录面板开着也不能阻止工具条自动收**（踩过：旧逻辑把
  `IsSettingsVisible` 当保留条件，面板一开工具条就永远挂在上面）；只有鼠标还在顶部热区 / 工具条上、或工具条里的
  **文本输入框**（搜索 / 缩放 / 跳页）聚焦才保留。⚠ **别用 `ToolbarBorder.IsKeyboardFocusWithin` 当保留条件**：
  点过「设置 / 目录 / 全屏」按钮后键盘焦点会留在那个按钮上，工具条就再也不自动收了（用户实际踩到的就是这个）；
  用 `IsToolbarTextInputFocused()`（`Keyboard.FocusedElement is TextBox/ComboBox`），收起时对非输入框焦点
  `Keyboard.ClearFocus()`。全屏时设置面板的 `Margin.Top` 要在 `IsFullscreen=True` 时设成 **112**（让出工具条高度），
  否则工具条一滑出就把设置面板顶部的标题 / 第一行挡住。
- ⚠ 工具条滑动动画**必须同时维护 `TranslateTransform.Y` 的“基准值”**：只 `BeginAnimation` 而基准值不动的话，
  下一次 `BeginAnimation(null)` 会瞬间把 Y 打回基准值——看起来就是「一收就整个消失、没有滑动」。
  正确做法：Show 前把基准设成起点、完成后把基准设成 0；Hide 先读当前 Y 作为起点、把基准设成起点再动画，完成后基准设成终点。
  两个工具都是这套；`YLT_*_HOVER_SEQUENCE_FOCUS=1` 可复现「点过按钮再离开」的自动收起 + 滑动。
  ⚠ 视图 `Loaded` 可能触发多次：用一个 `_fullscreenApplied` 守卫，重复同步时不能把已滑出的工具条又藏回去。
- 截图：`YLT_*_CONTROLS=1` 会把工具条**钉在滑出状态**（否则真实鼠标在窗口里触发的 move 会按热区规则把它收走），
  `=0` 拍沉浸帧；`YLT_*_HOVER_TOP=1` / `_HOVER_MIDDLE=1` 模拟鼠标在顶部 / 中间，用来验「靠近顶部才滑出」
  （跑之前先把真实鼠标移开顶部，否则真实 move 事件会覆盖模拟结果）。
  `YLT_*_HOVER_SEQUENCE=1` 先顶部再中间，用来验「离开顶部 1 秒后自动收起」；`YLT_SHOT_DELAY_MS=2200` 把截图延迟拉长，
  否则 1.2 秒的截图可能在工具条还没收完时就拍。
  `YLT_FULLSCREEN_ROUNDTRIP=1`（可再配 `YLT_START_MAXIMIZED=1`）进入全屏 400ms 再退出，
  截图尺寸应等于**工作区**（如 1920×1032 而不是 1928×1088），用来验「退出全屏是正常最大化」。

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
- 滚轮惯性（`WheelInertiaEnabled`，和拖动惯性 `InertiaEnabled` 独立）：用「实际走过的偏移差」判断方向后调
  `StartWheelInertia`，复用拖动惯性定时器；连续滚动（含 Shift 横滚、双页放大后 Shift 平移）会滑，双页默认翻页不滑。
  调试：`YLT_PDF_WHEEL_SIM=-120` 模拟滚一格；合成 `MouseWheelEventArgs` 的 `OriginalSource` 是 null，
  两个滚轮处理都改成 `(e.OriginalSource ?? e.Source)` 兜底才能走正常链路。
- ⚠ 阅读区右侧快速滚动条（以及以后放进 `ViewportGrid` 的任何 ScrollBar）：`OnPreviewMouseDown` 的拖拽判断必须把
  `ScrollBar` 当成可点击控件（`IsOnClickableControl`），否则 Preview 会先 `e.Handled = true` 并捕获鼠标 ——
  滚动条永远点不到，一点就变成拖画面（用户报过）。新增可点击控件类型时同步加进这个判断。
- ⚠ 文字层在后台 `Task.Run` 提取：切 PDF / 关页面要 `Cancel` 旧的 `CancellationTokenSource` 并比对 `_textLayerCts`，
  否则旧文档的结果可能覆盖新文档；完成回 UI 一律走 `Ui(...)`。
- 文件关联接线：`App.xaml.cs` 启动参数 + 第二实例 `HandleSecondInstance` 两处都要认 `.pdf`，
  `NormalizeLaunchArguments` 也要把 PDF 路径转成绝对路径；单实例开关是程序级的（读图片阅读器配置），PDF 没有单独开关。
- 调试开关（截图用）：`YLT_PDF_ZOOM=200`（**会写配置**，截图前删 config）、`YLT_PDF_SEARCH=关键词`、
  `YLT_PDF_OUTLINE=1`、`YLT_PDF_SETTINGS=1`。
- 渲染宽度上限 2000 px / 单页像素 800 万（缩放清晰度与内存的折中）；`PdfReaderChecks` 的「渲染宽度夹取」断言跟着这两个常量走。
- 全屏沉浸阅读（F11）和图片阅读器同一套：`IFullscreenHost` + 工具条浮层 + **鼠标靠近顶部才上下滑出 / 滑回** + 指针隐藏；
  ⚠ 视图 `Unloaded` 同样是「先 `ExitFullscreen()`（事件还订阅着）→ 再摘事件」。PDF 工具条是两行，全屏时仍浮在顶部；
  全屏时底部操作提示条收起、页码 / 进度条保留。动画 / 热区 / 重复 Loaded 守卫见上面图片阅读器那条（两边同一套代码）。
- PDF 设置面板：正文放在「行高 560 + 12px 横向 PaneSplitter + *」的 Grid 里，底部缝上下拖动调面板高度
  （`Key="pdf-reader.设置面板高度"`，随 `Config\layout.json` 记住；双击复位）。别再写成固定高度 + `MaxHeight` 的 Border，
  那样只有左侧宽度缝能拖、下面拖不动。页码条显隐是 `Cfg.ShowPageIndicator`，设置面板勾选框绑 `ShowPageIndicator`，
  改完要调 `UpdateModeChrome` / `UpdatePageIndicatorVisibility`（不要只改 Visibility 常量）。
  图片阅读器的设置卡片同理（`picture-viewer.设置面板高度`）；其它工具页在 v0.42 时已全部用 PaneSplitter 接好，
  以后新增浮动卡片也要同时给宽度 / 高度两条缝。
- 调试 / 截图：`YLT_PICTURE_FULLSCREEN=1` / `YLT_PDF_FULLSCREEN=1` 全屏启动；`YLT_PICTURE_CONTROLS=1` /
  `YLT_PDF_CONTROLS=1` 钉住滑出的工具条，`=0` 拍“沉浸”帧；`YLT_*_HOVER_TOP=1` / `_HOVER_MIDDLE=1` 验证「靠近顶部才滑出」。
- 记住上次阅读位置（`config.json` 的 `readingPositions`）：界面每帧都可调 `NoteReadingPosition`，VM 里 1.2s `DispatcherTimer`
  + 2 秒节流落盘；但**退出程序时 `View.Unloaded` 不一定触发**（实测截图 / 关窗路径没跑到），所以另外在 `Window.Closing`
  里 `FlushReadingPosition(saveNow: true)` 兜底 —— 只靠配置自动落盘会丢最后一秒的位置。恢复优先级：`YLT_PDF_PAGE` > 上次位置 > 第一页；
  **模式没变才按 `ReadingRatio` 恢复**，换了阅读方式按页号恢复。
- 最近浏览（`recentLimit` / `recentEntries`）：路径统一 `NormalizeRecentPath`（完整路径、去重；图片记录会去掉尾部斜杠）；
  配置属性虽是自动属性不发通知，但 `ToolConfigStore` 加载后有后台 400ms 差异检查，所以滑块改上限也能自动落盘。
  ⚠ 两个工具的「最近」面板都用 `SmallIconButtonStyle` 画关闭按钮 —— **这个样式只定义在 PDF 视图的 Resources 里**，
  图片视图没有，用了会 XamlParseException 闪退（已踩，图片侧改用 `SecondaryButtonStyle` 的 ✕）。
- 调试 / 截图：`YLT_PDF_JUMP=4` 打开后直接走一次「跳页输入框」跳到第 4 页；`YLT_PDF_PAGE=4` 会覆盖恢复位置。
  截图最近浏览面板 / 设置：`YLT_PDF_RECENT=1` / `YLT_PICTURE_RECENT=1`（打开面板）、`YLT_PDF_SETTINGS=1` / `YLT_PICTURE_SETTINGS=1`；
  两个面板别同时开（env 要清），否则互相盖住。

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
- ⚠ Overlay「强制置顶」是 500ms 定时 `SetWindowPos(HWND_TOPMOST)` 看门狗（只在「始终置顶 + 强制置顶」都开时跑，
  窗口关闭时 `OnClosed` 必须停表）。它只对窗口化 / 无边框全屏游戏有效：**真正的独占全屏任何普通窗口都盖不上去，
  管理员权限 / UAC 提权也无效**（提权只影响安全与消息权限，不改变独占全屏的显示层级），别为此加 UAC 重启按钮骗用户；
  也绝对不要尝试注入游戏进程（反作弊风险）。用户说「盖不住」时先让他把游戏改成无边框窗口。
- 自检的 Raw Input 断言要**过滤目标 VK**（`VK_F24`）：自检跑的时候用户/别的程序可能按别的键，只断言
  「收到 F24 / F24 只算一次」，否则会偶发失败（已踩过）。
- 调试开关：`YLT_KPS_DEMO=1` 造假统计（截图冒烟）、`YLT_KPS_SHOT_OVERLAY=<png>` 把 Overlay 渲染成 PNG 后退出，
  用法同 `YLT_SHOT`（见 AGENTS.md）。

### 液态玻璃 / 毛玻璃 / 亚克力主题（半透明界面 + 背景图）
- ⚠ 改 token 一起改：四套字典（`DefaultTheme` / `LiquidGlassTheme` / `FrostedGlassTheme` / `AcrylicTheme`）+ `ThemeManager.RequiredTokenKeys`（几何 key 在 `RequiredRadiusKeys`）；少一个静默退回控件默认值。
- 启动在 `new MainWindow()` 之前 `ThemeManager.Apply`；⚠ Storyboard 的 `TargetName` 必须同一命名域（写错启动崩）。
- 窗口背景层是 `Views/GlassBackdrop.xaml`（v0.43.0 由 `LiquidBackdrop` 升级）：默认主题下整层 `Collapsed`，流动光晕只在液态玻璃时跑；
  背景图由 `MainViewModel` 解码（不锁文件、超宽图按 2560 解码），毛玻璃 / 亚克力给静态 `BlurEffect` + `BitmapCache`，别把模糊加在动画层上。
- 背景图配置只存**纯文件名**（`Data\shell\background\`），`ShellBackgroundImage.IsSafeFileName` 挡 `..` / 绝对路径；换图先复制新图成功再删旧图。
- 背景图模糊度按风格分别存在 `ShellConfig` 的 `liquidGlassBlurRadius` / `frostedGlassBlurRadius` / `acrylicBlurRadius`，范围 / 默认值集中在 `Models/ShellBackdropSettings.cs`；设置页滑块只在选中对应风格时显示（`ShellSliderOption` + `EffectSliderTemplate`），改值只通知 `BackgroundImageBlurRadius`，别再在渲染层写死半径。

## 别做 · 先问用户

- `scripts/` 调试脚本会弹窗抢焦点、真动鼠标：跑前确认用户没在忙。
- 截图 / 界面验证必须 `Start-Process .\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe -Wait`（GUI 程序，直接跑不等它结束）。
- 自检别调 `PlayEntry()`（会真启动 FlashPlayer，无头自检卡死）；只调 `MarkPlayed()`。
- 别从 URL 反推本地路径（曾把文件写到游戏目录外）。
- 别引第三方库：新轮子手写进 `Core/`（参考 `ScrollChaining`、`Crc32`、`ParticleEngine`）；确有必要引 NuGet 时按 AGENTS.md 规则 2（优先微软官方 / 活跃维护、登记 `THIRD-PARTY-NOTICES.md`）。

## 跨领域的坑

### 存储与配置
- 配置用自动属性、后台内容比对；要联动刷新才用 `Set(ref …)`。
- `LoadConfig<T>()` 同类型只返回一个实例；工具关闭再打开框架自动恢复后台保存。
- `Dyn[键]` 键不存在返回 null：绑 `IsChecked` 得 null、点一下变 false；用 `LoadDynamicConfigWithDefaults` 预置或 `TargetNullValue=False`。

### WPF 与界面
- 工具页有多个区块时**别把区块大小写死**：用 `core:PaneSplitter` + `App.xaml` 的 `PaneSplitterVerticalStyle` / `PaneSplitterHorizontalStyle`（悬停蓝线 / ↔ ↕ 光标 / 自动记忆 / 双击复位），约定见 [layout.md](layout.md)。分隔条必须独占一行 / 一列，相邻两块要给 `MinWidth` / `MinHeight`，否则窗口小时会被拖没；写死像素的上下两块才适合放进 `ScrollViewer` 里上下拖，`*` 行在里面会塌成内容高度。
- 分隔条模板里有**两层线**：`RestLine` = 提示线（透明度由 `PaneSplitterSettings` 控制，主页设置滑块调）、`SplitterLine` = 悬停亮线（模板触发器改它）。⚠ 别把悬停触发器改到 `RestLine` 上，也别在代码里改 `SplitterLine.Opacity`（局部值会盖过触发器），否则悬停就不亮了。
- **每个页面级区块的相邻边界都要有分隔条**（v0.42.0 硬性要求）：别把两块塞进一个可拖块里再用 `*` 均分，用户要的是「每一块都能单独拖」；标题 / 状态条这类纯信息条不算。
- 区块可以被开关整块关掉时（例如棋牌「显示棋谱」）：用 `BoolToPaneLength`（Converters/）把同一行 `RowDefinition` 的 `Height` + `MinHeight` 绑到开关上，分隔条也绑同源 `Visibility`；⚠ `PaneSplitter` 改行高必须用 `SetCurrentValue`（直接给 `Height` 赋值会清掉绑定，开关就再也收不起来），而且它在不可见时要跳过「套用记忆尺寸」，否则隐藏瞬间又被记忆值撑回来。
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
- 目标框架：主程序 / 自检 `net9.0-windows10.0.19041.0`（PDF 阅读器要 Windows.Data.Pdf），工具库一般是 `net9.0-windows`；
  ⚠ **例外**：Live2D 桌宠与联机聊天室（摄像头采集要 WinRT `Windows.Media.Capture`）两个工具工程是
  `net9.0-windows10.0.19041.0`，主程序 / 自检本来也是 19041，引用兼容。库项目也要 `UseWPF=true`（Core 用 `Application.Current.Dispatcher`）。
- `Directory.Build.targets` 补 `System.IO` / `System.Text` 隐式 using；往数据目录放文件请程序首次运行自己写（`RuntimeReadme`）或发布脚本 `Copy-Item`。

### 截图与脚本
- `YLT_SHOT` 出的 PNG 顶部有重复标题残影：`RenderTargetBitmap` 时机老毛病，不是界面 bug。
- 长页面（固定像素行 + 整页滚动）一屏截不全：`YLT_PAGE_SCROLL=bottom`（或像素值）会在打开工具页 / 设置页后把页面根 ScrollViewer 滚到指定位置再截图（只认「页面根内容就是 ScrollViewer」的页面）。
  ⚠ 聊天室页面自己的 `Loaded` 会把外层页面滚回顶部（初次聚焦输入框的老问题），通用开关对它无效 → 用该页自己的 `YLT_CHAT_SCROLL=<像素>`（跟 `YLT_CHAT_SIZE` 配合看底部三栏）。
- 截图放 `shots/`（png / txt 不入库）；开窗口的调试脚本放 `scripts/`。
- 含中文 `.ps1` 必须 UTF-8 with BOM，否则 5.1 按 ANSI 解析报语法错误。

### 网页浏览器（WebView2 / Chromium）
- WebView2 是**独立原生窗口**（HwndHost）：WPF 的粒子 / 覆盖层 / 圆角裁剪都盖不住网页区域；`YLT_SHOT` 的 `RenderTargetBitmap` 也**抓不到网页内容**（只能截到工具条）。验证网页真的渲染出来要用 `CapturePreviewAsync`（工具里已接好：截图模式会另存 `<截图>.web.png`，`YLT_SHOT_DELAY_MS` 给足时间）。
- 外壳会缓存工具页（最多 12 个）：网页内核必须在 `Unloaded` 里 `Dispose`、`Loaded` 里重建，否则切走一个工具就多一个隐藏的 Edge 进程；用户数据目录放 `Cache\browser\WebView2`（「清理缓存」能清），内核运行中清不干净属正常，下次再清。
- **别把 `AreDefaultContextMenusEnabled` 设成 `false`**：微软文档写明那样会让 `ContextMenuRequested` 事件**完全不触发**（右键没有任何反应，v0.58.0 踩过）。正确做法：保持 `true`，在事件里 `Handled = true` + `GetDeferral()`（菜单 `Closed` 时 `Complete`），再弹自己的 WPF 菜单（第一条放「刷新」）。
- WPF 默认 `ContextMenu` / `MenuItem` 模板会给菜单项左侧留一条**白底「图标列」**（深色主题下非常突兀），只设 `Background` 是盖不掉的，必须换 `ControlTemplate`（见 `BrowserView.xaml` 的 `BrowserMenuItemStyle`）；自建菜单项时别忘了 `InputGestureText` 和 `IsHighlighted` 悬停色。
- **多标签页**（v0.59.0 起）：每个标签一个 `WebView2` 实例，**共享一次 `CoreWebView2Environment`**（同一用户数据目录）——这样 Cookie / 登录态互通、第二个标签起启动很快；别每个标签各 `CreateAsync` 一份环境。多个内核全叠在同一个 `Grid` 里，只有激活标签 `Visibility.Visible`：WebView2 的 `Collapsed` 会真正隐藏原生 HWND（直接从 `Children` 摘掉反而会触发重新挂载的坑）。创建是异步的，创建期间标签可能被关掉 → `EnsureCoreWebView2Async` 返回后要核对「字典里还是这个实例」再接线。
- `target=_blank` / `window.open` 走 `NewWindowRequested`：不处理就什么都不发生，本工具在**新标签页**里打开（`e.Handled = true` 再开标签）。
- 快捷键（Ctrl+T / Ctrl+W / Ctrl+Tab）挂在 UserControl 的 `PreviewKeyDown`：焦点在网页内部时 WebView2（HwndHost）可能自己吃掉按键，WPF 收不到——这是键盘路由限制，按钮永远可用，文档里如实写。
- 包会带原生 `WebView2Loader.dll`（在 `runtimes\win-x64\native\`），发布后检查 `dist` 里这份文件在；WebView2 **运行时**是系统组件，`WebView2RuntimeNotFoundException` 要提示用户去装，别把 100+MB 内核打进发布目录。
- **会话恢复时别在 `RestoreSession` 里触发 `SaveSession`**：`AddTab` → `CollectionChanged` → `SaveSession` 会把还没选中的 `ActiveTabIndex` 写回 0，恢复出来的激活标签永远是第一个（v0.63.0 踩过）。用 `_restoringSession` 开关挡住，恢复结束再允许保存。
- **下载**（v0.63.0）：`DownloadStarting.Handled = true` 只是**关掉默认下载 UI**，下载本身照常进行；进度从 `CoreWebView2DownloadOperation` 的 `BytesReceivedChanged` / `StateChanged` 读（`TotalBytesToReceive` 是 `ulong?`），事件可能重复触发，结束记录要幂等（从进行中字典 `Remove` 成功才落库）。**关标签 / 释放内核时进行中的下载不会再有完成事件**，必须自己标中断并写记录，否则下载面板一直停在「正在下载」。
- **缩放**：宿主自己设 `WebView2.ZoomFactor` **不会**触发 `ZoomFactorChanged`（官方文档明确），必须手动同步标签 / 工具条百分比；用户 Ctrl+滚轮 / Ctrl+加减 的原生缩放会触发该事件。
- `ProgressBar.Value` 的绑定默认是 **TwoWay**（`RangeBase` 的特性），绑到只读的 `Progress` 属性会抛 `XamlParseException`，整个工具启动崩溃——一律写 `Mode=OneWay`（v0.63.0 踩过，下载面板）。
- 齿轮菜单用 `Popup`（独立顶层 HWND）：`YLT_SHOT` 的 `RenderTargetBitmap` 截图**抓不到**，要验证只能用屏幕截图 / `PrintWindow`（调试钩子 `YLT_BROWSER_GEAR_TEST=1`）。
- **自定义右键菜单里执行 Chromium 默认命令**：从 `ContextMenuRequested` 的 `MenuItems` 里按内部 `Name`（`saveImageAs` / `copyImageLocation` / `saveLinkAs` …）找到 `CommandId`，菜单项点击时设 `args.SelectedCommandId`，等菜单 `Closed` 后 `Complete` deferral，原生保存对话框才会起来（v0.64.0 实测「图片另存为」能弹系统对话框；**别在没弹菜单时直接设 SelectedCommandId**，实测不会执行）。
- **浏览数据放 `Data\browser\`，别放 `Cache`**：历史 `history.json`、下载记录 `downloads.json`、书签 `bookmarks.json` 都是用户数据，放 Cache 会被「清理缓存」清掉；只有 WebView2 的 Cookie / 网页缓存才放 `Cache\browser\WebView2`（v0.65.0 起书签也是 Data）。
- **资源嗅探用 CDP 而不是 WebResourceRequested**（v0.67.0）：每个标签的 core 都要 `GetDevToolsProtocolEventReceiver("Network.responseReceived")` + `CallDevToolsProtocolMethodAsync("Network.enable", "{}")`；`WebResourceRequested` 只有请求头没有 mime / 大小，分辨不出图片音频视频。响应用 `Network.loadingFinished.encodedDataLength` 补 chunked 响应缺失的大小；保存原始字节用 `Network.getResponseBody`（带登录态 / 不受 CORS 限制），body 被淘汰时再退回带 Cookie / UA / Referer 的 `HttpClient`。`data:` / `blob:` 内联图标和 `favicon.ico` 必须过滤，否则一个带播放器的页面能刷出几百条 base64 假资源。

### 网络与 Flash 站点解析
- 判越界别用 `Contains("..")`：先 `Path.GetFullPath` 再比前缀（`SafeCombine`）。
- .NET Core 默认无 GBK：静态构造注册 `CodePagesEncodingProvider.Instance`，否则 `GetEncoding("gbk")` 抛异常。
- `UrlJoin` 先判 `//` 再判绝对网址（`Uri` 会把它当 `file://`）。
- 4399 的 `_strGamePath` 可能相对或完整；7k7k 的 `gamePath` 有时只是外壳（17 KB），主 swf 在旁 → 走「装入文件夹」+「关联文件下载器」。
- ArmorGames / Kongregate 的 swf 不能按文件名规律拼：从 `<param name="movie">` / `<embed src>` / `iframeConfig` / `swf_location` 里读；Kongregate 跳三跳、要排掉 API 库 swf（`API_AS3_*.swf`）、`iframeConfig` 不是合法 JSON。
- 散放 swf 本体就是自己：`GameFullPath` 别拼一层；`GamesLibrary` / `Library` 别写字段初始化器（构造前空字符串 → 游戏目录空、说明文件写到仓库根）。
- 播放器无稳定发现源：路径随时失效；查找器要覆盖用户自整理目录（`桌面\Flash Games\tool\`），找不到提示指定路径。

### 文本编辑器（AvalonEdit）
- ⚠ **WPF `Menu` / `MenuItem` 默认模板左侧有一条白底「图标列」**：深色主题下非常突兀，只设 `Background` 盖不住，
  必须给 `Menu` / `MenuItem` 写页面私有的 `ControlTemplate`（`TextEditorView.xaml` 的 `EditorMenuBarStyle` /
  `EditorMenuItemStyle`）；**别动 `App.xaml`**（会波及全程序菜单）。浏览器右键菜单踩过同一个坑。
- ⚠ **标签条 `ListBox` 的默认 `ItemsPanel` 是纵向 `VirtualizingStackPanel`**：不换面板标签会竖着叠、只看得见一个，
  必须内联 `<ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate>`；横向溢出还得套
  `ScrollViewer` + 在 `PreviewMouseWheel` 里自己把滚轮换成横向滚动，别指望 `ListBox` 原生横滚。
- ⚠ **宿主窗口快捷键（`Window.PreviewKeyDown`）要成对挂 / 摘，而且每次 `Loaded` 都要重挂**：外壳会缓存工具视图，
  切走再切回来会再触发 `Loaded`；`_vmWired` 只能守「订阅 ViewModel / 配置」这类只做一次的事，
  `Host` 赋值、快捷键重挂、标签可见性同步必须每次都做——否则切走再回来菜单里的撤销 / 复制 / 查找全部变灰点不动。
  VM 与 View 之间用 `IEditorHost` 接口路由（同浏览器 `IBrowserHost`）：View 在 `Loaded` 里 `vm.Host = this`、
  `Unloaded` 里 `vm.Host = null`，VM 的编辑命令只认 `Host`、不直接摸 AvalonEdit 控件。
- ⚠ **`ToolViewModelBase.NotifyClosed` 的顺序是「先 `ConfigStore.Flush()`，再 `OnClosed()`」**：在 `OnClosed` 里
  才写进配置的会话数据不会自动落盘；要么在 `OnClosed` 末尾补 `Cfg.SaveNow()`，要么在标签增删 / 切换时就把会话
  写进配置（靠后台自动保存）。而且外壳切走工具时只调 `ConfigStore.Flush()`，根本不调 `NotifyClosed`。
- ⚠ **AvalonEdit 内置语法高亮定义（`HighlightingManager`）是全局缓存**：覆写颜色必须幂等（记一份已处理的集合），
  并且单项覆写失败要吞异常——不能因为高亮把编辑器搞挂。
- ⚠ **内置配色是给白底设计的，深色主题下会「深蓝看不清」**：C# 定义里方法名 `MethodCall` 是 `MidnightBlue` 粗体、
  数字 `NumberLiteral` 是 `DarkBlue`、字符串 `String` 是 `Blue`、不少关键字是 `Navy` / `Maroon` / `Brown`。
  只改编辑器背景色不改这些，就会得到「方法名深蓝糊在黑底上」。正确做法是按 xshd 的**颜色名**整体重配
  （`EditorHighlighting.MapColor`：方法淡黄 / 关键字蓝 / 类型青绿 / 字符串橙 / 数字浅绿 / 注释绿），
  并且**没归类的颜色要回退浅灰**（否则又会留下深色）；顺便清 `Background`、把 `FontWeight` 归到 Normal（VS 不加粗）。
  C# 的 `///` 注释颜色在单独注册的 `XmlDoc` 定义里（被 C# `Import`），只调 C# 定义调不到，得单独补一次。
  自检 `[编辑器] C# 高亮定义里每个命名颜色覆写后都够亮` 会逐色算亮度防回归。
- ⚠ **编码探测别用默认的 `Encoding.GetEncoding(936)`**：.NET 默认不带 GB18030 代码页，必须先
  `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`（`TextFileCodec` 静态构造里做），
  否则中文 ANSI 文件直接抛异常。
- ⚠ **别再 `HighlightingManager.RegisterHighlighting` 去抢内置已注册的扩展名**：AvalonEdit 内置定义已经占用了
  `.md` / `.sql` 这批扩展名，再注册会撞车（同名扩展名互相覆盖，谁生效看加载顺序）。项目改成自己一层解析
  （`EditorSyntaxDefinitions`）：**文件名 → 扩展名 → 内置注册表兜底**，`Dockerfile` / `Gemfile` / `Rakefile` /
  `Vagrantfile` 这类没有扩展名的也能认。自检断言 13 个自定义 xshd 全部加载成功、常用路径都解析到正确定义。
- ⚠ **自定义 xshd（v2）里每个 `color="..."` 必须在同一个文件里 `<Color name="..."/>` 定义过**：漏了会在
  `HighlightingLoader.Load` 时报「找不到颜色」，整个语言加载失败退回纯文本；跨定义的 `<Import ruleSet>` 也不能用
  ——Vue 的 `script` / `style` 分段要在 `Span` 里写内联 `RuleSet`，不能 Import 别的定义。
- ⚠ **XML 注释里不能出现连续 `--`**：给 Lua 写 xshd 时把 `--[[` 直接抄进 `<!-- -->` 注释，XML 解析会直接失败
  （xshd 整个加载不出来，Lua 变纯文本）。注释里要描述 Lua 长注释就拆开写 / 用文字绕开，别让 `--` 连续出现。
- ⚠ **会话恢复要先把 `SessionActiveIndex` 取出来再用**：`OpenPath` 里会调 `RefreshSessionConfig()`，按
  「当前激活标签」把 `SessionActiveIndex` 覆盖掉；等恢复完再读就永远是 0（表现是切回来永远选第一个标签）。
  `RestoreSession` 开头先 `var savedActiveIndex = Cfg.SessionActiveIndex;`，恢复完再按它夹取。
- ⚠ **`ColumnDefinition` 不在逻辑 / 视觉树里，拿不到 `DataContext`**：别用 `Binding` 管列宽（绑定静默失效，
  列宽永远保持 XAML 默认值）。设置面板显隐 / 记忆宽度、Markdown 预览窗的显隐与高度都在 code-behind 里设
  （`ApplySettingsColumnWidth` / `UpdatePreviewVisibility`），像素由 `PaneSplitter` 记在 `Config\layout.json`，
  双击复位也交给它。
- ⚠ **AvalonEdit 没有公开的行高设置**（`TextView.DefaultLineHeight` 只读、由字体度量算出来）：想做「行距」
  只能用 `VisualLineElementGenerator` 在**每个文档行行尾**插一个 `DocumentLength=0` / `VisualLength=1` 的元素，
  它的 TextRun 用放大的 `FontRenderingEmSize` + 零宽空格（U+200B）把 WPF `TextLine.Height` 撑起来
  （`Rendering/LineSpacingElementGenerator.cs`；改了系数要 `TextView.Redraw()`）。
  已知边界：一个文档行被自动换行折成多条文本行时，撑高只作用在最后一段 → 行距看起来不完全均匀；
  另外 `HeightTree` 的滚动估算仍按默认行高，跳很远滚动时属于近似值（滚动过的地方会按实测行高修正）。

### FFmpeg（音频工具：视频转音频 / 分段合成）
- ⚠ **本地 m3u8 直接 `-i playlist.m3u8` 会报 “Unsafe script / extension not allowed”**：HLS demuxer 默认只放行白名单扩展名，必须加
  `-allowed_extensions ALL`；网络 / AES key 还要加 `-protocol_whitelist file,http,https,tcp,tls,crypto,data`（见 `SegmentMerger.BuildArguments`）。
- ⚠ **分段文件夹别自己拼输出，用 concat demuxer**：清单格式是每行 `file '路径'`，路径里的 `'` 要转义成 `'\''`；清单用 **UTF-8 无 BOM** 写（带 BOM 第一行会解析失败），`-safe 0` 才允许绝对路径。清单放 `Cache\video-merge`，合成结束删除。
- ⚠ **进度要自己解析**：FFmpeg 的 `-progress pipe:1` 输出 `out_time_us=` / `out_time_ms=`（不是 stderr 的 `time=`），配合 `-nostats -loglevel error`；先 `ffprobe -show_entries format=duration` 拿总时长才能算百分比，分段文件夹 / 直播拿不到时长就用 indeterminate，别把进度条写死。
- ⚠ **HLS 直播 m3u8（没有 `#EXT-X-ENDLIST`）会一直等新分段**，FFmpeg 不退出；界面的取消必须 `Kill(entireProcessTree: true)`，否则 ffmpeg 子进程残留。
- ⚠ **`.m4s` 分片可能依赖独立 `init.mp4`**，concat 拼出来可能不能播；优先让配套 m3u8/mpd 交给 FFmpeg 解析。DRM（Widevine/PlayReady）无法用 FFmpeg 解密。

### Word / Excel 阅读器（手写 OOXML：docx / xlsx）
- docx / xlsx / pptx 都是 **ZIP + XML 部件 + `_rels/*.rels` 关系**：图片、超链接、工作表位置都要按关系文件里的 `r:id` / `r:embed` 去查；
  相对 `Target` 是相对**源部件所在目录**（`word/document.xml` + `media/a.png` → `word/media/a.png`），`../` 与开头 `/` 都要归一。
  Core 的 `OoxmlPackage` 已经把部件名规范化 / 关系解析 / 安全 XML / 体积上限 / 单位换算做好，别在工具里再写一份。
- ⚠ **XML 必须禁 DTD 与外部实体**（`XmlReaderSettings.DtdProcessing = Prohibit` + `XmlResolver = null`）：OOXML 不受信，
  否则一个恶意 docx 就能读本机文件；坏 XML / 缺部件统一抛中文 `OoxmlException`，界面显示在错误区（别弹 MessageBox）。
- ⚠ **解析完别把 `XDocument` / ZIP 流长期挂在内存**：工具页会被外壳缓存最近 12 个视图，解析器要在一个 `using` 里
  把文本 / 图片字节拷成纯模型再释放包；`OnClosed` 只释放大对象（FlowDocument / 解析结果），别依赖 `Unloaded`。
- ⚠ **单位换算写错会「图片变巨大 / 表格错位」**：EMU ÷ 9525 = DIP；twip ÷ 15 = DIP；磅 × 96/72 = DIP。
  docx 的列宽 / 缩进用 twip，图片 `wp:extent` 用 EMU，字号 `sz` 是**半点**（÷2 才是磅）。
- ⚠ **Excel 日期序列号不是 Unix 时间**：1900 系统有「假闰日」，serial 60 = 不存在的 1900-02-29，`>= 61` 要减 1 天；
  工作簿 `date1904` 时基准又是 1904-01-01，必须按 `workbookPr/@date1904` 分支。公式只显示文件里缓存的 `<v>`，
  **不做公式计算**；用 openpyxl 这类库生成的文件常常没有缓存值 → 单元格显示空是数据问题，不是解析器 bug。
- ⚠ **共享字符串 `sharedStrings.xml` 的 rich text 是多个 `<r><t>`**：要把所有 `<t>` 拼起来才能得到完整文字，
  并且要跳过日文注音 `rPh`（否则会出现重复的假名）。
- ⚠ **隐藏行 / 隐藏列**：隐藏列不要建 DataGrid 列（否则列标题 A/B/C 与数据错位，必须按 `ExcelColumn.ColumnIndex` 反查
  行里的 `Cells[列号-1]`）；隐藏行直接跳过会让行号**跳号**，所以行头必须绑 Excel 原始行号（`DataGridRow.Header`），
  不能拿 Items 下标当行号。
- ⚠ **DataGrid 的每格样式不能用 `Setter.Value` 绑 `Binding`**（WPF 会抛「Binding 不能设置在 Setter 上」）：
  用 `DataGridTemplateColumn` + 每列绑定 `Cells[列号-1]`，模板里 `Border`（填充色）+ `TextBlock`（前景 / 字体 / 对齐）；
  列定义可以在代码后置用 `FrameworkElementFactory` 生成（每列一份，不是每格），行 / 列虚拟化必须开
  （`EnableRowVirtualization` / `EnableColumnVirtualization` + `Recycling`），否则 5000 行 × 200 列会把界面卡死。
- ⚠ **Word 的「白纸」是文档内容，不是主题色**：FlowDocument 纸面固定白底 + 近黑字（PDF 阅读器同样处理），
  工具条 / 大纲栏 / 状态栏才用 `DynamicResource`；`FlowDocumentScrollViewer.Zoom` 绑 ViewModel 的缩放值，
  Ctrl+滚轮要自己在 `PreviewMouseWheel` 里接（它只处理普通滚动）。大纲 / 查找跳转靠渲染器返回的
  「块下标 → `Paragraph`」映射，表格 / 图片块没有段落要判空。
- ⚠ **Excel 列宽单位是「字符数」**：近似 `width × 7 + 5` 像素并夹在 24 ~ 600；`width=0` / `hidden="1"` 的列别显示。
  行高同理（`ht` 单位是磅），两个阅读器都不还原真合并（DataGrid 逐格显示，合并区只在锚点格有值 + 状态栏说明）。

