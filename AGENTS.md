# AGENTS.md — YuLiangToolkit（凉雨工具集）开发入口

> **入口地图**，不是手册：动手前读本文件；实现细节看源码 XML 注释，排错查 [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md)。
> 本文件 ≤120行/≤5KB，**别塞细节**；注释 / 提交 / 文档 / 回答一律用**中文**。

## 项目速览

| 项 | 内容 |
| --- | --- |
| 技术栈 | Windows 10 2004+ + .NET 9（主程序 / 自检 `net9.0-windows10.0.19041.0`，工具库 `net9.0-windows`）+ WPF + 手写 MVVM；NuGet 包：`OsuParsers`（MIT）、`Microsoft.Web.WebView2`（浏览器工具用的 Chromium 内核，运行时是系统组件）、`AvalonEdit`（MIT，文本编辑器内核）、`SharpCompress`（MIT，压缩包工具内核：解 RAR / ZIP / 7Z / TAR.*，写 ZIP / 7Z）、`SkiaSharp`（MIT，屏幕贴纸的 GIF / 动图 WebP 帧解码内核）、`MailKit` / `MimeKit`（MIT，邮箱代收的 IMAP / MIME 解析）、`System.Security.Cryptography.ProtectedData`（微软，DPAPI 加密授权码）；PDF 渲染用系统 `Windows.Data.Pdf`，文字搜索 / 书签目录是手写 PDF 解析器 |
| 主程序 | 根目录 `YuLiangTookit.csproj`（少一个 `l` 是历史拼写）：标题栏 / 主页 / 类别页 / 面包屑、六套主题（深浅色 + 配色）、背景图、粒子 |
| 核心库 | `src/YuLiangToolkit.Core/`：工具契约 / MVVM / 存储 / 配置 / 维护（缓存清理·配置重建）/ OOXML 包读取（docx·xlsx 共用）/ FFmpeg / 粒子 / TCP 房间 |
| 功能 | 48 个（39 个 `Tools.*`）：音频工具（视频转音频 / 分段合成） / 视频转 GIF（视频文件截取一段 → GIF 动图：起止 / 帧率 / 宽度 / 颜色 / 抖动，FFmpeg 两遍调色板） / 录屏 / GIF（GIF / MP4·MKV·MOV·AVI·WebM 多编码器 + 声音 / 摄像头 / 自定义快捷键 / 截图） / 剪辑工具（视频 / GIF 裁剪 + 多片段拼接 + 缩略图时间轴 + 低清预览） / 播放器 / 图片阅读器 / PDF 阅读器 / Word 阅读器 / Excel 阅读器 / PPT 阅读器（pptx 幻灯片还原 + 缩略图 + 备注 + 全屏放映） / 重命名 / 文本编辑器 / 压缩包工具 / 打开方式 / Flash / 聊天室 / 网页浏览器 / 邮箱代收（IMAP 只读拉多个邮箱的收件箱 / 已发送 / 垃圾箱 + 文件夹导航 + 合并列表 + 附件拖拽；HTML 富文本预览 + 排序；授权码 DPAPI 加密；可跟随系统代理） / 邮件推送（富文本 ⇄ HTML 双向编辑 + 附件 / 抄送密送 + WebView2 预览 + Resend POST /emails 发送与投递状态查询；UA 必带、API Key DPAPI 加密） / osu! 下图器 / osu!mania 三件套 + 谱面编辑器（边听边摆 note：普通键 / LN / 右键删 / 拖拽 / 撤销 + 多选框选与 Z·V 区间扩选 + **osu! 风格全屏编辑** + Note 浏览区宽度与贴左·居中·贴右 + **谱面背景图（铺满中间区域，可拖图换背景）与 Note 区底色可调** + 空闲自动保存与退出提醒；NAudio 设备位置驱动的时间轴精确同步；零控件自绘抗大量 note；红线 BPM 与绿线 SV 可读写） / 棋牌 / 斗地主（经典 / 双副牌炸弹多 / 底牌明牌 + 3 人联机排队换人 + 电脑对手） / 军棋（二人 / 四国，暗棋自动裁判 + 联机排队换人 + 电脑对手） / 围棋（9·13·19 路 / 禁自杀 / 禁打劫 / 中国规则数子 + 联机观战换人 + 电脑对手） / 贪吃蛇（共享果 / 大混战 + 九档 AI + 联机）/ 俄罗斯方块（7-bag / SRS 墙踢 / 幽灵落点 / 暂存 / 接下来 3 块，消行得分升级，最高分存配置；局域网 1 对 1 联机：垃圾行互攻 / 各自计分，房主权威、对手棋盘缩略图） / KPS 按键统计 / Spug 验证码测试 / Live2D 桌宠 / 屏幕贴纸（图片贴到桌面：原始尺寸 / 拖动 / 四边四角自由拉伸（Shift 等比）/ 滚轮缩放 / 层级 / 单独置顶 / GIF · APNG · WebP 动画 / 恢复默认 + 最近使用） / 右键菜单管理 / 软件升级器（winget 读应用与可用更新 + 多选批量升级） / 应用管理器（仿设置·应用：注册表只读列出已安装桌面应用 + 驱动器筛选 + 名称·日期·大小排序正逆序 + 行内卸载 / 修改） / 软件启动器（exe / 快捷方式图标墙 + 详情 + 清空无效项） / 表情包收藏夹（图片收藏 + 单击复制并粘贴到 QQ + 自动发送 / 仅复制） / 计算器（科学计算器：整数 / 有限小数走 `Core/Numbers/` 任意精度，其余共用 `Core/Expressions/` 表达式引擎） / 随机数生成器（可调下限 / 上限 / 精确度，数值走 `Core/Numbers/ExactDecimal`，支持超大整数与超细步长） / 数字转换器（中文大小写 / 人民币金额大小写 / 英文念法·序数·支票 / 罗马数字 / 千分位 / 进制 / 科学计数法，也能反向读回中文金额） / 函数绘图（多函数 + 每条定义域 + 自动求点） / 任务管理器（CPU / 内存 / GPU / 硬盘读写监测 + 底部实时折线 + 进程表格（可拖列宽 / 列头排序 / 列头带总占用 / 空格分词搜索）PID / CPU / 内存 / GPU / 占用端口 + 结束任务三级后备 + 按需 UAC 提权重启） / 开机启动管理（注册表 Run·RunOnce / 启动文件夹的启用禁用·删除·打开位置·复制命令 + 按需 UAC） / 示例模板 |
| 自检 | `tests/YuLiangToolkit.FrameworkSelfCheck/`：6085 项（50 组），退出码 0 = 通过（按分组跑） |
| 数据 | **在 exe 旁边**（`bin\Debug\net9.0-windows10.0.19041.0\Data`、`Config`、`Cache`）；仓库根同名目录只有说明 |

