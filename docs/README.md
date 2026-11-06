# 文档索引

> 根目录的 [README](../README.md) 放「项目简介 + 快速开始 + 工具一览」；**每个工具的详细说明都在这里**，
> 一页一个，和 GitHub Wiki 的用法一样（都在仓库里，跟着版本走，不用另外维护一套网页）。

## 工具说明

| 工具 | 一句话 | 详细说明 |
| --- | --- | --- |
| 🎵 音频工具 | 视频转音频（MP3 / FLAC / 原样提取）+ 分段视频合成（m3u8 / ts → mp4/mkv） | [tools/audio-tools.md](tools/audio-tools.md) |
| 🎥 录屏 / GIF | 用 FFmpeg 的 gdigrab 录屏：全屏 / 框选区域 / 指定窗口 → GIF 或 MP4 / MKV / MOV / AVI / WebM（H.264 / H.265 / AV1 / VP9 / MPEG-4 / MJPEG 编码器可选，画质或码率可调）；支持麦克风 + 系统声音、摄像头预览与屏幕叠加、可自定义全局快捷键，也能截 PNG | [tools/screen-recorder.md](tools/screen-recorder.md) |
| ✂️ 剪辑工具 | 简易视频 / GIF 剪辑：时间轴缩略图定位、起止裁剪、多片段按顺序拼接，导出 MP4 / MKV / MOV / AVI / WebM 或 GIF，可生成低清预览片段确认效果 | [tools/media-editor.md](tools/media-editor.md) |
| 🎬 视频 / 音乐播放器 | 本地播放器：播放列表、循环随机、可调步长的快进快退 | [tools/media-player.md](tools/media-player.md) |
| 🖼️ 图片连续阅读器 | 把一个文件夹的图片首尾相接成一条长带（无缝、无间距），滚轮连续看条漫 / 长图 / 漫画；六种阅读方式（连续滚动三种方向 / 双页翻页左右 / 单页浏览），支持缩放、自由拖拽、拖动惯性 / 滚轮惯性、F11 全屏沉浸阅读（工具条自动隐藏）、🕘 最近浏览（上限可调）、双击图片打开与三层图片边框 | [tools/picture-viewer.md](tools/picture-viewer.md) |
| 📄 PDF 阅读器 | 简单 PDF 阅读器：纵向 / 横向连续滚动 + 双页并排一次翻两页，支持缩放、Ctrl+F 文字搜索、书签目录、双页页面间距、拖动惯性 / 滚轮惯性、自动记住每本 PDF 上次阅读位置、页码跳转、🕘 最近浏览（上限可调）、F11 全屏沉浸阅读（工具条自动隐藏）与双击 .pdf 关联；只读不改原文件 | [tools/pdf-reader.md](tools/pdf-reader.md) |
| 📘 Word 阅读器 | 只读浏览 `.docx`：标题大纲、字符格式、列表、表格、图片、超链接、页眉页脚、脚注 / 尾注、批注面板、修订视图，50% ~ 300% 缩放、Ctrl+F 查找（区分大小写 / 表格内）、复制全文、打印、导出 HTML / 纯文本、自动记住阅读位置、🕘 最近打开、拖入即开；手写 OOXML 解析，不改原文件 | [tools/word-viewer.md](tools/word-viewer.md) |
| 📊 Excel 阅读器 | 只读浏览 `.xlsx`：工作表页签、列宽 / 行号 / 列标题、数字 / 日期 / 百分比格式、合并单元格、字体与填充色、冻结窗格还原、自动行高 / 换行、显示公式、超链接单元格、行列标题高亮，Ctrl+F 查找单元格、右键复制值 / 公式 / 地址、自动记住工作表与阅读位置；手写 OOXML 解析，只显示公式缓存值、不改原文件 | [tools/excel-viewer.md](tools/excel-viewer.md) |
| 📽️ PPT 阅读器 | 只读浏览 `.pptx` / `.pptm` / `.ppsx`：幻灯片原样还原（文字 / 图片 / 表格 / 形状 / 背景 / 主题色）、缩略图导航、25% ~ 400% 缩放与适应窗口、演讲者备注、Ctrl+F 查找、复制本页文字、F5 全屏放映、最近打开、阅读位置记忆、文件关联（⚙ 设置里勾选后双击演示文稿直接打开）；手写 OOXML 解析，不装 Office、不改原文件 | [tools/ppt-viewer.md](tools/ppt-viewer.md) |
| ⚡ Flash 下载器 | 4399 / 7k7k / 17yy / ArmorGames / Kongregate 小游戏下载到本地离线玩 | [tools/flash-downloader.md](tools/flash-downloader.md) |
| 💬 联机聊天室 | 主机开端口、客户端连进来，局域网 / 内网穿透都能用，带流量上限；房间内还能开**群语音 / 群视频通话**（最多 8 人，媒体复用同一个端口），也能**按住说话发语音消息**（自动转 MP3、气泡显示秒数、**可拖进度条反复听**、计入文件流量上限） | [tools/chat-room.md](tools/chat-room.md) |
| 🌐 网页浏览器 | Chromium 内核（WebView2）多标签：上方 / 左侧标签栏、前进后退刷新、网页右键刷新、会话恢复（懒加载）、历史记录 / 下载 / 书签 / 资源嗅探侧栏、页面缩放（Ctrl+滚轮）、主页与清空记录 | [tools/browser.md](tools/browser.md) |
| 🏷️ 批量重命名 | 规则清单 + 预览 + 两阶段改名 + 撤销 | [tools/batch-rename.md](tools/batch-rename.md) |
| 📝 文本编辑器 | 多标签文本 / 代码编辑（AvalonEdit）：语法高亮、查找替换、编码与换行自动识别、界面缩放 | [tools/text-editor.md](tools/text-editor.md) |
| 🎧 osu! 下图器 | 搜谱面集、批量下 `.osz` | [tools/map-downloader.md](tools/map-downloader.md) |
| 🎹 osu! Key 转换器 | 低键数 mania 谱面「复制列 / 插空白列」加成高键数（2~10 可选） | [tools/osu-key-converter.md](tools/osu-key-converter.md) |
| 🍜 osu! LN 转换器 | 按 14 档强度把普通键改成 LN（长条），可选保留原 LN / 修尾巴 | [tools/osu-ln-transformer.md](tools/osu-ln-transformer.md) |
| ⭐ osu! 等级计算器 | 拖入 mania 谱面算星数与等级，批量表格；等级按 8K 谱面拟合（只对 8K 有参考意义），SR 通用 | [tools/osu-level-calculator.md](tools/osu-level-calculator.md) |
| 🎹 KPS 按键统计 | 实时 KPS / 每键计数器 / 键位可视化 + 透明置顶 Overlay（支持强制置顶）；4K~8K 预设、改键、单键图片、音符效果、键音、全局热键（功能灵感来源 DmNote，独立重写） | [tools/kps.md](tools/kps.md) |
| 🐟 Live2D 桌宠 | 内置 Live2D 模型测试台 + 桌面宠物：参数 / 部件 / 动作 / 表情 / 物理逐项调试，透明置顶、可拖动、眼睛跟随鼠标、点击穿透 | [tools/live2d.md](tools/live2d.md) |
| 📌 屏幕贴纸 | 把任意图片贴到桌面上：每张一个独立透明窗口，多选打开 / 从剪贴板直接粘贴、GIF 完整动画并可调播放速度；自由拖动 / 滚轮缩放 / 调层级 / 单独强制置顶，带最近使用与贴纸管理 | [tools/screen-sticker.md](tools/screen-sticker.md) |
| ⚫ 联机五子棋 | 开 TCP 房间和朋友下五子棋：可选禁手（三三 / 四四 / 长连）、悔棋、聊天 | [tools/gomoku.md](tools/gomoku.md) |
| 🀄 联机象棋 | 开 TCP 房间下中国象棋：自由摆棋 + 一键默认开局、让子 / 让先、中文记谱 | [tools/xiangqi.md](tools/xiangqi.md) |
| 📨 Spug 验证码测试 | 联调 Spug 推送助手（push.spug.cc）的短信 / 邮箱验证码模板：发验证码、本地比对验证；推送 URL 是调用凭证，别外传 | [tools/spug-captcha.md](tools/spug-captcha.md) |
| 🖱️ 右键菜单管理 | 列出资源管理器右键菜单里的项目（所有文件 / 文件夹 / 文件夹空白处 / 驱动器 / 桌面背景），搜索 + 位置 / 类型筛选后一键隐藏、随时恢复；还能**删除残留菜单（删前自动备份 .reg，HKLM 弹 UAC）**与**添加「用 XX 打开」**（指定后缀 / 文件夹 / 所有文件）；隐藏 / 添加只写当前用户注册表（HKCU）、不需要管理员权限，系统关键项不可隐藏 / 删除 | [tools/context-menu-manager.md](tools/context-menu-manager.md) |
| 📦 压缩包工具 | 拖入即用：解压 RAR（RAR4/RAR5）/ ZIP / 7Z / TAR / TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST / GZ / BZ2 / XZ / ZST（支持密码、旧版 GBK 文件名、批量、进度、取消、路径穿越防护）；压缩成 ZIP / 7Z。**RAR 因版权原因只能解、不能压** | [tools/archive.md](tools/archive.md) |
| 🗂️ 打开方式 | 把本工具登记进 Windows 右键「打开方式」：**按文件类型自动分流 / 以文本方式 / 以压缩包方式** 3 个入口；PDF / 图片 / Word / Excel / PPT / 压缩包 / 音视频 / 文本自动找对应工具；开关在主页设置（只有 Release / 发布版可开），关掉后启动自动清理旧条目；只写 HKCU、不需要管理员 | [tools/open-with.md](tools/open-with.md) |
| ⬆️ 软件升级器 | 用系统自带的 winget 读出本机已安装应用与可用版本；逐个勾选 / Ctrl·Shift 多选行 / 全选 / 全不选 / 反选后一键批量升级，带搜索过滤、升级前确认、逐个进度与日志、可取消；包 Id 白名单校验、扫描只读、不需要管理员 | [tools/software-updater.md](tools/software-updater.md) |
| 🧮 计算器 | 科学计算器：表达式直接输入、边打边出结果；三角函数 / 反三角 / 双曲 / 对数 / 阶乘 / 常量，角度制与弧度制切换，结果有效位数可调；历史记录点选回填并自动保存；Ans、复制结果 | [tools/calculator.md](tools/calculator.md) |
| 📈 函数绘图 | 同时画多条函数曲线：每条可单独设定义域 / 颜色 / 显示开关；拖动平移、滚轮缩放、双击复位，网格与坐标轴可隐藏；自动求点——光标处实时读数、输入 x 求所有函数值、按范围自动生成点值表 | [tools/function-plot.md](tools/function-plot.md) |
| 🧪 框架示例 | 开发用模板（Data/Config 轮子演示，可整目录删除） | [tools/sample-framework.md](tools/sample-framework.md) |

