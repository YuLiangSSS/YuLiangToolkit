# AGENTS.md — YuLiangToolkit（凉雨工具集）开发指南

> **本文件是这个项目唯一的开发文档，写给 AI 编码代理 / 未来的自己。**
> 改这个项目之前先读本文件，**不要再去通读 `src/`**：这里把所有已经造好的「轮子」（可复用组件）的
> **用途 + 签名 + 一行用法**、Data/Config 目录约定、新增功能的标准流程、以及踩过的坑都写全了。
>
> * **当前版本 v0.15.0** —— 唯一来源是 `Directory.Build.props` 的 `<Version>`（状态栏读它）。**别在别处写死版本号。**
> * 十三个功能：视频转音频 · **视频 / 音乐播放器** · **图片连续阅读器** · 批量重命名 · Flash 下载器 · 联机聊天室 · osu! 下图器 · **osu! Key 转换器** · **osu! LN 转换器** · **osu! 等级计算器** · **联机五子棋** · **联机象棋** · 框架示例（模板，可整目录删除）。
>   osu! 的四个工具归入「音游工具」分类；**联机五子棋 / 联机象棋归入「棋牌」分类**（主页多级目录 → 类别页 → 工具，见 2.15 / 2.18）。
> * 平台：Windows + .NET 9（`net9.0-windows`）+ WPF + 手写 MVVM；**唯一第三方依赖是 `OsuParsers`**（osu `.osu` 解析/写回，MIT，见 2.17），其余轮子都手写在 `Core/`。
> * 语言：代码注释、提交信息、文档、以及回答用户，一律用**中文**。

---

## A. AI 开工守则（动手前先读完这一节）

### A.1 改完代码必须做的两件事

```powershell
.\build.ps1                                                    # ① 编译：必须 0 错误（有警告也顺手清掉）
dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
                                                               # ② 自检：1541 项行为逐条真跑，退出码 0 = 全通过
```

* **改了行为就同步改自检断言**（自检就是"这份文档里承诺的行为"的执行版本，见第 6 节）。
* **改了用户看得见的行为 / 设置项**，顺手更新 `docs/tools/<工具Id>.md`（一页一个工具）与 README 的「工具一览」表。
* 界面相关的改动再按第 6 节跑一次截图冒烟（`YLT_SHOT`）。
* 改完只在必要处顺手更新本文件；**版本号 / 变更记录（第 9 节）/ 自检项数**三处要一起改。

### A.2 硬性规则（违反 = 破坏项目或删掉用户数据）

| # | 规则 | 为什么 |
| --- | --- | --- |
| 1 | **数据目录在 exe 旁边**（`bin\Debug\net9.0-windows\Data`、`…\Config`），仓库根的 `Data/`、`Config/` 只放说明文件。**绝不要把运行时数据写到仓库根。** | 见第 3 节；早期版本踩过这个坑 |
| 2 | **绝不用 `<None CopyToOutputDirectory>` 往 exe 旁边拷 Data/Config 里的东西** | 构建的 IncrementalClean 会把输出目录里的同名文件当垃圾删掉 —— 而那里正是用户数据（第 5 节，删过用户的游戏和播放器） |
| 3 | `dotnet clean` / 手删 `bin`、`dist` = **删用户数据**（下载的游戏、配置都在里面），清理脚本别把 `Data`、`Config` 一起端了 | 同上 |
| 4 | **别用 PowerShell 的 `Get-Content`/`Set-Content` 改 XAML / 源码** | Windows PowerShell 5.1 按 GBK 读，中文会变乱码、`Title="…"` 的引号会被弄坏（XML 直接 MC3000）。用编辑工具改 |
| 5 | `build.ps1` / `publish.ps1` 以及任何 `.ps1`：**保持纯 ASCII，或存成 UTF-8 with BOM** | 同上，5.1 按 ANSI 解析含中文的脚本会报语法错误 |
| 6 | 数据目录里的**相对路径不能带 `..`、不能是绝对路径** | `ToolStorage` 故意抛 `ArgumentException` 防逃逸 |
| 7 | 配置类里**带 setter 的计算属性必须标 `[JsonIgnore]`** | 否则会被当成配置项写进 JSON |
| 8 | 后台线程改界面一律走 `Ui(...)` / `UiAsync(...)` | 直接赋值会抛"调用线程无法访问此对象" |
| 9 | 新增**顶层目录**时，同步改主项目 `DefaultItemExcludes` **和** `.gitignore` | 少改前者，目录里随便一个 `.cs` 都能把主程序编译搞崩 |
| 10 | 新增持久化入口如果绕过 `ToolConfigStore`，**自己接 `FlushAll`** | 程序退出 / 切换功能时框架只 Flush 它管的那部分 |
| 11 | 工具被切走时框架调 `NotifyClosed()`：**挂在缓存对象（配置对象等）上的事件记得在这里摘掉** | 配置对象按类型缓存，不摘就会每次打开多挂一个旧 VM |
| 12 | 想改配置的**即时性**（用户一敲键就刷新界面）时，属性必须写 `Set(ref 字段, 值)` | 自动属性只保证"最终会存下来"，不发通知（见 2.4 / 第 5 节） |
| 13 | **唯一允许的第三方 NuGet 包是 `OsuParsers`**（osu!mania 三件套解析 `.osu` 用）。别再引入第二个包；它首次需要联网还原，之后走本机 NuGet 缓存（`build.ps1` 离线也能命中） | 见 2.17；离线构建 / 发布不能悄悄多出依赖 |
| 14 | **`ParticleOverlay` 必须放在 Window 内的 Grid 里并铺满**（别塞进 StackPanel / 别写死宽高），且 `IsHitTestVisible="False"` | 它靠 `Window.GetWindow` 挂鼠标事件、靠 Grid 拉伸获得尺寸；塞错地方会塌成 0 或挡住点击（见 2.16） |

### A.3 别做 / 先问用户

* **`scripts/` 下的调试脚本会弹窗抢焦点**（`e2e-batch-rename-drag.ps1` 会真的动鼠标 2 秒）：跑之前确认用户没在忙别的（见 `scripts/README.md`）。
* **截图 / 界面验证必须用 `Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait`**：它是 GUI 子系统程序，PowerShell 直接跑不会等它，你会误判"截图没生成"。
* 自检里**不要调 `PlayEntry()`**（会真启动 FlashPlayer 窗口，无头自检卡死）；簿记入口 `MarkPlayed()` 是公开的，自检只调它。
* 别在业务代码里从 URL 反推本地路径（见 2.11 / 第 5 节，反推过一次就把文件写到游戏目录外面了）。
* 别重复引入第三方库：本项目刻意"无 NuGet 依赖"（唯一例外是 osu 解析器 `OsuParsers`，见 2.17），新轮子优先手写进 `Core/`（参考 `ScrollChaining`、`Crc32`、`ParticleEngine`、`ToolCatalog`）。

### A.4 本文件怎么查（按任务定位）

| 我要做的事 | 看哪节 |
| --- | --- |
| 搞清楚项目结构、有哪些项目、环境变量开关 | **0 / 1** |
| 用现成的轮子（存储 / 配置 / MVVM / FFmpeg / UI 样式） | **2.1 – 2.9.1** |
| 改某个具体功能（批量重命名 / Flash / 聊天室 / osu! / 播放器 / 图片阅读器） | **2.10 – 2.19**（每节都写了分层、约定、坑） |
| **给功能做多级分类 / 类别页 / 可点击面包屑** | **2.15** |
| **粒子效果 / 主页设置窗口** | **2.16** |
| **osu!mania 三件套（Key 转换 / LN 转换 / 等级计算）** | **2.17** |
| **棋牌：联机五子棋 / 联机象棋 / 房间协议 / 断线重连** | **2.18** |
| **连续看图 / 六种阅读方式 / 双页翻页 / 双击图片打开（文件关联）** | **2.19** |
| 找 Data / Config 放哪、目录长什么样 | **3** |
| **新增一个功能** | **4**（照抄 `Tools.Sample` 或 `Tools.BatchRename` 模板） |
| 排查某类毛病（存储、WPF、文件系统、构建、网络） | **5** |
| 跑验证（编译 / 自检 / 截图 / 发布冒烟） | **6** |
| 看某功能当前的 Id、选项存哪、数据在哪 | **7** |
| git 约定、哪些东西不入库 | **8** |
| 某个版本改了什么 | **9** |

> 第 0–9 节是稳定编号，**源码注释里到处用 `AGENTS.md 的 2.11 节` 这种引用指向它**（搜索 `AGENTS.md` 就能找到全部引用点）。
> 重排或删除章节编号会让这些引用失效——要改就一起改。
>
> ⚠️ **本文件约 2300 行 / 222 KB**：作为"工作区指令"自动注入时可能被上下文预算截断（只保留前半段）。
> 一旦发现后半段不见了，**用读取工具按行号接着读**；定位章节最稳的办法是搜索标题（`## 3.` / `### 2.17`），
> 别依赖行号（本文件一直在长，行号会漂）。

### A.5 常用命令速查

```powershell
# 构建 / 运行 / 发布
.\build.ps1                                        # 一键 restore + build（离线，走本机 NuGet 缓存）
dotnet run --project YuLiangTookit.csproj          # 直接跑主程序
.\publish.ps1                                      # 发布到 dist\YuLiangToolkit\（-SelfContained 自带运行时）

# 自检（退出码 0 = 全通过）
dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj

# 无头截图（不抢焦点，可后台跑）：先设工具 Id，再设输出 png
$env:YLT_SHOT_TOOL="chat-room"; $env:YLT_SHOT="shots\chat-room.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_SHOT_TOOL, Env:YLT_SHOT            # 用完清掉，否则后续启动都进截图模式

# git（中文文件名已配置不转义显示）
git status; git add -A; git commit -m "..."; git log --oneline --stat -3
```

全部调试用环境变量（`YLT_*`，不设就是正常启动）见第 0 节；截图 / 专项核对流程见第 6 节。

---

## 0. 项目速览

| 项 | 内容 |
| --- | --- |
| 技术栈 | .NET 9（`net9.0-windows`）+ WPF + 手写 MVVM；唯一第三方依赖 `OsuParsers`（MIT，osu 谱面解析/写回） |
| 版本 | `Directory.Build.props` 的 `<Version>`（状态栏显示的 v0.15.0 就是读它；**别在别处写死**） |
| 解决方案 | `YuLiangToolkit.sln`（13 个项目：主程序 + Core + 10 个功能 + 自检） |
| 主程序 | `YuLiangTookit.csproj`（注意：文件名少一个 `l`，是历史拼写）→ 自定义标题栏外壳、主页/类别页/侧边栏/面包屑、深色主题、窗口级粒子层 |
| 核心库 | `src/YuLiangToolkit.Core/`（工具契约、目录树、MVVM 基类、存储、配置、FFmpeg 封装、粒子引擎/覆盖层、通用 TCP 房间底座 `Core/Net`） |
| 功能 | `Tools.VideoToAudio`（视频转音频）、`Tools.MediaPlayer`（视频 / 音乐播放器）、`Tools.PictureViewer`（图片连续阅读器）、`Tools.BatchRename`（批量重命名）、`Tools.FlashDownloader`（Flash 下载器）、`Tools.ChatRoom`（联机聊天室）、`Tools.MapDownloader`（osu! 下图器）、`Tools.OsuMania`（osu! Key / LN / 等级三个工具）、`Tools.BoardGames`（联机五子棋 / 联机象棋）、`Tools.Sample`（框架示例，可删） |
| 数据/配置 | **在 exe 旁边**（`bin\Debug\net9.0-windows\Data`、`…\Config`）——见第 3 节；仓库根的 `Data/`、`Config/` 只剩说明文件 |
| 自检 | `tests/YuLiangToolkit.FrameworkSelfCheck/`（**1541 项**行为逐条验证，纯控制台）——见第 6 节 |
| 构建 | `.\build.ps1`（离线，走本机 NuGet 缓存；会设 `DOTNET_CLI_HOME` 到 `.dotnet-cli-home`） |
| 运行 | `dotnet run --project YuLiangTookit.csproj` 或直接跑 `bin\Debug\net9.0-windows\YuLiangTookit.exe` |

### 环境变量（开发调试用，不设就是正常启动）

| 变量 | 作用 |
| --- | --- |
| `YLT_NAV_TOOL=<工具Id>` | 启动后直接打开某个功能（如 `video-to-audio`） |
| `YLT_SHOT_TOOL=<工具Id>` | 同 `YLT_NAV_TOOL`，与 `YLT_SHOT` 配合 |
| `YLT_SHOT=<png路径>` | 渲染当前界面为 PNG 并退出（约 1.2 秒后自动截图退出）。**该模式下窗口不激活、不抢焦点**，可以放心在后台跑 |
| `YLT_HOME=<目录>` | 覆盖数据根目录（Data/Config 的父目录），测试时隔离用 |
| `YLT_HOME_SEARCH=<关键词>` | 主页启动时预置搜索词（截图验证“筛选后 / 无结果”状态用） |
| `YLT_NO_SERVER=1` | 打开「Flash 下载器」时不自动启动本地服务器；打开「联机聊天室」时不自动开局（自检 / 无头构造 VM 时避免抢端口） |
| `YLT_MEASURE=<文件>` | 打开「框架示例」时把文件列表表头/数据行的真实几何（宽度、列起点）写进文件，用来核对“表头与数据列有没有对齐” |
| `YLT_MEASURE_RESIZE=<宽>x<高>` | 配合 `YLT_MEASURE`：先缩到指定窗口尺寸再测（专门抓“一伸缩窗口就歪”的毛病） |
| `YLT_WHEEL_TEST=<文件>` | 打开「框架示例」时把内层列表/外层页面的滚动几何与滚动链判定写进文件，**每滚一下刷新一次**（核对"滚轮到底滚了谁"用） |
| `YLT_DRAG_DEBUG=<日志文件>` | 把「批量重命名」每次拖放判定（目标 + 插前/插后）写进文件（详见 2.10） |
| `YLT_FLASH_SELECT=<文件名>` | 打开「Flash 下载器」时自动选中列表里的那一行（截图检查选中高亮用） |
| `YLT_CHAT_HOME=<目录>` | 覆盖「联机聊天室」的会话根目录（默认 `Data\chat-room`）；自检也认它，指定了就不清掉 |
| `YLT_CHAT_DEMO=1` | 打开「联机聊天室」时往界面塞几条演示消息（只为截图看样式，不影响真实连接） |
| `YLT_CHAT_SIZE=<宽>x<高>` | 打开「联机聊天室」时先把窗口改成这个尺寸（底部三栏在默认 820 高里看不到，核对布局用） |
| `YLT_CHAT_SCROLL=<像素>` | 配合 `YLT_CHAT_SIZE`：把页面滚到指定位置再截图（专门看底部那几栏） |
| `YLT_CHAT_TRACE=<文件>` | 自检里把聊天室的状态提示 / 收发消息 / 断线原因写进文件（排错用） |
| `YLT_CHAT_NET_TRACE=<文件>` | 把聊天室每个收发帧（类型 + 载荷长度 + 读循环退出原因）写进文件（排协议问题用） |
| `YLT_MAP_DEMO=1` | 打开「osu! 下图器」时塞 12 行假结果（超长标题 / 无上架日期 / 不同 CS 都有），只为看版式与核对表头对齐，**不发任何请求** |
| `YLT_PLAYER_DEMO=1` | 打开「视频 / 音乐播放器」时塞 6 行假列表（音频 / 视频都有），只为看列表与封面版式；点了会提示“文件不在了” |
| `YLT_PLAYER_SETTINGS=1` | 打开「视频 / 音乐播放器」时直接把设置面板展开（截图核对那一屏用） |
| `YLT_PLAYER_FULLSCREEN=1` | 打开「视频 / 音乐播放器」时启动就进全屏（截图核对全屏版式用；要配 `YLT_PLAYER_DEMO`） |
| `YLT_PLAYER_HIDE_CONTROLS=1` | 再配合上面那个：进全屏后先把控制条藏起来（核对「鼠标不动时只剩画面」那一帧） |
| `YLT_PLAYER_MEASURE=<文件>` | 打开「视频 / 音乐播放器」1 秒后，把全屏时各层的真实尺寸（窗口 / 播放列 / 视频 Border / 控制条 / 封面）与底部命中元素写进文件，核对「底部黑条 / 控制条被裁」用 |
| `YLT_PICTURE_DEMO=1` | 打开「图片连续阅读器」时用 WPF 现画 8 张演示图（`Data\picture-viewer\demo\`，已存在就复用）并加载，截图/冒烟用，不读真实图片 |
| `YLT_PICTURE_MODE=vertical\|ltr\|rtl\|spread-ltr\|spread-rtl\|single` | 打开「图片连续阅读器」时直接指定阅读方式（连续：纵向 / 横向左→右 / 横向右→左；翻页：双页左→右 / 双页右→左 / 单页），截图核对六种版式用 |
| `YLT_PICTURE_PAGE=<第几张>` | 翻页 / 单页模式启动后跳到第 N 张（1 基），截图核对页码 / 双页配对 / 最后一屏留空用 |
| `YLT_PICTURE_ZOOM=<百分比>` | 打开「图片连续阅读器」时直接指定缩放（如 `200`），截图核对放大后的排版 / 跨轴滚动 |
| `YLT_PICTURE_CROSS=0~1` | 配合缩放：加载完先把**跨轴**偏移移到指定比例（`0.5` = 中间），截图核对「放大后能左右 / 上下拖」 |
| `YLT_PICTURE_SCROLL=0~1` | 配合演示：加载完先按阅读比例滚到指定位置（`1` = 滚到最末尾），核对「左右方向到底从哪头开始」 |
| `YLT_PICTURE_ANCHOR=1` | 把中键锚点指示器画在视口正中间（截图核对箭头方向 / 样式） |
| `YLT_PICTURE_SETTINGS=1` | 直接展开右侧设置面板（截图核对那一屏） |
| `YLT_PICTURE_MEASURE=<文件>` | 加载完约 0.9 秒后把真实几何写进文件：模式 / 视口与内容尺寸 / 物理偏移与可滚范围 / **阅读偏移**（含 `fromPhysical` 反算值）/ 槽位长度 / 首个子元素是不是第 1 张 / 锚点与箭头可见性 |
| `YLT_PICTURE_SWITCH_MODE=vertical\|ltr\|rtl` | 配合 `YLT_PICTURE_MEASURE`：先写一份 `*.before` 几何，再切到指定滚动方式、0.35 秒后写正式文件（核对「换方向保持阅读位置」用） |
| `YLT_PICTURE_INPUT_TRACE=<文件>` | 把每次鼠标按下 / 松开 / 丢失捕获（按钮、事件源类型、是否来自视口、谁握着捕获、处理后状态）追加进文件；排查「某个控件点不动」先看它 |
| `YLT_TASKBAR_TRACE=<文件>` | 把「全屏藏任务栏」每次藏 / 还的细节（hwnd、Win32 错误码、是否仍然可见）写进文件；**启动时的完整性自检也会追加到这里**。**全屏时任务栏没藏住先看它**：错误码 `5` = 完整性级别不够（见 2.14） |
| `YLT_MINI_DEMO=1` | 启动时先开一下播放器再回主页，制造「正在后台播放」的状态 —— 左下角迷你播放条会出现（截图核对用） |
| `YLT_NAV_CATEGORY=<路径>` | 启动后直接进某个类别页（如 `音游工具` 或任意深度的 `音游工具/谱面处理`；见 2.15） |
| `YLT_OPEN_SETTINGS=1` | 启动后直接打开「主页设置」窗口（截图核对粒子开关用；见 2.16） |
| `YLT_PARTICLE_DEMO=1` | 粒子覆盖层进入演示模式：没有鼠标输入也持续在窗口中部生成粒子（无头截图用，见 2.16） |
| `YLT_OSU_DEMO=1` | 打开三个 osu 工具时自动准备一份合成 4K 谱面/示例结果（截图与冒烟用，不发网络请求；见 2.17） |
| `YLT_GOMOKU_DEMO=1` | 打开「联机五子棋」时造一局演示棋（同机双人 12 手 + 禁手点），截图/冒烟用，不联网 |
| `YLT_XIANGQI_DEMO=1` | 打开「联机象棋」时造一局演示棋（同机双人 11 步中盘），截图/冒烟用，不联网 |
| `YLT_XIANGQI_SETUP=1` | 打开「联机象棋」时直接进摆棋并套用「让单马」（截图核对摆棋卡用） |
| `YLT_BOARD_DEMO_PAUSE=1` | 棋牌演示「对局暂停 / 正在重连」状态（截图核对暂停 UI 用；不影响真实联机） |
| `YLT_BOARD_RECONNECT_MS` / `YLT_BOARD_RECONNECT_TRIES` | 自检加速断线重连重试（默认 2000ms × 30 次，正常使用不要设） |
| `YLT_WINDOW_SIZE=<宽>x<高>` | 启动后把窗口改成指定尺寸（如 `1024x700`，截图核对最小窗口版式用） |

---

## 1. 目录地图（哪里该放什么）

```
YuLiangToolkit.sln
YuLiangTookit.csproj              # 主程序（DefaultItemExcludes 排除了 src/ Data/ Config/ tests/ MapDownloader/ PictureView/）
AGENTS.md                         # ← 本文件：给 AI / 未来自己的开发指南（轮子速查 · 约定 · 踩坑）
README.md                         # 面向使用者的说明：工具一览表 + 快速开始（**保持短**）
docs/                             # 面向使用者的详细文档（一页一个工具，GitHub 上就是 wiki 的效果）
│  ├─ README.md                   #   文档索引
│  ├─ shell.md                    #   主页搜索/排序/标签 · 多级分类/面包屑 · 主页设置 · Data/Config 目录约定
│  ├─ tools/<工具Id>.md           #   每个工具一页：能做什么 / 怎么用 / 设置项 / 数据在哪 / 注意
│  └─ assets/100-percent-ai-coding.png   # README 标题旁那行手写体徽标（System.Drawing 画的）
build.ps1                         # 一键还原 + 构建（保持纯 ASCII，见第 8 节）
publish.ps1                       # 一键发布到 dist\YuLiangToolkit\（-SelfContained 可打成自带运行时）
global.json                       # SDK 版本锁定（9.0.317）
Directory.Build.props / .targets  # 公共编译属性 + 隐式 using 补充
.gitignore / .gitattributes       # 忽略规则 / 换行与二进制约定（见第 8 节）
Data/                             # 只有说明文件（真正的运行时 Data 在 exe 旁边，见第 3 节）
Config/                           # 同上，只有说明文件
dist/                             # publish.ps1 的发布输出【不入库，gitignore】
MapDownloader/                    # 改写「osu! 下图器」时参考的原版 WinForms 小项目【不入库，见第 8 节】
PictureView/                      # 移植「图片连续阅读器」时参考的原版小项目（已移到桌面）【不入库，见第 8 节】
tests/YuLiangToolkit.FrameworkSelfCheck/   # 轮子自检控制台程序
scripts/                          # 开发/调试脚本（UI 自动化、截图分析）——会弹窗抢焦点，慎跑
                                  #   e2e-batch-rename-drag.ps1 是唯一覆盖“WPF 拖放事件”那层的验证（会真的动鼠标 2 秒）