## 开工 / 收尾（硬性）

```powershell
.\build.ps1                          # ① 编译：0 错误
.\scripts\check.ps1                  # ② 按改动自动选组：build + 只跑受影响的自检组
git add -A; git commit -m "<中文提交信息>"   # ③ 提交：每轮改动都要提交（硬性）
```

1. **验证按影响面分级，默认只部分自检**：`check.ps1` 从 `git status` 映射出受影响的组并跑（先 build）；**别凭猜选组、也别每轮全量**。`-All` 只给四个触发点：发布前 · 改 `src/YuLiangToolkit.Core/` 公共轮子 · 改自检接线（`Program.cs` / `SelfCheckScratch.cs` / 自检 csproj） · 一次动多个功能（选到 ≥8 组脚本会自动升全量）或映射不到影响面。分级/映射/预算见 [`docs/dev/verification.md`](docs/dev/verification.md)。
2. 纯文档（只动 `*.md`）可跳 ① ②；只改 XAML / 文案 / 样式（不动逻辑）→ 只 build + 截图冒烟（命令见验证纪律页），不跑自检（例外见验证纪律页）。照样提交；`git push` 由用户决定。
3. 改行为 → 同步自检断言；改用户可见行为 → 更新 `docs/tools/<工具Id>.md` 与 README 工具表。
4. 界面改动 → 截图冒烟；发布前 `.\publish.ps1` 打一次包，确认 Data/Config 在发布目录旁。
5. **验证一次即止**：同轮跑过的组不重跑；偶发时序失败最多原样重跑一次；退出码 0 就是结论，不手工再验一遍；全量每轮最多一次。
6. 版本号 / 自检项数只在必要处改（版本唯一来源 `Directory.Build.props`）。

