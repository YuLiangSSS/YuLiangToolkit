# YuLiangToolkit（凉雨工具集|自用小工具）99.99% AI Coding

一个可扩展的 WPF 工具集应用：主页是工具卡片墙（**搜索 / 排序 / 标签**），左侧导航自动列出所有已发现的工具。
功能多了会按类别收进**类别页**（支持任意层级分类，顶部是可点击的面包屑 `主页 › 音游工具 › 工具名`）；
右上角「⚙ 主页设置」进入**设置页面**（面包屑 `主页 › 主页设置`）：可以切换**四套界面风格（默认深色 / 液态玻璃 / 毛玻璃 / 亚克力）**、**上传窗口背景图（半透明风格下透出来，自适应窗口不拉伸）**、分别勾选**自动效果（粒子拖尾 / 鼠标散光）**与**点击效果（粒子爆炸 / 散光扩散 / 光晕闪烁 / 反色圆环，可多选、与自动效果独立；数量 / 范围 / 半径都有滑块可调）**、自定义**粒子颜色（1~2 种颜色随机渐变）**、开关**主页标签墙（标签多时默认只排 3 行 + 横向滚动条，可一键展开，也可以整块隐藏）**，还能选**点 × 时「关闭应用 / 最小化到托盘」**，页面底部还有**清理缓存 / 重建配置（恢复默认）/ 界面布局（恢复默认布局）**三张卡（清掉缩略图、最近浏览这类缓存；把一个功能的选项恢复默认；把各工具页拖出来的区块大小恢复默认）；程序在**任务栏右侧有托盘小图标**（单击恢复窗口，右键菜单：显示主窗口 / 主页设置 / 退出）；顶部工具条还有**浏览器式「← 后退 / → 前进」**，跳错页面可以快速回退。
默认深色主题，另有三套**玻璃风格主题**（液态玻璃 = 半透明玻璃面板 + 流动彩色光晕，参考 [Glassium](https://github.com/Kerxs/glassium)；
毛玻璃 = 奶白磨砂 + 重模糊背景，不跑动画；亚克力 = 深色着色 + 轻模糊 + 颗粒噪点，类似 Win11 亚克力），
首页设置页面里一键切换、立即生效；还支持**上传窗口背景图**（复制进 `Data\shell\background\`，铺满窗口或完整显示，都不拉伸，
只在半透明风格下从面板后面透出来；选中风格后可在它下面用滑块调**背景图模糊度** 0~60 px，三套风格各记各的值）；自定义标题栏（最小化 / 最大化 / 关闭按钮画在界面里，没有系统窗口顶栏；**最大化时按住标题栏往外拖，
会先还原成原来的窗口大小再跟着鼠标走** —— 和普通 Windows 窗口一样）。
**工具页的区块可以自己拖**（v0.41.0）：页面里每个区块与相邻区块之间都能单独拖（左右调宽度、上下调高度，不留死块）；
鼠标移到缝隙上会亮起蓝色竖线 / 横线、光标变 ↔ / ↕，拖动即可调整大小；平时留一条「提示线」帮你找缝隙，它的透明度可以在
「主页设置 → 界面布局 → 分隔条提示线」里调（0% = 完全看不见 / 默认 18% / 100% = 一直显示）。松手自动记住，双击某条缝恢复这一处，
全部恢复默认也在「主页设置 → 界面布局」。
**左侧「功能列表」栏也能拖**（v0.66.0）：鼠标放它右边边缘（↔ 光标）左右拖即可改宽度、自动记住、双击恢复 232；
主页设置「界面布局」里还能勾掉「显示左侧「功能列表」栏」把整块收起（内容区占满），需要时再勾上。

## 工具一览

每个工具都有**单独一页**详细说明（用法、设置项、数据放在哪、注意事项）—— **点表里的工具名就能进去**：

| 工具 | 能干什么 |
| --- | --- |
| [**🎵 音频工具**](docs/tools/audio-tools.md) | 视频转音频（MP3 / FLAC / 原样提取）+ 分段视频合成（m3u8 / ts → mp4/mkv），批量 + 进度 + 取消 |
| [**🎬 视频 / 音乐播放器**](docs/tools/media-player.md) | 本地播放器：播放列表、单曲 / 随机、**沉浸式全屏（鼠标一动浮出控制条）**、**后台播放（切页不断，左下角迷你条：标题来回滚 + 进度条可拖 + 播放模式可点，右上角 × 关小窗并停播）**、**每个文件夹记住上次听到哪首 / 哪儿（可选恢复后自动接着放）**、**关应用 / 后台播放时也记住当前那首与进度**、**列表排序（名称 / 日期 / 大小，正反序）**、可调步长的快进快退 |
| [**🖼️ 图片连续阅读器**](docs/tools/picture-viewer.md) | 把一个文件夹的图片首尾相接拼成一条长带（无缝、无间距），滚轮连续看条漫 / 长图 / 漫画，可切纵向 / 横向 / 日式从右到左，也可切**双页翻页 / 单页浏览**；支持 **25% ~ 800% 缩放**、放大后**自由拖拽**、可开关的**拖动惯性（连续甩会越滑越快）**与**滚轮惯性（滚一下能滚很远）**、**F11 全屏沉浸阅读（工具条自动隐藏）**；可**关联图片后缀，双击图片直接打开并定位到那张**（默认**复用同一个窗口**，也能在设置里切成**多开**）；**🕘 最近浏览可快速重开看过的文件夹（上限可调）**；**点工具条上的文件夹路径可在资源管理器打开所在目录**；还能调**三层图片边框（RGBA 颜色 + 调色板）**并**一键恢复默认** |
| [**📄 PDF 阅读器**](docs/tools/pdf-reader.md) | 简单 PDF 阅读器：纵向 / 横向连续滚动、双页并排一次翻两页；支持 **25% ~ 800% 缩放**（Ctrl+滚轮 / 双页放大后拖动平移）、**Ctrl+F 文字搜索**、**📑 书签目录**、**双页页面间距可调**、**拖动惯性 / 滚轮惯性可分别开关**、**双击 .pdf 关联打开**、**自动记住每本 PDF 上次读到哪**、**页码跳转**、**🕘 最近浏览（上限可调）**、**F11 全屏沉浸阅读（工具条自动隐藏）**；只渲染看得见的页省内存，用 Windows 自带的 `Windows.Data.Pdf` 渲染；只读不改原文件 |
| [**📘 Word 阅读器**](docs/tools/word-viewer.md) | 只读浏览 `.docx`：标题大纲、加粗 / 斜体 / 下划线 / 颜色 / 字号、列表、表格、图片、超链接；支持 **50% ~ 300% 缩放**、**Ctrl+F 按段落查找**、**🕘 最近打开**、拖入即开；手写 OOXML 解析（不依赖 Word、不引第三方库），**不改原文件** |
| [**📗 Excel 阅读器**](docs/tools/excel-viewer.md) | 只读浏览 `.xlsx`：工作表页签、Excel 列宽 / 行号 / 列标题、数字 / 日期 / 百分比等格式、合并单元格、单元格字体与填充色；状态栏显示选中单元格的地址 / 值 / 公式；**🕘 最近打开**、拖入即开；手写 OOXML 解析，**公式只显示缓存值（不计算）、不改原文件** |
| [**⚡ Flash 下载器**](docs/tools/flash-downloader.md) | 4399 / 7k7k / 17yy / ArmorGames / Kongregate 的小游戏下载到本地离线玩 |
| [**💬 联机聊天室**](docs/tools/chat-room.md) | 主机开端口、客户端连进来，文字 / 图片 / 文件随便传，带单次流量上限；房间内还能开**群语音 / 群视频通话**（最多 8 人，媒体复用同一个端口，不计入文件流量上限），也能**按住说话发语音消息**（自动转 MP3、气泡显示秒数、**可拖进度条反复听**、计入文件流量上限） |
| [**🌐 网页浏览器**](docs/tools/browser.md) | Chromium 内核（WebView2）**多标签浏览器**：**上方 / 左侧两种标签栏**一键切换（+ 新建 / 中键关标签 / **会话恢复（只恢复标签、点开才加载）**），地址栏输入网址 / 搜索词（默认 Google），**前进 / 后退 / 刷新 / 主页**按钮，**网页里右键也能刷新**；工具条 **⚙ 齿轮**里还有**历史记录 / 下载侧栏、页面缩放（Ctrl+滚轮）、默认主页设置、清空浏览记录**；**右键图片可「图片另存为…」/ 复制图片**；**☆ 收藏 / 书签侧栏**；**🧲 资源嗅探（图片 / 音频 / 视频）**；`target=_blank` 在新标签页打开 |
| [**🏷️ 批量重命名**](docs/tools/batch-rename.md) | 规则清单 + 实时预览 + 两阶段改名 + 可撤销 |
| [**📝 文本编辑器**](docs/tools/text-editor.md) | 轻量代码 / 文本编辑器（AvalonEdit 内核）：**26 种语言 / 格式的语法高亮**（C / C++ / C# / Java / Python / Go / Rust / Ruby / F# / Kotlin / PHP / JS / TypeScript / Vue / HTML / CSS / JSON / XML / YAML / INI / Batch / PowerShell / Lua / SQL / Dockerfile / Markdown…）、**多标签**、**Markdown 实际效果预览**（标题 / 表格 / 列表 / 代码块 / 本地图片真渲染）、**行号 + VS 深色配色**、**查找 / 替换**（区分大小写 / 全部替换可一步撤销）、**界面缩放 25% ~ 400%**、**偏好设置**调字体 / 字号 / **行距** / 背景颜色 / 自动换行 / 缩进；**自动识别编码（GBK 文件不乱码）**并保持原换行风格，**自动保存（默认开）** + 最近文件 + 会话恢复 |
| [**🎧 osu! 下图器**](docs/tools/map-downloader.md) | 搜谱面集、批量下 `.osz`（含视频 / 无视频） |
| [**🎹 osu! Key 转换器**](docs/tools/osu-key-converter.md) | 把低键数 mania 谱面「复制列 / 插空白列」加成高键数（2~10 可选），支持批量 |
| [**🍜 osu! LN 转换器**](docs/tools/osu-ln-transformer.md) | 按 14 档强度把普通键改成 LN（长条），可选保留原 LN / 修尾巴误差 |
| [**⭐ osu! 等级计算器**](docs/tools/osu-level-calculator.md) | 拖入 mania 谱面算星数与等级，批量表格；**等级按 8K 谱面拟合（只对 8K 有参考意义）**，SR（xxy rating）通用 |
| [**🎹 KPS 按键统计**](docs/tools/kps.md) | 实时 KPS / 峰值 / 平均 + 每键计数器 + 键位可视化 + **透明置顶 Overlay**（可锁定点击穿透 / 拖动 / 滚轮缩放 / **强制置顶**）；4K~8K 预设、改键、单键图片、音符效果、键音、全局热键（功能灵感来源 DmNote，独立重写） |
| [**🐟 Live2D 桌宠**](docs/tools/live2d.md) | 内置 Live2D 模型测试台 + 桌面宠物：**参数 / 部件 / 动作 / 表情 / 物理 / 眼睛跟随**逐项调试，**面部参数测试（眼睛 / 嘴 / 舌头 + 预设）**、**鼠标跟随灵敏度可调（横向 / 抬头 / 低头 / 纵向偏移）**，透明置顶、可拖动、滚轮缩放、右键菜单、点击穿透、待机眨眼 / 呼吸 / 整身轻倾；另外支持**摄像头面部动捕（转头 / 眨眼 / 张嘴）**、**身体晃动测试（直观看到物理链带动头发 / 裙子 / 尾巴）**、**🎵 音乐跟唱 / 律动（系统声音回环分析，自动识别纯音乐 / 带歌词，跟着唱嘴型 + 跟节拍摇）**、**🖐 动作演示（8 手势 / 6 表演 / 4 舞蹈 + 待机随机小动作）**、**🐭 鼠标互动玩法（摸头 / 额头三态表情 / 捏脸 / 挠痒 / 护住 / 点手竖食指 / 拖拽物理，Ctrl+Alt+L 穿透、Ctrl+Alt+0 退出）**与**💬 语音说话 / 轻对话（默认微软 Edge 在线软妹音，断网回退本地 TTS；嘴型 + 聊天气泡同步）**；**模型 / 动作 / 音乐分析 / 互动全本地、不需要显卡**（官方 Cubism Core + 自写软件渲染器 + Windows 自带人脸检测 / WASAPI / SMTC / TTS；仅在线语音需联网发送文字），也能换成自己的 `.moc3` 模型 |
| [**⚫ 联机五子棋**](docs/tools/gomoku.md) | 开 TCP 房间联机下五子棋：**最多 16 人多人观战 + 房主安排上场 / 自动轮换**、可选禁手（三三 / 四四 / 长连）、悔棋、聊天、同机双人 |
| [**🀄 联机象棋**](docs/tools/xiangqi.md) | 开 TCP 房间联机下中国象棋：**多人观战 + 房主安排上场 / 自动轮换**、自由摆棋 + 一键默认开局、让子 / 让先、中文记谱 |
| [**📨 Spug 验证码测试**](docs/tools/spug-captcha.md) | 联调 Spug 推送助手（push.spug.cc）的短信 / 邮箱验证码模板：发验证码 + 本地比对验证 + 接口日志；推送 URL 是调用凭证，别外传 |
| [**🖱️ 右键菜单管理**](docs/tools/context-menu-manager.md) | 把资源管理器右键菜单里的项目（所有文件 / 文件夹 / 文件夹空白处 / 驱动器 / 桌面背景）列成一张表：名称 / 类型 / 位置 / 状态一目了然，**搜索 + 位置 / 类型筛选 + 只看已隐藏**；不想要的（例如「上传到百度网盘」）**一键隐藏、随时恢复**，还能「恢复全部（仅本工具隐藏的）」与重启资源管理器；卸载残留的菜单可以**删除**（删前自动备份成 `.reg` 到 `Data\context-menu-manager\backups\`，删 HKCU 直接删、删 HKLM 弹一次 UAC，系统关键项不可删），缺的可以自己**添加「用 XX 打开」**（指定后缀 / 文件夹 / 文件夹空白处 / 所有文件，只写 HKCU）；**隐藏与添加都只写当前用户（HKCU）的覆盖项、不改电脑级设置、不需要常驻管理员权限**，系统关键项（新建菜单 / 打开）不可隐藏 |
| [**📦 压缩包工具**](docs/tools/archive.md) | 拖入即用：解压 RAR（RAR4 / RAR5）/ ZIP / 7Z / TAR / TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST / GZ / BZ2 / XZ / ZST，支持**密码**、**旧版 GBK 文件名**、批量、进度、取消、**路径穿越防护**；也能把文件 / 文件夹压成 **ZIP / 7Z**，同名文件默认自动改名不覆盖；**RAR 因版权原因只能解不能压** |
| [**🗂️ 打开方式**](docs/tools/open-with.md) | 把本工具集登记进 Windows 右键 **打开方式**：**3 个入口** —— **按文件类型自动分流**（PDF / 图片 / Word / Excel / 压缩包 / 音视频 / 文本）、**以文本方式打开**、**以压缩包方式打开**；任意文件右键都能选到，只写当前用户注册表（HKCU）、不需要管理员权限，取消勾选即可完全恢复 |
| [**🧪 框架示例**](docs/tools/sample-framework.md) | 开发用模板（Data / Config 轮子演示，可整目录删除） |

> 🎵 **音游工具**是主页上的一个分类：点「音游工具」类别卡片进类别页，再点工具卡片打开；
> 顶部的面包屑（`主页 › 音游工具 › 工具名`）每一层都能点回去。osu! 相关的四个工具和「KPS 按键统计」都收在这个分类里。
>
> 🎲 **棋牌**也是主页上的一个分类：联机五子棋与联机象棋都收在里面。一个房间最多 16 人，
> 两人上场、其余观战；房主安排谁上场，也可以开自动轮换（输的下场 / 双方都下场）。
>
> 🐟 **Live2D 桌宠**收在主页「**桌面**」分类里：点「桌面」类别卡片进类别页，再点工具卡片打开
> （它是这个分类目前唯一的工具）。

> 全部文档的索引（含通用说明）在 [docs/](docs/README.md)。

> 主页的搜索 / 排序 / 标签、自定义标题栏、主页设置底部的清理缓存 / 重建配置、以及**数据存在哪
> （Data / Config / Cache 目录约定）**：见 [docs/shell.md](docs/shell.md)。
>
> 🔄 **工具页状态保留**（v0.46.0）：切去别的工具 / 主页再切回来，打开的文件 / 文件夹、阅读位置、
> 列表、游戏局面都会原样恢复（外壳缓存最近 12 个工具页）；见 [docs/shell.md](docs/shell.md#工具页状态保留切走不丢)。

## 快速开始

```powershell
# 一键还原 + 构建（离线可用，用本机 NuGet 缓存）
.\build.ps1

# 运行（或直接在 Rider / Visual Studio 里打开解决方案按运行）
dotnet run --project YuLiangTookit.csproj

# 发布到 dist\YuLiangToolkit\（加 -SelfContained 可打成不依赖本机 .NET 的版本）
.\publish.ps1
```

要求：.NET 9 SDK（`global.json` 已锁定 9.0.317）；系统需 **Windows 10 2004（build 19041）及以上**（PDF 阅读器用系统自带的 `Windows.Data.Pdf`）。
只有「音频工具」（视频转音频 + 分段视频合成）依赖 FFmpeg（应用会自动检测 `PATH` 里的 `ffmpeg.exe`，也可以在工具里手动指定）——
见 [音频工具](docs/tools/audio-tools.md)。osu! 三个转换 / 计算工具用 `OsuParsers`（MIT）解析 `.osu`：
从源码构建时**首次需要联网还原一次** NuGet 包，之后走本机缓存（`build.ps1` 离线也能命中）。
「网页浏览器」用微软官方的 **WebView2**（Chromium 内核，和 Edge 同源）承载网页：需要系统装有
**Microsoft Edge WebView2 Runtime**（Win10 2004+ 一般随 Edge 自带，缺失时工具会提示去安装；
浏览器内核不随本项目打包），源码构建时同样**首次需要联网还原一次** `Microsoft.Web.WebView2` 包。
「文本编辑器」用 **AvalonEdit**（MIT）做编辑内核（行号列 / 语法高亮 / 大文件虚拟化 / 撤销重做都是它提供的），
源码构建时同样**首次需要联网还原一次** NuGet 包；运行时不依赖任何额外系统组件。
「Word 阅读器 / Excel 阅读器」**不需要安装 Office / WPS**：`.docx` / `.xlsx` 本质是 ZIP + XML，
解析由 `Core/Documents/OoxmlPackage` + 各自手写的解析器完成，**没有新增任何 NuGet 包**（只读、不修改原文件）。
「Live2D 桌宠」不需要额外装任何东西（模型和官方 Cubism Core 原生库都随程序发布，仅限 Windows x64）；
它的**摄像头面部动捕**用 Windows 自带摄像头与 FaceAnalysis API（不需要额外软件，默认关闭，
打开时需在 Windows 隐私设置里允许「桌面应用访问摄像头」，画面不录像、不联网）。

## 文档

- 📘 **[docs/](docs/README.md)** —— 每个工具一页的详细说明（就是 GitHub Wiki 那种用法，但它跟着代码一起走）；
- 🧰 **[AGENTS.md](AGENTS.md)** —— 改代码前先看它：**精简的开发入口地图**（流程 / 硬性规则 / 找东西）；
  踩坑细节按需查 **[docs/dev/pitfalls.md](docs/dev/pitfalls.md)**（只在排错 / 改危险代码时读）；
- ✅ **自检**：3853 项行为逐条真跑（退出码 0 = 全通过）。**按改动影响面只跑相关组，不每轮全量**：

  ```powershell
  .\scripts\check.ps1          # 自动读 git 改动 → 选组 → build → 只跑这些组
  .\scripts\check.ps1 -List    # 看「文件 → 自检组」映射（分级规则见 docs/dev/verification.md）
  ```

## 数据与配置

**运行时目录就在 exe 旁边**（`bin\Debug\net9.0-windows10.0.19041.0\` 或发布出来的目录），首次运行自动生成：

- `Data\<功能Id>\` —— 功能自己的东西（下载的游戏、聊天记录、播放列表快照…）；
- `Config\<功能Id>\config.json` —— 那个功能所有选项的值，**改一下就自动保存，没有「保存」按钮**；
- `Cache\<功能Id>\` —— 缓存（缩略图 / 临时文件之类），**随时可删**、删了最多下次重新生成，「主页设置 → 清理缓存」里可以一键清。

因为数据跟着 exe 走，**整个程序目录拷走即可迁移 / 发布**；反过来 `dotnet clean`、手删 `bin`/`dist`
会连数据一起删掉。细节见 [docs/shell.md](docs/shell.md#数据与配置都在-exe-旁边)。

## 项目结构

```
YuLiangTookit.csproj                     # 主程序（WPF 壳：自定义标题栏、主页/类别页/面包屑、侧边栏导航、四套主题 + 背景图）
Views/Themes/                            # 主题 token：默认深色 / 液态玻璃 / 毛玻璃 / 亚克力 + 运行时换肤 ThemeManager（主页设置里切换）
build.ps1 / publish.ps1                  # 一键构建 / 一键发布（发布到 dist\YuLiangToolkit\）
scripts/check.ps1                       # 日常自检触发器：按改动自动选组，只跑受影响的组（全量例外见 docs/dev/verification.md）
AGENTS.md                               # 精简开发入口地图（流程 / 硬性规则 / 找东西；细节按需查 docs/dev/）
SingleInstance.cs                       # 单实例唤醒（Mutex + 命名管道：再启动一次只唤醒已开窗口）
FileLaunchRouter.cs                     # 「打开方式」启动路由（命令行 → 工具页 → IFileLaunchTarget 交出文件）
README.md                               # 本文件：工具一览 + 快速开始
LICENSE                                 # Unlicense（随便用，不用署名）
THIRD-PARTY-NOTICES.md                  # 第三方组件许可（OsuParsers / WebView2 / AvalonEdit / SharpCompress / ppy 数学工具 / Live2D Cubism Core / 内置模型）
docs/                                   # 每个工具一页的详细说明（见 docs/README.md）
docs/dev/pitfalls.md                    # 踩坑清单（按需读）
docs/dev/verification.md                # 验证纪律：自检分级 / 路径→组映射 / 全量例外
Data/  Config/  Cache/                   # 只有说明文件【运行时目录在 exe 旁边】
src/
├── YuLiangToolkit.Core/                 # 核心库：工具契约 / 多级分类目录树 / MVVM 基类 / 存储 / 配置 / FFmpeg 封装 / 粒子与光标特效 / 通用 TCP 房间
├── YuLiangToolkit.Tools.AudioTools/     # 功能：音频工具（视频转音频 + 分段视频合成）
├── YuLiangToolkit.Tools.MediaPlayer/    # 功能：视频 / 音乐播放器
├── YuLiangToolkit.Tools.PictureViewer/  # 功能：图片连续阅读器（连续长带 / 双页翻页 / 单页 + 缩放 / 惯性 / 双击打开 / 图片边框）
├── YuLiangToolkit.Tools.PdfReader/      # 功能：PDF 阅读器（上下 / 左右连续滚动 + 双页翻页 + 拖动惯性）
├── YuLiangToolkit.Tools.WordViewer/     # 功能：Word 阅读器（docx 只读：大纲 / 缩放 / 查找 / 表格 / 图片；手写 OOXML 解析）
├── YuLiangToolkit.Tools.ExcelViewer/    # 功能：Excel 阅读器（xlsx 只读：工作表页签 / 数字格式 / 合并单元格 / 公式缓存值）
├── YuLiangToolkit.Tools.BatchRename/    # 功能：批量重命名（规则清单引擎 + 两阶段改名 + 撤销）
├── YuLiangToolkit.Tools.FlashDownloader/# 功能：Flash 下载器（五站点解析 + 本地服务器 + 自动补全资源）
├── YuLiangToolkit.Tools.ChatRoom/       # 功能：联机聊天室（TCP 直连 / 内网穿透 + 传文件 + 流量上限）
├── YuLiangToolkit.Tools.Browser/        # 功能：网页浏览器（WebView2 / Chromium 内核 + 多标签页 / 历史记录 / 下载 / 缩放 / 主页设置）
├── YuLiangToolkit.Tools.TextEditor/     # 功能：文本编辑器（AvalonEdit 内核 + 26 种语言高亮 + 多标签 + Markdown 实际效果预览 + 查找替换 + 界面缩放）
├── YuLiangToolkit.Tools.Archive/        # 功能：压缩包工具（解压 RAR / ZIP / 7Z / TAR.* / GZ / BZ2 / XZ / ZST + 创建 ZIP / 7Z；RAR 只能解不能压）
├── YuLiangToolkit.Tools.MapDownloader/  # 功能：osu! 下图器（搜谱面集 + 批量下 .osz）
├── YuLiangToolkit.Tools.OsuMania/       # 功能：音游工具（osu! Key 转换器 / LN 转换器 / 等级计算器）
├── YuLiangToolkit.Tools.BoardGames/     # 功能：棋牌（联机五子棋 / 联机象棋 + 房间联机底座）
├── YuLiangToolkit.Tools.Kps/            # 功能：KPS 按键统计（Raw Input 全局输入 + KPS 统计 + 置顶透明 Overlay + 全局热键）
├── YuLiangToolkit.Tools.Live2D/         # 功能：Live2D 桌宠（内置模型测试台 + 透明置顶桌宠 + 眼睛跟随 + 摄像头面部动捕 + 身体晃动测试；官方 Cubism Core + 自写软件渲染器 + Windows 自带人脸检测）
├── YuLiangToolkit.Tools.SpugCaptcha/    # 功能：Spug 验证码测试（短信 / 邮箱验证码模板联调 + 本地比对验证）
├── YuLiangToolkit.Tools.ContextMenu/    # 功能：右键菜单管理（枚举 / 隐藏 / 恢复 / 删除残留 / 添加自定义打开菜单；隐藏写 HKCU、删除前自动备份 .reg、HKLM 项走 UAC 提权）
├── YuLiangToolkit.Tools.OpenWith/       # 功能：打开方式（登记 Windows 右键「打开方式」3 个入口：自动分流 / 文本 / 压缩包；入口副本 + 只写 HKCU）
└── YuLiangToolkit.Tools.Sample/         # 框架示例（新功能的复制模板，可整目录删除）
tests/YuLiangToolkit.FrameworkSelfCheck/ # 轮子自检控制台程序（3853 项行为逐条验证）
```

## 如何添加一个新工具

实现 `ITool`（提供 `Id / Name / Description / EmojiIcon / Category / Order` 与 `CreateView()`）就会被
`ToolDiscovery` 在启动时自动发现；想收进类别页就再加一个 `CategoryPath`（例如 `new[] { "音游工具" }`，
支持任意层级，不写就放在主页根层）。选项继承 `ToolConfig`，ViewModel 继承 `ToolViewModelBase`，
Data / Config 的路径与持久化全由框架接管。也可以把编译好的 dll 放进程序目录的 `plugins\`（外部插件，不用改主程序）。

最省事的做法是复制 `src\YuLiangToolkit.Tools.Sample` 当模板，完整步骤见
[AGENTS.md](AGENTS.md) 与 [框架示例](docs/tools/sample-framework.md)。

## 许可证

本项目采用 **Unlicense**（见 [LICENSE](LICENSE)）：

> **随便用、随便改、随便 fork、商用、二次发布、打包分发都可以，不需要署名，也不用保留版权声明。**
> 唯一请求：别把作者标成别人。

仓库里有少量第三方内容仍遵循它们自己的许可：`OsuParsers` 与 ppy osu!lazer 的数学工具（均为 MIT）、
`Microsoft.Web.WebView2`（BSD-3-Clause，Chromium 内核控件；WebView2 运行时是系统组件，不随程序分发），
以及「Live2D 桌宠」用到的 **Live2D Cubism Core 原生库**（Live2D Inc. 的 Proprietary Software License）
与内置模型（作者自有，非开源素材）；摄像头动捕用的是 **Windows 自带** MediaCapture / FaceAnalysis
（操作系统组件，非第三方库）。详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

