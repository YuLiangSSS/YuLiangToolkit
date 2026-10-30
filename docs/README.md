# 文档索引

> 根目录的 [README](../README.md) 只放「工具一览 + 快速开始」；**每个工具的详细说明都在这里**，
> 一页一个，和 GitHub Wiki 的用法一样（都在仓库里，跟着版本走，不用另外维护一套网页）。

## 工具说明

| 工具 | 一句话 | 详细说明 |
| --- | --- | --- |
| 🎥 视频转音频 | 抽视频里的音轨：MP3 / FLAC / 原样提取 | [tools/video-to-audio.md](tools/video-to-audio.md) |
| 🎬 视频 / 音乐播放器 | 本地播放器：播放列表、循环随机、可调步长的快进快退 | [tools/media-player.md](tools/media-player.md) |
| 🖼️ 图片连续阅读器 | 把一个文件夹的图片首尾相接成一条长带（无缝、无间距），滚轮连续看条漫 / 长图 / 漫画；六种阅读方式（连续滚动三种方向 / 双页翻页左右 / 单页浏览），支持缩放、自由拖拽、拖动惯性 / 滚轮惯性、F11 全屏沉浸阅读（工具条自动隐藏）、🕘 最近浏览（上限可调）、双击图片打开与三层图片边框 | [tools/picture-viewer.md](tools/picture-viewer.md) |
| 📄 PDF 阅读器 | 简单 PDF 阅读器：纵向 / 横向连续滚动 + 双页并排一次翻两页，支持缩放、Ctrl+F 文字搜索、书签目录、双页页面间距、拖动惯性 / 滚轮惯性、自动记住每本 PDF 上次阅读位置、页码跳转、🕘 最近浏览（上限可调）、F11 全屏沉浸阅读（工具条自动隐藏）与双击 .pdf 关联；只读不改原文件 | [tools/pdf-reader.md](tools/pdf-reader.md) |
| ⚡ Flash 下载器 | 4399 / 7k7k / 17yy / ArmorGames / Kongregate 小游戏下载到本地离线玩 | [tools/flash-downloader.md](tools/flash-downloader.md) |
| 💬 联机聊天室 | 主机开端口、客户端连进来，局域网 / 内网穿透都能用，带流量上限 | [tools/chat-room.md](tools/chat-room.md) |
| 🏷️ 批量重命名 | 规则清单 + 预览 + 两阶段改名 + 撤销 | [tools/batch-rename.md](tools/batch-rename.md) |
| 🎧 osu! 下图器 | 搜谱面集、批量下 `.osz` | [tools/map-downloader.md](tools/map-downloader.md) |
| 🎹 osu! Key 转换器 | 低键数 mania 谱面「复制列 / 插空白列」加成高键数（2~10 可选） | [tools/osu-key-converter.md](tools/osu-key-converter.md) |
| 🍜 osu! LN 转换器 | 按 14 档强度把普通键改成 LN（长条），可选保留原 LN / 修尾巴 | [tools/osu-ln-transformer.md](tools/osu-ln-transformer.md) |
| ⭐ osu! 等级计算器 | 拖入 mania 谱面算星数与等级，批量表格；等级按 8K 谱面拟合（只对 8K 有参考意义），SR 通用 | [tools/osu-level-calculator.md](tools/osu-level-calculator.md) |
| 🎹 KPS 按键统计 | 实时 KPS / 每键计数器 / 键位可视化 + 透明置顶 Overlay；4K~8K 预设、改键、单键图片、音符效果、键音、全局热键（功能灵感来源 DmNote，独立重写） | [tools/kps.md](tools/kps.md) |
| ⚫ 联机五子棋 | 开 TCP 房间和朋友下五子棋：可选禁手（三三 / 四四 / 长连）、悔棋、聊天 | [tools/gomoku.md](tools/gomoku.md) |
| 🀄 联机象棋 | 开 TCP 房间下中国象棋：自由摆棋 + 一键默认开局、让子 / 让先、中文记谱 | [tools/xiangqi.md](tools/xiangqi.md) |
| 📨 Spug 验证码测试 | 联调 Spug 推送助手（push.spug.cc）的短信 / 邮箱验证码模板：发验证码、本地比对验证；推送 URL 是调用凭证，别外传 | [tools/spug-captcha.md](tools/spug-captcha.md) |
| 🧪 框架示例 | 开发用模板（Data/Config 轮子演示，可整目录删除） | [tools/sample-framework.md](tools/sample-framework.md) |

> 🎵 上面四个 osu! 工具和「KPS 按键统计」在主页上收在「**音游工具**」类别里：点类别卡片进类别页，顶部面包屑可点回上一级。
>
> 🎲 五子棋与象棋收在「**棋牌**」类别里（同样点类别卡片进类别页，
> 联机方式见各自那一页的「怎么联机」）。

## 通用说明

| 内容 | 位置 |
| --- | --- |
| 主页的搜索 / 排序 / 标签（3 行横向滚动、可展开 / 可隐藏）、**多级分类导航（类别页 + 可点击面包屑）**、**主页设置页面**（界面风格 / 主页显示 / 自动与点击特效 / 粒子颜色 / 清理缓存 / 重建配置）、自定义标题栏、**Data / Config / Cache 目录约定** | [shell.md](shell.md) |
| 构建 / 运行 / 发布 / 自检 / 截图验证 | [../README.md](../README.md)（快速开始） |
| 改代码前先看：**精简开发入口地图**（流程 / 硬性规则 / 找东西） | [../AGENTS.md](../AGENTS.md) |

## 开发者文档

踩坑细节按需查（只在排错 / 改危险代码时读）：

| 想干什么 | 看哪里 |
| --- | --- |
| 踩过的坑、别做什么 | [dev/pitfalls.md](dev/pitfalls.md) |
| 验证纪律：这轮该跑哪些自检组 / 全量例外 / 验证预算 | [dev/verification.md](dev/verification.md) |