## 硬性规则（违反 = 破坏项目或删用户数据；更多细节见踩坑清单）

1. **运行时数据在 exe 旁边**；仓库根 `Data/`、`Config/`、`Cache/` 只放说明。绝不用 `<None CopyToOutputDirectory>` 拷 Data/Config；`dotnet clean` / 删 `bin`、`dist` = 删用户数据。
2. **NuGet 包可自由引入**（项目所有者 2026-11-23 起解除「不引新包」限制）：引入第三方库时在
   `THIRD-PARTY-NOTICES.md` 登记来源 / 许可证，并在功能文档里说明用途与运行时要求（追溯用，不是限制）。
   小轮子要不要手写进 `Core/` 由当次实现自行权衡，不再作为硬性要求。
3. **带 setter 的计算属性必须 `[JsonIgnore]`**；配置自动落盘（无保存按钮）；要「一敲键就刷新」用 `Set(ref 字段, 值)`。
4. **后台线程改界面走 `Ui(...)` / `UiAsync(...)`**；切走时在 `NotifyClosed()` 里摘缓存对象上的事件；绕过 `ToolConfigStore` 的持久化入口自己接 `FlushAll`。
5. **数据目录里的相对路径不能带 `..`、不能是绝对路径**（`ToolStorage` 故意抛异常防逃逸）。
6. **改主题 token 要一起改**：四套深色字典（`DefaultTheme` / `LiquidGlassTheme` / `FrostedGlassTheme` / `AcrylicTheme`）+ `ThemeManager.RequiredTokenKeys`（几何 key 在 `RequiredRadiusKeys`）；道系 / 云间天上 / 浅色 / 配色由 `ThemePaletteBuilder` 生成的 token 补全，新增颜色要保证生成器也覆盖；界面颜色一律 `{DynamicResource}`。
7. **新增顶层目录**时同步改主项目 `DefaultItemExcludes` **和** `.gitignore`。
8. **别用 PowerShell 改 XAML / 源码**（5.1 按 GBK 读会乱码）；`.ps1` 纯 ASCII 或 UTF-8 with BOM。
9. **`ParticleOverlay` 必须放 Window 内 Grid 铺满 + `IsHitTestVisible="False"`**。
10. **缓存写 `Cache\<工具Id>\`**（`ToolCache`），「主页设置 → 清理缓存 / 重建配置」按已发现工具自动覆盖新工具；缓存混在 Config 里就实现 `IToolCacheSource` 报告清理项（详见 `Core/Maintenance/` 注释）。
11. **改完必须提交 git**；其他别做 / 先问用户的见踩坑清单。
12. **有多个区块的工具页必须可拖拽，而且每个页面级区块的相邻边界都要有分隔条**（v0.41.0 起；v0.42.0 强化：别把两块合并进一个可拖块，要最高自由度）：用 `core:PaneSplitter` + `App.xaml` 的样式（悬停蓝线 / ↔ ↕ 光标 / 自动记住 / 双击复位 / 提示线透明度可在主页设置调），**别再把区块大小写死**；新页面照 `docs/dev/layout.md` 做，布局恢复默认在「主页设置 → 界面布局」。
13. **右键菜单必须套 `App.xaml` 的 `DarkMenu` 样式**（`DarkContextMenuStyle` + `ContextMenu.Resources` 里隐式套 `DarkMenuLeafItemStyle` / `DarkMenuSeparatorStyle`，有子菜单的父项用 `DarkMenuParentItemStyle`），别只设 Background：WPF 默认模板在深色主题下左侧会留一条白色图标列（详见 `docs/dev/pitfalls.md`）。

## 新功能要接的「主页设置」关联点（漏了功能就是残的）

主页设置（⚙ 页面）集中管理跨工具的东西；工具**按需实现接口 / 加约定**，外壳自动收进去，**别去改设置页**：

| 关联点 | 工具要做什么 | 底层 / 约定 |
| --- | --- | --- |
| **清理缓存** | 缓存写 `Cache\<工具Id>\` = 自动清；**混在 Config / 别处** 的（最近打开等）实现 `IToolCacheSource` 报告 `ToolCacheItem` | `Core/Maintenance/IToolCacheSource.cs`、`CacheCleaner` |
| **重建配置 / 导入导出** | 配置走 `ToolConfig`（`Config\<工具Id>\config.json`）就自动出现、自动回默认、自动进 zip；**凭据类**（DPAPI 密钥 / 授权码 / token）登记进 `ConfigArchive.CredentialConfigToolIds`（整目录不导出）；随包要带的 `Data\` 文件加进 `ExportableDataPrefixes` | `Core/Maintenance/`（`ConfigResetter`·`ConfigArchive`） |
| **打开方式关联**（Windows 右键） | 只写扩展名分流逻辑；开关统一在主页设置（`ShellConfig.OpenWithAssociationEnabled`），工具页只做状态 / 修复 | `Core/Shell/OpenWith*.cs`、`docs/tools/open-with.md` |
| **开机自启 / 联动启动** | 实现 `IStartupTask`（在 **工具实例**上，非 VM；必须立刻返回、不抛异常、想清重启去重） | `Core/Tools/IStartupTask.cs` |
| **后台保活**（切走还在跑，如播放器） | 实现 `IBackgroundTool`（`KeepAliveOnLeave` / `ShowMiniPanel` / `StopBackgroundPlayback`） | `Core/Tools/IBackgroundTool.cs` |

> 快捷键、界面布局 / 分隔条、主题 token、全局通知的接法见硬性规则 6/12 与 [`docs/dev/shortcuts.md`](docs/dev/shortcuts.md)。
> 原则：外壳**不认识具体工具**——工具实现 `Core` 里的接口、写自己的配置，设置页只负责渲染。

## 找东西

| 我要… | 看 |
| --- | --- |
| 全貌 / 流程 / 硬性规则 | 本文件 |
| 验证纪律（本轮该跑哪些组 / 全量例外 / 预算） | [`docs/dev/verification.md`](docs/dev/verification.md) |
| 轮子（工具契约 / MVVM / 存储 / 配置 / 粒子 / TCP 房间）、目录与新功能 | `src/YuLiangToolkit.Core/` 源码注释；新功能复制 `src/YuLiangToolkit.Tools.Sample/` 模板 |
| 工具页区块可拖拽 / 布局记忆 / 恢复默认 | [`docs/dev/layout.md`](docs/dev/layout.md) |
| 给新工具接**全局快捷键**（在别的窗口也能触发） | [`docs/dev/shortcuts.md`](docs/dev/shortcuts.md) 的「一·补：完整接线清单」（实现 `IShortcutProvider` + `IShortcutActionHost`、常驻 VM、`ShortcutHub.ApplyAll`、`NotifyOpened`、自检） |
| 某个功能的实现细节 / 踩过的坑 | 对应源码 + [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md) |
| Windows「打开方式」/ 文件关联 / 入口副本 | [`docs/tools/open-with.md`](docs/tools/open-with.md) + `Core/Shell/OpenWith*.cs` |
| Live2D 桌宠（Cubism Core / 自写软件渲染器 / 桌宠窗口 / 模型格式） | [`docs/dev/live2d.md`](docs/dev/live2d.md) |
| 面向用户的文档 | [`docs/tools/`](docs/tools/)（索引 [`docs/README.md`](docs/README.md)） |
| 版本改动 | `git log`（旧版手册在 git 历史里） |