shots/                            # 开发调试截图输出【png/txt 不入库】
testdata/                         # 手工测试素材（如 测试视频.mp4）
src/
├─ YuLiangToolkit.Core/           # 核心库（所有功能都引用它）
│  ├─ Tools/      ITool.cs · ToolDiscovery.cs · IBackgroundTool.cs（IBackgroundTool + IFullscreenHost）
│  ├─ Mvvm/       ObservableObject.cs · RelayCommand.cs · AsyncRelayCommand.cs
│  │              ToolViewModelBase.cs · OptionItem.cs · ScrollChaining.cs
│  ├─ Storage/    AppPaths.cs · RuntimeReadme.cs · ToolStorage.cs · JsonFileStore.cs · FileSize.cs
│  ├─ Configuration/ ToolConfig.cs · ToolConfigStore.cs · DynamicToolConfig.cs
│  ├─ Net/        RoomProtocol.cs · RoomFrame.cs · RoomLink.cs · RoomHost.cs · RoomClient.cs（通用 TCP 房间底座，见 2.18）
│  ├─ Services/   AppSettingsService.cs · FFmpegLocator.cs · FFmpegRunner.cs · TaskbarHider.cs · IntegrityCheck.cs
│  ├─ Particles/  Particle.cs · ParticleEngine.cs（纯逻辑粒子引擎，见 2.16）
│  ├─ Controls/   ParticleOverlay.cs（WPF 覆盖层：OnRender 画粒子 + 鼠标/渲染事件）
│  └─ Models/     AppSettings.cs（全局设置，只放跨功能项）
│  Tools/ 里还有 ToolCatalog.cs（任意层级分类目录树 + 面包屑，见 2.15）
├─ YuLiangToolkit.Tools.VideoToAudio/      # 功能 1
│  ├─ VideoToAudioTool.cs · VideoToAudioConfig.cs · LegacyAppSettings.cs
│  ├─ ViewModels/VideoToAudioViewModel.cs · Views/VideoToAudioView.xaml
├─ YuLiangToolkit.Tools.BatchRename/       # 功能 2：批量重命名（纯逻辑与界面分离得最干净，可当第二个模板）
│  ├─ BatchRenameTool.cs · BatchRenameConfig.cs      # 契约 + 规则清单 + 使用偏好
│  ├─ RenameRule.cs                                  # ★一条规则（种类 + 作用范围 + 参数 + 摘要文案）
│  ├─ RenameEngine.cs                                # ★纯逻辑：按规则清单算新名字 + 冲突检测（自检直接断言它）
│  ├─ RenameExecutor.cs                              # ★纯逻辑：两阶段改名 / 撤销（只碰文件系统）
│  ├─ RenameHistory.cs                               # 改名流水账（撤销的依据，落盘到 Data\batch-rename\）
│  ├─ ViewModels/BatchRenameViewModel.cs · RenameItem.cs · RenameRuleItem.cs
│  └─ Views/BatchRenameView.xaml(.cs)                # 规则拖动排序 / 拖入文件 / 多选同步在代码后置里
├─ YuLiangToolkit.Tools.Sample/            # 示例 + 新功能模板（可整目录删除）
│  ├─ SampleTool.cs · SampleConfig.cs
│  ├─ ViewModels/SampleViewModel.cs · Views/SampleView.xaml(.cs) · Views/ScrollBarWidthConverter.cs
├─ YuLiangToolkit.Tools.FlashDownloader/   # 功能 4：Flash 下载器（最“重”的一个功能，见 2.11）
│  ├─ FlashDownloaderTool.cs · FlashDownloaderConfig.cs
│  ├─ Core/     GameMeta.cs                 # GameSite / GameContentKind / GameMeta（解析结果）+ GameSites
│  │            GamePageParser.cs           # ★纯逻辑：站点正则 / 文件名清洗 / 编码 / 批量路径校验
│  │            GameUrlInfo.cs              # ★纯逻辑：网址 → 站点 + 后缀（域名认不出按字母兜底）
│  │            SwfNetPathIni.cs            # ★纯逻辑：文件夹标记 SWFNetPath.ini 的读写与容错
│  │            GameEntry.cs                # ★纯逻辑：游戏目录里一条记录（命名规则 + 能否游玩/转换）
│  │            GamesLibrary.cs             # 游戏目录：扫列表 / 删除 / 装入文件夹 / 改为 htm
│  │            BatchDownloadPlanner.cs     # ★纯逻辑：批量下载预处理（合规→去重→已存在）+ 统计文本
│  │            GameFilter.cs               # ★纯逻辑：游戏列表的搜索 / 排序
│  ├─ Engine/   FlashHttp.cs                # HttpClient 封装（UA/Referer/超时/404 分开报）
│  │            GameDownloader.cs           # ★网址 → 游戏信息 → 落盘（swf 散放 / htm 建文件夹）
│  │            RelatedFileDownloader.cs    # 批量下载执行 + 自由下载工具
│  │            FlashPlayerLocator.cs       # 找 FlashPlayer.exe / ruffle.exe 并拷进 tools
│  ├─ Web/      FlashWebServer.cs           # 本地 HTTP 服务器 + 缺失文件自动下载 + 内置播放页模板
│  ├─ ViewModels/ FlashDownloaderViewModel*.cs（3 个 partial）· GameItem.cs · RelatedRow
│  ├─ Views/    FlashDownloaderView.xaml(.cs)
│  └─ View/Web/ frame.htm · FlashPage.htm · h5api-replacement.js   # 嵌入资源，首次运行释放到 Data
├─ YuLiangToolkit.Tools.ChatRoom/          # 功能 5：联机聊天室（要联机 + 要开 TCP 监听，见 2.12）
│  ├─ ChatRoomTool.cs · ChatRoomConfig.cs   # 契约 + 全部选项（昵称 / 端口 / 流量上限 / 发图压缩…）
│  ├─ Protocol/ ChatProtocol.cs             # ★纯逻辑：帧格式 / 常量 / 地址解析 / 协议说明释放
│  │            ChatFrameHeader.cs          # ★纯逻辑：包头的 JSON（类型 + 成员 + 载荷长度 + extra）
│  │            ChatMessages.cs             # 线上 DTO：ChatMember / Hello / Text / TransferInfo …
│  │            chat-protocol.json          # 嵌入资源：人能读的协议说明，首运行释放到 Data
│  ├─ Traffic/  TrafficMeter.cs             # ★纯逻辑：单次流量上限 + 用量/速率快照 + 发前预检
│  │            Crc32.cs                    # ★纯逻辑：边收边算的校验（标准向量自检）
│  ├─ Sessions/ ChatSessionNaming.cs        # ★纯逻辑：会话文件夹命名 / 大类归目录 / 文件名清洗
│  │            ChatSessionStore.cs         # 会话文件夹 + chat-log.jsonl + session.json 的读写
│  ├─ Media/    ImageUtil.cs                # 缩略图（含 EXIF 方向）——两端各自本地压，不占流量
│  ├─ Net/      ChatLink.cs                 # ★一条 TCP 连接：帧读写 / 心跳 / 排队发文件 / 流量计量
│  │            ChatRoomConnection.cs       # 主机与客户端的共同部分（落盘 / 消息 / 文件收发）
│  │            ChatHost.cs                 # 主机：监听 + 多客户端 + 中转广播 + 移出成员
│  │            ChatClient.cs               # 客户端：连 IP/域名:端口 + 握手 + 收发
│  │            ChatRoomSession.cs          # 把上面这些拼成“一次会话”的门面
│  ├─ ViewModels/ ChatRoomViewModel*.cs（3 个 partial）· ChatMessageItem.cs（含 FileProgress）· ChatMemberItem.cs
│  └─ Views/    ChatRoomView.xaml(.cs)
├─ YuLiangToolkit.Tools.MapDownloader/     # 功能 6：osu! 下图器（从仓库根的 WinForms 小工具改写，见 2.13）
│  ├─ MapDownloaderTool.cs · MapDownloaderConfig.cs   # 契约 + 凭据/搜索条件/下载目录
│  ├─ Core/     OsuModels.cs               # osu! API 的 DTO + 搜索请求模型 + OszItem
│  │            OsuQuery.cs                # ★纯逻辑：搜索查询串 / URL / 换 Token 表单
│  │            BeatmapFilter.cs           # ★纯逻辑：日期与 Key 数本地筛选 + 展示文案
│  │            OszNaming.cs               # ★纯逻辑：文件名清洗 / {Id} {标题}.osz / 镜像地址
│  │            OsuOptions.cs              # 模式 / 状态 / 排序 / Key 数的下拉选项
│  ├─ Engine/   OsuApiClient.cs            # 换 Token + 搜谱面集（失败返回结果对象，不抛异常）
│  │            OszDownloader.cs           # 并发下 .osz（跳过已存在 / 取消不留半个文件 / 统计）
│  ├─ ViewModels/ MapDownloaderViewModel*.cs（2 个 partial）· BeatmapSetItem.cs
│  └─ Views/    MapDownloaderView.xaml(.cs) · ScrollBarWidthConverter.cs
├─ YuLiangToolkit.Tools.OsuMania/          # 功能 8：osu!mania 三件套（转 K / 转 LN / 等级计算，见 2.17）
│  ├─ OsuKeyConverterTool.cs · OsuKeyConverterConfig.cs
│  ├─ OsuLnTransformerTool.cs · OsuLnTransformerConfig.cs
│  ├─ OsuLevelCalculatorTool.cs · OsuLevelCalculatorConfig.cs
│  ├─ OsuDemoData.cs                       # YLT_OSU_DEMO=1 用的合成谱面/示例数据
│  ├─ Core/     OsuManiaBeatmap.cs         # ★工作模型（ManiaNote / TimingPoint / Metadata）
│  │            OsuBeatmapFile.cs          # ★OsuParsers 解析桥 + 保存（按元数据拼文件名）
│  │            KeyConverterEngine.cs      # ★纯逻辑：低 k → 高 k（KeyConverter 算法）
│  │            LnTransformerEngine.cs     # ★纯逻辑：14 档 LN 转换（LNTransformer 算法）
│  │            LevelCalculator.cs         # ★纯逻辑：星数 → 等级（三段拟合公式）
│  │            OsuFileBatch.cs · OsuTransformUtils.cs
│  │            Difficulty/ StarRatingCalculator.cs · EasyObject.cs · Utils.cs（星数算法）
│  ├─ ViewModels/ OsuTransformViewModelBase.cs + 三个工具 VM + OsuFileItem.cs · LevelRowItem.cs
│  └─ Views/    OsuKeyConverterView.xaml(.cs) · OsuLnTransformerView.xaml(.cs) · OsuLevelCalculatorView.xaml(.cs)
├─ YuLiangToolkit.Tools.BoardGames/        # 功能 9：棋牌：联机五子棋 / 联机象棋（见 2.18）
│  ├─ Shared/   BoardProtocol.cs · BoardMessages.cs · BoardRoomSession.cs   # ★房间协议 / 会话（握手、转发、断线分类）
│  │            ViewModels/ BoardGameRoomViewModelBase.cs（房间生命周期 / 聊天 / 悔棋求和认输 / 断线重连）
│  │            Views/ RoomPanel.xaml · ChatPanel.xaml（两游戏共用的房间与聊天面板）
│  ├─ Gomoku/   GomokuTool.cs · GomokuConfig.cs · GomokuBoard.cs · GomokuForbidden.cs · GomokuGame.cs  # ★五子棋引擎 + 禁手
│  │            ViewModels/GomokuViewModel.cs · Views/GomokuView.xaml(.cs) · GomokuBoardView.cs（自绘棋盘）
│  └─ Xiangqi/  XiangqiTool.cs · XiangqiConfig.cs · XiangqiPiece.cs · XiangqiBoard.cs · XiangqiRules.cs
│               XiangqiGame.cs · XiangqiSetup.cs · XiangqiNotation.cs（自由摆棋 / 让子 / 让先 / 中文记谱）
│               ViewModels/XiangqiViewModel.cs · Views/XiangqiView.xaml(.cs) · XiangqiBoardView.cs（自绘棋盘）
├─ YuLiangToolkit.Tools.MediaPlayer/       # 功能 7：视频 / 音乐播放器（MediaElement + 可换假播放器，见 2.14）
│  ├─ MediaPlayerTool.cs · MediaPlayerConfig.cs · MediaPlaylistState.cs
│  ├─ MediaTypes.cs                        # ★纯逻辑：扩展名 → 音频 / 视频 + 读取方式过滤
│  ├─ MediaTime.cs                         # ★纯逻辑：时间文本 / 进度比例 / 快进快退夹取（0.1 秒精度）
│  ├─ MediaScanner.cs                      # ★纯逻辑：扫文件夹（递归 / 过滤 / 自然排序）
│  ├─ PlaylistMerger.cs                    # ★纯逻辑：追加去重 / 覆盖 + 统计文案
│  ├─ PlayQueue.cs                         # ★纯逻辑：列表循环 / 单曲循环 / 随机（洗牌一轮不重复）
│  ├─ MediaItem.cs                         # 列表行模型（不碰播放器）
│  ├─ IMediaPlayback.cs                    # 播放器抽象：界面用 MediaElement 实现，自检用假的
│  ├─ ViewModels/ MediaPlayerViewModel.cs + .Playlist.cs + .Playback.cs（3 个 partial）
│  └─ Views/    MediaPlayerView.xaml(.cs) · MediaElementPlayback.cs   # MediaElement 适配器
├─ YuLiangToolkit.Tools.PictureViewer/     # 功能 10：图片连续阅读器（连续长带 / 双页翻页 / 单页，见 2.19）
│  ├─ PictureViewerTool.cs · PictureViewerConfig.cs
│  ├─ PictureScrollMode.cs                 # ★纯逻辑：六种阅读方式分类 + 翻页滚轮动作 + 文案
│  ├─ PictureScrollMath.cs                 # ★纯逻辑：滚轮 / 中键锚点 / 拖拽 / 比例定位 / 统一缩放尺寸
│  ├─ PicturePaging.cs                     # ★纯逻辑：翻页配对（日式镜像）/ 页号步进 / 槽位排版 / 平移夹取
│  ├─ PictureFileAssociation.cs            # 双击图片打开：HKCU 文件关联 + 启动请求 + 关联状态说明
│  ├─ PictureAnchorMachine.cs              # ★纯逻辑：中键「单点留下 / 按住松手消失」状态机
│  ├─ PictureScanner.cs                    # ★纯逻辑 + 磁盘：扩展名过滤 / 自然排序 / 只读文件头拿尺寸
│  ├─ PictureDemoData.cs                   # YLT_PICTURE_DEMO=1 现画 8 张演示图
│  ├─ ViewModels/ PictureViewerViewModel.cs（配置 / 页清单 / 六种方式选项 / 无头可测的加载流程）
│  └─ Views/    PictureViewerView.xaml(.cs) + .Paged.cs  # 连续长带 / 翻页视图 / 翻页动画 / 鼠标键盘
App.xaml / App.xaml.cs             # 全局样式资源 + 启动/退出钩子（截图模式设为不抢焦点）
MainWindow.xaml(.cs)               # 自定义标题栏外壳
Models/ShellConfig.cs              # 外壳配置：主页排序偏好 + 各功能上次使用时间（存 Config/shell/）
ViewModels/MainViewModel.cs        # 侧边栏/主页/功能切换、搜索词与排序状态
ViewModels/ToolCard.cs             # 主页卡片模型（含预拼搜索索引、标签、上次使用文案）
ViewModels/ToolFilter.cs           # 主页过滤/排序/标签收集（纯逻辑，可自检）
Views/HomeView.xaml(.cs)           # 主页：搜索框 + 排序下拉 + 标签条 + 卡片墙 + 空状态
Views/MiniPlayerPanel.xaml(.cs)    # 左下角迷你播放条（后台播放时浮出，标题放不下才来回滚）
Converters/                        # InverseBoolConverter · InverseBoolToVisibilityConverter · EmptyStringToVisibilityConverter
```

---

## 2. 轮子总表

### 2.1 工具契约与发现（原有）

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `ITool` | `Core/Tools/ITool.cs` | 功能契约：`Id` `Name` `Description` `EmojiIcon` `Category` `Order` `CreateView()`，以及**可选**的 `Tags`（主页标签搜索用，接口有默认实现，见下）与**可选**的 `CategoryPath`（多级分类目录，默认空 = 挂在主页根层，见 2.15） |
| `ToolDiscovery.Discover()` | `Core/Tools/ToolDiscovery.cs` | 反射扫描本程序目录 `YuLiangToolkit.*.dll` + `plugins/*.dll`，实例化所有有公共无参构造的 `ITool`，按 `Order` 排序 |
| `ToolCatalog.Build(tools, categories?)` | `Core/Tools/ToolCatalog.cs` | **纯逻辑**：按每个工具的 `CategoryPath` 建任意层级的目录树，生成类别页/面包屑/计数（见 2.15） |

```csharp
public class FlashDownloaderTool : ITool
{
    public string Id => "flash-downloader";     // 同时是 Data/Config 下的小文件夹名，定了就别改
    public string Name => "Flash 下载器";
    public string Description => "主页卡片上的一句话说明";
    public string EmojiIcon => "⚡";
    public string Category => "媒体";            // 分类标签：算作第一个标签（不写 CategoryPath 时就挂在根层）
    public IReadOnlyList<string> CategoryPath => Array.Empty<string>();  // 可选：多级分类路径，如 ["音游工具"]
    public IReadOnlyList<string> Tags => new[] { "flash", "swf", "下载" };  // 可选：主页蓝字 + 搜索命中
    public int Order => 20;                      // 越小越靠前（主页“默认顺序”排序用）
    public FrameworkElement CreateView() => new FlashDownloaderView { DataContext = new FlashDownloaderViewModel() };
}
```
> `Tags` / `CategoryPath` 都是**接口默认实现**（返回空数组）：老工具不写它们照样编译运行；写了就自动出现在主页标签条 / 目录树里。

### 2.2 MVVM 基类（原有）

| 轮子 | 签名要点 |
| --- | --- |
| `ObservableObject` | `protected void OnPropertyChanged([CallerMemberName] string?)` · `protected bool Set<T>(ref T field, T value, [CallerMemberName] string?)` |
| `RelayCommand` | `new RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)`；`CanExecuteChanged` 挂在 `CommandManager.RequerySuggested` 上 |
| `AsyncRelayCommand` | `new AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)`；**执行期间自动防重入**（`CanExecute` 返回 false），异常写 Debug 输出 |

### 2.3 数据存储

| 轮子 | 用途 |
| --- | --- |
| `AppPaths`（静态） | 解析 Data/Config 根目录（**默认就在 exe 旁边**），拼某个功能的小文件夹路径 |
| `RuntimeReadme`（静态） | 首次运行时往 Data/Config 里各写一份「怎么用这两个文件夹.md」（只写缺失的，不覆盖用户改动） |
| `ToolStorage` | 功能数据文件夹门面：列文件 / 读写 / 导入 / 打开资源管理器 |
| `StorageFile`（record） | 数据文件信息：`FullPath` `RelativePath` `Name` `SizeBytes` `LastWriteTime` + 计算属性 `SizeText` `ModifiedText` `Extension` |
| `JsonFileStore`（静态） | 统一的 JSON 读写：**原子写**、容错读（损坏自动备份并回退默认值）、`camelCase` + 中文不转义 |
| `FileSize.Format(long)` | 字节 → `"1.5 MB"` |

```csharp
AppPaths.Root                 // 数据根目录（Data 与 Config 的父目录）
AppPaths.DataRoot             // <Root>\Data
AppPaths.ConfigRoot           // <Root>\Config
AppPaths.ToolDataDir("flash-downloader")     // <Root>\Data\flash-downloader（只拼路径）
AppPaths.ToolConfigDir("flash-downloader")   // <Root>\Config\flash-downloader
AppPaths.EnsureCreated()      // 建好 Data / Config（App 启动时已调用）
AppPaths.Describe()           // "…\bin\Debug\net9.0-windows　[程序目录（exe 同级）]"（状态栏在显示这个）
```

**数据根目录解析顺序**（`AppPaths` 内部，先命中先用，结果进程级缓存）：

1. 环境变量 `YLT_HOME`（自检 / 特殊部署用，指向哪就用哪）；
2. **exe 所在目录**（默认，即 `bin\Debug\net9.0-windows\` 或发布出来的目录）；
3. `%AppData%\YuLiangToolkit`——只有 exe 目录**不可写**时（例如装在 `C:\Program Files`）才退到这里。

> ⚠️ **历史坑**：早期版本在“开发态”会向上找 `YuLiangToolkit.sln` 并把数据写到**仓库根**，
> 导致测试 exe 时数据不在 exe 旁边、发布也就无从谈起。v0.4.1 起已改成上面这套（仓库里的
> `Data/`、`Config/` 只剩说明文件，不再当运行时目录）。
> ⚠️ 另外 `AppPaths.Root` 是**进程级缓存**：进程里改 `YLT_HOME` 对已解析过的进程无效。

```csharp
// ToolStorage：功能数据文件夹（推荐入口）
var data = ToolStorage.For("flash-downloader");        // → Data\flash-downloader\
data.EnsureCreated();                                  // 首次写入时才真正建目录
string p   = data.PathOf("player", "ruffle.swf");      // 组装路径（不检查存在）；也支持 PathOf("a/b.swf")
bool has   = data.FileExists("a.swf");
long size  = data.GetSize("a.swf");                    // 不存在返回 -1
var files  = data.List("*.swf", recursive: true);      // IReadOnlyList<StorageFile>，按相对路径排序
long total = data.TotalSize();
var f      = data.Import(@"D:\下载\game.swf");          // 拷进来；重名自动变 "game (1).swf"
string? wp = data.WriteText("说明.txt", "内容");         // 自动建子目录，返回完整路径；失败返回 null
string? wb = data.WriteBytes("x.bin", bytes);
string? rt = data.ReadText("说明.txt");                 // 读不到返回 null
byte[]? rb = data.ReadBytes("x.bin");
bool ok    = data.Delete("x.bin");                     // ClearAll() 清空整个功能数据目录
data.OpenInExplorer();                                 // 打开该功能的数据文件夹
data.RevealInExplorer("a.swf");                        // 在资源管理器里选中它
```
> ⚠️ 相对路径带 `..` 或写绝对路径会抛 `ArgumentException`（防止逃出功能自己的数据目录）；其它 IO 失败一律返回 `null`/`false`/空列表，不抛异常。

```csharp
// JsonFileStore：需要自己存 JSON（不是配置项）时用
var model = JsonFileStore.Read<MyModel>(path, fallback: null);  // 损坏 → 备份成 *.bad-时间戳.json 并返回 fallback
JsonFileStore.Write(path, model);                               // 原子写：先 .tmp 再替换
JsonFileStore.Serialize(obj);                                   // 对象 → 缩进 JSON 字符串
```

### 2.4 功能配置（**最常用**）

| 轮子 | 用途 |
| --- | --- |
| `ToolConfig`（抽象基类） | 强类型配置：**属性一改就自动写盘**，无需任何 Save 调用 |
| `ToolConfigStore` | 一个功能一份（或多份）配置文件的加载/保存/Flush/事件 |
| `DynamicToolConfig` | 动态键值配置：不建类也能存，XAML 直接 `{Binding Dyn[键名]}` |
| `OptionItem<T>` / `OptionItem` | 下拉框选项（值 + 显示文本分离，配置文件里存干净的值） |
| `ToolViewModelBase` | 功能 VM 基类：一次备好配置、数据目录、忙碌/进度/日志/跨线程调度 |

```csharp
// ① 声明配置类：自动属性就行（真的能存，别怀疑）
public sealed class FlashDownloaderConfig : ToolConfig
{
    public string SaveFolder { get; set; } = "";      // Textbox
    public bool AutoRename { get; set; } = true;      // Checkbox
    public int TimeoutSeconds { get; set; } = 30;
    public string Mode { get; set; } = "标准";         // 下拉框（配合 OptionItem）

    [JsonIgnore] public bool HasFolder => SaveFolder.Length > 0;  // 只读计算属性：不会被写进文件
}
```
```csharp
// ② 在 VM 里加载（一行；Config\flash-downloader\config.json）
Cfg = LoadConfig<FlashDownloaderConfig>();
```
```xml
<!-- ③ XAML 直接双向绑定：改一下就自动存，别写保存按钮 -->
<TextBox  Text="{Binding Cfg.SaveFolder, UpdateSourceTrigger=PropertyChanged}" />
<CheckBox IsChecked="{Binding Cfg.AutoRename}" />
<ComboBox ItemsSource="{Binding ModeOptions}" DisplayMemberPath="Label"
          SelectedItem="{Binding SelectedMode}" />
```

**关于“怎么才算即时”**：后台每 400ms 把配置序列化一次并与上次落盘内容比对，不同就原子写盘。
所以：自动属性 `{ get; set; }` 能用；用 `Set(ref _f, value)` 会额外发界面通知（**需要联动刷新时用**，
例如 `Mode` 变了要让 `IsMp3Mode` 单选按钮跟着变）。工具关闭/切换、程序退出时都会强制 Flush 一次，不丢改动。

```csharp
// ToolConfigStore：需要手动控制时
var store = ToolConfigStore.For("flash-downloader");            // fileName 默认 config.json，可传第二份
store.FilePath / store.Directory / store.Exists / store.IsDirty / store.LastSavedAt / store.LastError
var cfg  = store.Load<FlashDownloaderConfig>(() => 从老配置迁移());  // 首次运行才调用工厂
store.Track(cfg);                    // 接管一个已构造好的配置对象
store.SaveNow();                     // 立刻写（无论是否有改动）
store.Flush();                       // 有改动才写
store.StartWatching() / StopWatching();  // 开/关后台自动保存（切换功能时基类已处理）
store.OpenInExplorer() / DeleteFile();   // 打开配置文件夹 / 删除配置（= 恢复默认值）
store.Saved += (_, _) => { };         // 每次写盘成功后触发（可能在后台线程，记得 Ui(...)）
store.SaveFailed += (_, msg) => { };  // 写盘失败（只读盘等）
ToolConfigStore.FlushAll();           // 退出前落盘所有配置（App 已接好）
```

```csharp
// DynamicToolConfig：不想建类时（Config\<功能名>\options.json）
var dyn = DynamicToolConfig.Load("flash-downloader");   // 第二参数可换文件名，默认 options.json
dyn.SetValue("lastUrl", "https://…");                   // 传 null = 删除该键
int  t = dyn.GetInt("timeoutSeconds", 10);
bool b = dyn.GetBool("autoRename");
string s = dyn.GetString("note");
double d = dyn.GetDouble("ratio");
if (dyn.TryGet<int>("retry", out var retry)) { }
dyn.Contains("note") / dyn.Remove("note") / dyn.Clear() / dyn.Keys / dyn.ToJson();
object? v = dyn["anyKey"];                              // 索引器：bool/long/double/string 自动还原
```

```csharp
// 推荐用法：声明默认值（键缺失时补齐并落盘）
Dyn = LoadDynamicConfigWithDefaults(new Dictionary<string, object?>
{
    ["autoRename"] = true,     // 声明过 → 配置文件里一开始就有这一项（自带“有哪些选项”的说明）
    ["lastUrl"] = "",          // 且绑 CheckBox 时不会因“键不存在 → null”导致第一次点击没反应
});
// 也可以直接用静态方法： DynamicToolConfig.LoadWithDefaults(toolId, defaults, fileName)
```
```xml
<!-- XAML 绑动态配置的索引器（值一改就存） -->
<CheckBox IsChecked="{Binding Dyn[autoRename], TargetNullValue=False}" />
<TextBox  Text="{Binding Dyn[lastUrl], UpdateSourceTrigger=PropertyChanged}" />
```
> 动态配置的 CheckBox **要么用 `LoadDynamicConfigWithDefaults` 预置 false，要么写 `TargetNullValue=False`**：
> 键不存在时索引器返回 `null`，WPF 里 `IsChecked=null` 的复选框点一下会变成 `false`（看起来“点了没反应”）。

```csharp
// OptionItem：下拉框/单选列表的“值 ↔ 显示文本”分离
public IReadOnlyList<OptionItem<int>> LevelOptions { get; } = OptionItem.From(
    (0, "0（最快）"), (5, "5（推荐）"), (8, "8（最小）"));

// ComboBox 记得写 DisplayMemberPath="Label"；SelectedItem 需要一个桥接属性：
public OptionItem<int> SelectedLevel
{
    get => OptionItem<int>.Find(LevelOptions, Cfg.FlacCompressionLevel) ?? LevelOptions[1];
    set { if (value is null) return; Cfg.FlacCompressionLevel = value.Value; OnPropertyChanged(); }
}
```

### 2.5 功能 ViewModel 基类（**新功能直接继承它**）

```csharp
public sealed class FlashDownloaderViewModel : ToolViewModelBase
{
    public FlashDownloaderViewModel() : base("flash-downloader")   // Id 即 Data/Config 下的小文件夹名
    {
        Cfg = LoadConfig<FlashDownloaderConfig>();     // Config\flash-downloader\config.json
        Dyn = LoadDynamicConfig();                    // Config\flash-downloader\options.json（可选）
        DownloadCommand = new AsyncRelayCommand(_ => DownloadAsync());
    }

    public FlashDownloaderConfig Cfg { get; }
    public DynamicToolConfig Dyn { get; }
    public AsyncRelayCommand DownloadCommand { get; }

    private async Task DownloadAsync()
    {
        IsBusy = true;                                 // 绑定按钮 IsEnabled
        try
        {
            Log("开始下载…");                            // 自动切 UI 线程、自动换行、超长自动截断
            var target = Storage.PathOf("game.swf");   // Data\flash-downloader\game.swf
            await Task.Run(() => Thread.Sleep(300));   // 干活；Log/状态由基类 Ui() 切回 UI 线程
            StatusText = $"已保存到 {target}";
        }
        finally { IsBusy = false; }
    }
}
```

| 基类成员 | 说明 |
| --- | --- |
| `ToolId` | 功能 Id |
| `Storage` | `ToolStorage`（`Data\<功能名>`） |
| `ConfigStore` | `ToolConfigStore`（`Config\<功能名>`） |
| `AppSettings` | `AppSettingsService`：全局设置（如 `FFmpegPath`），存 `%AppData%\YuLiangToolkit\settings.json` |
| `IsFirstRun` | 首次运行（配置文件还不存在）→ 可用来显示引导 |
| `StoragePathText` / `ConfigPathText` | 两个路径文本，直接绑界面展示 |
| `LoadConfig<T>(Func<T>? createDefault)` | 加载强类型配置（带自动保存） |
| `LoadDynamicConfig(fileName)` | 加载动态键值配置 |
| `LoadDynamicConfigWithDefaults(defaults, fileName)` | 同上，但会补上声明的默认键（**推荐**，见 2.4 的说明） |
| `IsBusy` `Progress`(0~1) `ProgressText` `StatusText` `LogText` | 通用界面状态，直接绑定 |
| `Log(line)` / `ClearLog()` | 追加日志 / 清空 |
| `Ui(action)` / `UiAsync(func)` | 切回 UI 线程（后台线程改界面的唯一正确姿势） |
| `NotifyClosed()` / `OnClosed()` | 主程序切换功能时自动调用（Flush 配置 + 停后台保存）；子类可重写 `OnClosed` 做清理 |

### 2.6 FFmpeg 与全局设置（原有）

| 轮子 | 签名要点 |
| --- | --- |
| `FFmpegLocator.Find(string? configuredPath)` | 依次从 配置路径 → PATH → 常见安装位置 找 `ffmpeg.exe`；`FindFFprobe(ffmpegPath)` 找同目录 `ffprobe.exe` |
| `FFmpegRunner.RunAsync(exe, args, ct, onOutputLine, onErrorLine)` | 异步跑进程、流式捕获输出、取消时杀进程树；返回 `RunResult(ExitCode, Output, Error)`（`Success => ExitCode == 0`）；`GetVersionAsync(path)` 取版本号 |
| `AppSettingsService` | `Settings`（`AppSettings`）、`Save()`；静态 `SettingsDirectory` / `SettingsFilePath` |
| `AppSettings` | **只放跨功能共享项**：`FFmpegPath`。功能自己的选项请放 `Config\<功能名>\` |

### 2.7 主窗口外壳（原有）

| 轮子 | 说明 |
| --- | --- |
| `MainViewModel` | `NavItems`（侧边栏：首页 + 根层类别 + 根层工具）· `Tools` · 目录树 `ToolCatalogNode`（`CurrentNode`/`Breadcrumbs`）· `ToolCards`/`CategoryCards`（过滤+排序后的卡片）· `AllTags` · `SearchText` · `SortOptions`/`SelectedSort` · `CurrentView` · `BackVisible` · `SelectedNavItem` · `StatusText` · `CurrentTitle` · `ParticlesEnabled` · `GoHomeCommand` · `OpenToolCommand` · `NavigateCategoryCommand`/`NavigateParentCommand`/`NavigateBreadcrumbCommand` · `OpenSettingsCommand` · `ClearSearchCommand` · `SearchTagCommand`；**切换功能时会调用上一个 VM 的 `NotifyClosed()`**；打开功能时记录“上次使用” |
| `MainWindow` | `WindowStyle=None` + `WindowChrome(CaptionHeight=0)`；标题栏拖拽/双击最大化在代码里手动实现；`WM_GETMINMAXINFO` 把最大化钳制到工作区；工具条是可点击面包屑（主页 › 类别 › 工具）；根部有窗口级 `ParticleOverlay`（见 2.16）；`CaptureTo(png)` 是截图模式实现 |
| `HomeView` | 主页：搜索框 + 全部类别卡片 + 标签条 + 根层工具卡片 + 空状态；代码后置只做 Ctrl+F 聚焦 / Esc 清空 / 进入主页自动聚焦搜索框 |
| `CategoryView` | 类别页（任意层级）：类别 emoji/名称/描述/统计 + 子类别卡片 + 直接工具卡片 + 类别内搜索 + 空状态（见 2.15） |
| `ShellSettingsWindow` | 「主页设置」窗口：粒子效果开关等外壳级设置（见 2.16） |

> 主窗口的启动位置：`WindowStartupLocation="Manual"`，构造函数里按 `SystemParameters.WorkArea`（已扣掉任务栏）算 `Left/Top`
> —— `CenterScreen` 会把任务栏那 48px 也算进屏幕高度，窗口看着偏低（v0.9.6 修，1920×1080 + 底部任务栏时窗口开在 `320,106`）。

### 2.8 主页搜索 · 排序 · 标签（v0.3.0）

**分工**：`ToolFilter`（纯逻辑，可自检）→ `MainViewModel`（状态与命令）→ `HomeView`（界面）。
想改搜索/排序规则，只动 `ToolFilter` 就够了，改完跑自检立刻知道有没有坏。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `ToolCard` | `ViewModels/ToolCard.cs` | 一张卡片：`Tool`（原始 `ITool`）· `Name/Emoji/Description/Category` · `Tags`/`TagsText`（分类在前 + 工具标签，去重）· `Matches(keywords)` · `LastUsed`/`LastUsedText` · `ShowLastUsed`；搜索索引 = 名称 + 分类 + 标签 + 描述 + Id + `CategoryPath` |
| `ToolFilter` | `ViewModels/ToolFilter.cs` | `SplitKeywords(text)`（空格/逗号/顿号/分号切分）· `Filter(cards, keywords)`（AND 命中，大小写不敏感）· `Sort(cards, mode)` · `Apply(cards, text, mode)` · `CollectTags(cards)` |
| `ToolSortMode` | `Models/ShellConfig.cs` | `Default`（按 `Order`）· `RecentlyUsed` · `NameAsc` · `NameDesc` · `Category`；中文用 `StringComparer.CurrentCulture`（中文系统即按拼音） |
| `ShellConfig` | `Models/ShellConfig.cs` | 外壳自己的配置（`Config\shell\config.json`）：`SortMode` + `LastUsed` 字典（`MarkUsed(id)` / `GetLastUsed(id)`）+ `ShowParticles`（见 2.16） |
| `CategoryCard` / `BreadcrumbItem` | `Models/` | 类别卡片展示模型 / 面包屑项（见 2.15） |
| `ShellCategories` | `Models/ShellCategories.cs` | 类别元数据清单（图标/描述/排序），见 2.15 |

```csharp
// 主页逻辑怎么用（要加“收藏/分组”之类的，照这个改）
var cards  = tools.Select(t => new ToolCard(t, shell.GetLastUsed(t.Id))).ToList();
var shown  = ToolFilter.Apply(cards, searchText, shell.SortMode);   // 过滤 + 排序
var tags   = ToolFilter.CollectTags(cards);                          // 标签条用的去重标签
```

**界面行为约定**

* 搜索：输入即过滤（`UpdateSourceTrigger=PropertyChanged`）；空格 = 多关键词且**全部命中**；搜不到显示空状态卡片（含“清空搜索”）。
* 快捷键：进入主页自动聚焦搜索框；`Ctrl+F` 聚焦并全选；`Esc` 清空（都在 `HomeView.xaml.cs`，切走时自动摘掉钩子）。
* 标签条：无搜索词时显示，点标签 = 把该标签填进搜索框；某个功能的所有标签也会以蓝字列在它卡片上。
* 排序：默认按 `ITool.Order`；“最近使用”把刚打开过的排最前，从未用过的垫底，并只在此时于卡片上显示“x 分钟前用过”。
* **搜索词不持久化**（重启回到全部功能，免得下次打开以为功能丢了）；排序方式与使用时间持久化。

### 2.9 UI 样式资源（App.xaml，全部 `StaticResource` 可用）

**颜色/画刷**：`WindowBackgroundBrush` `HeaderBackgroundBrush` `SidebarBackgroundBrush` `CardBackgroundBrush`
`CardHoverBackgroundBrush` `BorderBrush` `AccentBrush` `AccentHoverBrush` `AccentPressedBrush`
`TextPrimaryBrush` `TextSecondaryBrush` `DangerBrush` `SuccessBrush` `InputBackgroundBrush`

**控件样式**：`CardStyle`（卡片 Border）· `TextBoxStyle` · `LogTextBoxStyle`（日志/预览只读框）·
`PrimaryButtonStyle` · `SecondaryButtonStyle` · `WindowButtonStyle` · `WindowCloseButtonStyle` ·
`CheckBoxStyle` · `RadioButtonStyle` · `SegmentedListBoxStyle`（码率那种分段选择）· `ComboBoxStyle` ·
`ComboBoxItemStyle` · `ProgressBarStyle` · `SliderStyle`（水平深色滑块）· `TagChipButtonStyle`（主页标签芯片）·
`SidebarListStyle` / `SidebarItemStyle` · `FileListStyle`（文件列表，**配 `FileRowItemStyle`，见下**）·
`ToolCardButtonStyle`（主页卡片按钮）；滚动条是无 `x:Key` 的隐式样式，全局生效。

**`FileListStyle` + `FileRowItemStyle`（v0.5.1 修过，所有功能的文件/游戏列表都用它）**：
`FileListStyle` 只管 ListBox 本身，行样式在 `FileRowItemStyle` 里（`ItemContainerStyle` 已指过去）。
这个行样式必须同时具备四样东西，**少一样就会很难看**：

| 项 | 少了会怎样 |
| --- | --- |
| `Padding="10,8"` + `Margin="10,1"` | 文字紧贴卡片边框，看着很挤 |
| `IsMouseOver` / `IsSelected` 触发器 | 单击选中完全没反馈（用户根本不知道选上了没有） |
| `TextElement.Foreground` = `TextPrimaryBrush` | 换掉 ListBoxItem 模板后，子 `TextBlock` 会继承**默认黑色**，深色背景下几乎看不见 |
| 左侧 3px 强调条（选中时显示） | 只靠背景色区分，弱光屏 / 色弱用户不容易看出选中项 |

> 自己写 `ListBox` 时如果覆盖了 `ItemContainerStyle`（例如批量重命名里的 `RenameRowStyle`），
> **上面四条要自己补齐**，别指望框架会兜底。

**表头 + 数据行的列表（像表格那样）怎么对齐（v0.5.2 踩了很多坑，照这个来）**：

框架示例的「功能数据文件夹」列表是这种形态，正确做法只有一条 ——

> **把表头写进 `ListBox` 自己的 `ControlTemplate` 里**（`Grid` 两行：第 0 行表头、第 1 行 `ScrollViewer` + `ItemsPresenter`），
> 表头与数据行**同处一列**，宽度天然一致；表头左边距让出行内容的缩进、末尾再占一列
> `<ColumnDefinition Width="{Binding Converter={StaticResource ScrollBarWidth}}" />` 给滚动条。

为什么只能这么做（都实测过，别再试）：表头放 ListBox 外面按 `ActualWidth − N` 算会差 12px；
改用内部 `ScrollViewer.ActualWidth − N` 差 5px（`SystemParameters.VerticalScrollBarWidth`=17，实际预留 12）；
`SharedSizeGroup` **会忽略星号列**，列宽塌成内容宽度；跨模板 `ElementName` 绑模板内部控件**静默失败**；
在代码里挂 `LayoutUpdated` 逐帧算边距能跑但很绕（早期为它写过的那套同步代码已删）。

两个结论值得记住：
1. **表头与数据行必须在同一个列容器里**，否则总有几像素对不上 —— 别用"算宽度"的办法硬凑；
2. 行样式（`FileRowItemStyle`）的几何改了就重新核对表头边距。核对别靠眼睛，用 `YLT_MEASURE`（见第 0 节）把
   表头与数据行**相对各自容器**的列起点打出来，两个数一样才算对齐；**顺带在 1024×700 / 1600×900 各测一遍**。

**转换器**：`InverseBoolConverter` · `BoolToVis`（`BooleanToVisibilityConverter`）·
`EmptyToVis`（字符串为空才 Visible，用于输入框占位提示，避免压在内容上）·
`EmptyToVisInverse`（反过来：**非空才 Visible**，用于“有内容才显示”的提示，例如「搜不到匹配的游戏」）·
`InverseBoolToVis`（bool 取反后**直接给 Visibility**；⚠ 别拿 `InverseBoolConverter` 去绑 `Visibility`——它返回 bool，WPF 转换会静默失败、控件一直保持 Visible，v0.11.0 棋牌面板踩过）

> ⚠ **`ComboBoxStyle` 支持可编辑下拉，但有三个硬要求**（v0.6.0 后续修，别再踩）：
> ① 模板里**必须有 `PART_EditableTextBox` 命名部件** —— `IsEditable="True"` 时 WPF 只把编辑区文字放进它，
> 没有它就会「选中之后框里一片空白」；
> ② 只读文字 `Display`、可编辑框 `PART_EditableTextBox`、箭头、底板要在**同一个命名域**里
> （放进 `ToggleButton` 自己的 `ControlTemplate` 就是另一个命名域，外层 `Trigger` 找不到，编译报 `MC4111`）；
> ③ **高度必须由样式显式给 `MinHeight="30"`、垂直居中靠 `Margin="10,0"` + `VerticalAlignment="Center"`**：
> 别用 `Margin="10,6"` / `Padding="10,7"` 那种上下边距去凑（30px 高的框里文字会明显偏下）；
> 而 `Margin` 改成 `10,0` 之后内容区就没有上下留白了，模板里的 `Border` 撑不起高度，
> 整个下拉框会塌成 ≈21px、比旁边的输入框矮一截 —— **这就是「ComboBox 变得很小」的根因**，
> 所以 `MinHeight` 只兜底、用法处写 `Height="32"` 照样覆盖，将来字号变高也还能自然长高。
> ④ `Popup` 挂在最外层 `Grid` 上，所以要显式写
> `PlacementTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}"`，否则下拉面板会贴错位置。
>
> `TextBoxStyle` 的垂直居中同理：`Padding="10,0"` + `VerticalContentAlignment="Center"`。

```xml
<Border Style="{StaticResource CardStyle}"> … </Border>
<Button Content="开始" Style="{StaticResource PrimaryButtonStyle}" Command="{Binding RunCommand}"
        IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBoolConverter}}" />
<Slider Minimum="0" Maximum="1" Value="{Binding Cfg.Threshold}" Style="{StaticResource SliderStyle}" />
```

### 2.9.1 滚轮「滚动链」（重做过：内层滚到头 → 交给外层页面）

鼠标停在**内层列表**（游戏列表 / 文件列表 / 日志框）上滚滚轮：内层滚到头、或者压根没得滚
（空列表 / 内容比视口矮）时，这一格滚轮**交给外层页面**继续滚，不会卡在列表上不动。
`src\YuLiangToolkit.Core\Mvvm\ScrollChaining.cs` 干的就是这件事，已经挂在全部 18 处内层列表上
（Flash 下载器 3 · 联机聊天室 5 · 批量重命名 3 · 视频转音频 1 · 播放器 2 · 框架示例 1 · 棋牌棋谱/聊天 3）。

```xml
<!-- View 顶部加命名空间 -->
xmlns:core="clr-namespace:YuLiangToolkit.Core.Mvvm;assembly=YuLiangToolkit.Core"
<!-- 给内层可滚动控件挂上（ListBox / 日志 TextBox 都行） -->
<ListBox core:ScrollChaining.Enabled="True" … />
```

| 轮子 | 说明 |
| --- | --- |
| `ScrollChaining.Enabled`（附加属性） | XAML 里挂开关；代码里 `ScrollChaining.Attach(list)` / `SetEnabled(list, false)` |
| `ScrollChaining.Decide(inner, outer, delta)` | **纯函数**：这一步该谁滚（`None` / `Inner` / `Outer`）+ 方向 |
| `ScrollChaining.CanScroll(viewer, down)` | 这个 ScrollViewer 这个方向还能不能滚（空列表 / 内容比视口矮都算不能） |
| `ScrollChaining.ScrollOneNotch(outer, down)` | 替外层滚**一格**（步长与原生完全一致） |
| `ScrollChaining.LastDecision` / `DecisionMade` | 上一次的判断结果 / 每次判断都触发的事件（排查用） |

**为什么必须自己插手（WPF 的坑，别再想当然）**：`ScrollViewer.OnMouseWheel` 的源码是

```csharp
if (e.Handled) return;
if (ScrollInfo != null) { if (e.Delta < 0) ScrollInfo.MouseWheelDown(); else ScrollInfo.MouseWheelUp(); }
e.Handled = true;      // ← 无条件置 true：**滚不动也置**
```

也就是说它**哪怕一点都没滚，也会把事件标成"已处理"**，事件再也冒泡不到外层 ——
"滚到头就卡住"的根因就是这个。⚠ v0.5.3 那一版以为"滚不动就不会吃事件"，于是只挂了个观察用的处理器、
**什么都不做**，现象一点没好（自检里"没挂滚动链时：外层一动不动"那条断言现在把这个病钉住了）。

**现在的做法**：在**隧道阶段**（`PreviewMouseWheel`）判断，赶在内层 `ScrollViewer` 之前决定：

* 内层还能滚 → **不插手**，让内层按原生逻辑滚（我们要是抢先置 `Handled`，内层会直接 `return`，列表就滚不动了）；
* 内层滚不动了 → `e.Handled = true`（把事件吃掉）+ 替外层滚一格 —— 不吃掉的话内层会把它吞掉，外层永远收不到；
* 内外都滚不动 → 什么都不做，随事件继续冒泡（别抢）。

**步长必须和原生一样**：替外层滚时走的是 `ScrollViewer.OnMouseWheel` 同一条路 —— 拿到外层自己的
`IScrollInfo`（像素滚动的页面就是 `ScrollContentPresenter`）调它的 `MouseWheelUp` / `MouseWheelDown`。
实测**一格 = 3 行 = 48px**（`WheelScrollLines × 16px`）。
⚠ 早期版本是自己 `LineDown()` 一次（只有 16px），用户一眼看出"滚到头之后滚上级页面超级慢" —— 别走回头路。
另外：**自定义 ListBox / ItemsControl 模板里那个 `ScrollViewer` 必须 `x:Name="ScrollViewer"`**
（`ItemsControl` 按这个名字找"滚动宿主"，不起名就完全没有滚动能力；框架示例的表头模板踩过）。

**验证方式**：`ScrollChainingChecks`（24 项）造真的嵌套结构（外层页面 ScrollViewer + 内层 ListBox），
按 WPF 真实输入的顺序扔事件（**先隧道 `PreviewMouseWheel`，没被吃掉再冒泡 `MouseWheel`**），
断"内层滚了几项 / 外层滚了几像素 / 事件被谁吃掉 / `LastDecision` 是什么"。
⚠ 两个测试上的坑：
① **只抛冒泡事件的话，挂在 `PreviewMouseWheel` 上的处理器一次都不会执行**（必须两趟都抛，
否则测出来永远是"滚动链没生效"）；
② `ScrollToXxx` 是排队在 `LayoutUpdated` 里执行的，测量前要先把 layout 过干净，否则它会在事件之后生效、
把偏移改回去（看起来就像"合成事件滚不动"——早期就是这么误判的）。
手动核对：`YLT_WHEEL_TEST=<文件>` 打开框架示例，**每滚一下就把内层/外层偏移与判断写回文件**。

### 2.10 批量重命名工具的内部结构（v0.4.0）

**这是本项目里「逻辑与界面分离」做得最彻底的一个功能，想加“别的改名玩法”就照它改。**
分工：`RenameEngine`（算名字，纯函数）→ `RenameExecutor`（动手改，只碰文件系统）→
`RenameHistory`（流水账）→ `BatchRenameViewModel`（串起来 + 预览）→ `BatchRenameView`（界面）。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `RenameRule` | `RenameRule.cs` | **一条规则**：种类 + 作用范围 + 该种类的全部参数。会被整条序列化进 `config.json` 的 `rules` 数组 |
| `RenameRuleKind` | 同上 | `Replace` / `Cleanup` / `DeleteRange` / `Case` / `Affix` / `Sequence` / `Extension`（加新种类改这里 + `ApplyRule` 的 switch） |
| `RuleScope` | 同上 | `Stem`（主名，默认）/ `Extension`（扩展名）/ `FullName`（整个文件名，改完会重新拆） |
| `RenameRule.Create(kind)` | 同上 | 新建一条默认参数的那类规则（界面「添加规则」用） |
| `RenameRule.Summary` | 同上 | 一行中文说明（列表收起时显示）；只读 → 不会写进 JSON |
| `RenameEngine.BuildName(fileName, index, rules)` | `RenameEngine.cs` | **纯函数**：一个文件名 + 它在列表里的下标 + 规则数组 → 新文件名；**按数组顺序逐条套用**。不碰磁盘 |
| `RenameEngine.BuildPlan(paths, cfg, include)` | 同上 | 把一批文件算成完整计划：`IReadOnlyList<RenamePlanEntry>`（新名 + 状态 + 原因） |
| `RenameEngine.ParseExtensionFilter(text)` | 同上 | `"jpg; png、.bmp"` → `["*.jpg","*.png","*.bmp"]` |
| `RenameEngine.Validate(cfg)` / `ValidateRule(rule)` | 同上 | 规则有没有写错（返回错误文案或 null）；`Validate` 的报错会带上「第 N 条规则（种类）」 |
| `RenamePlanEntry` | 同上 | 一条计划：`SourcePath` `OriginalName` `NewName` `TargetPath` `Status` `Message` `WillRename` |
| `RenameStatus` | 同上 | `Ok` / `Unchanged` / `Skipped` / `Conflict` / `Error`（计划期）+ `Done` / `Failed` / `Undone`（执行后） |
| `RenameExecutor.Execute(entries, ct, onProgress)` | `RenameExecutor.cs` | **两阶段改名**，返回 `List<RenameOutcome(Entry, Success, Error)>` |
| `RenameExecutor.Undo(session, ct, onProgress)` | 同上 | 按流水账改回去（同样两阶段）；`session.Undone` 为 true 时直接拒绝 |
| `RenameSession` / `RenameHistory` / `RenameHistoryStore` | `RenameHistory.cs` | 一批改名 / 账本（最多 20 批）/ 落盘读写 |
| `RenameRuleItem` | `ViewModels/RenameRuleItem.cs` | 规则清单的一行 = `RenameRule` + 界面状态（`IsExpanded` / `ShowDropTop` / `ShowDropBottom`）+ 卡片里几个下拉的选项列表 |

**规则怎么套用**（`BuildName`）：先把文件名拆成「主名 + 扩展名」，然后**按清单顺序**逐条套用；
每条规则只动它 `Scope` 指定的那一块（`Extension` 种类的规则不看 Scope，天生只动扩展名）。
同一种规则可以出现多次，**顺序不同结果就不同**。收尾统一 `Trim()`（Windows 会悄悄吃掉结尾的空格和点）。
想加新的玩法：在 `RenameRuleKind` 加一个成员 → 在 `RenameRule.Summary` 加一段文案 →
在 `RenameEngine.ApplyRule` 的 switch 加一个 case → 界面加一块参数面板。四处都很好找。

```
每条规则独立处理自己负责的那一段：
  Scope=Stem       → 只改主名（默认，最不容易误伤扩展名）
  Scope=Extension  → 只改扩展名（含点，例如把 .JPG 换成 .jpg）
  Scope=FullName   → 改整个文件名，改完再拆回「主名 + 扩展名」供后面的规则继续处理
```

**冲突检测是迭代到稳定的**（`RenameEngine.MarkConflicts`，这里的坑踩过，别改回去）：
“目标位置有没有被占”取决于“占着它的那个文件这次会不会被移走”，而这件事又取决于冲突判定结果本身，
所以先按单项规则定状态，再循环：`会让位的文件 = 当前状态为 Ok 的那些`，重新判一轮，直到不再变化。
否则会出现这类漏判：`1.txt` 改名后还是 `1.txt`（Unchanged，不会移走），`2.txt` 想改成 `1.txt` ——
若把 Unchanged 的文件也算作“会让位”，执行时第二阶段必然撞车。

**为什么要两阶段改名**（`RenameExecutor.Execute`）：先把所有要改的文件改成同目录临时名
（`.ylt-renaming-<guid>.tmp`），第二阶段再改成最终名。这样才支持：A→B、B→A 互换，
只改大小写（`photo.jpg` → `Photo.jpg`，Windows 上一步 `File.Move` 会报“已存在”），A→B→C 链式改名。
失败的那一条会被**改回原名**；取消时同样把还没落地的统统改回去，不会留下 `.tmp`。

**撤销**：`Data\batch-rename\rename-history.json`（用 `JsonFileStore` 原子写 + 损坏自动回退），
所以关掉程序再打开仍然能撤销。撤销互换（A→B、B→A）时“原名位置已经有文件”如果正好是本批的另一个目标，
不算占用——这批改出来的名字在阶段 1 会一起被挪走（见 `willBeVacated`）。

```csharp
// 想复用这套逻辑（例如给别处做“按规则生成名字”）：
var rules = new[]
{
    RenameRule.Create(RenameRuleKind.Cleanup),                                     // 去空格 / 删字符
    new RenameRule { Kind = RenameRuleKind.Replace, FindText = "IMG_", ReplaceText = "照片" },
    new RenameRule { Kind = RenameRuleKind.Sequence, SequenceDigits = 3 },          // 001 起编号
    new RenameRule { Kind = RenameRuleKind.Extension, ExtensionMode = ExtensionMode.Lower },
};
RenameEngine.BuildName("IMG_01.JPG", 0, rules);        // → "照片_01_001.jpg"（只算名字）

var cfg  = new BatchRenameConfig { Rules = rules.ToList() };
var plan = RenameEngine.BuildPlan(filePaths, cfg);     // 算整批 + 冲突检测，只算不改
foreach (var e in plan)
    Console.WriteLine($"{e.OriginalName} → {e.NewName}  [{e.Status}] {e.Message}");

var outcomes = RenameExecutor.Execute(plan);           // 真改（只改 Ok 的那些）
var session  = new RenameSession();
foreach (var o in outcomes) if (o.Success)
    session.Entries.Add(new RenameHistoryEntry { From = o.Entry.SourcePath, To = o.Entry.TargetPath });
RenameExecutor.Undo(session);                          // 后悔了
```

**只改名字、绝不换目录**：`TargetPath = Path.Combine(同一目录, NewName)`。想加“移动到子文件夹”
之类的功能，得同时动 `RenamePlanEntry.TargetPath`、`RenameExecutor`（跨目录要建目录）和撤销逻辑。

**界面层的小约定**（都集中在 `BatchRenameView.xaml(.cs)` 和 `BatchRenameViewModel`）：

* 规则清单是**两份表示**：`Rules`（`ObservableCollection<RenameRuleItem>`，带展开状态等 UI 信息）和
  `BatchRenameConfig.Rules`（要落盘的数据）。任何增删改排序之后都调一次 `SyncRulesToConfig()` 对齐两边 ——
  **清单顺序 = 执行顺序**。
* **拖动排序**：规则行左边那个「☰」把手（`Grid` 里 Width=32 的透明 Border，`Cursor=SizeAll`）上挂了
  `PreviewMouseLeftButtonDown` / `PreviewMouseMove`，越过系统最小拖动距离就 `DragDrop.DoDragDrop`，
  用**私有数据格式** `YltBatchRename.Rule` 传对象（不是 `FileDrop`，所以和“拖文件进来”互不干扰）。
  卡片 `DragOver` 里按鼠标在卡片上/下半决定插到前面还是后面，`ShowDropTop/Bottom` 画出指示线；
  拖到清单空白处 = 放到末尾。**`DoDragDrop` 会阻塞到拖放结束**，所以清指示线写在它后面的 `finally` 里最稳。
* **排序用一次 `ObservableCollection.Move`**，不要写成“先 `RemoveAt` 再 `Insert`”：后者会发两个集合变更事件，
  ListBox 要为所有卡片重新生成/复用容器（拖动时肉眼可见地闪），也更容易踩到状态不一致。
  往自己后面拖时要补偿下标：`to = 目标下标 + 1; if (from < to) to--;`
* ⚠️ **绝不要在 `CollectionChanged` 处理函数里调 `ScrollIntoView`（或任何会强制布局的东西）**：
  `Rules.Add` 刚返回时 ListBox 自己的 `ItemCollection` 还在消化这次变更，此时强制布局会让
  `ItemContainerGenerator.Verify()` 抛 `InvalidOperationException: 某个 ItemsControl 与它的项源不一致`；
  而这条路径是**从原生 OLE 拖放回调**进来的，异常没人接 → **整个程序闪退**（v0.4.0 踩过）。
  正确做法：VM 加 `event EventHandler<RenameRuleItem>? RuleAdded`（`Rules.Add` 完全返回后触发），
  界面用 `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)` 延后滚动，并先 `Items.Contains(item)`。
* ⚠️ **规则行的根 `<StackPanel … Background="Transparent">` 不能省**：`Panel`/`Border` 不设 `Background`
  就**不参与命中测试**，插入指示线自带的 `Margin` 与左右 2px 缩进成了“死区”，鼠标停在那里会穿透到
  ListBox 的兜底分支（“空白处 = 放到最末尾”），于是「当前位置 ↔ 最末尾」疯狂对跳
  （用户报的“一碰到蓝线就闪到最底下”）。用真实画刷 `Transparent`（可命中、不影响外观），别靠调 Margin 凑；
  指示线本身也必须**始终占位、只换颜色**（用 `Visibility` 切换会让行高变化，同样自激）。
* 排查拖放用 `YLT_DRAG_DEBUG=<日志文件>`：把每次 `DragOver`/`Drop`（目标 + 插前/插后）写进文件，
  一条日志就能区分“判定算错”和“命中测试穿透”（不设变量时只在静态字段读一次，没有开销；
  `scripts\e2e-batch-rename-drag.ps1` 的边界用例也读它）。
* 拖放回调里抛异常没人接，所以 `OnRuleDrop` / `OnRuleListDrop` 包了一层 `TryReorder(...)`：出错写日志、
  界面继续用。规则卡片还重写了 `ToString()`（返回「种类：摘要」），否则 UI 自动化看到的是类型全名。
* 点标题行展开 / 收起参数（`ToggleExpandCommand`，同时只展开一条，列表才不会长得看不见）。
* 文件列表的 `Height` 改成了 `MinHeight/MaxHeight`（内容自适应），否则空列表那一大块会把「命名规则」顶到屏幕外。
* `BatchRenameViewModel.Confirm` 是「二次确认」的可替换出口，**自检程序靠它无头跑完执行+撤销整条流程**。

### 2.11 Flash 下载器工具的内部结构（v0.5.0）

**这是本项目里唯一「要联网 + 要起服务器」的功能，结构按“纯逻辑 / 引擎 / 服务器 / 界面”四层分开，
自检能无头把整条链路跑完（自检自己起一个假游戏站）。**

分工：`GamePageParser` + `GameUrlInfo` + `SwfNetPathIni` + `GameEntry` + `BatchDownloadPlanner`（纯逻辑，自检直接断言）
→ `FlashHttp` / `GameDownloader` / `RelatedFileDownloader`（引擎，只碰网络与文件）
→ `FlashWebServer`（本地服务器 + 自动下载）
→ `FlashDownloaderViewModel`（3 个 partial：主流程 / 批量与自动下载 / 界面数据）
→ `FlashDownloaderView`（界面）。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `GameSite` / `GameContentKind` | `Core/GameMeta.cs` | 站点枚举（4399 / 7k7k / 17yy / ArmorGames / Kongregate / 其它）+ 内容类型（Swf / Htm）；`GameSites` 负责字母 a/b/c/d/e/z ↔ 站点 ↔ 显示名，`CanResolveFromAppendix` 说明哪些站点的后缀能反推回游戏页 |
| `GameMeta`（record） | 同上 | 一次解析的结果：`CleanName` `Appendix` `Site` `Width` `Height` `Kind` `ContentUrl` `IsNewPage` `NewPageUrl` `PageContent`，以及 `Referer` `IsValid` `SizeText` `Describe()` |
| `GamePageParser` | `Core/GamePageParser.cs` | **纯函数**：`ToSafeFileName` `GetUrlInfo` `GetUrlNumber` `AppendixToUrl` `UrlJoin` `GetParentUrl` `SanitizeBatchPath` `JoinBatchNetPath` `JoinBatchLocalPath` `DecodeWebContent` `LooksLikeNotFoundPage` + 五站点的全部正则（ArmorGames 的 `GetArmorGames*`、Kongregate 的 `GetKongregate*`） |
| `GameUrlInfo` | `Core/GameUrlInfo.cs` | 网址 → 站点（先域名，认不出按后缀字母 a/b/c/d/e 兜底） |
| `SwfNetPathIni` | `Core/SwfNetPathIni.cs` | 游戏文件夹里的 `SWFNetPath.ini`：`Parse` / `Load` / `Save` / `BuildContent` / `Describe`；类型行对不上会记 `ErrorText` 并按文件夹后缀归一 |
| `GameEntry` | `Core/GameEntry.cs` | 游戏目录里一条记录：`FromFolder` / `FromSwfFile` + 命名规则（`GameNameOfFolder` / `AppendixOfFolder` / `FolderExtOf` / `IsLegacy`）+ `CanPlay` / `CanMoveIntoFolder` / `CanConvertToHtm` |
| `GamesLibrary` | `Core/GamesLibrary.cs` | 游戏目录门面：`List()` `Find()` `Delete()` `MoveIntoFolder()` `RenameFolderToHtm()` `WriteMainPage()` `TryResolve()` `BuildUniqueGameName()` |
| `BatchDownloadPlanner` | `Core/BatchDownloadPlanner.cs` | **纯逻辑**：`Plan(lines, folder, skipExisting, fileExists)` → 条目 + `BatchDownloadSummary`（总数/成功/404/失败/不规范/重复/已存在），以及 `MakeUniqueDirectory` |
| `FlashHttp` | `Engine/FlashHttp.cs` | `GetBytesAsync(url, referer)` / `DownloadToFileAsync(...)` / `PostFormForJsonAsync(...)`；**404 与网络错误分开报**（`FetchStatus`），自带 UA 与 Referer |
| `GameDownloader` | `Engine/GameDownloader.cs` | `ResolveAsync(url)`（五站点各一套规则，域名不认识时按页面内容猜；粘的是 `.swf` 直链就直接当本体）+ `DownloadAsync(library, meta)`（swf 散放成 `游戏名_?123.swf`，网页建成 `游戏名_?123h/` 并写好 ini） |
| `RelatedFileDownloader` | `Engine/RelatedFileDownloader.cs` | `RunAsync(netBase, referer, folder, entries, summary, maxParallel, onProgress, ct)`：并发下载一批相对路径，404 会删掉半成品并单独统计 |
| `FreeFileDownloader` | 同上 | 「自由下载工具」：网址 + Referer + 文件名 → 直接存进游戏目录 |
| `FlashPlayerLocator` | `Engine/FlashPlayerLocator.cs` | `Find()` / `EnsureAvailable()`：配置路径 → tools 目录 → 程序目录（exe 同级）/ 仓库根 → 常见安装位置（Program Files、`%LocalAppData%`、桌面、下载、文档，以及它们下面的 `tool\` / `Flash Games\` / `Ruffle\` 等子目录），找到就拷进 `<exe 旁边>\Data\flash-downloader\tools\` |
| `FlashWebAssets` | `Web/FlashWebServer.cs` | 三个网页模板（`frame.htm` 播放页 / `FlashPage.htm` 加载页 / `h5api-replacement.js` 替代 API）以**嵌入资源**随 dll 走，`EnsureTemplates()` 首次运行释放到 tools 且**不覆盖用户改过的** |
| `FlashWebServer` | 同上 | `Start(port)` / `Stop()` / `BeginSession(...)` / `EndSession()` / `Resolve(path, gamesRoot, tools)`（纯静态路径映射）/ `FileFetcher` 回调 |
| `AutoDownloadSession` | 同上 | 自动下载的统计与日志（`Total/Loading/Success/NotFound/Failed`、`DescribeStats()`、`MakeRelativePath()`） |
| `GameSortMode` / `GameFilter` | `Core/GameFilter.cs` | 游戏列表的搜索 / 排序（**纯函数**，见下）；`GameEntry.SiteSortKey` 是站点排序键（4399=1 / 7k7k=2 / 17yy=3 / ArmorGames=4 / Kongregate=5 / 其它=9，**不能拿 `SiteText` 直接排**，否则「7k7k」会跑到「4399」前面） |

**URL 约定**（服务器根 = 游戏目录的父目录，模板在 tools 目录里；两者都从磁盘直读）：

```
http://localhost:<端口>/                 → <exe 旁边>\Data\flash-downloader\tools\（frame.htm / tool/…）
http://localhost:<端口>/games/<游戏名>h/<游戏名>.htm   → 游戏目录里的网页游戏（缺失时触发自动下载）
http://localhost:<端口>/tool/substiH5WanApi.js        → 4399 h5 游戏的替代 API
```

**「自动下载所需文件」的原理**（原始工具最巧的一招，这里照搬）：

```
游戏（浏览器里的 h5 / Unity 游戏）向本地服务器要某个文件
   → 本地有 → 直接给
   → 本地没有 → 服务器**同步**去网上把这个文件下下来（带 Referer）→ 再答复游戏
   → 网上也没有（404）→ 如实回 404（而且要把它从磁盘删掉：
     有些游戏靠“文件不存在”决定分支，塞个空文件反而会把它搞坏）
```
所以玩游戏时会出现“加载很慢”的错觉——那是服务器在替它下载；同一文件并发请求只会下一次
（`_inFlight` 任务表），不同文件并发受 `_downloadGate` 限制。

**命名规则**（三位一体，改任何一处都要同时改另外两处）：

```
散放的 swf：  游戏名_a12345.swf
网页游戏：    游戏名_a12345h/  ← 目录名结尾的 h = 用网页打开
              ├─ 游戏名_a12345.htm
              └─ SWFNetPath.ini      ← 网络路径 / 宽高 / 是否新页面（批量下载与打开都靠它）
播放器游戏：  游戏名_a12345s/  ← 目录名结尾的 s = 用 Flash 播放器打开
              ├─ 游戏名_a12345.swf
              └─ SWFNetPath.ini
字母：a = 4399，b = 7k7k，c = 17yy，d = ArmorGames，e = Kongregate，z = 其它（数字是站点里的游戏编号）
      ⚠ Kongregate 的 e 编号反推不出游戏页（页面地址是「作者 / 游戏名」，编号只在网页正文里），见 2.11
```
`SWFNetPathIni` 的格式（人也能拿记事本改）：

```
第 1 行  网络路径（s 文件夹放 swf 地址；h 文件夹放 htm 地址）
第 2 行  必须为 swf / htm / unknown
swf 多三行：宽度（正整数或 Unknown）/ 高度 / 是否有新页面（True / False / Unknown）
            第五行是 True 时再补第 6 行：新页面网址
htm 多两行：宽度 / 高度
```

```csharp
// 想复用这套逻辑（例如自己写个批量下载脚本）：
var (appendix, site) = GamePageParser.GetUrlInfo("https://www.4399.com/flash/6853.htm"); // ("a6853", Site4399)
var meta = await downloader.ResolveAsync(url);                      // 只解析，不下东西
var result = await downloader.DownloadAsync(library, meta.Meta!);   // 落盘
var (entries, summary) = BatchDownloadPlanner.Plan(lines, gameFolder, true, File.Exists);
await related.RunAsync(GamePageParser.GetParentUrl(ini.NetPath), ini.NetPath, gameFolder, entries, summary);
```

**游戏列表的搜索 / 排序（v0.5.1）**：

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `GameSortMode` | `Core/GameFilter.cs` | `Default` / `LastPlayed` / `Name` / `Size` / `Site`（按名字进 JSON，别改名） |
| `GameFilter.SplitKeywords(text)` | 同上 | 空格 / 中英文逗号 / 顿号 / 分号都算分隔符；空串返回空数组 |
| `GameFilter.Apply(entries, 搜索词, 排序, 是否逆序, 上次游玩表)` | 同上 | 过滤 + 排序一步到位（**纯函数**，自检直接断言） |
| `GameFilter.Matches(entry, keywords)` | 同上 | 搜索索引 = 文件名 + 游戏名 + 站点后缀 + 站点名 + 类型 + 打开方式 + 本地路径，所以「4399」「网页」「a90302」都搜得到 |
| `GameFilter.EmptyHint(总数, 显示数, 搜索词)` | 同上 | 空状态文案：目录里真没有 vs 被搜索筛掉了（说的不是同一句话） |
| `Cfg.GameSortMode` / `GameSortDescending` / `LastPlayedAt` | `FlashDownloaderConfig.cs` | 排序偏好与「上次游玩时间」表（键 = 文件名/文件夹名）；**搜索词故意不持久化** |

VM 里对应的是「扫盘 → 过滤 → 展示」三步：`RefreshGames()` 只负责扫盘填 `_allGames`，
`RebuildGameList()` 负责过滤+排序填界面上的 `Games`（搜索框、排序下拉、正逆序按钮都只调它，不重新读盘）。
游玩成功会走 `MarkPlayed()` 记时间并顺手清掉已删游戏的死记录；
**「按上次游玩」排序时刚玩过的会立刻窜到最上面**。

```csharp
// 复用这套过滤/排序（例如自己写个命令行工具）：
var shown = GameFilter.Apply(library.List(), "4399 动作", GameSortMode.LastPlayed, false, cfg.LastPlayedAt);
```

**界面层的小约定**（都在 `FlashDownloaderView.xaml(.cs)` 与三个 ViewModel partial 里）：

* 四个“原工具弹窗”被做成了**同一页里的面板**：关联文件下载器（含自动下载）/ 自由下载工具 / 设置 / 选中游戏信息，
  不弹子窗口（WPF 里子窗口要自己管生命周期，反而更麻烦）。
* `FlashDownloaderViewModel.Confirm` 是「二次确认」的可替换出口，**自检靠它无头跑完装入文件夹 / 改为 htm**。
* 游戏目录可以改：`Cfg.GamesFolder`（空 = 用 `Cfg.DefaultGamesDirectory`，即 `Data\flash-downloader\games\`）
  与 `Cfg.DefaultGamesDirectory` 一起算出 `Cfg.EffectiveGamesDirectory`，落盘也走强类型配置，没有第二份配置文件。
  改目录后 `Library` 与服务器都要跟着换，所以 `GamesDirectory` 是个**带 setter 的属性**而不是直接绑 `Cfg`。
  ⚠ 顺序要紧：**先 `LoadConfig` → 填 `DefaultGamesDirectory` → 再算 `GamesDirectory` → 最后才 `new GamesLibrary(...)`**
  （写成字段初始化器会拿到空目录，见第 5 节的踩坑）。
* 目录里有变化（别的程序往游戏目录丢文件）时，2.5 秒轮询一次自动刷新列表（不抢焦点、不弹窗）。
* 游戏列表的**搜索框 / 排序下拉 / 正逆序按钮**都在列表上方一行；选中项高亮靠 `FileRowItemStyle`（见 2.9）。
* 快捷键：`Ctrl+F` 聚焦游戏搜索框、`Esc` 清空搜索、`Ctrl+L` 回地址栏、地址栏里回车 = 下载并游玩。
* 双击列表游玩时**取鼠标底下那一行**（`VisualTreeHelper` 往上找 `ListBoxItem`），不是取“当前选中项”——
  否则双击空白处也会启动游戏。**自检里不要调 `PlayEntry()`**：它会真的启动 FlashPlayer 窗口，无头自检会卡死（踩过）；
  簿记入口 `MarkPlayed()` 是公开的，自检只调它。
* `YLT_FLASH_SELECT=<文件名>`：启动即选中那一行，用于截图检查选中高亮。

### 2.12 联机聊天室工具的内部结构（v0.6.0；v0.7.2 加「傻瓜模式」）

**这是本项目里唯一「两端对等联机」的功能：主机与客户端跑的是同一套代码，区别只有“谁开监听”。**
结构按「纯逻辑 → 会话存储 → 网络 → 界面」四层分开，自检里能在 `127.0.0.1` 上**真开一局**跑完双向收发。

分工：`ChatProtocol` + `ChatFrameHeader` + `ChatMessages`（协议，纯逻辑）→
`TrafficMeter` + `Crc32`（流量与校验，纯逻辑）→ `ChatSessionNaming` + `ChatSessionStore`（会话文件夹与记录）
→ `ChatLink`（一条 TCP 连接的读写）→ `ChatHost` / `ChatClient`（主机 / 客户端）→
`ChatRoomSession`（一次会话的门面）→ `ChatRoomViewModel`（3 个 partial）→ `ChatRoomView`（界面）。

#### 线上协议（帧格式）

```
每个包 = [4 字节小端长度][JSON 头][裸载荷]
         └─ 长度只数 JSON 头自己的字节数（不含前缀、不含载荷）
载荷多长由头里的 payload 字段说明；文件本体跟在包头之后，属于“裸字节”而不是载荷
```

包类型（线上是字符串，加减类型不会让老版本错位）：

| 类型 | 方向 | 干什么 |
| --- | --- | --- |
| `hello` | 双向 | 自报家门（magic / 版本 / 昵称 / 头像 / 角色），客户端先发 |
| `welcome` | 主机 → 客户端 | 定成员 Id、告诉你本次会话文件夹名、当前成员名单 |
| `text` | 双向 | 文字（载荷 = UTF-8 文本） |
| `system` | 双向 | 加入 / 离开 / 被移出 / 流量到顶之类的系统提示 |
| `members` | 双向 | 有人进出时重发完整名单（不做增量，简单不易错） |
| `kick` | 主机 → 客户端 | 你被移出房间 |
| `transferStart` | 双向 | 要发文件了：文件名 / 大小 / 大类 / CRC / 留言，**紧跟裸字节** |
| `transferDone` | 接收方 → 发送方 | 回执：成没成 / 存成什么名字 / 那边算出的 CRC |
| `transferCancel` | 双向 | 取消或失败 |
| `ping` / `pong` | 双向 | 心跳（15 秒一次）与延迟 |
| `bye` | 双向 | 礼貌退出 |

协议全貌另有一份**人能读的 JSON 说明**：`Protocol/chat-protocol.json`（嵌入资源，首次运行释放到
`Data\chat-room\Protocol\`）——**改协议常量时记得同步那份文件**，自检也会断言它确实随 dll 打包。

#### 轮子

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `ChatProtocol` | `Protocol/ChatProtocol.cs` | 帧格式常量、`BuildFrame`（**自动把载荷长度写进头里**）、`BuildLengthPrefix`、`IsValidFrameLength`、包类型名、`ParseEndpoint` / `ResolveAsync`（IPv4 / IPv6 / 域名 + 可选端口）、`FormatEndpoint`、`DetectLanIPv4` / `ListLocalAddresses`、`EnsureProtocolDoc` / `Describe` |
| `ChatFrameHeader` | `Protocol/ChatFrameHeader.cs` | 包头：`Kind` `From` `Frame` `ReplyTo` `Payload` `Extra`；`ExtraAs<T>()` / `WithExtra()` / `New(kind, from, extra)` |
| `ChatMember` 等 DTO | `Protocol/ChatMessages.cs` | `ChatMember` / `HelloInfo` / `WelcomeInfo` / `MemberListInfo` / `TextInfo` / `SystemInfo` / `KickInfo` / `TransferInfo` / `TransferResultInfo` / `CancelInfo` / `ByeInfo` |
| `TrafficMeter` | `Traffic/TrafficMeter.cs` | **单次流量上限**：`Check(bytes)`（发前预检）、`AddSent/AddReceived`、`Snapshot()`（收发/剩余/比例/速度）、`SetLimit`（中途改立刻生效）、`Reset()`、`ShouldNotifyExhausted()`；`TrafficQuotaScope` = 收发合计 / 仅上传 / 仅下载 / 不限 |
| `TrafficSnapshot` | 同上 | 界面直接绑的只读快照：`Summary` / `LimitText` / `RateText` / `UsedRatio` … |
| `Crc32` | `Traffic/Crc32.cs` | `Compute` / `Begin`+`Append`+`Finish`（边收边算，缓冲区复用也能用） |
| `ChatSessionNaming` | `Sessions/ChatSessionNaming.cs` | **纯逻辑**：`FolderName(时间)` → `2025.1.3-14.5.9`、`ParseFolderName`（连 `-2` 后缀一起认）、`KindOfExtension` / `FolderOf` / `LabelOf`、`SafeFileName`（非法字符 / 结尾点空格 / Windows 保留名）、`ReadmeText` |
| `ChatSessionStore` | `Sessions/ChatSessionStore.cs` | 会话文件夹门面：`CreateSession`（建目录 + 说明 + 抬头）、`AppendLog` / `ReadLog` / `AppendSystemLine`、`ImportFile`、`MakeRelativeFilePath`、`SaveInfo` / `LoadInfo` / `ListSessions`、`OpenInExplorer` / `RevealInExplorer` |
| `ChatLink` | `Net/ChatLink.cs` | 一条 TCP 连接：读循环 + `SendTextAsync` / `SendSystemAsync` / `SendMembersAsync` / `SendFileAsync`（**排队**，一次一个）/ `SendTransferResultAsync` / `SendPingAsync` / `SendByeAsync`、`PumpRawBytesAsync` / `SkipRawBytesAsync`、`Close`、`ChatLink.Describe(异常)`（把人话翻出来） |
| `ChatRoomConnection` | `Net/ChatRoomConnection.cs` | 主机与客户端的共同部分：会话文件夹 + 聊天记录 + 收发文字/文件 + 流量检查；`SendFileToAsync`（落本端一份 → 交界面 → 推过去 → 等回执）、`ReceiveFileAsync`（边收边算 CRC → 落盘 → 回执） |
| `ChatHost` | `Net/ChatHost.cs` | `StartAsync(地址, 端口)` / `StopAsync` / `SendTextAsync`（广播）/ `SendFileAsync` / `KickAsync` / `KickAllAsync`、`ClientCount`、`EndPoint`（端口填 0 时问真实端口） |
| `ChatClient` | `Net/ChatClient.cs` | `ConnectAsync(地址, 端口)`（可带端口）/ `SendTextAsync` / `SendFileAsync` / `PingAsync` / `CloseAsync`、`LatencyMs`、`ServerText`、`HostName` |
| `ChatRoomSession` | `Net/ChatRoomSession.cs` | 一次会话的门面：`StartHostAsync` / `ConnectAsync` / `StopAsync` / `SendTextAsync` / `SendFileAsync` / `KickAsync` / `KickAllAsync` / `PingAsync`，以及 `IsOnline` / `ListenText` / `PeerText` / `ClientCount` |
| `ImageUtil` | `Media/ImageUtil.cs` | `CreateThumbnail`（含 EXIF 方向校正）、`GetPixelSize`、`EstimateThumbnailText` |

```csharp
// 想复用这套网络层（例如自己写个命令行联机工具）：
var store = new ChatSessionStore("chat-room");                           // Data\chat-room\Sessions\…
var meter = new TrafficMeter(100L * 1024 * 1024, TrafficQuotaScope.Both); // 单次 100 MB，超了发不出去
var host  = new ChatRoomSession("chat-room", "我的主机", "🐟", meter, store);
host.MessagePosted  += m => Console.WriteLine($"{m.SenderName}: {m.Text}");
host.TransferChanged += t => Console.WriteLine($"[{t.Progress.StateText}] {t.FileName}");
await host.StartHostAsync("0.0.0.0", 5200);        // 监听（IPv6 就写 "::"）
// 另一台机器上：
var client = new ChatRoomSession("chat-room", "我的客户端", "🐻", meter2, store2);
await client.ConnectAsync("192.168.1.7", 5200);    // 内网穿透就填穿透工具给的域名
await client.SendTextAsync("你好");
await client.SendFileAsync(@"D:\图片\截图.png");
```

#### 几个必须知道的约定（都在自检里钉住了）

* **会话文件夹格式**：`年份.月份.日期-时间.分钟.秒`（`2025.1.3-14.5.9`），**月/日/时/分/秒不补前导零**
  —— 所以按名字排序 ≠ 按时间排序；界面上的「历史记录」是按时间排的。同一秒开两次自动补 `-2`。
  全部会话放在 `Data\chat-room\Sessions\` 下（**别摊在 `Data\chat-room\` 根下**：根下还有协议说明，
  摊着的话「历史记录」会把它当成一次聊天列出来）。
* **文件按大类归子目录**：`images/` `audio/` `video/` `files/`；重名自动加序号（`照片 (1).png`），**绝不覆盖**。
* **每端都存一份**：自己发的和收到的都落进本端会话文件夹 —— 会话文件夹就是“这次聊了什么”的完整账本。
* **聊天记录**：`chat-log.jsonl` 一行一条 JSON（追加写，坏行自动跳过）；抬头 `session.json` 记开始/结束时间、
  身份、对端、消息条数与流量统计。
* **预览图不占流量**：图片的缩略图由两端各自在本地生成（`ImageUtil`），**不把预览图发过去**（发过去等于白花流量）。
* **流量上限是两端的**：谁到顶谁停止发送（连接不断），发了提醒给界面；接收方也会做一次预检，
  不够就把对方正在灌的字节读干净再回执拒绝（**必须读干净**，否则后面的包全乱）。
* **文件走控制连接本身**：穿透工具只映射主机那一个端口，另开数据端口从外网连不上——这是设计约束，不是偷懒。

#### 界面层的小约定（都在 `ChatRoomView.xaml(.cs)` 与三个 ViewModel partial 里）

* 一整页放完：标题+状态 → 连接条（主机 / 客户端并排）→ 聊天记录（占满宽度）→ 流量 / 发送选项 / 文件传输 →
  房间成员 / 历史记录 → 协议与日志。**不弹子窗口**。
* 消息列表：自己发的靠右（蓝底气泡）、对方靠左、系统提示居中；图片显示缩略图（**双击打开原图**）、
  音频与视频用 `MediaElement` + 「▶ 播放 / 暂停」按钮（**不能用自动播放**，一屋子消息会一起响）。
* ⚠ **不能在这里用 `ListBox.ScrollIntoView`**：它走 `BringIntoView` 路由，请求会一路传到**外层页面**的
  ScrollViewer，表现是“一打开工具整页被顶下去、标题跑出视口”。要滚到底就找到列表内部那个 `ScrollViewer`
  直接 `ScrollToEnd()`（见 `ScrollMessagesToEnd`）。
* ⚠ **卡片内部不要用 `Height="*"` 的行**：整页在 ScrollViewer 里，量内容时给的是无限高度，
  星号行会退化成内容高度、卡片被撑得极高，上半个卡片被顶到视口外面（截图里一片空白就是这么来的）。
  行高一律 Auto + 给列表 `MinHeight` / `MaxHeight`。
* 打开工具的前 600ms 不认“用户往上翻了”（`_autoScrollArmedAt`）：启动过程自己会发一串 `ScrollChanged`，
  认了就会把自动滚到底关掉。
* **底部三栏的标签放在输入框上面**（不是左边）：1024 宽的最小窗口下，侧边标签会把「发图压缩」这类
  下拉框挤到只剩几十像素、文字被裁掉。改这几栏时别再把标签挪回左边。
* **输入框别给固定像素宽度**：`*` 列 + `MinWidth` 才跟得上窗口变化；纯数字那种框用
  `ColumnDefinition Width="Auto"`（正文字号下 5 位数字加内边距天然放得下，比写死像素稳）。
* **监听地址是普通输入框，不是下拉框**（用户要求）：常见值就那几个，「全部 IPv4 / 含 IPv6 / 只本机」
  三个按钮一点就填好；本机全部地址改成输入框下面那行 `LocalAddressHint` 提示文字。
  （v0.7.2 之后这些控件默认藏起来 —— 见下面「傻瓜模式」，取消勾选才露出来。）
* **傻瓜模式（v0.7.2，默认开）**：用户不用选监听地址与端口，`ChatProtocol.ChooseSimpleListenPlan`
  自己挑（优先双栈 `::` + 上次那个端口，被占了改让系统挑），开完房间把本机 IPv4 / IPv6 与端口
  列成一行行「地址:端口」给用户复制。⚠ 注意**不是把 IP 和端口藏起来** ——
  内网穿透要照着填本地端口，所以该显示的一个都不少，只是不让用户「选」。
* ⚠ **绑 `::` 必须显式开双栈**：Windows 上 IPv6 通配地址默认 `IPV6_V6ONLY=1`（只收 IPv6），
  而 .NET 的 `TcpListener` **不会**替你打开 `DualMode`。不设那一行，绑了 `::` 之后 IPv4 客户端
  **一个都连不进来**（对方的报错是「对方拒绝连接」），而界面上的按钮还写着「同时收 IPv4 与 IPv6」。
  实测确认过，自检里 `CheckDualStack` 专门兜这条（把 `DualMode` 那行去掉会立刻红 3 条）。
* **客户端 IP 地址要在「报告给用户」之前筛一遍**：回环、`169.254.*`、IPv6 链路本地 `fe80::`
  （必须带 `%网卡` 才通）、以及 Teredo 那种隧道网卡地址 —— 报出去对方都连不上，是纯误导。
  另外**只绑了回环时不要列局域网地址**（`RebuildShareInfo` 里判 `IPAddress.IsLoopback`），
  那次是「本机测试用」，列一堆地址等于骗人。
* **`EmptyToVis` 是「空才显示」，非空要显示得用 `EmptyToVisInverse`**：前者是给输入框占位符用的。
  拿错了两者的提示文字会一直不出现（而且不报错），截图核对时才看得出来。
* **别用 PowerShell 的 `Get-Content`/`Set-Content` 改 XAML / 源码**：PS 5.1 默认按 GBK 读，
  会把中文写成乱码、还会把 `Title="…"` 的引号弄坏（XML 直接报 MC3000）。改文件用编辑工具。
* ⚠ 卡片里的 `CheckBox` 文案**不会自动换行**（`CheckBoxStyle` 的模板是横向 `StackPanel`，
  内容宽度不受约束），太长会被卡片边缘裁掉 —— 文案请控制在 10 个汉字以内。
* `StartHostAsync` / `ConnectAsync` 里要**先把 `IsOnline` 置起来再算连接摘要**：
  `UpdateConnectionSummary()` 靠 `IsOnline` 区分“没连上”和“连着但还没人进来”，顺序反了就永远显示未监听。

### 2.13 osu! 下图器（v0.7.0 新增，从独立小工具改写）

**来历**：原版是仓库根 `MapDownloader/` 里那个 WinForms 小程序（`net8.0-windows`、凭据存 `Properties.Settings`、
结果表是 `DataGridView`、两个 Tab）。改写时**网络与筛选逻辑照搬**，界面按本工具集约定重写成 WPF + MVVM
（一页放完、卡片式、列表用全局 `FileListStyle` + `FileRowItemStyle`）。
分工：`Core/`（纯逻辑，自检直接断言）→ `Engine/`（osu! API + `.osz` 下载）→ `ViewModels/` → `Views/`。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `BeatmapSet` / `BeatmapInfo` / `BeatmapSetSearchResult` / `OsuSearchRequest` / `OszItem` | `Core/OsuModels.cs` | API 的 DTO（`[JsonPropertyName]` 对上 snake_case）＋搜索请求模型＋「要下载的一行」（Id + 标题） |
| `OsuQuery` | `Core/OsuQuery.cs` | **纯函数**：`BuildSearchQuery`（`q/m/s/sort/nsfw/cursor_string`，顺序与原版一致；只有关键词与游标要转义）、`BuildSearchUrl`、`BuildTokenForm`（`client_credentials` + `scope=public`）、`TokenUrl` / `ApiBaseUrl` / `OAuthAppUrl` |
| `BeatmapFilter` | `Core/BeatmapFilter.cs` | **纯函数**：`MatchesDate` / `MatchesKeyCount`（含 `10+` = 10 键以上）/ `Passes`、`ParseDate`、`FormatDate`（`yyyy-MM-dd`）、`FormatKeyRange`（`4.0-7.0`）、`StatusText`、`DisplayTitle` |
| `OszNaming` | `Core/OszNaming.cs` | **纯函数**：`Sanitize`（非法字符 → `_`、截断 80、空 → `_`）、`FileName`（`{Id} {标题}.osz`）、`BuildUrl`（`{镜像}/{full\|novideo}/{Id}`，镜像可换） |
| `OsuOptions` | `Core/OsuOptions.cs` | 模式 / 状态 / 排序 / Key 数的选项（`OptionItem<string>`，**值是 API 认的字符串**）＋ `ToModeValue` / `KeyCountApplies` |
| `OsuApiClient` | `Engine/OsuApiClient.cs` | `GetTokenAsync(clientId, secret)` → `OsuAuthResult`；`SearchAsync(token, request)` → `OsuApiResult<T>`；**失败不抛异常**（401 / 404 / 超时 / 解析失败都翻成人话） |
| `OszDownloader` | `Engine/OszDownloader.cs` | `DownloadAsync(items, includeVideo, onProgress, ct)` → `OszDownloadSummary`；并发、跳过已存在、404 单独统计、取消不留半个文件；`MirrorBase` 可换（自检指向本地假镜像） |
| `MapDownloaderConfig` | `MapDownloaderConfig.cs` | 凭据（`ClientId` / `ClientSecret` / `AutoGetToken` / `AccessToken` / `TokenExpiresAtUtc`）＋搜索条件＋下载目录与并发数；`EffectiveDownloadFolder` / `HasCredentials` / `HasValidToken` 都是 `[JsonIgnore]` 计算属性 |
| `BeatmapSetItem` | `ViewModels/BeatmapSetItem.cs` | 结果表一行：展示字段构造时算好 ＋ `IsSelected`（勾选框）＋ `Tooltip`（把游玩数 / 收藏数 / 难度名塞进悬停提示）＋ 重写 `ToString()` |
| `MapDownloaderViewModel` | `ViewModels/MapDownloaderViewModel*.cs` | 两个 partial：一个管状态与命令，一个管实际操作（换 Token / 搜索 / 加载更多 / 获取全部 / 下载） |

**与原版一致的地方**（改写的底线）：搜索参数与拼接顺序、日期与 Key 数走**本地筛选**（osu! 搜索接口不支持这两项）、
`{Id} {标题}.osz` 命名、并发 5、已存在跳过、UA / Accept / Referer、含视频 = `full`、无视频 = `novideo`、
「获取全部」沿 `cursor_string` 翻页（页间隔 200ms、可停）。
默认下载目录从"exe 旁边的 `Downloads`"改成 `Data\map-downloader\downloads`（可在界面上换，跟其它功能一致）。

**故意改掉的三处**（`OszDownloader` 的类注释里也写了）：
① 先下到 `xxx.osz.part` 再改名落地 —— 原版直接写目标文件，中途断线会留下半个 `.osz`，下次还被「已存在」跳过，
等于**永久坏文件**；② 取消不再往外抛异常（原版会把它抛进 `async void` 事件处理器）；
③ 「跳过（已存在）」单独统计（原版算进"成功"，再点一次下载看起来像重下了一遍）。

**几处坑（踩过 / 已钉住）**
* **结果表的表头对齐**照抄 2.9 的做法：表头写进 ListBox 自己的 `ControlTemplate`（左边距 33、右边距 21、
  末尾再占一列 `ScrollBarWidth`），表头与数据行的列定义**必须一字不差**。这次是**放大 3 倍截图**核对的
  （连滚动条出现时也对齐）—— 别靠眼睛扫一眼就说"对上了"。
* **Client ID 按文本存**（原版就是文本框 + 用时 `int.TryParse`），判定放在 `HasCredentials` 里 ——
  免得粘贴时带个空格就写不进去。
* **换 Token 是"自动来一发"**：构造函数里 `AutoGetToken && 凭据齐全 && Token 已过期` 就后台换一次
  （和 Flash 下载器自启服务器一个套路），自检 / 无头构造 VM 用 `YLT_NO_SERVER=1` 关掉。
* **Key 数只在 Mania（`m=3`）下有意义**：`KeyCountEnabled` 控制下拉框可用性，切走模式时顺手把已选的 Key 数清空
  （原版也是这么灰掉的）。
* **`FolderText` 是计算属性**（直接读 `Cfg.EffectiveDownloadFolder`）：原来写成字段时，配置一变界面不跟着变
  （自检里就抓到过 `FolderText` 还是旧值）。
* `YLT_MAP_DEMO=1` 塞 12 行假结果（超长标题 / 无上架日期 / 不同 CS 都有），只为看版式与核对表头对齐，不发请求。
* 下载进度回调是从**下载线程**来的，界面更新一律走基类的 `Ui(...)`；自检里收集进度要用线程安全的队列
  （`List.Add` 并发会坏）。

**验证方式**：`MapDownloaderChecks`（60 项）——查询串 / 文件名清洗 / 本地筛选 / 下拉选项 / 配置轮子逐条断言；
再起一个**假镜像**把「下载 → 跳过已存在 → 404 → 取消 → 多级目录」整条链路真跑一遍；
最后无头构造 VM 走一遍筛选 / 勾选 / 下载流程。
⚠ 假镜像**故意不用 `HttpListener`**：它要往 http.sys 注册前缀，有些环境（受限沙箱）`HttpListener.IsSupported`
直接是 `false`，那一整组就只能"跳过"（Flash 下载器那组就吃过这个亏）。改用裸 `TcpListener` 手写几十行
HTTP/1.1 响应，下载器只认状态码和 body，到哪儿都能跑。

### 2.14 视频 / 音乐播放器（v0.8.0 新增）

**定位**：左边是工具列表（外壳，不动），中间播放（视频 / 音频封面 + 进度条 + 一排控制按钮），右边是播放列表。
功能上对齐网易云那种本地播放器：打开文件夹 / 文件 / 拖放、列表循环 / 单曲循环 / 随机、可调步长的快进快退、
音量、记住上次的列表与进度。

分工：`MediaTypes` / `MediaTime` / `MediaScanner` / `PlaylistMerger` / `PlayQueue`（**纯逻辑**，自检直接断言）
→ `MediaPlayerViewModel`（3 个 partial：状态与设置 / 列表 / 播放控制）
→ `MediaPlayerView.xaml(.cs)` + `MediaElementPlayback`（真播放器：把 `MediaElement` 包成 `IMediaPlayback`）。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `MediaKind` / `MediaIncludeMode` | `MediaTypes.cs` | 音频 / 视频；读取方式（一并读取 / 仅音频 / 仅视频）；`KindOf` `IsAudio` `IsVideo` `Accept(path, mode)` `Describe` `KindText` `KindGlyph` `DescribeExtensions` |
| `MediaTime` | `MediaTime.cs` | **纯函数**：`Format`（`3:05` / `1:02:03`）· `FormatSeconds` · `RoundStep`（**0.1 秒精度 + 夹在 0.1~60 秒**）· `Clamp` · `Seek(position, duration, delta)` · `Ratio` · `FromRatio` |
| `MediaScanner` | `MediaScanner.cs` | `EnumerateFiles(folder, recursive, mode)` / `BuildItems(...)` / `FromFiles(paths, mode)`；读不动的子目录**跳过不抛异常**；按**自然顺序**排序（`2.mp3` 在 `10.mp3` 前） |
| `NaturalStringComparer` | 同上 | 数字段按数值比、非数字段大小写不敏感；`Instance` 直接用（也可以拿去排别的列表） |
| `PlaylistMerger.Merge(existing, incoming, append)` | `PlaylistMerger.cs` | **纯逻辑**：追加（保留下原列表 + 补新的）或覆盖（只用新的一批）；按完整路径**大小写不敏感去重**；返回 `MergeReport(Items, Added, Skipped, Displaced, Describe(append))` |
| `PlayQueue` | `PlayQueue.cs` | **纯逻辑**：`Mode`（`LoopAll` / `RepeatOne` / `Shuffle`）· `Next(count, autoAdvance)` · `Previous(count)` · `SetCurrent(index)` · `Reset()`；随机模式维护洗牌表（一轮不重复，重洗不会紧接着重放上一首）；`ModeText` / `ModeGlyph` / `ModeTooltip` / `Cycle` |
| `MediaItem` | `MediaItem.cs` | 列表行：`FullPath` `FileName` `Title` `Folder` `Kind` `KindText` `KindGlyph` `Number` `IsPlaying` `Tooltip`；**不碰播放器**（所以自检可以随便造） |
| `IMediaPlayback` | `IMediaPlayback.cs` | 播放器抽象：`Load/Play/Pause/Stop/Seek/Close` + `Position/Duration/IsPlaying/HasVideo/Volume` + `Opened/Ended/Failed` 事件 |
| `MediaPlayerConfig` | `MediaPlayerConfig.cs` | 全部设置（见下） |
| `MediaPlaylistState` | `MediaPlaylistState.cs` | 「记住上次」的快照：`Paths` + `CurrentPath` + `PositionSeconds` |
| `MediaElementPlayback` | `Views/MediaElementPlayback.cs` | 真播放器：`LoadedBehavior/UnloadedBehavior=Manual`、`ScrubbingEnabled=true`、`Seek` 先夹范围、`MediaFailed` 翻成人话 |
| `MediaPlayerViewModel` | `ViewModels/*.cs` | 配置桥接 + 界面状态 + 命令；`.Playlist.cs` 管打开/增删/恢复，`.Playback.cs` 管播放/快进快退/上一首下一首 |

#### 设置项（`Config\media-player\config.json`）

| 设置 | 存的值 | 说明 |
| --- | --- | --- |
| 读取哪些文件 | `IncludeMode`（枚举字符串） | 音频 + 视频 / 仅音频 / 仅视频；**打开文件与拖放也按它过滤**，不然设置就没意义 |
| 连子文件夹一起读取 | `ScanRecursive`（默认 true） | 递归扫描；递归在后台线程跑（`IsBusy`） |
| 再开一个文件夹 | `AppendWhenOpeningFolder`（默认 true=追加） | 追加 / 覆盖；**「打开文件」按钮和拖放一律追加**（那个设置只管文件夹） |
| 快进 / 快退步长 | `SeekStepSeconds`（默认 5.0） | 0.1 ~ 60 秒、**0.1 秒精度**；滑块 + 数字框都能改；`SeekStepEffective` 负责规整 |
| 播放顺序 | `PlayOrder`（默认列表循环） | 与播放条上那个「循环 / 单曲 / 随机」按钮是同一个值 |
| 时间显示 | `TimeDisplayMode`（默认 `Elapsed`） | 进度条**左边**那个时间：`Elapsed` = 已播放（0:00 → 总时长），`Remaining` = 剩余倒计时（总时长 → 0:00）；**右边始终是总时长**；纯逻辑在 `MediaTimeDisplay.Format` |
| 音量 | `Volume`（默认 80） | 0 ~ 100；`VolumeEffective` 夹范围 |
| 恢复上次的播放列表 / 位置 | `RestorePlaylist` / `ResumeLastPlayed` | 列表快照在 `Data\media-player\playlist.json`；恢复了也**不自动出声** |
| 后台播放 | `BackgroundPlayback`（默认 true） | 切走时继续出声，并在外壳左下角显示迷你播放条；关掉 = 切走即停 |
| 上次打开的文件夹 | `LastFolder` | 文件对话框的起始目录 + 设置页上的一行提示 |

> **时间显示**（v0.9.7）：左侧那个时间由纯函数 `MediaTimeDisplay.Format(position, duration, mode)` 算 ——
> `Elapsed` 从 `0:00` 数到总时长，`Remaining` 从总时长倒数到 `0:00`（还差多少播完）；**右侧永远是总时长**。
> 切换下拉立刻刷新（不等 200ms 计时器）；倒计时夹在 0 ~ 总时长，时长还没读到显示 `--:--`。

#### 全屏 · 后台播放 · 迷你条（v0.9.0 加的三件）

这三样都**不能只在播放器里做**，需要外壳配合，所以 Core 里定了两个契约（`Core/Tools/IBackgroundTool.cs`）：

| 契约 | 谁实现 | 干什么 |
| --- | --- | --- |
| `IFullscreenHost` | `MainWindow`（外壳） | `IsFullscreen` / `SetFullscreen(bool)`：进 / 出全屏，返回操作后的状态 |
| `IBackgroundTool` | 播放器的 ViewModel | `KeepAliveOnLeave`（切走要不要保住视图）· `ShowMiniPanel`（要不要在左下角浮小窗）· `BackgroundStateChanged` 事件 |

**全屏（真·整窗全屏 + 沉浸式控制条）**：播放器只管切 `IsFullscreen` 并抛 `FullscreenChanged`，界面代码后置
找到外壳去操作窗口。外壳的做法是：

1. 把标题栏 / 工具条 / 状态栏三行的**高度改成 0**、侧边栏**列宽改成 0**（边框跟着一起消失）；
2. `_fullscreen = true` 之后再 `WindowState = Maximized`，让 `WM_GETMINMAXINFO` 走「**整块显示器**」而不是工作区；
3. `Topmost = true`（盖住其它普通窗口），并把这圈非客户区的可拖拽边框推到屏幕外面；
4. **把 Windows 任务栏真的藏起来**（`TaskbarHider`，见下面的「藏任务栏」）；
   退出时反过来：先 `_fullscreen = false` 再还原 `WindowState` 与各行列尺寸，并把任务栏还回去。

**⚠ 全屏底部一条黑带 / 控制条被裁（v0.9.5，`MainWindow.ClearFullscreenRegion`）** —— v0.9.3/0.9.4 之后暴露的第二个坑：

* 现象：任务栏确实藏掉了、窗口 `GetWindowRect` 也是 1928×1088，但**屏幕最下面约 48px 露出黑色桌面**，
  全屏控制条最底下的状态文字被裁掉（用户原话：“播放按钮下方显示不全，是不是只把任务栏涂黑了”）。
* 根因：最大化时 **Windows 给窗口套了一个「工作区大小」的窗口区域（Region）** —— `GetWindowRgn` 返回
  `(4,4)-(1924,1036)`（正好 1920×1032 工作区），窗口矩形 1088、**实际可见 / 命中区域只有 1032**。
  `WM_GETMINMAXINFO` **只改窗口矩形，不改 Region**。
* 修法：进全屏时 + 看门狗里调 `SetWindowRgn(hwnd, IntPtr.Zero, true)` 清掉
  （`MainWindow.ClearFullscreenRegion`：先用 `GetWindowRgn` 判断有没有区域，避免每 400ms 无谓重绘）。
* ⚠ 清掉 Region 后全屏窗口会**真的盖住任务栏**，所以失活 / 最小化时必须把 `Topmost = false`（`SyncTaskbar`）——
  否则“还回去的任务栏”会被全屏窗口继续压在下面，用户 alt-tab 出去会以为任务栏没了。
* 用 `GetWindowRect` / `GetClientRect` **看不出来**（两个都是 1088）；判断这件事得用 `GetWindowRgn` +
  `WindowFromPoint`（在屏幕底部点一下，命中 `Progman` = 没盖住）。

**藏任务栏（v0.9.3，`Core/Services/TaskbarHider.cs`）** —— 这是 v0.9.0~v0.9.2 一直没修好的那块：

* **“铺满 + 置顶”盖不住任务栏**：任务栏自己也是置顶窗口，z 序不一定听我们的（有的机器/主题上照样露出来）。
  所以改成真的藏：`FindWindow("Shell_TrayWnd")` + 每块副屏的 `FindWindowEx("Shell_SecondaryTrayWnd")`，
  然后 `ShowWindowAsync(SW_HIDE)` + `SetWindowPos(SWP_HIDEWINDOW)` + `ShowWindow(SW_HIDE)` 三管齐下，
  **以 `IsWindowVisible` 的真实结果为准**（`ShowWindow` 的返回值靠不住：窗口本来是隐藏的它也返回 False）。
* **explorer 会把任务栏自己显示回来**（切激活窗口、桌面刷新……），所以外壳在全屏期间挂了个 **400ms 的看门狗**
  （`MainWindow._taskbarWatch`）：`Hide()` 每次先检查上次藏的那些窗口有没有又冒出来，冒出来就重新藏。
  `Hide()` 很便宜、幂等，**不要在里面 `Thread.Sleep` 等它生效**（那是 UI 线程，全屏那一下会卡 200ms；
  v0.9.2 之后那版就写了 4×50ms 的等待，手感很糟）。
* **什么时候还回去**：退出全屏 / 窗口失活 / 最小化 / 关窗口 / 进程退出（`AppDomain.ProcessExit`、未处理异常钩子）。
  失活就还原是有意的 —— 用户 alt-tab 出去不能发现任务栏没了。
* ⚠ **藏不住时的排查顺序**：先看程序启动时打的控制台「完整性自检」（v0.9.4，见下面），再设
  `YLT_TASKBAR_TRACE=<文件>` 看每次藏 / 还的 Win32 错误码。
  **错误码 `5`（拒绝访问）= 本进程完整性级别比任务栏低**（Windows 的 UIPI 不允许跨级别动别人的窗口），
  这时 `ShowWindow` / `SHAppBarMessage(ABM_SETSTATE)` 全都会失败，**代码怎么写都没用**。
  v0.9.3 真机上就是这么个坑：工作区被别的沙箱工具留下了 `Low Mandatory Level` 标记，exe 以 Low 权限运行
  （`whoami /groups` 里看不到，得读进程令牌或用 `icacls <exe>` 看 `Mandatory Label`）。
  修法：`icacls <目录> /setintegritylevel "(OI)(CI)Medium" /T`，然后重启程序。
  ⚠ **标签是标在“文件”上的，不是目录**：不加 `/T` 只改目录、已有的 `exe/dll` 仍然是 Low（v0.9.4 实测：
  目录是 Medium，`bin` 里 143 个文件是 Low）；**用低权限沙箱重新编译**也会把输出再标回 Low。
  正常安装/发布出去的 exe 不会带这个标记。
* 自检**只测只读的完整性判定**（`IntegrityCheck` 的等级归类 / 报告文案，9 项），**不真藏任务栏** ——
  跑测试时把开发者自己的任务栏藏起来太吓人；真机验证见第 6 节。

**启动时的「完整性自检」（v0.9.4，`Core/Services/IntegrityCheck.cs`）** —— 全屏藏不住任务栏时**先看控制台**：

* `App.OnStartup` 第一件事调 `IntegrityCheck.WriteReport()`：把「进程完整性级别 + 任务栏窗口句柄 / 可见性 + 结论」
  三行打到控制台（从终端 / `dotnet run` 启动可见；双击启动没有控制台，不显示也不报错），同时写 `Debug` 输出；
  设了 `YLT_TASKBAR_TRACE` 还会追加到那个文件。
* 级别低于 `Medium` 时结论直接写「藏不住任务栏（错误码 5）」并给出
  `icacls "<exe 所在目录>" /setintegritylevel "(OI)(CI)Medium" /T` 的修复命令 ——
  这样「权限问题」和「代码问题」一眼就能分开，不用再猜。
* 实现是**只读**的：`OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)` 读自己的令牌，
  `FindWindow("Shell_TrayWnd")` 找任务栏；绝不隐藏 / 显示任何窗口，所以自检程序能直接断言。
* 自检程序启动时也会打一行 `IntegrityCheck.Summary()`，例如
  `进程完整性=Medium（0x2000）· 任务栏窗口 0x…（可见=True）· 全屏可藏任务栏=是`。
* ⚠ **从 IDE（Rider / Visual Studio）里看是乱码**（v0.9.5 修）：它们按 UTF-8 读 stdout，而 .NET 默认按
  控制台代码页（中文系统 936/GBK）写。`WriteReport` 在 `Console.IsOutputRedirected` 为真时切 UTF-8，
  **真正的控制台保持原样**（否则在 cmd 里反而会乱）。

播放器界面这一侧的「沉浸式」做法（**全在 `MediaPlayerView` 里**）：

* 控制条（进度 + 按钮 + 状态）包在 `Border x:Name="ControlBar"` 里，`Grid.Row` 由 `ControlBarStyle` 的
  DataTrigger 在全屏时改成 **0 + 底部对齐 + 上透下黑的渐变背景** → 它就从「画面下面一排」变成「浮在画面上」；
  画面那块 Border 同时在全屏时 `Grid.RowSpan=2`、去圆角去边框，于是画面铺满整屏；
  ⚠ 默认的 `Grid.Row="1"` **必须写在 Style 的 Setter 里**：直接写在 Border 上属于本地值、优先级高于
  DataTrigger，全屏时 `Grid.Row=0` 会**静默失效**（v0.9.5 才查出来；现在已挪进 `ControlBarStyle`）。
* 控制条 `Visibility` 绑 `ControlsVisible`：**鼠标一动就显示**（`PreviewMouseMove` 隧道事件，画面上的
  `MediaElement` 也盖不住），**2.5 秒不动就隐藏**，同时 `Cursor = Cursors.None` 把鼠标指针也藏了；
  鼠标停在控制条上 / 正在拖进度条时不隐藏；
* 画面右下角那个圆钮（普通模式的全屏入口）在全屏时藏起来（`ShowCornerFullscreenButton`），
  改用控制条最右边的「退出全屏」—— 否则它会挡画面。
* 控制条里的大播放键**独自占中间那个 `Auto` 列、左右两列都是 `*`**，它的中心才等于控制条中心；
  别把「快退 + 播放 + 快进 + 停止」当成一组居中 —— 组居中 ≠ 播放键居中，全屏时会明显偏左（v0.9.6 修）。

> ⚠️ **别用 `Left/Top/Width/Height` 直接铺满屏幕**：那样会被窗口管理器夹回**工作区**（实测 1920×1080 的屏只拿到
> 1920×1032，任务栏那 48px 空着）。正路只有「临时改 WM_GETMINMAXINFO 再最大化」这一条。
> ⚠ **截图验证全屏只能看版式**：`RenderTargetBitmap` 抓不到 `MediaElement` 的视频画面（抓到的是空白/怪色块），
> 真机播放时的画面缩放靠 `Stretch="Uniform"` 保证。核对版式用 `YLT_PLAYER_FULLSCREEN=1` +
> `YLT_PLAYER_HIDE_CONTROLS=1` 这两张图对照。

**后台播放**：外壳的 `CloseCurrentTool()` 在调完 `vm.NotifyClosed()` 之后问一句 `KeepAliveOnLeave`：

* 要保活 → 把**视图实例**存进 `_keptAliveView`（**不销毁**：视图一销毁，里面的 `MediaElement` 就没了、声音就断），
  并订阅它的 `BackgroundStateChanged`；
* 不要保活 → 丢掉引用（本来也没人用了）。

播放器这边配套改了两处：

* `OnClosed()`：`KeepAliveOnLeave` 为真时**不清播放器、不停**，只 `SavePlaylistState()` 就返回；
* `MediaPlayerView.OnUnloaded`：`KeepAliveOnLeave` 为真时**不调 `_playback.Close()`**，只停计时器。

切回来时外壳把同一个视图挂回 `CurrentView`，并调 `ToolViewModelBase.NotifyOpened()`（重置 `_closed` +
重新 `ConfigStore.StartWatching()`，否则这个 VM 的配置自动保存会一直停着）。

**迷你播放条**（`Views/MiniPlayerPanel.xaml`，属于**主项目**，不是播放器项目）：

* 外壳用 `MiniPanelSource`（= 那个后台工具的 ViewModel）当它的 `DataContext`，用 `MiniPanelVisible` 控制显示；
* 显示条件 = 「后台工具存在 **且** 它不在前台 **且** 工具的 `ShowMiniPanel` 为真」；
* **标题滚动（v0.9.2 改成「来回滚」）**：**放得下就一动不动**（完整显示）；放不下才滚 ——
  先右→左滚到 `-(标题宽 − 可视宽)`（最后一个字正好贴着右边）→ 停 1.2 s → 再左→右滚回 0（第一个字回来）
  → 停 → `RepeatBehavior.Forever` 循环。速度 34 px/s；只溢出几像素时至少滚 1 s（不然像闪一下）。
  实现是 `DoubleAnimationUsingKeyFrames` 打在 `TranslateTransform.X` 上；
  **「要不要滚 / 一趟多久」抽成纯函数 `MiniPlayerPanel.ScrollDuration(textWidth, viewWidth)`**，自检直接断言。
  ⚠ 别再用“两份文本首尾相接一直滚”的老写法：短标题也会被滚走，看的人反而读不全（v0.9.0 就是这么错的）。
* 三个按钮：**左键**走各自的 `Command`（快退 / 播放暂停 / 快进），**右键**在代码后置里接成
  上一首 / 下一首 —— 跟播放器页里那两个按钮完全一致（`Button.Command` 只认左键）；
* 标题那一行本身是个按钮，点它 `OpenBackgroundToolCommand` → 回到播放器；
* ⚠ 它的 `DataContext` 是**播放器 VM**，所以“显示与否”这类外壳属性要用
  `{Binding DataContext.Xxx, RelativeSource={RelativeSource AncestorType=Window}}` 取，别写成 `{Binding Xxx}`。

#### 几个必须知道的约定

* **播放器必须抽成 `IMediaPlayback`**：`MediaElement` 要真的解码、还要活在界面线程上，无头自检造不出来。
  自检里塞一个 `FakeMediaPlayback`（只记录被要求做了什么 + 手动抛 `Opened/Ended`），
  就能把「播放 → 快进快退 → 播完自动接下一首 → 单曲循环 → 随机 → 记住上次」整条链路跑完（99 项断言）。
  **凡是新加播放相关逻辑，先想「假播放器能不能断言」，不能就别写进 VM。**
* **`MediaElement` 的两个行为必须设 `Manual`**：`LoadedBehavior`/`UnloadedBehavior` 留默认值时，
  它会自己在加载时开播、卸载时自己关掉，跟 ViewModel 的命令打架；时长要等 `MediaOpened` 之后才可信。
* **进度的来源只有一个**：界面里 200ms 的 `DispatcherTimer` 读 `IMediaPlayback.Position` 调
  `vm.UpdatePosition(pos, duration)`；拖进度条时 `_seeking=true` 把计时器挡住，否则滑块会被拽回去。
  进度条本身是 `ProgressBar`（`IsHitTestVisible=False`）+ 一个透明 `Grid` 接鼠标事件（点一下 / 按住拖都能跳）。
* **上一首 / 下一首是两个独立命令**（`PreviousCommand` / `NextCommand`），
  **右键快退 / 右键快进**在代码后置里接到这两个命令上：`Button.Command` 只响应左键，右键连 `Click` 都不会触发。
* **`PlayItem` 里只在 `index != _queue.Current` 时调 `SetCurrent`**：否则「播完自动下一首」也会被当成
  「用户跳着点」，随机模式那一轮的顺序就作废了（表现为随机播放老是回到开头几首）。
* **单曲循环是 VM 自己处理的**（`OnPlayerEnded` 里 `Seek(0)+Play`）：`PlayQueue` 只回答「下一首是谁」，
  `RepeatOne` 的 `Next(..., autoAdvance: true)` 返回原地，但「重放」这个动作必须有人做。
* **覆盖模式下正在播的那首被顶掉了要停播**（不然界面显示着一首已经不存在的曲目）。
* **恢复上次时文件可能已经不在**：逐条 `File.Exists` 过滤，并报「N 个已不存在，已跳过」。
* **`ManageClosed`**：`OnClosed` 里 `SavePlaylistState()` + `DetachPlayer()`（切走工具就停声，不留后台播放）。

#### 界面层的坑（都实测过）

* ⚠ **控制按钮的图标别用 `⏪ ⏩ ⏹ 🔁 🔂 🔀` 这些字符**：本机字体没有这些码位，
  会被渲染成「带方框的豆腐块」（截图核对时才发现，肉眼一眼能看出是坏的）。
  现在全部用**矢量 `Path`**（播放/暂停/快进/快退/停止），齿轮用 `Segoe MDL2 Assets` 的 `E713`（打开后变 `E72B`），
  播放顺序按钮直接写两个字（循环 / 单曲 / 随机）。**加新图标照这个来，别赌字体。**
  （`🎵 🎬 📂 ✕` 这几个 emoji 在本机是能正常显示的，列表行与「文件夹 / 文件 / 移除」按钮可以用。）
* **设置面板是同页面板**，用一层不透明 `Border` **盖住左边整列**（播放区 + 进度条 + 按钮），
  不是子窗口：子窗口要自己管生命周期（关工具时还得负责关掉它），这一屏一页放得下就没必要。
* **设置面板要盖整列而不是只盖视频区**：只盖视频区时内容比可视区高，底部的「恢复默认设置 / 返回播放」被切掉。
* **列表行里正在播的那行**用 `DataTrigger` 把行号换成 `▶`（不额外占宽度，也不影响对齐）。
* 播放条右侧那一组（模式按钮 + 音量）与左边 5 个按钮共用同一行：`Grid` 三列（`Auto`/`*`/`Auto`），
  中间那组 `HorizontalAlignment=Center`，窗口变窄时也不会错位。
* `YLT_PLAYER_DEMO=1` 塞 6 行假列表（点了会提示文件不在了）、`YLT_PLAYER_SETTINGS=1` 直接展开设置面板 ——
  截图核版式用，跟 `YLT_MAP_DEMO` / `YLT_CHAT_DEMO` 一个套路。

**验证方式**：`MediaPlayerChecks`（99 项）——扩展名归类 / 时间与快进快退 / 自然排序 / 真扫临时目录 /
合并去重 / 三种播放顺序（随机用固定种子断言“一轮不重复”）/ 配置落盘 / 无头 VM 全流程。
⚠ 自检里**不要碰真的 `MediaElement`**（要解码、要界面线程，无头跑既慢又依赖本机编解码器）；
界面上能不能播只能靠手动 + 截图确认。

---

### 2.15 多级分类目录 · 类别页 · 可点击面包屑（v0.10.0）

**定位**：主页从"平铺卡片墙"升级为**可任意分层的目录树**。工具可以声明 `CategoryPath`（任意层级），
外壳自动建树、生成类别页与可点击面包屑；**不写 `CategoryPath` 的老工具仍在根层，行为完全不变**。
「音游工具」就是第一个类别（内含 osu! 下图器 + Key/LN/等级计算器四个工具）；v0.11.0 起第二个类别是「棋牌」（联机五子棋 + 联机象棋，见 2.18）。

分工：`ITool.CategoryPath`（契约）→ `Core/Tools/ToolCatalog.cs`（**纯逻辑**建树/查找/面包屑，自检直接断言）
→ `Models/ShellCategories.cs`（可选的类别图标/描述/排序"补妆"）→ `MainViewModel`（导航状态机）
→ `HomeView` / `CategoryView` / `MainWindow`（界面）。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `ITool.CategoryPath` | Core/Tools/ITool.cs | 默认空数组（根层）；工具覆盖为 `new[] { "音游工具" }`，或任意深度 `new[] { "音游工具", "谱面处理" }`；`Category` 仍是标签/排序用 |
| `ToolCategoryInfo` | Core/Tools/ToolCatalog.cs | 类别元数据：`Path` / `EmojiIcon`（默认 📁）/ `Description` / `Order` |
| `ToolCatalogNode` | 同上 | `Name` `Path` `Parent` `Children` `Tools` `AllTools` `ToolCount` `EmojiIcon` `Description` `Ancestors` `Breadcrumb` `IsAncestorOf` `FindByPath` `IsRoot`；**全部只读、纯数据** |
| `ToolCatalog.Build(tools, categories?)` | 同上 | 按 `CategoryPath` 建任意深度树：同路径合并、空段忽略、类别按 `Order→名称`、工具按 `Order→名称` 排序 |
| `ToolCatalog.NormalizePath` / `BuildBreadcrumb` / `FindNodeOf` | 同上 | 路径规整 / 面包屑生成 / 按工具找所属节点（纯函数，自检用） |
| `ShellCategories` | Models/ShellCategories.cs | 静态清单：给「音游工具」配 🎵/描述/`Order=10`；以后加类别元数据只在这里加一条 |
| `CategoryCard` / `BreadcrumbItem` | Models/ | 类别卡片展示模型（名称/emoji/描述/N 个工具）/ 面包屑项（Title、IsCurrent、NavigateCommand） |
| `NavItem` / `ShellPageKind` | ViewModels/MainViewModel.cs | 侧边栏项（首页/根层类别/根层工具）；当前页面类型 Home/Category/Tool |
| `MainViewModel` | 同上 | `_catalogRoot`、`CurrentNode`、`Breadcrumbs`、`CategoryCards`、`NavigateCategoryCommand`/`NavigateParentCommand`/`NavigateBreadcrumbCommand`、`ParticlesEnabled`、`OpenSettingsCommand`；**后台保活/迷你条逻辑原样保留** |
| `HomeView` / `CategoryView` | Views/ | 主页 = 搜索 + **全部分类** + 标签条 + 根层工具；类别页 = 标题/描述/统计 + 子类别卡 + 直接工具卡 + 类别内搜索 + 空状态 |
| `MainWindow` 工具条 | MainWindow.xaml(.cs) | 可点击面包屑（🏠 主页 › 类别 › 工具）+「← 返回上一级」；当前项不可点 |

**约定**

* **主页搜索始终在"全部工具"里扁平过滤**（分类里的工具也能搜到）；类别页的搜索只在本类别子树内过滤。
* **类别页只平铺「直接工具 + 子类别卡片」**，不递归平铺子分类里的工具 —— 任意层级下界面都不会乱。
* 侧边栏只列根层类别与根层工具；进入某类别子树时侧边栏高亮该根类别。
* 新增工具归入某类别：只要实现 `CategoryPath`，**不用改外壳**；要类别图标/描述/排序：在 `ShellCategories` 加一条。
* 调试入口：`YLT_NAV_CATEGORY=<路径>`（如 `音游工具`，支持任意深度用 `/` 分隔）。
* 自检：`NavigationChecks`（53 项）覆盖任意深度建树/同路径合并/排序/`FindByPath`/面包屑/计数/搜索/空类别。

### 2.16 窗口级粒子效果 + 主页设置（v0.10.0）

**定位**：鼠标粒子效果：粒子跟随鼠标生成、向随机方向飞出并淡出；
**左键爆发**（速度+数量拉满）、**右键加量**；窗口失活/最小化/鼠标离开自动清空暂停。
开关在主页右上角「⚙ 主页设置」窗口里（默认开），存 `Config\shell\config.json` 的 `showParticles`。

**实现要点（相对经典写法的改进）**：按帧固定增量在高刷屏会速度翻倍 → 按真实时间归一化；
每帧 `new Ellipse/SolidColorBrush` → 改 `DrawingContext.DrawEllipse` + 冻结画刷调色板（每帧零分配）；
`CompositionTarget.Rendering` 静态事件从不取消（泄漏）→ Loaded/Unloaded 成对挂摘、关闭即退订；
没有粒子上限 → 硬上限 600。

分工：`ParticleEngine`（纯逻辑，38 项自检）→ `ParticleOverlay`（WPF 元素）→ `MainWindow` 根部一层 → 设置窗口开关。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `Particle` / `ParticleEngine` | Core/Particles/ | 可注入随机种子；常量：默认速度 0.15 / 上限 3 / 每帧衰 0.1；数量 1→10、每帧衰 0.5；寿命 30~89 帧；初始直径 5；`Update(frameSteps)` 按 60fps 归一（单步上限 4、有生成累加器）；`Active` / `Speed` / `Count` / `EmitterX/Y` / `Particles` / `TotalSpawned`；`SetEmitter` `BoostSpeedAndCount` `BoostCount` `Clear` `Reset` `Reseed` |
| `ParticleOverlay` | Core/Controls/ | `FrameworkElement` + `OnRender`；DP：`IsActive`（默认 false）、`ParticleColor`（默认 #F2EEFF 淡紫白）、`GlowColor`（柔光）；Loaded 挂 Window 鼠标/Rendering 事件、Unloaded/Closed 全摘；DPI/最大化/全屏自适应；`YLT_PARTICLE_DEMO=1` 无鼠标演示 |
| `ShellConfig.ShowParticles` | Models/ShellConfig.cs | 默认 `true`；`MainViewModel.ParticlesEnabled` 的 get/set 直接读写它（自动落盘） |
| `ShellSettingsWindow` | Views/ | 「主页设置」窗口：外观（粒子开关）+ 说明；`Owner=主窗口`、`CenterOwner`、`ShowInTaskbar=false`；Esc / 完成关闭；以后设置项加在这里 |
| `MainWindow` 接线 | MainWindow.xaml | 根部 `<core:ParticleOverlay Grid.RowSpan="4" Panel.ZIndex="900" IsHitTestVisible="False" IsActive="{Binding ParticlesEnabled}" />` |

**注意**

* 粒子层**必须** `IsHitTestVisible="False"`，否则全窗口点不动。
* 覆盖层**必须**放在 Window 内的 Grid 里铺满（`Grid.RowSpan`），别塞 StackPanel / 写死宽高（会塌成 0）。
* 关闭开关时除清空粒子外要**退订渲染事件**（空闲零 CPU）；窗口失活/最小化也暂停。
* 截图验证：`YLT_PARTICLE_DEMO=1`（无鼠标也能看到粒子）+ `YLT_SHOT`；设置窗口用 `YLT_OPEN_SETTINGS=1`。
* 自检：`ParticleChecks`（38 项，纯逻辑：生成/寿命/衰减/开关/上限/确定性）。

### 2.17 osu!mania 三件套：Key 转换 / LN 转换 / 等级计算（v0.10.0）

**来历**：KeyConverter / LNTransformer / LevelCalculator 三个算法工具。**算法逐行照搬**，界面按本工具集 WPF + MVVM 重写；
解析器不再用手写 `OsuFileV14.cs`，改用 **OsuParsers 1.7.2（NuGet，MIT，netstandard2.0）**，写回也用它。

分工：`Core/`（解析桥 + 工作模型 + 纯算法引擎，自检直接断言）→ `Tool`/`Config` → `ViewModels/` → `Views/`。
三个工具同一个项目 `src/YuLiangToolkit.Tools.OsuMania/`，各是一个独立 `ITool`，都挂在「音游工具」类别：

| 工具 | Id | 名称 | Emoji | Order |
| --- | --- | --- | --- | --- |
| Key 转换器 | `osu-key-converter` | osu! Key 转换器 | 🎹 | 51 |
| LN 转换器 | `osu-ln-transformer` | osu! LN 转换器 | 🍜 | 52 |
| 等级计算器 | `osu-level-calculator` | osu! 等级计算器 | ⭐ | 53 |

`CategoryPath => new[] { "音游工具" }`（与 `map-downloader` 同类）。三个工具入口一致：
拖 `.osu` / 文件夹 → 文件列表 → 参数（`ToolConfig` 自动保存）→ 后台串行批量执行 → 进度/日志/统计 → 打开输出目录。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| OsuParsers（NuGet） | 项目 `PackageReference` | `BeatmapDecoder.Decode(path)` → `OsuParsers.Beatmaps.Beatmap`；`beatmap.Save(path)`；mania 对象 `ManiaNote`/`ManiaHoldNote`、`GetColumn/SetColumn`。⚠ **解码器是 static 状态、非线程安全**（实测并发会串台/异常）→ 解析桥里加锁、批量串行；⚠ `Save()` 是全量重排：未知段落/未知键/部分注释会丢（早期版本也是重写，但库能多保留 `[Editor]` 等已知段） |
| `OsuManiaBeatmap` / `ManiaNote` / `OsuTimingPoint` / `OsuBeatmapMetadata` / `OsuNoteExtras` | Core/OsuManiaBeatmap.cs | 与 UI/解析库解耦的工作模型：`KeyCount` `OverallDifficulty` `Metadata` `TimingPoints` `HitObjects` `StarRating`；`ManiaNote` 有 `Column`(0 基) `StartTime` `EndTime` `IsLongNote` `HitSound` `Extras` `IsLN` `Clone` `ToNote`；`ColumnX(keys,column)` 通用列坐标（3~10k 与 `KeyX` 表等价，兼容 1k/2k）；`TimingPointAt/BeatLengthAt` |
| `OsuBeatmapFile` | Core/OsuBeatmapFile.cs | `TryLoad/Load`（校验 mania、键数 1~10、解析失败给中文原因）；OsuParsers ↔ 工作模型双向映射（保留 HitSound/Extras/TimingPoints，列号 = `GetColumn(keyCount)`）；`Save()` 按旧规则写「原目录 + `{Artist} - {Title} ({Creator}) [{Difficulty}]` 去掉 9 个非法字符 + .osu」并返回输出路径；解码/保存加 `DecodeLock` |
| `StarRatingCalculator` / `EasyObject` / `Utils` | Core/Difficulty/ | 星数算法（**不是** osu!lazer 源码，是第三方 Star Rating Rebirth / `srcalc` 思路的 C# 重写；`Utils.cs` 来自 ppy osu!lazer，MIT）；入口 `CalculateStarRating(IReadOnlyList<ManiaNote>, od, keys, rate=1.0)`，失败返回 0。**数值基准 10/10 差 0** |
| `LevelCalculator` | Core/LevelCalculator.cs | 旧三段拟合：`[2.76257856739498, 10.5541834716376]` 内 `6.9615·e^(0.1374·SR)`；低于下界 `3.6198·SR`；高于上界 `2.791·SR+0.5436`；其余 `NaN`；`FormatLevel` |
| `KeyConverterEngine` / `KeyConverterOptions` | Core/KeyConverterEngine.cs | 旧 `ApplyToBeatmap` + `ProcessArea` 逐行改写：只支持升 key；按 Gap 公式 `29998.8584·e^(-0.3176·Gap)+347.7248`（Gap=0→∞）分块；每块随机复制列 + Blank 空白列 + 随机插入列；按列 50ms/10ms 合并清理；改 Difficulty 前缀 `[源To目标C...]` 与 Creator。`Apply(beatmap)` 只改内存、`Apply(file)` 转换+保存 |
| `LnTransformerEngine` / `LnTransformerOptions` | Core/LnTransformerEngine.cs | 旧 14 档 Level（-3 Hold off、-2 Random ms、-1 Random divide、0 Regular LN、1~9 高斯密度、10 Invert）+ `AfterTransform` 按列/每 Gap 行随机重分配轨道 + `FixError` 用 `PreciseTime` 吸附网格 + OD 开关 `KeepOverallDifficulty`（默认保留原谱 OD）+ `IsConverted` |
| `OsuFileBatch` | Core/OsuFileBatch.cs | 拖放路径 → 递归枚举 `.osu`（大小写不敏感、去重）；`RunKeyConverter/RunLnTransformer` **串行**批处理 + `IProgress` + `CancellationToken`；`ConvertOne`；`OsuBatchResult` 统计文案 |
| Tool / Config / ViewModel / View | 项目根 + `ViewModels/` `Views/` | `OsuTransformViewModelBase` 复用拖放/列表/参数/进度/日志；三个工具各自的 Tool/Config/VM/View；`YLT_OSU_DEMO=1` 生成合成谱面供截图/冒烟 |

**行为说明（有意修复的已知问题）**

1. **1k/2k 不再崩**：`KeyX` 表没有 1/2 键；这里用通用列坐标公式（3~10k 与 `KeyX` 逐列相等）。
2. **HitSound/Extras 保留**：KeyConverter 收尾重建对象时把它们全丢（固定 0）；新版从源音符复制。
3. **Clean=0 = 不去子弹**：原有代码在 clean=0 分支会把整列删掉（bug），新版按参数本意"不去重"。
4. **Original LN 对所有列生效**：`originalLNObjects` 必须累加，不能在按列循环里被覆盖（否则只有最后一列生效）。
5. **OD 默认保留**：默认 `KeepOverallDifficulty=true`（不会把谱面 OD 写坏），想改 OD 才勾。
6. **没有红线不再闪退**：`TimingPointAt` 全绿点时 `RedPoints[-1]` 崩；新版转成"跳过+原因"。
7. **加载/保存用成熟解析库**：实际格式版本、`[Editor]`/`[Events]` 等已知段保留度更好（但 `Save()` 仍只保留已知段落）。
8. 进度/取消/日志/统计/参数持久化（原先只有拖放 + 一个进度条，失败静默）。

**数据/配置**

* 配置：`Config\osu-key-converter\config.json`、`Config\osu-ln-transformer\config.json`、`Config\osu-level-calculator\config.json`（参数下次打开还在）。
* 输出：**写在原谱面所在目录**（按上面的元数据文件名规则；**没有备份**，同名会被覆盖 —— 日志里会显示输出路径）。不写 `Data\`。
* 输出规则：因为工具会改 Creator/Difficulty，通常生成**新文件**；重复转换已转过的文件时输出路径会等于输入路径（原地重写）。

**自检与验证**

* `OsuManiaChecks`（158 项）：解析往返 / 保存文件名 / 非 mania / 键数校验 / StarRating / Level 公式 / KeyConverter（4→7 列范围、可复现、固定种子）/ LN 各档 / OriginalLN / FixError / 批量与取消 / 线程安全；`StarRatingChecks`（38 项）钉住星数参考值。
* 截图冒烟：`$env:YLT_OSU_DEMO="1"; $env:YLT_SHOT_TOOL="osu-key-converter"; $env:YLT_SHOT="shots\osu-key.png"`（三个 Id 各一张；`YLT_OSU_DEMO` 会生成合成 4K 谱面到 `Data\osu-*\`）。
* 限制：**OsuParsers 解码器非线程安全** —— UI 与自检都必须串行处理 `.osu`，别改成并发。

---

### 2.18 棋牌：联机五子棋 + 联机象棋（v0.11.0）

**定位**：在「联机聊天室」之外再做一个**能真下棋的 TCP 房间**：主机开监听、客户端填地址加入，
五子棋 / 中国象棋各一个工具；两个工具共用一套**通用 TCP 房间底座 + 棋牌房间协议 + 房间界面**。
同时新增「棋牌」工具大类（`ShellCategories.BoardGames = "棋牌"`，🎲，`Order = 20`）。用户最在意的两点：
**断网重连要稳**（不能一掉线就丢棋局）、**象棋要能自由摆棋**（让单马 / 让单车 / 让几先，一键默认开局）。

分工（纯逻辑 → 网络 → 界面，严格分层，自检直接断底层）：

`Core/Net`（通用 TCP 底座，**与棋牌无关，以后别的联网工具也能复用**）
→ `Tools.BoardGames/Shared`（棋牌房间协议 + 会话 + 基类 ViewModel + 房间/聊天面板）
→ 两个游戏目录（`Gomoku/`、`Xiangqi/`：规则引擎 → VM → 自绘棋盘控件 → View）。

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `RoomProtocol` | `Core/Net/RoomProtocol.cs` | 帧格式（**4 字节小端长度 = JSON 头字节数**，后面跟 JSON 头 + 裸载荷）、`BuildFrame`、`JsonOptions`（camelCase/中文不转义）、端点解析 `ParseEndpoint`/`ResolveAsync`（IPv4/域名/IPv6/带端口）、`FormatEndpoint`、本机地址分享 `ListShareAddresses`（IPv4/IPv6，过滤回环/169.254/链路本地）、傻瓜监听方案 `ChooseSimpleListenPlan`、`IsPortAvailable`、`WithPort` |
| `RoomFrame` | `Core/Net/RoomFrame.cs` | 包头：`Kind` `From` `Frame` `ReplyTo` `Payload` `Extra`；`ExtraAs<T>()` / `WithExtra()` / `New()`；载荷长度由 `BuildFrame` 自动写 |
| `RoomLink` | `Core/Net/RoomLink.cs` | 一条连接：读循环、帧收发、15s 心跳 / 90s 空闲判掉线、ping/pong 内部处理、`LatencyMs`、`IdleMs`、`FrameReceived`（**载荷先读干净再抛事件**）/`Closed` |
| `RoomHost` / `RoomClient` | `Core/Net/RoomHost.cs` `.RoomClient.cs` | 主机监听（`::` 时**必须显式 `DualMode=true`**，端口 0 = 系统分配）与客户端连接（5s 超时、可 `Abort`）；不掺棋牌语义 |
| `BoardProtocol` / `BoardMessages` | `Shared/BoardProtocol.cs` `.BoardMessages.cs` | magic=`YLTBOARD1`、版本、gameId（`gomoku`/`xiangqi`）、包类型 `hello/welcome/snapshot/action/chat/notice/request/response/bye`、请求类型 `undo/draw/rematch/resign`、`WrapSnapshot`/`ReadSnapshot`（局面包装 `{seq,hostSide,clientSide,game}`）、断线分类 `BoardDisconnectKind`（LocalLeave/PeerBye/PeerClosed/NetworkError） |
| `BoardRoomSession` | `Shared/BoardRoomSession.cs` | 主机 + 客户端共用门面：握手校验、**只允许 1 个对手**（第三人提示「房间已满」）、掉线重连（welcomeFactory 每次重给执边 + 最新局面）、聊天/提示/动作/请求/响应/快照/bye 转发、断线原因归类 |
| `BoardGameRoomViewModelBase` | `Shared/ViewModels/BoardGameRoomViewModelBase.cs` | 两个游戏 VM 的公共基类：房间生命周期、配置绑定（`IBoardGameRoomConfig`：玩家名/上次地址/端口）、地址分享、聊天、**悔棋/求和/再来一局/认输的协议流 + 确认弹层**、快照广播、**断线自动重连/对局暂停**；子类只实现 `BuildGameSnapshot` / `ApplyGameSnapshot` / `OnHostAction` / `ApplyUndoFor` / `ApplyDraw` / `ApplyResign` / `BeginNewGame` 等游戏钩子 |
| `RoomShareRow` / `BoardChatItem` | `Shared/ViewModels/` | 分享地址行 / 聊天行 |
| `RoomPanel` / `ChatPanel` | `Shared/Views/` | 两游戏共用：房间面板（模式/开房/加入/分享地址/状态/暂停重连块/离开）与聊天面板（**聊天放页面最下**，别挤掉对局操作） |
| `GomokuBoard` / `GomokuForbidden` / `GomokuGame` / `GomokuSnapshot` / `GomokuText` / `GomokuConfig` | `Gomoku/` | 五子棋：棋盘、禁手判定、对局状态机（落子/胜负/悔棋/快照）、坐标与棋谱文案、配置 |
| `GomokuViewModel` / `GomokuBoardView` / `GomokuView` | `Gomoku/ViewModels` `Gomoku/Views` | 界面：联机/同机双人、禁手设置、自绘棋盘（`OnRender`）、棋谱 |
| `XiangqiPiece` / `XiangqiBoard` / `XiangqiRules` / `XiangqiGame` / `XiangqiSnapshot` / `XiangqiSetup` / `XiangqiNotation` / `XiangqiConfig` | `Xiangqi/` | 中国象棋：棋子/棋盘/FEN、全走法 + 将军/将死/困毙/飞将/自然限着、对局状态机（含让先）、摆棋校验与让子预设、中文记谱、配置 |
| `XiangqiViewModel` / `XiangqiBoardView` / `XiangqiView` | `Xiangqi/ViewModels` `Xiangqi/Views` | 界面：自由摆棋 + 默认开局 + 让子/让先、走法提示、自动翻转、自绘棋盘、棋谱 |

#### 线上协议（与聊天室同一套帧格式，内容不同）

```
每个包 = [4 字节小端长度][JSON 头][裸载荷]
         长度只数 JSON 头自己的字节数；载荷多长由头里的 payload 说明
```

包类型（线上是字符串，加减类型不会让老版本错位）：`hello`（客户端自报门第：magic/版本/gameId/玩家名）·
`welcome`（主机回执：你的执边 + 包装局面）· `snapshot`（主机 → 客户端：完整局面）· `action`（客户端 → 主机：
走棋等动作，主机校验后执行并广播）· `chat` / `notice`（双向聊天与系统提示）· `request` / `response`
（悔棋/和棋/再来一局/认输 + 同意/拒绝）· `bye`（礼貌退出；收到 bye = 不自动重连）。

**主机是唯一权威**：棋局状态只在主机内存里推进，客户端**只从 `snapshot` 更新**；客户端动作先 `action` 发给主机，
主机校验轮次/合法性后落子再广播。因此客户端收到**任意时刻**的快照都必须能整体重建棋局（含 started/结果/让先剩余）。

#### 断线重连 / 对局暂停（用户点名重点，别改坏）

* **主机**：客户端掉线后**不重置棋局、不关房间**，保持监听等重连；`IsGamePaused = true`、
  `GamePauseStatusText =「对局暂停 · 等待对方重连」`，期间**主机不能落子**（`CanPlayNow` 必须要求 `HasPeer`）。
  对方重新连上（welcome 会带最新局面）后清暂停、提示「对方已重新连接，继续对局」。
* **客户端**：`PeerClosed`（TCP 断）或 `NetworkError`（重置/超时）→ 自动重连 **2 秒 × 最多 30 次 / 60 秒**，
  `IsReconnecting = true`、`ReconnectStatusText` 显示第几次、棋盘只读、按钮禁用；成功用 welcome 快照恢复
  （提示「已重新连接，继续对局」+ 钩子 `OnReconnected()`），失败回离线并保留地址提示手动加入。
* **不自动重连的情况**：本端主动 `LeaveRoom`（`LocalLeave`）、收到对方 `bye`（`PeerBye`，例如房主关房）、
  切到同机双人、关工具/切走工具 —— 这些会取消重连循环（`LeaveRoom` 在重连中 =「停止重连」）。
* **真实拔网线的坑**：对端拔线时旧 TCP 可能挂到 90 秒空闲才断，重连会被「房间已满」挡住 ——
  主机对「心跳停了 > 30 秒」的旧连接允许新连接**顶替**（`RoomLink.IdleMs`），正常第三人仍拒绝。
* 重连是**单实例循环**（代数号取消）；旧会话的迟到事件按会话引用丢弃；断线期间棋局/聊天记录都不清空。
* 自检可用 `YLT_BOARD_RECONNECT_MS` / `YLT_BOARD_RECONNECT_TRIES` 加速重试耗尽（生产默认 2000ms × 30）。

#### 五子棋规则与设置（`Config\gomoku\config.json`）

| 设置 | 存的值 | 说明 |
| --- | --- | --- |
| 玩家名 | `playerName` | 对方看到的名字 |
| 端口 | `listenPort`（默认 5310） | 0 = 让系统挑；开房后界面显示真实端口与本机地址 |
| 上次加入地址 | `lastJoinAddress` | 下次打开自动填 |
| 房主执子 | `hostSide`（`black`/`white`，默认黑） | 主机执黑/执白，客户端自动执另一边；再来一局自动换边 |
| **启用禁手** | `forbiddenEnabled`（默认 false） | **用户要的开关**：开 = 黑棋「三三 / 四四 / 长连（≥6）」判负（黑棋正好五连仍算赢）；关 = 双方长连都算赢 |
| 标记禁手点 | `showForbiddenPoints`（默认 true） | 黑方回合时把黑棋会形成禁手的空点标红叉（后台算 + `Ui()` 回界面） |
| 棋盘大小 | `boardSize`（15 / 19，默认 15） | 开房前/同机开局前可切 |
| 显示棋谱 | `showMoveList`（默认 true） | 下方棋谱卡 |

禁手判定（`GomokuForbidden`，纯逻辑）：以落子点为中心沿 4 个方向取 ±5 窗口 —— 正好 5 连 = 赢（优先）·
≥6 长连 = 禁手 · 同一方向存在「4 黑 + 1 空」的 5 格窗口 = 一个四 · 存在某空点能形成活四（连续 4 黑两端空）= 一个活三；
**四 ≥ 2（四四）或 活三 ≥ 2（三三）即禁手**。别去追求连珠全规则（文档里写明是"够用的判定"）。

#### 中国象棋：规则 · 自由摆棋 · 让子 / 让先 · 记谱

* 完整规则（`XiangqiRules`）：车直行、马走日（蹩马腿）、相/象走田（塞象眼、不过河）、仕/士九宫斜走、
  帅/将九宫直走 + **将帅照面**（同列中间无子时不能走成照面）、炮隔一子吃、兵/卒向前（过河可横走）；
  **不能走出被将军的棋**；将死判负、**困毙（无合法着法且未被将军）也判负**；连续 120 个半回合无吃子 = 自然限着判和。
* 自由摆棋（`XiangqiSetup`）：调色板选红/黑任意棋子点棋盘放置（再点同格 = 删除/覆盖），可清空重摆；
  **「默认开局」一键恢复标准布局**（用户点名）；FEN 可读可写。摆棋校验：双方各一个帅/将 + 在九宫内 +
  将帅不照面 + 非行棋方不得已被将军（`Validate`），有错时禁止开房/开局并在界面提示。
* 让子预设：让单马 / 让双马 / 让单车 / 让车马 / 让车马炮 / 让九子（`XiangqiHandicap`），**让子方（红/黑）可选**，
  从让子方那半边去掉对应棋子；让九子 = 去掉被让方双车双马双炮五卒。
* 让先：`0~5` 步；被让方（让子方以外那方）开局可以**连续走 N 步**再恢复正常轮换（`OpeningFreeMovesRemaining`），
  快照里要带上，重连后不能丢。
* 中文记谱（`XiangqiNotation`）：`炮二平五` / `马8进7` / `车九进一` / `相三进五` / 前车进一…；红方从右往左一~九、
  黑方从黑方右往左 1~9；同一路同种棋子用 前/中/后 区分。棋谱列表 +「复制棋谱」。

#### 配置 / 数据

* `Config\gomoku\config.json`、`Config\xiangqi\config.json`（`ToolConfig` 自动保存；两个 Config 都实现
  `IBoardGameRoomConfig` 供基类绑定玩家名/地址/端口）。
* 棋局、聊天记录、棋谱都在内存里（没有落盘）；`Data\gomoku\`、`Data\xiangqi\` 目前不使用。
* 删除配置文件 = 恢复默认选项（下次打开重建）。

#### 界面约定（两个棋盘控件都是自绘）

* 页面：外层 `ScrollViewer` → 标题行 → 左棋盘卡 + 右栏（宽 380）→ 下方棋谱卡 + 聊天卡。
  **1280×820 首屏必须能看到对局操作**（开始对局/悔棋/求和/认输/再来一局）；1024×700 不横向溢出。
  棋盘放在 `Viewbox` 里（固定逻辑尺寸、随列宽缩放），**绝不要在工具代码里改主窗口尺寸**。
* `GomokuBoardView` / `XiangqiBoardView`：`FrameworkElement` + `OnRender` 全自绘（木纹底、网格、棋子渐变、
  最后一手标记、禁手点红叉、获胜连线、可走点、将军标记）；画刷全部 `Freeze()`；`State` 用不可变记录整体替换，
  DP 变化 `InvalidateVisual`；点击换算处理边距、容差约半格；象棋翻转时屏幕/逻辑坐标互换。
* 断线/暂停状态：对局卡红底块 + 棋盘下提示，绑基类 `IsGamePaused/GamePauseStatusText/IsReconnecting/ReconnectStatusText`。
* 认输/离开房间要**二次确认**（再点一次确认），断线重连期间所有落子/操作按钮禁用（离开可用）。
* 调试/演示环境变量见第 0 节：`YLT_GOMOKU_DEMO` `YLT_XIANGQI_DEMO` `YLT_XIANGQI_SETUP`
  `YLT_BOARD_DEMO_PAUSE`（暂停/重连 UI 演示）`YLT_BOARD_RECONNECT_MS/_TRIES`（自检加速）。

#### 验证方式

* `BoardGamesChecks`（**407 项**，1005 项里旧轮子的断言原样保留）：注册/类别 25 · 五子棋 92 · 象棋 141 · 配置 23 · 网络/重连/联机 VM 126；注册/类别、五子棋规则与禁手、象棋全走法/将军/摆棋/让子/让先/
  记谱/快照、配置落盘、协议帧/端点、**本机 127.0.0.1 端口 0 真开房间**跑完一局 + 断线重连 + 第三人拒绝。
* 截图冒烟（见第 6 节）：主页 / 棋牌类别页 / 两个工具（demo 数据）/ 象棋摆棋 / 暂停态 / 1024×700。
* ⚠ 自检里**不要真开 WPF 窗口**、不要碰 `Clipboard`、不要在测试后留下监听端口（会话一定 Stop）。

---

### 2.19 图片连续阅读器：六种阅读方式 · 连续长带 / 双页翻页 / 单页 · 缩放 / 惯性 · 双击图片打开（v0.12.0 起；v0.13.0 缩放惯性；v0.14.0 翻页单页；v0.15.0 文件关联）

**定位**：把仓库根原来的 `PictureView` 小项目移植进来（只支持上下滚动 + 中键单击锚点），并加三种滚动方向、
「按住中键」、缩放 / 双轴自由拖拽 / 拖动惯性（越滑越快）。图片**首尾相接、无间距**贴在一条 `StackPanel` 长带上，滚轮 / 左键拖拽 / 中键锚点
都能连续移动。

**分工**（纯逻辑全部可自检，界面只做必须碰 WPF 的事）：

| 轮子 | 位置 | 说明 |
| --- | --- | --- |
| `PictureScrollMode` / `PictureScrollModes` | `PictureScrollMode.cs` | 6 种方式：`Vertical` / `HorizontalLeftToRight` / `HorizontalRightToLeft` / `SpreadLeftToRight` / `SpreadRightToLeft` / `SinglePage`；分类判断 `IsContinuous` / `IsPaged` / `IsSpread` / `IsSinglePage`，以及 `IsHorizontal` / `IsReverse` / `Describe` / `DescribeCompact` / `Hint` |
| `PicturePaging` | `PicturePaging.cs` | ★纯逻辑：翻页/单页的页号夹取、双页配对（日式镜像）、翻页步进、缩放后的槽位排版、平移夹取 |
| `PictureWheelAction` / `PictureWheelActions` | `PictureScrollMode.cs` | 翻页模式下滚轮做什么：`TurnPage`（翻页）/ `Zoom`（缩放），含归一化与文案 |
| `PictureScrollMath` | `PictureScrollMath.cs` | ★纯逻辑：偏移夹取 / 滚轮一格（向下=阅读前进）/ 中键锚点增量（沿阅读方向）/ 拖拽增量（阅读轴 + 跨轴，内容跟手）/ 按比例定位 / 统一缩放尺寸 / 缩放百分比 |
| `PictureInertia` | `PictureInertia.cs` | ★纯逻辑：拖动惯性的速度采样（最近 120ms）/ 指数衰减 / 起步阈值 / 「越滑越快」的速度叠加 + 上限 |
| `PictureAnchorMachine` | `PictureAnchorMachine.cs` | ★纯逻辑：中键「单点留下 / 按住松手消失」状态机（`None` / `Pressed` / `Latched`），时间由调用方传进来 |
| `PictureScanner` | `PictureScanner.cs` | ★纯逻辑 + 磁盘：扩展名过滤、自然排序（`2` 在 `10` 前）、只读文件头拿像素尺寸（坏图跳过） |
| `PictureDemoData` | `PictureDemoData.cs` | 开发用：`YLT_PICTURE_DEMO=1` 时现画 8 张演示图（宽高比不同，方便看排版） |
| `PictureViewerConfig` | `PictureViewerConfig.cs` | `Config\picture-viewer\config.json`：`ScrollMode` / `WheelStepPixels` / `MiddleHoldMilliseconds` / `AutoUnloadOffscreen` / `ZoomPercent` / `InertiaEnabled` / `InertiaAccelerate` / `PagedWheel` / `PageTurnAnimation` / `LastFolder` |
| `PictureFileAssociation` | `PictureFileAssociation.cs` | 双击图片打开：HKCU 文件关联（ProgId + OpenWithProgids + 各后缀默认值备份/还原）、状态文案、启动命令行判断 |
| `PictureFilePathLaunch` | `PictureFileAssociation.cs` | 主程序 → 工具的一次性启动请求（`PendingImagePath`），VM 构造时取走 |
| `PictureViewerView` | `Views/PictureViewerView.xaml.cs` | 连续模式：长带排版、可见范围解码 / 离屏释放、阅读 / 跨轴偏移换算、双轴拖拽 + 惯性、鼠标键盘 |
| `PictureViewerView.Paged` | `Views/PictureViewerView.Paged.cs` | 翻页 / 单页视图（partial）：页号状态、双页渲染、翻页滑动动画、缩放平移、左右按钮、页码、连续↔翻页切换 |
| `PictureViewerChecks` | 自检 | **129 项**：滚动数学 / 阅读偏移换算 / 缩放 / 跨轴拖拽 / 惯性采样与叠加 / 锚点状态机 / 翻页配对与排版 / 滚轮动作 / 双击定位 / 文件关联纯逻辑 / 页序 / 真扫临时图片目录 / 无头 VM / 配置 |

**核心概念：阅读偏移（不是 ScrollViewer 的物理偏移）**

* **阅读偏移**：0 = 第一张图开头，最大 = 最后一张末尾；界面里所有滚动操作只改它。
* 纵向：阅读偏移 = 物理垂直偏移；横向左→右：阅读偏移 = 物理水平偏移。
* **横向右→左**：图片**物理反着摆**（第一张在最右边，`ImageStrip.Children` 倒序添加），
  阅读偏移 = `ScrollableWidth − HorizontalOffset`。
  ⚠ 别改成 `FlowDirection=RightToLeft`：鼠标坐标 / 拖拽 / ScrollViewer 行为在 RTL 下很容易出玄学，
  现在这样是显式的，而且能用 `YLT_PICTURE_MEASURE` 直接核对（实测起始 `physicalOffset=3468` / `readingOffset=0`）。
* 滚轮**向下 = 阅读前进**，三种模式统一（横向不用按 Shift）；`WheelStepPixels` 默认 120，夹在 20~600。

**中键锚点的两种玩法**（`PictureAnchorMachine`）

* 按下中键 → `Pressed`（锚点立刻出现，按着就能滚）；
* 在判定时长内松手 = **单点** → `Latched`（锚点留下，**再按任意键**——左/中/右——消失，PictureView 原行为）；
* 超过判定时长才松手 = **按住** → `None`（一松手锚点立刻消失）；
* 判定时长 = `MiddleHoldMilliseconds`（默认 250ms，夹在 60~2000）；「任意键关闭」用 `OtherButtonDown`，
  按住中键的过程中按别的键不打断。
* 滚动速度 = 指针在**阅读轴**上离锚点的距离 × 0.5（每帧，约 16ms），与浏览器中键自动滚动同原理；
  横向模式看 X 轴、纵向看 Y 轴，右→左的符号反过来。
* 锚点**固定不动**（指示器画在点击处，箭头朝滚动轴），鼠标指针不隐藏（能看清离锚点多远）——
  原版是「指示器跟着鼠标、鼠标隐藏」，这里改了是为了让「按住」模式也能看懂速度来源。

**缩放 · 双轴自由拖拽 · 拖动惯性（v0.13.0）**

* 缩放：`ZoomPercent`（默认 100，夹 25~800）。**100% = 原来的「铺满基准」**（纵向按窗口宽度、横向按窗口高度），
  **不是原图像素 1:1**；工具条是「百分比输入框 + 两个放大镜（一步 10%）」，`Ctrl+滚轮` 也能调。
  改缩放时按**画面中心**定位（`ApplyZoomChange` 先记两个轴的中心比例，重排后再按新内容长度还原）。
* **两个偏移都要自己记**：`_readingOffset`（阅读轴）+ `_crossOffset`（另一轴）。缩放后两个轴都可能能滚；
  拖拽一次改两个（`DragDelta` + `CrossDragDelta`），滚轮默认滚阅读轴、`Shift+滚轮` 滚跨轴。
* **图片的宽和高必须都显式给**（`RelayoutItems`）：只给一个轴、另一个靠拉伸的话，内容在另一轴仍等于视口，
  ScrollViewer 会认为“没得滚”，缩放后那个方向就拖不动了（实测 200% 纵向时 `ScrollableWidth` 必须 > 0）。
* 惯性：`PictureInertia` 采样最近 120ms 的位移算速度，指数衰减（约 200ms 减半），低于 0.02 px/ms 自动停；
  「越滑越快」= 下一次拖拽开始时把还在滑的残余速度存进 `_carried*`，松手时 `Combine` 相加并封顶 6 px/ms
  （`InertiaAccelerate`，默认开；关掉就是每次只用这一下甩动的速度）。
* 惯性只在**自己拖拽**松手后启动；滚轮 / 缩放 / 锚点 / 工具条点击 / 开始新的拖拽都会停下（`AbortInertia` /
  `CancelInertia`）；撞到边界的那一轴速度清零，不在边上震。
* `YLT_PICTURE_ZOOM` / `YLT_PICTURE_CROSS` 是截图核对钩子（见第 0 节）。

**双击图片打开 · 文件关联（v0.15.0）**

* 启动参数：主程序 `App.OnStartup` 里用 `PictureFileAssociation.IsSupportedImageFile` 扫 `e.Args`，命中就把绝对路径写进
  `PictureFilePathLaunch.PendingImagePath` 并直接打开「图片连续阅读器」。ViewModel **构造时取走并清空**（只生效一次），
  View 在 `OnLoaded` 里 `LoadLaunchImageAsync()` → `LoadFolderAsync(同目录, 那张图)` → 照常按阅读方式重建。
* 自动定位：加载时 VM 用 `FindPageIndex` 找到那张图的下标，放进 `_pendingPageIndex`；界面**只消费一次**
  （`ConsumePendingPageIndex()`）：连续模式 `ScrollToPage` 用 `PictureScrollMath.ReadingOffsetForPage`
  （普通方向 = 该页主轴起点；日式右→左 = 总长 − 该页末尾）滚到那张图开头；翻页 / 单页直接 `_pageIndex = 下标`。
  ⚠ 别在 View 里按“第几张 × 平均长度”估算：每张图长度不同，必须用 `_slots[i].Start/MainLength` 算。
* 文件关联：**只写 HKCU**（不需要管理员）——
  `Software\Classes\YuLiangToolkit.PictureViewer`（ProgId + DefaultIcon + shell\open\command）+ `Applications\<exe>` + 各后缀的
  `OpenWithProgids`（让「打开方式」里有本工具）；同时把各后缀 `Software\Classes\.ext` 的默认值改成 ProgId，
  **改之前把原来的默认值备份到 ProgId\PreviousDefaults**，取消关联时还原（用户原来的默认程序不会丢）。
* **绝对不要碰** `Explorer\FileExts\.ext\UserChoice`：Win10/11 这个键带哈希、程序改不了（硬写会失效/被还原）。
  所以界面必须说清楚：如果系统默认程序（例如 WPS / 照片）已经占着，需要去「默认应用」确认一次；
  设置面板里的「打开 Windows 默认应用设置」按钮就是 `ms-settings:defaultapps`。
* 注册/注销结束都要 `SHChangeNotify(SHCNE_ASSOCCHANGED)`，否则资源管理器要等一会儿才认。
* 关联状态文案会数一遍每个后缀的 `UserChoice`：有几个还是别的程序就提示「其中 N 种格式的默认程序还是别的应用」。
* 无头自检只测纯逻辑（后缀表 / `BuildOpenCommand` 引号 / `IsSupportedImageFile` / 定位下标与只消费一次）；
  **不在自检里写真实 HKCU**（会污染系统），关联开关用 UIA 实测。

**阅读方式怎么选 · 翻页 / 单页视图（v0.14.0）**

* **工具条只放「当前阅读方式」一个按钮**（点它打开设置面板），六种方式的切换统一在设置面板的「阅读方式」下拉里。
  为什么不继续摆分段按钮：方式会越来越多（v0.13.0 才 3 种，v0.14.0 就 6 种），工具条放不下；以后加方式只改下拉，不动工具条。
* 模式分两类（`PictureScrollModes.IsPaged` 是唯一判据）：
  * **连续滚动**（纵向 / 横向左右）：长带 + `_readingOffset` / `_crossOffset` 那一套，滚轮 / 中键锚点 / 自由拖拽 / 缩放 / 惯性全保留；
  * **翻页 / 单页**（双页左右 / 单页）：走 `PictureViewerView.Paged.cs` 的 partial —— 页号 `_pageIndex` + 视口槽位排版 +
    平移 `_pagedPanX/Y`，图片按页解码并丢进一个小 LRU 缓存（12 张）。
* 双页配对按「一次翻一张」：左→右 `左槽=第 i 张、右槽=第 i+1 张`，前进 `i←i+1`（右边移到左边、新的从右边进）；
  右→左（日式）`右槽=第 i 张、左槽=第 i+1 张`，前进 `i←i+1`（左边移到右边、新的从左边进）。最后一屏不足两页时另一侧留空。
  这样翻页动画就是「整页滑一个页宽」，和用户描述的“把右边的图放到左边”一致。
* 翻页动画（`PageTurnAnimation`，默认开，约 190ms）：只在 **100% 缩放且没有平移** 时播放（放大/平移时直接切，避免和
  平移变换打架）；旧图 `TranslateTransform` 0→±页宽，新进的那张反向 ±页宽→0，动画结束再按新页号重排。
* 翻页模式滚轮行为由 `PagedWheel` 决定：`TurnPage`（默认，滚一下翻一张）/ `Zoom`；`Ctrl+滚轮` 永远缩放，
  `Shift+滚轮` 在放大后横向平移。方向键 `←`/`→` 在日式模式互换，`PageUp/PageDown`、空格 = 下一张，`Home/End` = 首尾。
* **模式切换保持位置**：连续→翻页按阅读比例换算页号（`round(ratio × (页数-1))`）；翻页→连续按页号换算比例
  （`pageIndex / (页数-1)`）；翻页↔翻页保持页号。实测 50% 纵向 → 双页 = 第 5 张、第 5 张 → 纵向 = 57% 位置。
* 翻页视图的按钮点击要**排除出拖拽接管**：`IsOnClickableControl` 往上找 `ButtonBase`，否则按下就被当成“开始拖拽”，
  按钮点不动（和 v0.12.1 下拉项那个坑同源，都是鼠标事件归属问题）。
* `YLT_PICTURE_PAGE` 是翻页截图钩子（见第 0 节）。

**踩过的坑 / 约定**

* `ScrollViewer` 的滚动条全部 `Hidden`：横向模式自己接管滚轮，留原生滚动条只会让人以为「左右滚要按 Shift」；
  底部那条 3px 细进度条代替它显示阅读进度。
* **阅读偏移由界面自己记（`_readingOffset`），别每帧从 `ScrollViewer` 反读**：实测 `ScrollToXxx`
  是**排队到布局阶段**才生效的（`YLT_PICTURE_MEASURE` 里 `readingOffset=693.6` 时 `physicalOffset` 还是 0），
  滚轮连击 / 锚点每帧滚时反读会把每一下位移都丢掉。现在只在 `SetReadingOffset` 里改它、`ScrollChanged` 不覆盖；
  ScrollViewer 只负责照着显示（测量文件的 `fromPhysical` 行可以核对两者最终一致）。
* 可见范围加载的 150ms 防抖**不能每次 ScrollChanged 都重置**（连续滚轮 / 拖拽 / 锚点时每帧都来，
  一重置就永远等不到到期，移动中画面空白）；只在计时器没跑时启动。
* 重建途中又来的请求（连着换方向 / 换文件夹）**排队补跑**，不能静默丢；解码队列条目自带 generation，
  并且只留一个 worker 排空（旧写法每入队起一个 worker，会互相偷条目）。
* 重建长带（切方向、换文件夹、切工具）时 `_generation++`：还在后台解码的那几张回来会被丢掉，
  否则旧图会贴到新槽上。
* 离屏释放有 **3 秒宽限**（`AutoUnloadOffscreen`）：来回滚不会反复解码；解码宽度按显示宽度
  （纵向 = 统一宽度，横向 = 这一张的显示宽度），不会把 4000px 原图整张吃进内存。
* 统一缩放：纵向按**宽度**、横向按**高度**，基准尺寸 = `min(图里最大的那一维, 视口边 / DPI)`，再乘 `ZoomPercent`；
  窗口缩放时按「偏移 × 新老基准比」保持位置（比按可滚范围比例更贴手，不跳半页）。
* 只读所选文件夹**当前层**、自然排序（纯数字文件名就是数字页序），**不做旋转 / 手动排序**（缩放有了，见上）。
* 鼠标事件都在 `Preview*` 上：滚轮抢在 ScrollViewer 前、左键拖拽抢在子控件前。**判断“这一下该不该我管”必须看事件源**
  （`IsFromViewport(e.OriginalSource)` + `Mouse.Captured` 守卫），**不能只看坐标在不在视口里**：
  弹层（ComboBox 的 Popup）是独立窗口，它的下拉项按坐标算“在视口里”，但元素不在视口里 ——
  老写法会把下拉项 / 输入框的点击当成「开始拖拽」吃掉并抢走鼠标捕获，表现就是「下拉项点不动」（v0.12.1 用户报的）。
  另外「滚动方式」改成了**分段按钮**（`SegmentedListBoxStyle`）而不是 ComboBox：常用操作少一次点击，也少一个弹层。

**验证**：自检 **120 项**（滚动数学 / 阅读偏移换算 / 缩放 / 跨轴拖拽 / 惯性采样与叠加 / 锚点状态机 / 翻页配对与排版 / 滚轮动作 / 页序 / 真扫临时目录 / 无头 VM）+ 截图冒烟
（第 6 节 3l：纵向 / 横向 LTR 起止 / 横向 RTL 起止 / 锚点指示器 / 设置面板 / 200% 缩放 / 双页左右 + 单页）；`YLT_PICTURE_MEASURE` 写真实几何核对
（RTL 下 `physicalOffset` 与 `readingOffset` 互为反数、`firstChildIsPage1=False`；200% 纵向时 `zoom=200%` 且 `crossOffset` 上限 > 0）。
**换方向保持阅读位置**用 `YLT_PICTURE_SWITCH_MODE=rtl` 配合 `YLT_PICTURE_MEASURE` 核对：会先写 `*.before`，
再切方向、0.35 秒后写正式文件 —— 前后 `ratio` 应相同，RTL 的物理偏移应是 LTR 的镜像。

---

## 3. Data / Config 目录约定（v0.2.0 起；v0.4.1 起位置在 **exe 旁边**）

**运行时目录 = exe 所在目录**（`bin\Debug\net9.0-windows\`、`bin\Release\…`、`publish.ps1` 出来的 `dist\YuLiangToolkit\`）。
仓库根的 `Data/`、`Config/` **只剩说明文件**，不再是运行时目录：

```
bin\Debug\net9.0-windows\          （或 bin\Release\… / dist\YuLiangToolkit\）
├─ YuLiangTookit.exe
├─ Data\                           ← 功能数据
│  ├─ 怎么用这两个文件夹.md          ← 程序首次运行写入（RuntimeReadme），只写缺失的
│  ├─ flash-downloader\games\…     ← 下载的游戏
│  ├─ flash-downloader\tools\…     ← 播放页模板 + FlashPlayer.exe
│  ├─ batch-rename\rename-history.json
│  ├─ chat-room\Sessions\…         ← 每次连接一个「年份.月份.日期-时间.分钟.秒」文件夹（见下）
│  ├─ map-downloader\downloads\…   ← osu! 的 .osz 落盘处（可以在界面上换成任意目录）
│  └─ media-player\playlist.json   ← 播放列表快照（下次打开恢复用：路径 + 上次那首 + 听到第几秒）
└─ Config\                         ← 功能配置
   ├─ 怎么用这两个文件夹.md
   ├─ shell\config.json            ← 外壳自己的：主页排序偏好 + 上次使用时间 + 粒子开关（showParticles）
   ├─ video-to-audio\config.json   ← 视频转音频的全部选项
   ├─ batch-rename\config.json     ← 批量重命名的全部规则
   ├─ flash-downloader\            ← config.json（含游戏目录、并发、端口、播放器路径…）
   ├─ chat-room\config.json        ← 昵称/头像、监听地址与端口、要连的主机、单次流量上限、发图压缩…
   ├─ map-downloader\config.json   ← osu! 凭据（Client ID/Secret、Access Token）+ 搜索条件 + 下载目录与并发
   ├─ media-player\config.json     ← 播放器：读取方式 / 递归 / 追加覆盖 / 音量 / 播放顺序 / 快进快退步长 / 恢复上次
   ├─ osu-key-converter\config.json    ← Key 转换器：上次用的目标 Keys / Gap / Clean / Blank / Seed / Creator…
   ├─ osu-ln-transformer\config.json   ← LN 转换器：上次用的 Level / LN% / Divide / Column / Gap / OD 开关…
   ├─ osu-level-calculator\config.json ← 等级计算器：上次的显示/计算偏好
   ├─ gomoku\config.json           ← 联机五子棋：玩家名 / 端口 / 房主执子 / 禁手开关 / 显示禁手点 / 棋盘大小
   ├─ xiangqi\config.json          ← 联机象棋：玩家名 / 端口 / 房主执棋 / 走法提示 / 自动翻转 / 显示坐标
   └─ sample-framework\            ← config.json + options.json
```

> **发布 / 迁移**：整个程序目录拷走即可（Data、Config 跟着走）。
> `.\publish.ps1` 发布到 `dist\YuLiangToolkit\`（顺带放好两个 README），首次运行 exe 会自动补齐这两个目录。
> ⚠️ `dotnet clean`、手删 `bin`/`dist`、重新克隆仓库都会**丢掉 exe 旁边的下载内容与配置**——
> 那是用户数据、不在版本库里，重要的话自己备份一份。

`Data\flash-downloader\` 的结构（v0.5.0）：

```
Data/flash-downloader/
├─ games/                     ← 下载下来的游戏（可以在界面上改成任意目录）
│  ├─ 怎么放游戏.txt           ← 首次运行放一份命名规则说明，可删
│  ├─ 囧星人大冒险_a6853.swf   ← 散放的单个 swf
│  └─ 欢乐厨房_a12346h/        ← 网页游戏（多文件）单独一个文件夹
│     ├─ 欢乐厨房_a12346.htm
│     ├─ SWFNetPath.ini       ← 网络路径 + 宽高（自动下载/批量下载都靠它）
│     └─ assets/…             ← 游戏运行时需要的其它文件
└─ tools/                     ← 播放页与替代 API（首次运行从 dll 里释放，可自己改）
   ├─ frame.htm  FlashPage.htm  FlashPlayer.exe
   └─ tool/substiH5WanApi.js
```
> `Data\flash-downloader\tools\FlashPlayer.exe` 由 `FlashPlayerLocator` 找一份后**拷进功能 tools 目录**
> （查找顺序：配置路径 → 功能 tools 目录 → 程序目录 → 常见安装位置）；11 MB 的第三方二进制，不入库。

`Data\chat-room\` 的结构（v0.6.0）——**每开始一次连接就新建一个以时间命名的会话文件夹**：

```
Data/chat-room/
├─ Protocol/chat-protocol.json   ← 协议说明（首次运行从 dll 释放，人能读；改过就不覆盖）
└─ Sessions/                     ← 全部会话都在这里（别把别的东西丢进来，「历史记录」会当成一次聊天列出来）
   ├─ 2025.1.3-14.5.9/           ← 文件夹名 = 开始连接的时刻：年份.月份.日期-时间.分钟.秒
   │  ├─ 怎么用这个文件夹.md      ← 一次会话里放了什么，自己看这份
   │  ├─ session.json            ← 抬头：开始/结束时间、身份、对端、消息条数、流量统计
   │  ├─ chat-log.jsonl          ← 全部消息，一行一条 JSON（文字 / 文件 / 系统提示）
   │  ├─ images/  audio/  video/ files/   ← 收到与发出的文件按大类归位（重名自动加序号）
   │  └─ …                       ← 同名会话（同一秒开两次）自动补 -2、-3
   └─ 2025.1.3-20.31.7/
```

> ⚠️ **这些是「功能性数据」，不是缓存**：删 `Data\batch-rename\rename-history.json` 等于放弃所有撤销记录；
> 删 `Data\flash-downloader\games\*\SWFNetPath.ini` 就没法批量下载 / 自动下载（游戏本体还在，能玩）；
> 删 `Sessions\` 下的文件夹就等于删掉那次的聊天记录与收到的文件（程序不依赖它们，只是「历史记录」列表会变空）；
> `Data\map-downloader\downloads\*.osz` 就是下回来的谱面包（已经在 osu! 里导入过的删掉无妨，**重新下要花流量**）。
> 用 `YLT_CHAT_HOME=<目录>` 可以把会话根目录挪到别处（自检就是这么隔离的）。

> **osu!mania 三件套（Key / LN / 等级）不往 `Data\` 写输出**：转换结果写在**原谱面所在目录**
> （文件名按元数据拼，见 2.17）；只有 `YLT_OSU_DEMO=1` 截图模式会在 `Data\osu-key-converter\` 等处
> 生成一份合成 `.osu` 当演示素材。等级计算器只读不写。

* **小文件夹名 = `ITool.Id`**（kebab-case，例如 `flash-downloader`）；文件夹**首次真正写入时才创建**。
  `Config\shell\` 是外壳（主页/导航）自己的配置，不属于任何工具。
* 手工放数据也可以：直接建同名文件夹把 swf 等丢进去，代码里 `ToolStorage.For("flash-downloader").List()` 就能列出来。
* 配置文件内容是 camelCase、带缩进、中文不转义的 JSON，**可以用记事本改**，改完重开功能即生效：
  ```json
  { "saveFolder": "D:\\Flash", "autoRename": true, "timeoutSeconds": 30, "mode": "Quality" }
  ```
* 枚举按**字符串**保存（`"mode": "Mp3Lossy"`），加减枚举成员不会错位；**给配置类新增属性不影响老文件**（缺失字段自动用类里的默认值）。
* 删除配置文件 = 该功能选项恢复默认（下次打开自动重建）。
* 文件损坏会被自动改名备份成 `xxx.bad-20250101-120000.json`，程序回退默认值继续跑，不崩。
* 两个目录都已从主项目编译项里排除（`DefaultItemExcludes`），往里丢 `.cs`/`.xaml` 也不会被编译。

---

## 4. 新增一个功能：标准流程（照抄 `src/YuLiangToolkit.Tools.Sample/`）

> 两个模板可选：**`Tools.Sample`** 最小最干净，适合“一张配置表 + 一个动作”的工具；
> **`Tools.BatchRename`** 更完整，适合“列表 + 实时预览 + 长任务 + 可撤销”的工具（见 2.10）。
> **`Tools.FlashDownloader`** 是“要联网 / 要起服务 / 有嵌入网页模板”的那一类参考（见 2.11）：
> 纯逻辑（`Core/`）与引擎（`Engine/`）严格分开，界面只做展示；自检里还自己起了一个假的游戏站来跑真下载。

1. **复制模板**：把 `src/YuLiangToolkit.Tools.Sample/` 整个文件夹复制成 `src/YuLiangToolkit.Tools.<你的功能>/`。
2. **改 csproj**：`AssemblyName` / `RootNamespace` 改成新名字（其它不用动，它已引用 Core）。
3. **改 4 个文件**（模板里都有注释）：
   * `SampleTool.cs` → `<你的>Tool.cs`：换 `Id`（= 文件夹名）/ `Name` / `Description` / `EmojiIcon` / `Category` / `Order`，
     再加 `Tags`（可选，写了就能被主页标签搜索命中、卡片上会显示蓝字）；
     想归入某个分类就加 `CategoryPath`（可选，见 2.15，例如 `new[] { "音游工具" }`）；
   * `SampleConfig.cs` → `<你的>Config.cs`：把选项写成属性（`ToolConfig` 子类）；
   * `ViewModels/SampleViewModel.cs` → `<你的>ViewModel.cs`：继承 `ToolViewModelBase`，构造函数传 `Id`，`LoadConfig<T>()`；
   * `Views/SampleView.xaml` → `<你的>View.xaml`：直接绑 `Cfg.Xxx`，样式用 `App.xaml` 里现成的。
4. **接入解决方案**（二选一）：
   * 主程序引用：`dotnet sln YuLiangToolkit.sln add <csproj 路径>`，再在 `YuLiangTookit.csproj` 里加一条
     `<ProjectReference Include="src\<你的>\<你的>.csproj" />`；
   * 外部插件：编译后的 dll 丢进程序目录的 `plugins/`，**不用改主程序**（`ToolDiscovery` 会自动扫到）。
5. **验证**：
   ```powershell
   .\build.ps1
   $env:YLT_SHOT_TOOL="<你的Id>"; $env:YLT_SHOT="shots\new.png"
   Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait   # 截图模式：看界面 + 自动退出
   ```
   然后确认 `Config\<你的Id>\config.json` 已生成、勾选/输入后内容跟着变。
   > 必须用 `Start-Process -Wait`：exe 是 GUI 子系统程序，PowerShell 直接跑不会等它，你会以为“截图没生成”。
6. **（可选）纯逻辑就顺便加进自检**：把规则/算法写成不依赖 WPF 的静态类（参考 `RenameEngine` / `ToolFilter`），
   在自检工程里加一条 `ProjectReference`，然后在 `tests\...\Program.cs` 里补几行 `Check(...)`。
   改算法后跑一次自检就知道有没有改坏，比开界面点一遍快得多。
   **要联网 / 要起服务的功能也能自检**：`FlashDownloaderChecks.Run(result, plain)` 就在自检里
   临时起了一个 `HttpListener` 当“假游戏站”（含防盗链校验），把「解析 → 下载 → 批量下载 → 自动下载」
   整条链路跑一遍，完全不碰外网。新功能照这个套路写即可。
7. **（可选）旧配置迁移**：如果这个功能以前把选项存在别处，参考
   `VideoToAudioConfig.FromLegacySettings()` + `LegacyAppSettings.cs`：
   `LoadConfig(() => 你的Config.FromLegacy())` —— 工厂只在“配置文件不存在”时被调用，跑过一次后自然失效。

---

## 5. 约定与坑（踩过的都在这）

**存储 / 配置**
* 配置**属性用自动属性就行**，保存靠后台内容比对，不依赖变更通知；需要“联动刷新界面”的属性才用 `Set(ref …)`。
* 带 setter 的**计算属性必须标 `[JsonIgnore]`**，否则会被当成配置项写进 JSON（只读计算属性不会被序列化）。
* `ToolViewModelBase.LoadConfig<T>()` 同一类型只返回一个实例（重复调用不会造出两个互相覆盖的配置对象）；
  工具关闭再打开时框架会自动恢复后台保存。
* 动态配置（`Dyn[键]`）**键不存在时读取返回 null**：绑 `IsChecked` 会得到 null，点一下变 false（看起来没反应）。
  用 `LoadDynamicConfigWithDefaults(...)` 预置，或写 `TargetNullValue=False`。
* 数据目录里的**相对路径不能用 `..` 或绝对路径**，会抛 `ArgumentException`（这是故意的防逃逸）。
* 后台线程改界面状态一律走 `Ui(...)` / `UiAsync(...)`；直接赋值会抛“调用线程无法访问此对象”。
* 程序退出、切换功能时框架会自动 Flush 配置；**新的持久化入口如果绕过 `ToolConfigStore`，记得自己接 `FlushAll`**。

**WPF / 界面**
* ComboBox 绑 `OptionItem<T>` 列表时必须写 `DisplayMemberPath="Label"`，否则显示类型名。
* 索引器绑定（`{Binding Dyn[键]}`）依赖 `OnPropertyChanged("Item[]")`——`DynamicToolConfig.SetValue` 已处理。
* 占位提示文字压在输入内容上的问题用 `EmptyToVis` 解决（见 `VideoToAudioView.xaml` 的“与源文件夹相同”）。
* 标题栏：`CaptionHeight` 必须为 0，否则窗口按钮会被命中测试成标题栏拖拽而点不动（HTCAPTION）。
* 无边框窗口最大化会盖住任务栏，靠 `MainWindow.WndProc` 里的 `WM_GETMINMAXINFO` 钳制。
* **自定义枚举别起名 `SequencePosition`**：`System.SequencePosition` 撞名，任何 `using System;` 的文件里
  用它都要写全名（v0.4.0 踩过，已改名 `SequencePlacement`）。同理避开 `Path`、`Timer`、`Index` 这类。
* 想在**用户一敲键就刷新界面**（不是等 400ms 落盘）时，配置属性必须用 `Set(ref 字段, 值)` 写法并发通知；
  自动属性只保证“最终会存下来”，界面拿不到即时通知（见 `BatchRenameConfig`）。
* 工具被切走时框架会调 `NotifyClosed()`；**挂在缓存对象上的事件记得在这里摘掉**
  （配置对象是按类型缓存的，不摘的话每次打开工具都会多挂一个旧 VM 上去）。
* 列表里的 `ListBox.ScrollIntoView` 会**顺着 `BringIntoView` 一路请求到外层页面**，把整页顶下去 ——
  要滚内层就找它内部的 `ScrollViewer` 直接滚（见 2.12）。
* **按钮/菜单里的图标别用 `⏪ ⏩ ⏹ 🔁 🔂 🔀` 这类字符**：本机字体没有这些码位，会渲染成「带方框的豆腐块」。
  要稳就用矢量 `Path`，或者 `Segoe MDL2 Assets`（`FontFamily` 设上，配 `E713` 这种码位），
  或者干脆写字（播放器那个「循环 / 单曲 / 随机」按钮）。`🎵 🎬 📂 ✕` 这几个 emoji 在本机是能显示的。
  ⚠ 这类毛病**截图放大才看得出**，别只看一眼小图就说"没问题"。
* **`MediaElement` 的 `LoadedBehavior` / `UnloadedBehavior` 都要设 `Manual`**：留默认值时它自己在加载时开播、
  卸载时自己关掉，会跟 ViewModel 的命令打架（见 2.14）。
* **整窗全屏别直接设 `Left/Top/Width/Height`**：会被窗口管理器夹回工作区（实测 1920×1080 的屏只拿到 1920×1032）。
  必须临时改 `WM_GETMINMAXINFO`（用整块显示器的矩形）再 `WindowState = Maximized`，并临时 `Topmost = true`
  才盖得住任务栏（见 2.14）。
* **想让切走的工具“继续干活”，就不能让外壳把视图丢掉**：视图一离开可视化树，里面的
  `MediaElement` / 定时器就没意义了。做法是 `IBackgroundTool.KeepAliveOnLeave` + 外壳存住视图实例，
  而且界面 `Unloaded` 时**不能**顺手 `Close()` 播放器（见 2.14）。

**分类目录 / 粒子 / osu 工具（v0.10.0 踩过 / 钉住）**

* **老工具的 `Category` 不要自动当分类目录**：目录树只认 `CategoryPath`（默认空）。早期如果把 `Category` 直接当路径，所有老工具都会被塞进文件夹，主页会大变样。
* **类别页只平铺「直接工具 + 子类别卡片」**，不要递归平铺子分类工具 —— 否则层级一深就重复、乱序。
* **主页搜索是对全部工具扁平过滤**（分类里的也能搜到）；这条别改成"只搜当前层"（用户已经习惯全局搜索）。
* **`ParticleOverlay` 的事件必须成对挂摘**：它订阅 `CompositionTarget.Rendering`（静态事件）+ Window 的鼠标事件，Unloaded/Window.Closed 不摘就是内存泄漏 + 关窗口后还在跑。
* **粒子别用"每帧 new 控件/画刷"**：用 `DrawingContext.DrawEllipse` + 冻结画刷；否则粒子一多 UI 线程直接卡。
* **OsuParsers 解码器是 static 状态、非线程安全**：批量 `.osu` 必须串行（解析桥已加锁兜底），别改成 `Parallel.ForEachAsync` —— 实测会串台/抛 `FormatException`。
* **OsuParsers 的 `Save()` 是全量重写**（只保留已知段落）：未知段/注释会丢，这个要在 UI 上（工具页"注意"）说清楚；不要承诺"原样保留"。
* **输出文件写在原谱面目录、按元数据命名**：不要"好心"改成覆盖原文件 —— 正常就是生成新文件；同名时才会覆盖（日志里必须显示输出路径）。
* **星数算法不要拿 osu!lazer 官方值对答案**：这套 Level 公式是按 `StarRating.cs`（Star Rating Rebirth 思路）拟合的，官方 mania SR 与它差 ~1.2 星。

**文件系统 / 批量改名**
* Windows 上**只改大小写的改名**（`photo.jpg` → `Photo.jpg`）用一步 `File.Move` 会失败（目标“已存在”）；
  必须先把源改成别的临时名再改成目标名（`RenameExecutor` 的两阶段就是干这个的）。
* `File.Move` 默认**不覆盖**：目标已存在会抛 `IOException`。所以“目标位置有没有被占”必须在预览阶段
  就算清楚，别指望执行时靠异常兜底。
* 磁盘上的文件名大小写要用 `Directory.GetFiles(...)` 读回来验证 —— `File.Exists("Photo.jpg")` 在
  Windows 上对 `photo.jpg` 同样返回 true，验不出大小写到底改没改。

**构建 / 工具链**
* 主项目在仓库根，`DefaultItemExcludes` 排除了 `src/` `Data/` `Config/` `tests/` `FlashGameDownloader/` `MapDownloader/`——
  **新增顶层目录时记得同步加进去**，否则里面随便一个 `.cs` 都会把主程序编译搞崩。
* 命令行构建统一用 `.\build.ps1`（它把 `DOTNET_CLI_HOME` 指到 `.dotnet-cli-home`，避免污染用户目录）；
  发布用 `.\publish.ps1`（输出到 `dist\YuLiangToolkit\`）。
* 目标框架是 `net9.0-windows`；库项目也要 `UseWPF=true`（Core 里用到了 `Application.Current.Dispatcher`）。
* `Directory.Build.targets` 给所有项目补了 `System.IO` / `System.Text` 隐式 using（WPF SDK 默认不含）。
* ⚠️ **绝对不要用 `<None CopyToOutputDirectory>` 往 exe 旁边拷 Data/Config 里的东西**（v0.4.1 踩过、
  删掉过用户数据）：这些文件会被记进 `obj\...\*.FileListAbsolute.txt`，仓库里的源文件一旦删掉/改名，
  下次构建的 **IncrementalClean** 就会把输出目录里的同名文件当垃圾删掉——而输出目录正是用户的数据目录。
  要往数据目录放东西，就**让程序自己在首次运行时写**（见 `RuntimeReadme`），或发布脚本里用
  `Copy-Item` 明确地拷（`publish.ps1` 就是这么做的）。
* 数据目录跟着 exe 走，所以 **`dotnet clean` / 删 `bin` / 删 `dist` = 删用户数据**（下载的游戏、配置都在里面）；
  写清理脚本时要小心，别把 `Data`、`Config` 一起端了。

**截图验证 / 脚本**
* `YLT_SHOT` 渲染出的 PNG 里，内容区顶部会出现**淡淡的重复标题残影**——这是 `RenderTargetBitmap`
  截图时机的老毛病（改动前留下的截图也有），**不是界面 bug，别去查**。
* 截图输出统一放 `shots/`（该目录的 `png`/`txt` 不入库）；**要开窗口的调试脚本放 `scripts/`，
  跑之前确认用户没在忙别的**（会弹窗抢焦点，见 `scripts/README.md`）。
* **含中文的 `.ps1` 必须存成 UTF-8 with BOM**，否则 Windows PowerShell 5.1 按 ANSI 解析、报语法错误；
  `build.ps1` / `publish.ps1` 因此保持纯 ASCII。

**网络 / 本地服务器（v0.5.0 的 Flash 下载器踩过的）**
* **`HttpListener` 给出的 `AbsolutePath` 是百分号编码的**（`/games/%E6%AC%A2…`），拿它当本地路径用会
  永远匹配不上中文目录名；`FlashWebServer.Resolve` 里第一步就是 `UrlDecode`。**自己拼 URL 时也别重复转义**
  （`BeginSession` 里就因为这个多转义了一次，导致自动下载永远认为“这不是我这个游戏的文件”）。
* **判定越界不要看到 `..` 就拒**：写成 `path.Contains("..")` 会让浏览器规范化过的正常请求
  （`/games/../img/x.jpg`）也拿不到文件。正确做法是先 `Path.GetFullPath` 规范化、再比前缀（见 `SafeCombine`）。
* **.NET Core 默认没有 GBK / GB2312 代码页**，`Encoding.GetEncoding("gbk")` 会抛
  `ArgumentException`；老游戏站大量是 GBK 页面，必须在静态构造函数里
  `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`（`GamePageParser` 里已做）。
* **`Uri` 会把 `file://` 之外的协议相对地址（`//host/x.swf`）当成绝对地址**（`file://host/x.swf`），
  所以 `UrlJoin` 必须先判 `//` 再判“是不是绝对网址”，顺序反了就会拼出怪地址。
* 4399 页面里 `_strGamePath` 给的可能是**站内相对路径**（`/upload/…`）也可能是**完整地址**，
  拼接前必须判一下（`To4399Absolute`）；不然会拼出 `https://sda.4399.com/4399swfhttp://…`。
* **7k7k 的 `gamePath` 有时只是个外壳**（实测《黑帮跑车终极无敌版》17 KB），真正的主 swf 在它旁边；
  这类游戏必须走「装入文件夹」+「关联文件下载器」。现在 7k7k 的解析会**优先看新页面**。

**ArmorGames / Kongregate（v0.7.1 加的两个国外站）**
* **ArmorGames 的 swf 文件名不能靠规律拼**：同一个站有两种写法 ——
  `strike-force-heroes--14775.swf`（两个连字符）和 `the-enchanted-cave-2-17682.swf`（一个）。
  必须从页面的 `<param name="movie">` / `<embed src>` 里**读**出来（`GetArmorGamesDirectSwfPath`）。
  顺带：这两个标签的属性会**换行**，所以标签正则用 `<(?:param|embed)\b[^>]*>`（`[^>]*` 天生跨行），
  属性则逐个用 `AttributeOf` 读，别给每种写法各写一条正则。
* **Kongregate 的 swf 要跳三跳**：游戏页 → `…/embed`（`iframeConfig` 里有 `game_type` / `game_width` /
  `game_height` / `game_host`）→ frame 页里那行 `var swf_location = "…swf"`。
  一眼判定 Flash 的三条线索：`game_type == "flash"`、`requires_flash == true`、
  `game_host == https://chat.kongregate.com`（h5 游戏的 game_host 是 `game编号.konggames.com`）。
* **`iframeConfig` 不是合法 JSON**（`iframe_url` 写成单引号 + `' + channel_id + '` 拼 JS 变量），
  所以 `iframe_url` 不能用「截到第一个引号」的办法取 —— 那样只会拿到 `…/frame/`，后面的
  `?kongregate_host=…` 全丢。要一路吃到「引号后面紧跟 `,` 或 `}`」（`KongregateIframeUrlRawPattern`）。
  `channel_id` 是每局随机会话号，服务端不校验，固定填一个串即可。
* **必须排掉 Kongregate 自己的 API 库 swf**：`chat.kongregate.com/flash/API_AS3_*.swf` 每个页面都有
  （`api_path`，十几万字节），当成游戏下下来是个死循环的壳子。`GetKongregateSwfPath` 会把它筛掉。
* **Kongregate 的游戏名不能只读 `<title>`**：游戏页标题是
  「Bloons Super Monkey on Kongregate – Play Now」，嵌入页是「… - Play on Kongregate」，
  尾巴格式都不一样（还有 `– Play Instantly Online | Kongregate`）。**优先读脚本里的
  `game_name` / `game_title`**，标题只当兜底。（联网验收里名字一度变成
  `BloonsSuperMonkeyonKongregate–PlayNow_e72514.swf`，就是这么发现的。）
* **Kongregate 的 e 编号反推不出游戏页**：页面地址是「作者 / 游戏名」，网址里没有编号，
  站点也不提供「编号 → 页面」的跳转（试过 `/games/12345`、`/games/game_id/12345`、
  `/games/12345/游戏名` 等写法，全是 404 或跳回首页；作者名还区分下划线，`ferx_zero` 不能写成
  `ferx-zero`）。所以 `AppendixToUrl("e…")` 返回 null，`ResolveByAppendixAsync` 会给出人话提示；
  代价是这类游戏点「装入文件夹」时 ini 里的网络路径会留空（手工填一次即可）。
* **两个站的 swf CDN 都不校验 Referer**（实测带不带都能下），但仍然把游戏页当 Referer 传过去，
  免得哪天加了防盗链就抓瞎。
* **游戏名里可能含下划线**，所以「文件夹名 → 游戏名」的正则整串都会被吃掉（`^(.+)_([a-z][0-9]+)([sh])$`），
  必须用捕获组，不能用 `match.Length` 去截。
* **散放的 swf 条目，本体就是它自己**：`GameFullPath` 不要再拼一层，否则列表里每个 swf 都报“找不到本体”
  （自检抓到的）。
* **别在业务代码里从 URL 反推本地路径**：服务器才知道游戏文件夹名与相对路径，所以 `FileFetcher` 的签名
  直接把 `LocalFullPath` 传出去（`AutoDownloadFetch`）。反推过一次，结果是自动下载的文件写到了游戏目录外面。
* **`GamesLibrary`（以及 Flash 下载器 VM 里的 `Library`）千万别写成字段初始化器**：字段初始化器在构造函数
  体之前执行，那时 `GamesDirectory` 还是空字符串，于是游戏目录为空、首次运行的说明文件被写到了仓库根目录
  （自检的“无头构造 VM”那组断言就是为它加的）。
* **播放器没有稳定的“发现源”**：仓库根以前放过一份 `FlashPlayer.exe`（不在版本库里，现在已不在），
  所以配置里记住的路径随时可能失效。查找器要覆盖「用户自己整理过的目录」（如 `桌面\Flash Games\tool\`），
  找不到时的提示也要明确给出「指定…」这条路 —— 选一次就会记住并拷进 tools。

---

## 6. 验证清单（改完框架后跑一遍）

```powershell
# 1) 编译（0 警告 0 错误）
.\build.ps1

# 2) 轮子自检：1541 项行为逐条真跑（Data/Config 轮子 + 主页搜索排序 + 多级分类导航/面包屑
#    + 批量重命名规则/冲突/执行/撤销
#    + Flash 下载器的站点解析/命名/ini/批量计划/真下载/本地服务器/自动下载
#    + 联机聊天室的协议/CRC/流量上限/会话文件夹/傻瓜模式挑监听/双栈真连两族/回环真连一局
#    + 滚轮滚动链的真行为：造真结构扔真事件，断内层/外层各滚了多少
#    + osu! 下图器的查询串/命名/本地筛选 + 打自检自己起的假镜像真下载
#    + 播放器的扩展名归类/时间与快进快退/自然排序/真扫临时目录/合并去重/三种播放顺序/假播放器跑完整条 VM 流程
#    + 粒子引擎的生成/寿命/衰减/开关/上限/确定性
#    + osu!mania 三件套的解析往返/转 K/转 LN/等级/批量/线程安全 + StarRating 参考值
#    + 棋牌：五子棋禁手/象棋全规则/自由摆棋让子让先/回环联机与断线重连，
#    + 图片连续阅读器的滚动偏移数学/中键单点-按住状态机/页序自然排序/真扫临时图片文件夹/无头 VM，
#    退出码 0 = 全通过
dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj

# 3) 界面冒烟：直接开各功能截图（会生成对应 Config\<Id>\config.json；窗口不抢焦点）
$env:YLT_SHOT_TOOL="sample-framework"; $env:YLT_SHOT="shots\check-sample.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait

# 3b) Flash 下载器（会顺带启动本地服务器、把 FlashPlayer.exe 拷进 exe 旁边的 Data\flash-downloader\tools\）
$env:YLT_SHOT_TOOL="flash-downloader"; $env:YLT_SHOT="shots\flash-downloader.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
#   检查游戏列表的“选中高亮”就再加 $env:YLT_FLASH_SELECT="火柴人打羽毛球2_a90302.swf"

# 3c) 核对「表头与数据列」有没有对齐（打出来的列起点两边一样才算对齐）
$env:YLT_NAV_TOOL="sample-framework"; $env:YLT_MEASURE="shots\measure.txt"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe; Start-Sleep 5
Get-Content shots\measure.txt          # 看「列起点对照」那几行
#   再换个窗口尺寸测一次（专抓“一缩放就歪”）：$env:YLT_MEASURE_RESIZE="1024x700"

# 3c-2) 核对滚轮滚动链（把鼠标放到列表里滚几下，再看这个文件——每滚一下都会刷新）：
#   $env:YLT_NAV_TOOL="sample-framework"; $env:YLT_WHEEL_TEST="shots\wheel.txt"
#   内层没到底 → 「内层还能滚 → 交给内层原生滚动」；内层到底 → 「内层到头 → 替外层滚了一格」且外层偏移变大

# 3d) 主页搜索的两种状态（预置搜索词，自动截图）
foreach ($kw in "视频", "不存在xx") {
    $env:YLT_HOME_SEARCH = $kw; $env:YLT_SHOT = "shots\home-$kw.png"
    Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
}

# 3e) 联机聊天室：两种状态各截一张（默认空界面 / 塞几条演示消息看气泡与文件卡片）
$env:YLT_SHOT_TOOL="chat-room"; $env:YLT_NO_SERVER="1"
$env:YLT_SHOT="shots\chat-room.png";      Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
$env:YLT_CHAT_DEMO="1"; $env:YLT_SHOT="shots\chat-room-demo.png"; Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
#   看底部三栏要配 YLT_CHAT_SIZE / YLT_CHAT_SCROLL；联机排错用 YLT_CHAT_TRACE / YLT_CHAT_NET_TRACE（见第 0 节）

# 3f) 视频 / 音乐播放器：空列表 / 有内容 / 设置面板三种状态
$env:YLT_SHOT_TOOL="media-player"; $env:YLT_SHOT="shots\player-empty.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
$env:YLT_PLAYER_DEMO="1"; $env:YLT_SHOT="shots\player-demo.png"      # 塞 6 行假列表看版式
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
$env:YLT_PLAYER_SETTINGS="1"; $env:YLT_SHOT="shots\player-settings.png"   # 直接展开设置面板
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
#   ⚠ 控制按钮的图标是矢量 Path，截图放大 3~4 倍核对一遍（曾经用字符图标，全是豆腐块）

# 3g) 播放器：全屏版式（窗口会铺满整块屏，截出来的图就是屏幕大小）
$env:YLT_SHOT_TOOL="media-player"; $env:YLT_PLAYER_DEMO="1"; $env:YLT_PLAYER_FULLSCREEN="1"
$env:YLT_SHOT="shots\player-fullscreen.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
# 3h) 后台播放的迷你条：不开 YLT_SHOT_TOOL（停在主页），让主页 + 左下角小窗出现在同一张图里
Remove-Item Env:YLT_PLAYER_FULLSCREEN
$env:YLT_MINI_DEMO="1"; $env:YLT_SHOT="shots\player-mini.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait

# 3i) 多级分类：主页 / 类别页 / 工具页面包屑 / 设置窗口（YLT_NAV_CATEGORY、YLT_OPEN_SETTINGS 见第 0 节）
Remove-Item Env:YLT_MINI_DEMO
$env:YLT_SHOT="shots\home-category.png"; Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
$env:YLT_NAV_CATEGORY="音游工具"; $env:YLT_SHOT="shots\category-osu.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_NAV_CATEGORY
$env:YLT_OPEN_SETTINGS="1"; $env:YLT_SHOT="shots\shell-settings.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_OPEN_SETTINGS

# 3j) 粒子 + 三个 osu 工具（YLT_OSU_DEMO 会在 Data\osu-*\ 生成合成谱面，不发网络请求）
$env:YLT_PARTICLE_DEMO="1"; $env:YLT_SHOT="shots\home-particles.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_PARTICLE_DEMO
$env:YLT_OSU_DEMO="1"
foreach ($id in "osu-key-converter","osu-ln-transformer","osu-level-calculator") {
    $env:YLT_SHOT_TOOL=$id; $env:YLT_SHOT="shots\$id.png"
    Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
}
Remove-Item Env:YLT_OSU_DEMO, Env:YLT_SHOT_TOOL, Env:YLT_SHOT

# 3k) 棋牌：两个工具 + 类别页 + 暂停态 + 1024 窄窗（演示数据，不联网）
$env:YLT_SHOT_TOOL="gomoku"; $env:YLT_GOMOKU_DEMO="1"; $env:YLT_SHOT="shots\gomoku.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
$env:YLT_WINDOW_SIZE="1024x700"; $env:YLT_SHOT="shots\gomoku-1024.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_WINDOW_SIZE
$env:YLT_BOARD_DEMO_PAUSE="1"; $env:YLT_SHOT="shots\gomoku-pause.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_BOARD_DEMO_PAUSE, Env:YLT_GOMOKU_DEMO
$env:YLT_SHOT_TOOL="xiangqi"; $env:YLT_XIANGQI_DEMO="1"; $env:YLT_SHOT="shots\xiangqi.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
$env:YLT_XIANGQI_SETUP="1"; $env:YLT_SHOT="shots\xiangqi-setup.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_XIANGQI_SETUP, Env:YLT_XIANGQI_DEMO
$env:YLT_NAV_CATEGORY="棋牌"; $env:YLT_SHOT="shots\category-boardgames.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_NAV_CATEGORY, Env:YLT_SHOT_TOOL, Env:YLT_SHOT

# 3l) 图片连续阅读器：三种滚动方向 + 锚点指示器 + 设置面板（YLT_PICTURE_* 见第 0 节）
$env:YLT_SHOT_TOOL="picture-viewer"; $env:YLT_PICTURE_DEMO="1"
foreach ($m in "vertical","ltr","rtl") {
    $env:YLT_PICTURE_MODE=$m; $env:YLT_SHOT="shots\picture-$m.png"
    Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
}
#   横向方向核对「到底从哪头开始」：加 $env:YLT_PICTURE_SCROLL="0" / "1" 各截一张；
#   几何核对用 $env:YLT_PICTURE_MEASURE="shots\picture-measure.txt"（RTL 时 physicalOffset 与 readingOffset 互为反数）
#   换方向保持位置：$env:YLT_PICTURE_SCROLL="0.2"; $env:YLT_PICTURE_SWITCH_MODE="rtl"; 配 MEASURE 会先写 *.before
#   缩放 / 惯性：加 $env:YLT_PICTURE_ZOOM="200"（200%）与 $env:YLT_PICTURE_CROSS="0.5"（跨轴居中）；
#   放大后 measure 里 crossOffset 的上限应 > 0（= 另一个方向真的能滚，纵向能左右拖、横向能上下拖）
#   翻页 / 单页：$env:YLT_PICTURE_MODE="spread-ltr"/"spread-rtl"/"single" + $env:YLT_PICTURE_PAGE="3"；
#   双页页码 / 配对看 measure 的 pageIndex / left / right；左右按钮 / 方向键 / 滚轮翻页用 UIA Invoke 或手动点一下
#   双击图片 / 命令行打开：Start-Process $exe -ArgumentList "`"<图片路径>`""；配 YLT_PICTURE_MODE 与 MEASURE，
#   看 status 里的「已定位到第 N 张」与 readingOffset / pageIndex 是否对得上（文件关联开关用设置面板勾选实测）
$env:YLT_PICTURE_ANCHOR="1"; $env:YLT_SHOT="shots\picture-anchor.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_PICTURE_ANCHOR
$env:YLT_PICTURE_SETTINGS="1"; $env:YLT_SHOT="shots\picture-settings.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_PICTURE_SETTINGS, Env:YLT_PICTURE_MODE, Env:YLT_PICTURE_DEMO, Env:YLT_SHOT_TOOL, Env:YLT_SHOT

# 4) 发布冒烟：发布到 dist\YuLiangToolkit\，跑一次确认 Data/Config 建在它自己旁边
.\publish.ps1
$env:YLT_SHOT="shots\published.png"; .\dist\YuLiangToolkit\YuLiangTookit.exe
```

> ⚠️ 截图时用 `Start-Process -Wait` 跑 exe，别直接 `.\YuLiangTookit.exe`：它是 GUI 子系统程序，
> PowerShell 不会等它结束，你会以为“没生成截图”。

**写自检时的坑（v0.6.0 踩过）**

* `Check` 的委托签名混了「带 detail / 不带 detail」两套时，脚本批量替换容易把换行吃掉，连着坏几轮 ——
  **改动断言代码后一定要先编译**，别靠眼睛看。
* `HttpListener` 在 `Start()` 失败后**连 `Prefixes` 都不让碰**（`ObjectDisposedException`）：
  复用同一个实例重试端口会随机挂，每次重试都 new 一个。
  另外**本机沙箱里 `HttpListener.IsSupported=False`**，那一组会打印一行“跳过”而不是失败。
* 测「坏行跳过」要用**真正写了一半的 JSON**：`System.Text.Json` 默认（宽容）模式能把半截对象解析成
  字段全空的对象，于是看起来像没生效；读日志要用严格模式 + 关键字段非空校验。
* `Environment.TickCount64` 只有约 15.6ms 分辨率，拿它做速率窗口会把「1 秒内 2 KB」算成 500 B/s；
  默认时钟改用 `Stopwatch`（自检可以注入假时钟）。
* **受限沙箱里子进程写不了系统临时目录**（`UnauthorizedAccessException` 会在第一组断言就崩，跟被测代码无关）——
  自检统一走 `SelfCheckScratch`：先探测系统临时目录能不能写，不能就退到自检程序自己旁边。
* **滚轮的"合成事件"要按真实输入的顺序抛两趟**（先 `PreviewMouseWheel`，没被吃掉再 `MouseWheel`）：
  只抛冒泡事件的话，挂在 `PreviewMouseWheel` 上的处理器**一次都不会执行**。
  另外 `ScrollToXxx` 是排队在 `LayoutUpdated` 里执行的，测量前要先把 layout 过干净，否则它会在事件之后
  生效、把偏移改回去（看起来就像"合成事件滚不动"）。详见 2.9.1。

---

## 7. 现有功能状态

| 功能 | Id | 选项存储 | 数据目录 | 备注 |
| --- | --- | --- | --- | --- |
| 主页 / 外壳 | `shell` | `Config\shell\config.json`（排序偏好 + 各功能上次使用时间 + 粒子开关 `showParticles`） | — | 多级分类目录/类别页/可点击面包屑（2.15）、主页设置窗口、窗口级粒子（2.16）；**搜索词不持久化** |
| 视频转音频 | `video-to-audio` | `Config\video-to-audio\config.json`（含从 `%AppData%` 老设置的一次性迁移） | `Data\video-to-audio\`（暂未使用） | `Order = 10`；FFmpeg 路径是全局设置，仍在 `%AppData%\YuLiangToolkit\settings.json`；标签：视频/音频/批量转换/FFmpeg/mp3/flac/提取音轨 |
| 批量重命名 | `batch-rename` | `Config\batch-rename\config.json`（**规则清单 `rules` 数组** + 冲突开关 + 排序偏好 + 上次文件夹） | `Data\batch-rename\rename-history.json`（改名流水账，撤销靠它，最多 20 批） | `Order = 40`；规则可增删、可重复、可拖动排序，**数组顺序 = 执行顺序**（见 2.10）；标签：重命名/改名/批量/文件/序号/编号/查找替换/正则/扩展名/撤销 |
| Flash 下载器 | `flash-downloader` | `Config\flash-downloader\config.json`（游戏目录 / 并发 / 超时 / 端口 / 播放器路径 / 上次输入） | `Data\flash-downloader\games\`（游戏本体 + `SWFNetPath.ini`）、`Data\flash-downloader\tools\`（播放页 / 替代 API / FlashPlayer.exe） | `Order = 20`；支持 4399 / 7k7k / 17yy / ArmorGames / Kongregate，本地服务器 + 自动下载所需文件（见 2.11）；**目录可在界面上改**；标签：flash/swf/小游戏/下载/4399/7k7k/17yy/ArmorGames/Kongregate/本地服务器/离线玩/h5/unity/网页游戏 |
| 联机聊天室 | `chat-room` | `Config\chat-room\config.json`（昵称 / 头像 / **傻瓜模式** / 监听地址与端口 / 要连的主机 / 单次流量上限与方向 / 发图压缩 / 大文件确认 / 最近会话） | `Data\chat-room\Protocol\`（协议说明）、`Data\chat-room\Sessions\<时间>\`（**每次连接新建**：`chat-log.jsonl` + `session.json` + `images`/`audio`/`video`/`files`） | `Order = 30`；主机开 TCP 端口、客户端填 IP/域名 + 端口，局域网与内网穿透通用（IPv6 也行）；文字 / 图片 / 音频 / 视频 / 任意文件双向发送；**单次流量上限 + 用量指示**；**傻瓜模式默认开**（工具自己挑双栈监听 + 列出本机 IPv4/IPv6 与端口，见 2.12）；标签：聊天/聊天室/局域网/联机/TCP/内网穿透/IPv6/传文件/发图片/语音/视频/流量限制/主机/客户端 |
| 联机五子棋 | `gomoku` | `Config\gomoku\config.json`（玩家名 / 端口 / 上次地址 / 房主执子 / **禁手开关** / 显示禁手点 / 棋盘大小 / 显示棋谱） | —（对局与棋谱只在内存，不落盘） | `Order = 60`；**归入「棋牌」类别**；主机开 TCP 房间 / 客户端加入 / 同机双人；**禁手（黑棋三三/四四/长连判负，五连优先）可开关**、15/19 路、悔棋求和认输再来一局、房间聊天；**断线自动重连**（2s×30）+ 对局暂停（见 2.18）；标签：五子棋/连珠/禁手/棋牌/联机/局域网/TCP/双人/对弈/悔棋 |
| 联机象棋 | `xiangqi` | `Config\xiangqi\config.json`（玩家名 / 端口 / 上次地址 / 房主执棋 / 走法提示 / 自动翻转 / 显示坐标 / 最后一步） | —（对局与棋谱只在内存，不落盘） | `Order = 61`；**归入「棋牌」类别**；完整中国象棋规则（马腿/象眼/不过河/九宫/飞将/炮架/过河兵、将军/将死/困毙/自然限着）；**自由摆棋 + 一键默认开局 + 让子（单马/双马/单车/车马/车马炮/九子，让子方红黑可选）+ 让先 0~5**；走法提示/自动翻转/中文记谱/悔棋求和认输再来一局/断线重连；标签：象棋/中国象棋/棋牌/联机/局域网/TCP/双人/对弈/摆棋/让子/让先/悔棋 |
| osu! 下图器 | `map-downloader` | `Config\map-downloader\config.json`（Client ID / Client Secret、Access Token 与到期时间、搜索条件、下载目录与并发数） | `Data\map-downloader\downloads\`（下回来的 `.osz`，**目录可在界面上改**） | `Order = 50`；**归入「音游工具」类别**（`CategoryPath`）；从独立 WinForms 小工具改写（见 2.13）；搜索走 osu! 官方 API（要自己注册 OAuth 应用换 Token），`.osz` 走第三方镜像 dl.sayobot.cn；标签：osu/osu!/谱面/beatmap/下图/下图器/osz/下载/搜索/mania/音游 |
| osu! Key 转换器 | `osu-key-converter` | `Config\osu-key-converter\config.json`（目标 Keys / Gap / Clean / Blank / Seed / Creator / 键数过滤） | —（转换结果写**原谱面目录**，见 2.17） | `Order = 51`；音游工具；低 k → 高 k（只支持升 key）；KeyConverter 算法逐行改写 + 修复 1k/2k/HitSound 等已知 bug |
| osu! LN 转换器 | `osu-ln-transformer` | `Config\osu-ln-transformer\config.json`（Level / LN% / Divide / Column / Gap / OD 开关 / Seed / Creator / Ignore / Original LN / Fix Error / 键数过滤） | —（同上） | `Order = 52`；音游工具；14 档 LN 转换（-3~10，含 Hold off / Random / Invert / 高斯密度）；LNTransformer 算法逐行改写 + 修复 Original LN/OD 等已知 bug |
| osu! 等级计算器 | `osu-level-calculator` | `Config\osu-level-calculator\config.json`（显示/计算偏好） | —（只读谱面，不写文件） | `Order = 53`；音游工具；拖入 `.osu` 批量算 mania 星数（Star Rating Rebirth 改写）→ 三段拟合公式得 Level；表格展示 |
| 视频 / 音乐播放器 | `media-player` | `Config\media-player\config.json`（读取方式 / 递归 / 追加还是覆盖 / 音量 / 播放顺序 / 快进快退步长 / 后台播放 / 恢复上次列表与位置） | `Data\media-player\playlist.json`（列表快照：路径 + 上次那首 + 听到第几秒） | `Order = 15`；本地播放器（见 2.14）：`MediaElement` 出画面，列表循环 / 单曲循环 / 随机，**左键快进快退（步长 0.1~60 秒可调）、右键上一首下一首**，**整窗全屏**（`F11` / 右下角按钮），**后台播放**（切走不断声 + 左下角迷你条），可拖放、记住上次的列表 |
| 图片连续阅读器 | `picture-viewer` | `Config\picture-viewer\config.json`（滚动方式 / 滚轮一格像素 / 中键单点-按住判定 / 离屏释放 / 上次文件夹） | `Data\picture-viewer\`（只有 `YLT_PICTURE_DEMO` 演示图；正常使用为空） | `Order = 16`；**媒体**；把文件夹图片拼成一条无间距长带：**纵向 / 横向左→右 / 横向右→左**，滚轮直接左右滚（不用 Shift）、左键拖拽、**中键锚点（单点留下按任意键消失 / 按住松手消失）**（见 2.19）；标签：图片/看图/漫画/条漫/长图/滚动/左右滚动/日式漫画/拖拽/中键/锚点 |
| 框架示例 | `sample-framework` | `Config\sample-framework\config.json` + `options.json` | `Data\sample-framework\`（首运行写入 `示例数据.txt`） | `Order = 900`；纯演示，整目录可删（删前把主程序里的 `ProjectReference` 与 sln 条目一起去掉）；标签：示例/模板/Data/Config/轮子演示 |

---

## 8. 版本管理约定（git）

本地仓库，分支 `master`。**改完代码先 `.\build.ps1` + 自检，再提交**。

### 什么不进版本库（都在 `.gitignore` 里，改规则时看那里的注释）

| 忽略项 | 原因 |
| --- | --- |
| `bin/` `obj/` `.dotnet-cli-home/` | 构建产物 / dotnet CLI 本地状态，随时可重建 |
| `.idea/` `.vs/` `*.user` `*.suo` | IDE 个人设置（想共享运行配置可单独 force-add） |
| `shots/*.png` `shots/*.txt` `testdata/out/` | 调试截图与转换产物 |
| `Config/*/*.json` `Config/*/*.tmp` | 运行时生成的选项文件，**含本机路径**，删除即恢复默认 |
| `Data/*/` | 功能数据可能很大（以后的 swf 等）；想版本化就删掉这条规则 |
| `FlashPlayer.exe` | 11 MB 第三方 Flash 运行时（仓库里那份现在已经没有了，规则留着无妨）：用「Flash 下载器」时它会被拷到 `Data\flash-downloader\tools\` 下，而 `Data/*/` 已忽略 |
| `FlashGameDownloader/` | 参考用的原版 Python 小项目，**已经删除**；忽略规则留着无妨（主项目 `DefaultItemExcludes` 里那条同理） |
| `MapDownloader/` | 改写「osu! 下图器」时参考的原版 WinForms 小项目（里面还有 bin/obj/publish 一堆二进制），**不入库**；已经改写进 `src/YuLiangToolkit.Tools.MapDownloader/`，想让它进版本库就删掉这条（或 `git add -f`） |
| `PictureView/` | 移植「图片连续阅读器」时参考的原版小项目（**已移到桌面**），**不入库**；已经移植进 `src/YuLiangToolkit.Tools.PictureViewer/`。主项目 `DefaultItemExcludes` 也加了 `PictureView\**`，万一再放回仓库根也不会把主程序编译搞崩 |

> `Config/README.md`、`Data/README.md` 这些**说明文件是入库的**（规则只忽略了它们下面的功能子目录/生成物）。

### 换行与二进制（`.gitattributes`）

仓库内统一存 LF，Windows 工作区按 `core.autocrlf=true` 检出为 CRLF；`*.exe/*.png/*.mp4/*.swf` 等标记为
`binary`，避免 git 做换行转换产生假 diff；`*.ps1`、`*.sln` 固定 CRLF。

### 常用命令

```powershell
git status                      # 看改动（中文文件名已把 core.quotepath 设为 false，不转义显示）
git add -A; git commit -m "..."  # 提交
git log --oneline --stat -3     # 看最近提交改了什么
git diff                        # 看未暂存改动
# 想让某个功能回到默认选项：删掉 Config\<功能Id>\*.json 再开一次功能即可
```

### 注意

* 二进制/大文件尽量别提交（git 历史不可回退，会把仓库永久撑大）；确需入库的先确认体积。
* 新增顶层目录时，除了 `.gitignore`，还要同步主项目的 `DefaultItemExcludes`（见第 5 节）。
* `.ps1` 保持纯 ASCII 或存成 UTF-8 with BOM（见第 5 节）。
* **唯一入库的第三方依赖是 `OsuParsers`（`src/YuLiangToolkit.Tools.OsuMania` 的 PackageReference）**。
  `packages.lock.json` 没有启用；开发机首次 `build.ps1` 需要联网还原一次（之后走本机 NuGet 缓存），
  `publish.ps1` 会把 `OsuParsers.dll` 一并发出来。别把 `bin/obj` 或下载的 `.nupkg` 提交进去。

---

## 9. 变更记录（按版本，细节都在前面各节里）

| 版本 | 内容 |
| --- | --- |
| **v0.15.0**（当前） | **「图片连续阅读器」支持双击图片直接打开（文件关联）**（见 2.19）：① 设置面板新增「文件关联」：勾选后把 .jpg / .jpeg / .png / .bmp / .gif / .webp / .tif / .tiff 登记到本工具（只写 HKCU，不需要管理员），可在「打开方式」里选本工具；② 双击图片（或把图片路径作为命令行参数）启动时，主程序直接进「图片连续阅读器」，加载这张图所在文件夹并**自动定位到它**：连续模式滚到那一张开头（日式模式按阅读偏移换算），翻页 / 单页模式直接翻到那一页；③ 关联前会备份各后缀原来的默认程序，取消勾选时还原；不硬碰系统保护的 UserChoice，界面提示去「默认应用」确认一次并给出按钮；④ 自检 1532 → **1541** 项（新增 9 项：ReadingOffsetForPage / 后缀表 / 打开命令引号 / 启动参数判断 / 定位与只消费一次）；UIA 实测关联勾选与注册表登记/还原、命令行启动定位截图。版本号 0.14.0 → 0.15.0 |
| v0.14.0 | **「图片连续阅读器」新增双页翻页 / 单页浏览，并把阅读方式挪进设置**（见 2.19）：① 阅读方式从 3 种扩到 **6 种**：连续滚动（纵向 / 横向左右）+ **双页翻页左→右**（中间两页，一次翻一张：右边移到左边、新的从右边进）、**双页翻页右→左**（日式镜像，整页往右滑）、**单页浏览**（左右按钮 / 滚轮 / 方向键）；② 工具条不再摆所有模式，只显示**当前方式按钮**（点它打开设置），切换统一在设置面板的「阅读方式」下拉里（以后加方式只改下拉）；③ 新增翻页设置：`PagedWheel`（滚轮 = 翻页 / 缩放）、`PageTurnAnimation`（约 0.19 秒滑动动画，默认开；只在 100% 未平移时播放）；④ 支持翻页模式下缩放 + 拖拽平移 + 惯性；左右按钮在日式模式自动互换语义，方向键 / PageUp/PageDown / 空格 / Home/End 可翻页，底部显示页码；⑤ 连续↔翻页切换保持位置（按比例/页号互算）；⑥ 新增 `PicturePaging` 纯逻辑（翻页配对 / 排版 / 平移夹取）与 `PictureViewerView.Paged.cs` partial；自检 1505 → **1532** 项（新增 27 项：6 种方式分类 / 双页配对 / 翻页步进 / 排版 / 平移夹取 / 滚轮动作 / 无头模式切换）。截图冒烟：双页左右 / 单页 / 1024 窄窗 / 设置面板 / 连续模式回归；UIA 实测左右按钮翻页与设置里切模式。版本号 0.13.0 → 0.14.0 |
| v0.13.0 | **「图片连续阅读器」加缩放 / 双轴自由拖拽 / 拖动惯性（越滑越快）**（见 2.19）：① 顶部工具条新增**百分比输入框（默认 100%，可自由改 25%~800%）+ 两个放大镜按钮（− / +，一步 10%）**，缩放作用于整条长带，`Ctrl+滚轮` 也能以画面中心缩放；**100% = 原来的铺满基准**（不是原图像素 1:1）。② 缩放后**另一个轴也能滚**：纵向模式可左右拖、横向模式可上下拖（`_crossOffset` + `Shift+滚轮`；图片宽高改为都显式给，否则 ScrollViewer 认为另一轴没得滚）。③ **拖动惯性**（`InertiaEnabled`，默认开）：松手后按甩动速度继续滑、指数衰减、太慢/到边界停；④ **惯性越滑越快**（`InertiaAccelerate`，默认开，JMComic2 手感）：下一次甩动把还在滑的残余速度**叠加**上去（封顶 6 px/ms），而不是清零重置。⑤ 纯逻辑抽成 `PictureInertia`（采样 / 衰减 / 叠加 / 上限），自检 1483 → **1505** 项（新增 22 项：缩放换算 / 跨轴拖拽 / 惯性采样 / 衰减 / 叠加 / 上限 / 无头缩放命令）。版本号 0.12.1 → 0.13.0 |
| v0.12.1 | **修复「滚动方式下拉点不动」+ 鼠标事件归属**（见 2.19）：① 根因是工具为了让左键拖拽/中键锚点生效，在 `PreviewMouseDown` 里**按坐标**判断“在长带视口里”就把事件吃掉并抢鼠标捕获 —— ComboBox 下拉弹层是独立窗口，点下拉项时坐标恰好在视口上，于是点击被当成“开始拖拽”吃掉（`Mouse.Captured` 也被抢走），表现就是**下拉项点不动**。修法：改成看事件源（`IsFromViewport`）+ 别人握着鼠标捕获时整个事件都不碰。② 「滚动方式」从 ComboBox 换成**顶部工具条里三个分段按钮**（`SegmentedListBoxStyle`，点一下就切、少一个弹层）。③ 新增排错钩子 `YLT_PICTURE_INPUT_TRACE`（把鼠标按下/松开/丢捕获写进文件），顺手把 ListBox/ListBoxItem 加进键盘快捷键的“别抢键”名单。自检仍 **1483** 项（纯 UI 交互，靠截图 + UIA 选中第 3 个模式核对 config 已落盘为 `HorizontalRightToLeft`）。版本号 0.12.0 → 0.12.1 |
| v0.12.0 | **新增「图片连续阅读器」**（`Tools.PictureViewer`，见 2.19）：把仓库根原来的 `PictureView` 小项目移植进来并扩展。① 一个文件夹的图片按页序**首尾相接拼成一条长带**（无间距、无分页），滚轮连续浏览，解决普通看图器一屏一张、条漫 / 长图要一张张点的痛点；② **三种滚动方式**：纵向（默认）/ **横向左→右**（中式漫画）/ **横向右→左**（日式漫画，第一张排在最右边）；横向模式下**滚轮直接左右滚**（不用按 Shift）、**左键按住可拖动画面**；③ **中键锚点两种玩法**：单点（按得短）→ 锚点留下、移动鼠标按离锚点的距离快速滚动、**再按任意键（左/中/右）消失**（PictureView 原行为）；按住（超过判定时长）→ 锚点出现、移动鼠标快速滚动、**一松手立刻消失** —— 两者用 `MiddleHoldMilliseconds`（默认 250ms）区分；④ 离屏图片自动释放（3 秒宽限）、按显示宽度解码、重建时用 generation 丢弃过期解码；⑤ 设置存 `Config\picture-viewer\config.json`；开发用 `YLT_PICTURE_DEMO` / `MODE` / `SCROLL` / `ANCHOR` / `SETTINGS` / `MEASURE`；⑥ 自检 1412 → **1483** 项（新增 71 项：滚动数学 / 阅读偏移↔物理偏移换算 / 锚点状态机 / 页序 / 真扫临时图片目录 / 无头 VM / 配置）；三种滚动方向 + 锚点 + 设置面板截图冒烟；移植来源 `PictureView/` 已移到桌面并加进 `.gitignore` 与 `DefaultItemExcludes`。版本号 0.11.0 → 0.12.0 |
| v0.11.0 | **新增「棋牌」类别：联机五子棋 + 联机象棋**。① 新增 `Core/Net/` 通用 TCP 房间底座（帧格式 / 双栈监听 / 心跳 / 端点解析 / 本机地址分享，与聊天无关、可复用）与 `Tools.BoardGames/Shared`（棋牌房间协议、`BoardRoomSession` 握手与转发、基类 `BoardGameRoomViewModelBase`、房间/聊天面板）。② **联机五子棋**（`gomoku`）：主机开房 / 客户端加入 / 同机双人；**禁手开关**（黑棋三三/四四/长连判负，五连优先，可标禁手点）、15/19 路、棋谱、悔棋求和认输再来一局、房间聊天。③ **联机象棋**（`xiangqi`）：完整规则（马腿/象眼/不过河/九宫/飞将/炮架/过河兵、将军/将死/困毙/自然限着）、**自由摆棋 + 一键默认开局 + 让子（单马/双马/单车/车马/车马炮/九子，让子方可选）+ 让先 0~5**、走法提示、自动翻转、中文记谱、悔棋求和认输再来一局。④ **断线重连**：客户端意外断线自动重连（2 秒 × 最多 30 次）、重连中可「停止重连」、主机保局并显示「对局暂停 · 等待对方重连」、房主关房/主动离开不重连；对局暂停期间禁止落子（主机需 `HasPeer` 才能走）。⑤ 新增 `InverseBoolToVis` 转换器（别拿 `InverseBoolConverter` 绑 Visibility）、开发用 `YLT_WINDOW_SIZE`。⑥ 自检 1005 → **1412** 项（棋牌 407：注册 25 + 五子棋 92 + 象棋 141 + 配置 23 + 网络/重连/联机 VM 126）；主页/类别页/两工具/摆棋/暂停态/1024 窄窗截图冒烟。版本号 0.10.0 → 0.11.0 |
| v0.10.0 | **多级分类导航 + 粒子效果 + osu!mania 三件套**。① 外壳：`ITool.CategoryPath`（默认空，老工具零改动）+ `Core/Tools/ToolCatalog.cs` 任意层级目录树；主页「全部分类」卡片 → 类别页（可继续下钻子类别）→ 工具；顶部工具条改成**可点击面包屑**「主页 › 类别 › 工具」；侧边栏只列根层类别 + 根层工具；搜索仍然扁平覆盖全部工具。新增「音游工具」类别（🎵，`ShellCategories` 可扩展），`osu! 下图器` 归入其中（见 2.15）。② 粒子：新增 `ParticleEngine`（纯逻辑，38 项自检）+ `ParticleOverlay`（OnRender、按 60fps 归一、粒子上限 600、事件成对挂摘），窗口级覆盖层；主页右上角新增「⚙ 主页设置」窗口，粒子开关默认开、存 `Config\shell\config.json` 的 `showParticles`（见 2.16）。③ 新增 `Tools.OsuMania`：`osu-key-converter` / `osu-ln-transformer` / `osu-level-calculator` 三个工具（算法逐行照搬 + 修复已知 bug：1k/2k 崩溃、HitSound 丢失、Original LN 只最后一列、OD 被误写 0、无红点闪退等），解析器从手写 `OsuFileV14.cs` 换成 **OsuParsers 1.7.2（NuGet，MIT，本项目唯一第三方依赖）**，星数算法数值基准 10/10 差 0；三个工具与下图器同属「音游工具」（见 2.17）。④ 自检 718 → **1005** 项（导航 53 + 粒子 38 + 星数 38 + osu 158）；主页类别/设置/粒子/三工具截图冒烟。版本号 0.9.7 → 0.10.0 |
| **v0.9.7** | **播放器新增「时间显示」设置**（见 2.14）：进度条左边那个时间可以选 **已播放时间**（`Elapsed`，默认，从 0:00 数到总时长）或 **剩余时间**（`Remaining`，从总时长倒数到 0:00，即「还差多少秒播完」）；右边始终是总时长。纯逻辑抽成 `MediaTimeDisplay.Format(position, duration, mode)`（倒计时夹在 0 ~ 总时长，时长未知显示 `--:--`），VM 切换下拉立即刷新（不等 200ms 计时器），设置项存 `TimeDisplayMode`（枚举按 C# 原名存字符串）。自检 713 → **718** 项。版本号 0.9.6 → 0.9.7 |
| **v0.9.6** | **摆正两处「不正中」**（见 2.7 / 2.14）：① 播放器控制条的大播放键以前是跟着「快退 + 播放 + 快进 + 停止」整组居中，**组居中 ≠ 播放键居中**，所以全屏时它明显偏左；改成**播放键独自占中间的 `Auto` 列、左右两列都是 `*`**，全屏实测 `playCenter.x = 960`（屏幕正中，蓝圆像素中心 959），快退/快进/停止贴着它两边，设置与音量仍靠边。② 主窗口以前用 `CenterScreen` 居中（把任务栏那 48px 也算进屏幕高度，窗口看着偏低），改成 `WindowStartupLocation="Manual"` + 按 `SystemParameters.WorkArea` 算**工作区正中间**（1920×1080 + 48px 任务栏时窗口开在 `320,106`，中心 `960,516`）。自检仍 **713** 项（纯版式，真机截图核对）。版本号 0.9.5 → 0.9.6 |
| **v0.9.5** | **修「全屏底部一条黑带、控制条最底下被裁」**（见 2.14）：`WM_GETMINMAXINFO` 把窗口撑到整块显示器后，Windows 还给窗口套了一个**工作区大小的窗口 Region**（`GetWindowRgn` = `(4,4)-(1924,1036)`），窗口矩形 1088、实际可见 / 命中区域只有 1032 —— 任务栏藏掉后底部 48px 露出黑色桌面，控制条的状态文字被裁掉。修法：`MainWindow.ClearFullscreenRegion` 在进全屏 + 400ms 看门狗里 `SetWindowRgn(hwnd, 0)`（先 `GetWindowRgn` 判断有没有区域，避免无谓重绘）；清掉 Region 后全屏窗口会真的盖住任务栏，所以失活 / 最小化时 `Topmost = false`，保证“还回去的任务栏”不会再被压住。顺带：控制条默认的 `Grid.Row="1"` 从控件本地值挪进 `ControlBarStyle`（本地值压过 DataTrigger，全屏 `Grid.Row=0` 一直没生效），新增 `YLT_PLAYER_MEASURE` 排查钩子；控制台自检在重定向（Rider / VS / 管道）时**切 UTF-8 输出**，IDE 里不再乱码（真正的控制台仍按代码页写）。自检仍 **713** 项（窗口 Region 没法无头断言，真机截图核对）。版本号 0.9.4 → 0.9.5 |
| **v0.9.4** | **启动时的「完整性自检」**（见 2.14）：全屏藏不住任务栏时，第一眼就能分清是「权限不够」还是「代码不对」。新增 `Core/Services/IntegrityCheck.cs` —— 只读地读本进程的 `Mandatory Integrity Level`（`OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)`）+ `FindWindow("Shell_TrayWnd")`，`App.OnStartup` 把「级别 / 任务栏 / 结论」三行打到控制台（同时写 `Debug`；设了 `YLT_TASKBAR_TRACE` 也追加），级别低于 `Medium` 时直接给出 `icacls "<exe 目录>" /setintegritylevel "(OI)(CI)Medium" /T` 的修复命令。**背景**：v0.9.3 的全屏代码本身没问题，是 `bin\Debug` 里 143 个文件又被低权限沙箱编译标成了 `Low`（目录是 Medium，标签在**文件**上，覆盖写会保留）→ UIPI 拒绝操作任务栏（错误码 5）；`/T` 改回 Medium 后实测全屏中 `IsWindowVisible(Shell_TrayWnd)=False`、退出后 `True`。自检 704 → **713** 项（只测只读的等级归类与报告文案，**不藏任务栏**）。版本号 0.9.3 → 0.9.4 |
| **v0.9.3** | **修「全屏藏不住任务栏」**（见 2.14 的「藏任务栏」）：任务栏自己也是置顶窗口，靠「铺满 + `Topmost`」盖它不可靠 —— 改成**真的藏**（`Core/Services/TaskbarHider.cs`：`ShowWindowAsync(SW_HIDE)` + `SetWindowPos(SWP_HIDEWINDOW)` + `ShowWindow(SW_HIDE)`，主屏 + 副屏都藏，以 `IsWindowVisible` 的真实结果为准），并挂 **400ms 看门狗**（`MainWindow._taskbarWatch`）盯住「explorer 把它显示回来」；退出全屏 / 失活 / 最小化 / 关窗口 / 进程退出一律还回去。顺带去掉 UI 线程上的 `Thread.Sleep`、新增 `YLT_TASKBAR_TRACE` 排查日志与 `TaskbarHider.LastError`。**真正踩的坑不在代码里**：工作区目录带着别的沙箱工具留下的 `Low Mandatory Level` 标记 → 文件夹里的 exe 以 **Low 权限**运行 → UIPI 让 `ShowWindow` / `SHAppBarMessage` 一律返回 `5`（拒绝访问）；用 `icacls <目录> /setintegritylevel "(OI)(CI)Medium"` 修好后一切正常。自检 704 项（这块**故意不进自检**，跑测试藏开发者任务栏太吓人）。版本号 0.9.2 → 0.9.3 |
| v0.9.2 | **迷你条标题改成「来回滚」**（见 2.14）：**放得下就一动不动**（之前是两份文本首尾相接一直滚，短标题也会被滚走，反而读不全）；放不下才滚 —— 右→左滚到最后一个字 → 停 1.2 s → 左→右滚回第一个字 → 停 → 循环。速度 34 px/s，只溢出几像素时至少滚 1 s；「要不要滚 / 一趟多久」抽成纯函数 `MiniPlayerPanel.ScrollDuration`。自检 672 → **678** 项。版本号 0.9.1 → 0.9.2 |
| v0.9.1 | **全屏改成「沉浸式」**（见 2.14）：画面铺满整屏（连任务栏一起盖住），平时屏幕上**只有画面**；**鼠标往屏幕下方一动**才浮出控制条（正在放什么 + 进度条 + 全套按钮 + 退出全屏），**2.5 秒不动鼠标**就自动缩回去、鼠标指针也一起隐藏（v0.9.0 那版是全屏后控制条一直占一条）。做法：控制条被包进 `ControlBar`，全屏时由 `ControlBarStyle` 的 DataTrigger 改成「`Grid.Row=0` + 底部对齐 + 上透下黑渐变」，画面那块 `Grid.RowSpan=2`；显隐走 `ControlsVisible`，闲置计时器在界面代码后置里（鼠标停在控制条上 / 拖进度条时不缩）。自检 665 → **672** 项。版本号 0.9.0 → 0.9.1 |
| v0.9.0 | **播放器加全屏 + 后台播放 + 左下角迷你条**（见 2.14）：① **整窗全屏**（`F11` / 画面右下角按钮，`Esc` 退出；藏掉标题栏 / 侧边栏 / 播放列表，连任务栏一起盖住）；② 设置项「**后台播放**」（默认开）：切到别的工具 / 回主页时**声音不断**，外壳会保住播放器的视图（`IBackgroundTool`），切回来是同一个 VM、列表与进度都还在；③ 后台播放时左下角浮出**迷你播放条**：标题从右往左循环滚 + 快退 / 播放暂停 / 快进（**左键跳步长、右键上一首 / 下一首**，与播放器页一致），点标题回播放器。踩坑：全屏**不能**直接设 `Left/Top/Width/Height`（会被夹回工作区，1920×1080 只拿到 1920×1032），必须临时改 `WM_GETMINMAXINFO` 再最大化 + 临时 `Topmost`；后台播放时界面 `Unloaded` **不能**顺手 `Close()` 播放器。自检 654 → **665** 项。版本号 0.8.0 → 0.9.0 |
| v0.8.0 | **新增「视频 / 音乐播放器」**（`Tools.MediaPlayer`，见 2.14）：左中右三栏（播放区 + 进度条与控制按钮 + 播放列表）；打开文件夹（可多选、可递归）/ 打开文件 / 拖放，按「音频 + 视频 / 仅音频 / 仅视频」过滤，再开一个文件夹可**追加**或**覆盖**；列表循环 / 单曲循环 / 随机播放（随机一轮不重复）；**可调步长的快进快退（0.1 秒精度、0.1~60 秒），左键快退快进、右键上一首下一首**；音量、记住上次的列表与听到的位置。播放器抽成 `IMediaPlayback`，自检用假播放器把整条流程跑完。踩坑：控制按钮用 `⏪ ⏩ ⏹ 🔁` 字符会变成豆腐块（改矢量 Path + MDL2 图标）、`MediaElement` 的 `Loaded/UnloadedBehavior` 必须 `Manual`、设置面板要盖整列而不是只盖视频区。自检 555 → **654** 项。版本号 0.7.2 → 0.8.0 |
| v0.7.2 | **「联机聊天室」加傻瓜模式**（默认开，见 2.12）：监听地址与端口不再让用户选，`ChooseSimpleListenPlan` 自己挑（优先双栈 `::` + 上次的端口，被占了改让系统挑）；开完房间直接把**本机 IPv4 / IPv6 地址 + 端口**列成可复制的一行行「地址:端口」（没有可用 IPv6 就直说「当前网络没有 IPv6」），内网穿透照着填本地端口即可。⚠ **IP 和端口一个都没藏**，只是不让用户「选」。顺带修一个真 bug：绑 `::` 时没开 `DualMode`，**IPv4 客户端一个都连不进来**（自检 `CheckDualStack` 用真连接兜住，去掉修复会红 3 条）。自检 535 → **555** 项。版本号 0.7.1 → 0.7.2 |
| v0.7.1 | **「Flash 下载器」新增 ArmorGames / Kongregate 两站的网页 swf 抓取**（见 2.11）：后缀字母新增 `d` / `e`；ArmorGames 从 `/play/编号` 页的 `<param name="movie">` / `<embed src>` 读 swf 与宽高，页面里没有 swf 的（HTML5 游戏）自动退回整页下载；Kongregate 走「游戏页 → `…/embed`（`iframeConfig`）→ frame 页（`swf_location`）」三跳抓 swf，并排掉它自己的 `API_AS3_*.swf`。顺带：粘 `.swf` 直链也能直接下载；`ResolveAsync` 的未知域名兜底也能认出这两家的页面结构。自检 501 → 535 项，另加一个**默认不跑**的联网验收开关 `YLT_LIVE_SITES=1`。版本号 0.7.0 → 0.7.1 |
| v0.7.0 | **新增「osu! 下图器」**（`Tools.MapDownloader`，见 2.13）：把仓库根那个独立的 WinForms 小工具（`MapDownloader/`）改写进来 —— 填 osu! 的 Client ID / Secret 换 Token，搜谱面集（模式 / 状态 / 排序 / NSFW / 日期 / Key 数），勾选或全量下载 `.osz`（含视频 `full` / 无视频 `novideo`）；下载走第三方镜像、并发 5、跳过已存在、**取消不留半个文件**。自检 441 → **501** 项（新增 `MapDownloaderChecks` 60 项，含"自检自己起假镜像真下载"）。版本号 0.6.0 → 0.7.0 |
| v0.6.0 | **新增「联机聊天室」**（`Tools.ChatRoom`）：主机开 TCP 端口 / 客户端填 IP/域名 + 端口，局域网与内网穿透通用；文字 / 图片 / 音频 / 视频 / 任意文件双向发送；**每次连接新建一个时间命名的会话文件夹**；**单次流量上限 + 用量指示**。自研帧协议见 2.12，自检在 `127.0.0.1` 上真开一局双向收发。自检 355 → 433（v0.7.2 时共 **555** 项） |
| v0.6.0 后续微调 | 修 `ComboBoxStyle` 的两个缺陷（缺 `PART_EditableTextBox` → 选中后一片空白；`Margin="10,6"` 顶得文字偏下）+ 补 `MinHeight`；宽度改成按内容定（数字框 `Width="Auto"`、底部三栏 5:6:5）；修「房间开着却显示未监听」（`IsOnline` 置位顺序）；**监听地址从下拉框改成输入框**。教训：layout 类问题要「先量内容真实宽度 → 按内容定宽 → 截图/UIA 实测」，别拍像素值 |
| v0.5.3 | 滚轮滚动链（`ScrollChaining`）第一版：原实现自己调 `LineUp/LineDown` 滚外层，比原生慢得多（用户指出），于是改成"**什么都不做、放行给外层**"；顺带修好框架示例模板里没起名的 `ScrollViewer`（它导致那个列表滚轮完全失效）。⚠ **"放行"这条思路是错的**（WPF 在内层滚不动时照样吃事件），见下面那行 |
| 滚轮修复（v0.6.0 后续微调 ③） | **滚动链重做**（见 2.9.1）：查清 `ScrollViewer.OnMouseWheel` 里 `e.Handled = true` 是**无条件**执行的（滚不动也吃事件）—— 这才是"卡在列表上滚不动"的根因；改成在**隧道阶段**判断，内层滚不动就替外层滚**原生一格（48px）**并把事件吃掉。自检从"7 项 FAIL、只能靠推理"变成 **24 项真行为断言全绿**（造真结构、按真实输入顺序扔事件，断内层滚几项 / 外层滚几像素 / 谁吃事件）；顺带删掉会死锁的 `--wheel-report` 入口与两个只能看不能测的探针文件 |
| v0.5.2 | **表头与数据列对齐**（见 2.9）：表头从 ListBox 外面挪进它自己的 `ControlTemplate`，与数据行同处一列。新增 `YLT_MEASURE` 钩子逐对核对列起点（1280×820 / 1024×700 / 1600×900 三档完全一致）；顺带删掉 `SampleView.xaml.cs` 里那套没跑通的布局同步代码 |
| v0.5.1 | **修文件列表行样式**（`FileRowItemStyle`：内边距 / 悬停选中反馈 / 前景色 / 选中强调条，见 2.9）；**游戏列表加搜索 + 排序 + 正逆序**（`Core/GameFilter.cs`）；游戏列表下方按钮分两行；修网页播放页 `frame.htm` 里“深字配深底”的配色 |
| v0.5.0 | **新增「Flash 下载器」**（`Tools.FlashDownloader`，见 2.11）：4399 / 7k7k / 17yy 解析、`游戏名_?123[s|h]/` 命名 + `SWFNetPath.ini`、本地服务器 + **自动下载所需文件**、关联文件批量下载、自由下载工具、4399 h5 替代 API；四个弹窗改成同页面板。**同一轮把 Data/Config 改到 exe 旁边**（`AppPaths`：`YLT_HOME` → exe 同级 → `%AppData%` 兜底）+ 新增 `publish.ps1`。真站手测过 4399/flash/6853、4399/flash/108914、7k7k/swf/123456（不进自检，避免依赖外网） |
| v0.4.1 | Data/Config 位置修正（从「向上找 sln 写到仓库根」改成 exe 旁边）。**踩坑**：中途用 `<None CopyToOutputDirectory>` 拷仓库 Data/Config 到输出目录，构建的 IncrementalClean 把 exe 旁边同名文件当垃圾删了（删掉过已下载的游戏与播放器）——该机制已移除，并写成第 5 节的禁令 |
| v0.4.0 | **新增「批量重命名」**（`Tools.BatchRename`，见 2.10）：规则清单（可增删 / 可重复 / 可拖动排序）+ 两阶段改名 + 撤销。踩坑：冲突判定要迭代到稳定、撤销互换要排除本批目标、`SequencePosition` 与 BCL 撞名、拖动时在 `CollectionChanged` 里 `ScrollIntoView` 会闪退、指示线死区导致「一碰蓝线就闪到最底下」（靠 `YLT_DRAG_DEBUG` 定位） |
| v0.3.0 | 主页**搜索 + 排序 + 标签**（`ITool.Tags` / `ToolFilter` / `ToolCard` / `ShellConfig`，见 2.8）。踩坑：枚举在 JSON 里按 **C# 原名**保存、别改成 camelCase；主页搜索词**故意不持久化** |
| v0.2.0 | 初次提交：Data / Config 存储与配置框架（`AppPaths` / `ToolStorage` / `JsonFileStore` / `ToolConfig` / `ToolConfigStore` / `DynamicToolConfig` / `OptionItem` / `ToolViewModelBase`，见 2.3–2.5）、「框架示例」工具、框架自检程序，并把「视频转音频」迁移到新配置体系（老选项快照留在 `LegacyAppSettings.cs`，迁移只在首次运行生效） |