> 🎵 上面四个 osu! 工具和「KPS 按键统计」在主页上收在「**音游工具**」类别里：点类别卡片进类别页，顶部面包屑可点回上一级。
>
> 🎲 五子棋与象棋收在「**棋牌**」类别里（同样点类别卡片进类别页，
> 联机方式见各自那一页的「怎么联机」）。

## 通用说明

| 内容 | 位置 |
| --- | --- |
| 主页的搜索 / 排序 / 标签（3 行横向滚动、可展开 / 可隐藏）、**多级分类导航（类别页 + 可点击面包屑）**、**主页设置页面**（外观模式 / 六套界面风格 / 云间天上云朵速度与密度 / **渲染显卡（GPU）** / 配色方案 / 主页显示 / 自动与点击特效 / 粒子颜色 / **应用内光标 + 背景图 / 光标图历史** / **左侧快捷功能栏** / 清理缓存 / 重建配置 / 界面布局）、**工具页区块可拖拽（分隔条自动记忆 + 恢复默认布局）**、自定义标题栏、**Data / Config / Cache 目录约定** | [shell.md](shell.md) |
| 构建 / 运行 / 发布 / 自检 | [../README.md](../README.md)（快速开始 / 文档） |
| 改代码前先看：**精简开发入口地图**（流程 / 硬性规则 / 找东西） | [../AGENTS.md](../AGENTS.md) |

## 开发者文档

踩坑细节按需查（只在排错 / 改危险代码时读）：

| 想干什么 | 看哪里 |
| --- | --- |
| 踩过的坑、别做什么 | [dev/pitfalls.md](dev/pitfalls.md) |
| 工具页区块可拖拽 / 布局记忆 / 恢复默认布局（新页面照这个做） | [dev/layout.md](dev/layout.md) |
| 验证纪律：这轮该跑哪些自检组 / 全量例外 / 验证预算 | [dev/verification.md](dev/verification.md) |
| Live2D 桌宠：Cubism Core / 自写软件渲染器 / 桌宠窗口 / 模型格式与坑 | [dev/live2d.md](dev/live2d.md) |
