# 🎵 音频工具

> 一个工具页里放两块 FFmpeg 能力：**视频转音频**（MP3 / FLAC / 原样提取）和
> **分段视频合成**（m3u8 播放列表 / ts 分段文件夹 → 整段 mp4 / mkv）。

[← 工具一览](../../README.md) ｜ [文档索引](../README.md)

## 能做什么

### ① 视频转音频（原来的独立工具，原样搬进来）

- **三种模式**（后端都是 FFmpeg）：
  - **有损 MP3**：`libmp3lame`，码率 128 / 192 / 256 / 320 kbps；
  - **无损 FLAC**：`-c:a flac`（compression_level 8）；
  - **无损提取原始音轨**：`-c:a copy` 不重新编码 —— 先用 `ffprobe` 探出音轨是什么编码，
    再自动挑容器（m4a / opus / ogg / flac / wav …）。因为不转码，**速度只取决于读盘**。
- **批量**：一次处理一整个文件夹，带进度条、逐文件状态、实时日志，随时可以取消
  （取消会把 ffmpeg 的进程树一起杀掉）。
- **重名不会覆盖**：输出文件重名自动加 ` (1)`、` (2)`。
- 源文件夹可以**递归**；扩展名过滤可改（默认只扫 `.mp4`，可以写成 `mp4;mov;mkv` 这种）。

### ② 分段视频合成（v0.68.0 新增）

- **输入方式二选一**：
  - **m3u8 播放列表**：本地 `.m3u8` / `.m3u` 文件，或直接粘贴 `http(s)` 链接；
  - **分段文件夹**：选一个装着 `.ts` / `.m4s` / `.m4v` / `.mp4` / `.aac` 等分段的文件夹，
    程序按**文件名自然顺序**（`seg2` 在 `seg10` 前面）拼成 concat 清单。
- **输出**：MP4（最通用）或 MKV（对编码更宽容），文件名可自定义，空了自动按来源起名。
- **两种合成方式**：
  - **直接复制流**（默认，`-c copy`）：不转码，快且无损；TS → MP4 一般直接可用；
  - **重新编码**（H.264 + AAC）：复制流失败 / 播放器不兼容时用，慢一些。
- **进度**：播放列表模式会先用 `ffprobe` 探总时长，再用 FFmpeg 的 `-progress pipe:1`
  换算百分比；拿不到时长（如分段文件夹）就走滚动进度条 + 实时日志。
- **取消**：随时停，FFmpeg 进程树一起杀；本地 m3u8 的分段路径、AES 加密的 HLS
  都交给 FFmpeg 处理（`-protocol_whitelist file,http,https,tcp,tls,crypto,data`）。

## 怎么用

1. 第一次用先确认 FFmpeg：工具会自动从「配置里的路径 → `PATH` → 常见安装位置」找 `ffmpeg.exe`；
   找不到就点「选择 FFmpeg…」手动指定，或者点「下载 FFmpeg」去官方下载页。
   **FFmpeg 是全局设置**，两个页签共用。
2. 顶部页签选「🎵 视频转音频」或「🎬 分段视频合成」。
3. 视频转音频：选**源文件夹**（可选递归）、**输出文件夹**（不选就放在源文件旁边），
   选模式与参数（MP3 码率 / FLAC 压缩级别 / 并行数），点开始。
4. 分段合成：选 `m3u8` 文件 / 填链接，或选分段文件夹；填输出文件夹 / 文件名、选容器与合成方式，
   点「开始合成」。日志里会显示 FFmpeg 错误（如果有）。
5. 中途想停就点「取消」；完成后点「打开输出文件夹」。

## 界面布局

- 顶部两个页签：**视频转音频** / **分段视频合成**；两个子页各自记住自己的布局。
- 视频转音频：「源文件与输出 | 转换设置」左右可拖；「设置行 / 文件列表 / 进度与日志」三块上下可拖。
- 分段合成：「设置 / 运行日志」上下可拖（`video-merge.设置与日志`）。
- 松手自动记住（`Config\layout.json`）；**双击**某条分隔线只恢复这一处，全部恢复默认：
  **主页设置 →「界面布局 → 恢复默认布局」**。

## 设置项

| 界面上的选项 | 存哪 | 说明 |
| --- | --- | --- |
| 视频转音频：模式 / 码率 / FLAC 级别 / 并行数 / 文件夹 / 过滤 | `Config\video-to-audio\config.json` | 改一下就自动保存；**沿用老工具配置，升级不丢** |
| 分段合成：输入方式 / 路径 / 输出文件夹 / 文件名 / 容器 / 复制流 | `Config\video-merge\config.json` | 改一下就自动保存 |
| FFmpeg 路径 | `%AppData%\YuLiangToolkit\settings.json` | **全局设置**，两个功能共用 |

> 从老版本升级时，会把旧配置（`%AppData%\YuLiangToolkit\settings.json` 里那套视频转音频选项）
> **一次性迁移**到这个功能的配置里，跑过一次就不再迁移。工具卡片从「视频转音频」变成「音频工具」，
> 但配置目录 / Data 目录名仍是 `video-to-audio`，所以老设置照常生效。

## 数据与配置

| 位置 | 内容 |
| --- | --- |
| `Config\video-to-audio\config.json` | 视频转音频选项（沿用老路径） |
| `Config\video-merge\config.json` | 分段合成选项 |
| `Data\video-to-audio\` | 暂时没用到（转换结果写到你选的目标文件夹） |
| `Cache\video-merge\concat-*.txt` | 分段文件夹模式的临时 concat 清单；合成结束自动删除，删不掉留给「清理缓存」 |

## 注意 / 限制

- **「原样提取」得到的是原始编码**：源视频是 AAC 就得到 m4a，是 Opus 就得到 opus —— 这是特性，不是 bug；
  想要统一格式就用 MP3 / FLAC 模式。
- **m3u8 只建议用于「已完结」的点播（VOD）播放列表**：直播 m3u8 没有 `#EXT-X-ENDLIST`，
  FFmpeg 会一直等新分段，直到你点取消。
- **DRM 加密视频**（Widevine / PlayReady 等）FFmpeg 解不了；普通 AES-128 HLS（有 key URI）可以。
- **纯 `.m4s` 分片**如果还依赖单独的 `init.mp4`，文件夹模式可能合不出能播的文件；
  这种情况优先用配套的 m3u8 / mpd 让 FFmpeg 自己解析。
- 复制流模式要求分段编码一致；报错就换「重新编码」或 MKV 试试。
- 转换 / 合成过程中别去动源文件；产物直接写在目标路径上（同名覆盖）。

## 相关代码

- 项目：`src/YuLiangToolkit.Tools.AudioTools/`
- 工具入口 / 顶部页签：`AudioToolsTool.cs`、`ViewModels/AudioToolsViewModel.cs`
- 视频转音频：`VideoToAudioConfig.cs`、`ViewModels/VideoToAudioViewModel.cs`、`Views/VideoToAudioView.xaml`
- 分段合成：`SegmentMergeConfig.cs`、`SegmentMerger.cs`（纯逻辑，自检 `audio-tools` 组）、
  `ViewModels/SegmentMergeViewModel.cs`、`Views/SegmentMergeView.xaml`
- 踩坑：[docs/dev/pitfalls.md](../dev/pitfalls.md)
