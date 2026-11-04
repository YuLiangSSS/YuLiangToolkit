# 📝 文本编辑器（AvalonEdit 内核 · 多标签）

> 主页卡片「📝 文本编辑器」（分类：**文件**）。编辑内核用开源的 **AvalonEdit**（WPF 代码编辑器控件），
> 多标签 / 顶部菜单 / 查找替换条 / 偏好设置面板这些外壳由本工具自己实现；
> 打开 Markdown 文件还能在编辑区下方实时看**渲染后的实际效果**。

[← 工具一览](../../README.md) ｜ [文档索引](../README.md)

## 一句话

在工具集里直接改文本 / 代码文件：**多标签**打开，带行号、**26 种语言 / 格式的语法高亮**、查找替换；
打开时**自动识别编码与换行**、保存按原样写回；Markdown 有**实际效果预览**；
字号 / 颜色 / 缩放都能调，改一下自动记住。

## 怎么用

1. 打开工具（主页「📝 文本编辑器」卡片）→ 自动恢复上次打开过的**有文件路径的标签**（可关），
   没有的话给你一个空的无标题标签。
2. **新建**（`Ctrl+N`，或标签条右边的 **+**）、**打开…**（`Ctrl+O`，可多选）、菜单里的**打开最近**
   （最多 10 条），或者把文件**直接拖进编辑区**。
3. **多标签**：点标签切换；**× / 中键**关标签（`Ctrl+W`）；关到最后一个会自动补一个无标题标签，
   页面永远有可编辑区。每个标签有自己的光标 / 滚动位置 / 撤销栈，互不影响；
   有未保存改动时会问「保存 / 不保存 / 取消」。
4. **编辑**：菜单「编辑」里有撤销 / 重做 / 剪切 / 复制 / 粘贴 / 全选；`Ctrl+F` 查找、`Ctrl+H` 替换（见下）。
5. **保存**：默认**自动保存**（停手约 0.9 秒写回原文件，见下）；也可以随时 `Ctrl+S`、
   `Ctrl+Shift+S` 另存为、菜单「全部保存」一次保存所有改过的标签。
6. **查看 / 设置**：`Ctrl+=` / `Ctrl+-` / `Ctrl+0` 缩放，`Ctrl+滚轮` 也行；`Ctrl+Shift+M` 开关 Markdown 预览；
   字体 / 颜色等在「偏好设置」面板里调。

## 菜单与快捷键

| 菜单 | 项目 | 说明 |
| --- | --- | --- |
| 文件 | 新建 `Ctrl+N` / 打开… `Ctrl+O` / 打开最近 / 保存 `Ctrl+S` / 另存为… `Ctrl+Shift+S` / 全部保存 / 关闭标签 `Ctrl+W` / 关闭全部 | 「打开最近」是子菜单（最多 10 条）；「打开…」可多选；「全部保存」只保存有改动的标签 |
| 编辑 | 撤销 `Ctrl+Z` / 重做 `Ctrl+Y` / 剪切 `Ctrl+X` / 复制 `Ctrl+C` / 粘贴 `Ctrl+V` / 全选 `Ctrl+A` / 查找 `Ctrl+F` / 替换 `Ctrl+H` | 撤销 / 重做是否可用跟着当前标签的撤销栈走；这些命令都只作用于激活标签的编辑器 |
| 查看 | 放大 `Ctrl+=` / 缩小 `Ctrl+-` / 重置缩放 `Ctrl+0` / 行号 ✓ / 自动换行 ✓ / Markdown 预览 `Ctrl+Shift+M` | 缩放只改编辑区文字大小（25%~400%，一步 10%）；「行号 / 自动换行 / Markdown 预览」打勾即生效并写进配置 |
| 偏好设置 | 打开偏好设置面板 / 重置缩放为 100% | 字体 / 字号 / 行距 / 缩放 / 颜色 / 行号 / 换行 / 缩进都在面板里 |

## 多标签

- 每个标签一个**独立的 AvalonEdit 实例**：光标、滚动位置、撤销栈互相不串；切标签不会丢状态。
- 标签条右边的 **+** 或 `Ctrl+N` 新建；**× / 中键**关标签（`Ctrl+W`）；
  「关闭全部」逐个问未保存的标签，点「取消」就停下不再关后面的。
- 关到 0 个标签会立刻补一个无标题标签，不会出现空窗口。
- **会话恢复**：打开工具时恢复上次的标签和选中项（设置里的 `RememberSession` 可关）。
  **只恢复有文件路径的标签**，无标题草稿不记（关程序前记得先存）；文件已经被删 / 移走的会跳过。

## 自动保存（默认开）

- 编辑区一有改动，**停手约 0.9 秒**（防抖）就自动写回原文件，不用按 `Ctrl+S`；标签上的 ● 会立刻消失，
  状态栏提示「已自动保存 xxx」。防抖期间继续敲字会重新计时，不会每敲一个字都写一次盘。
- 保存沿用文件原编码 / 换行风格（和手动保存完全一样），保存是「先写临时文件再替换」，中途失败不会留下半个文件。
- **无标题标签没有文件路径**：第一次要先 `Ctrl+S` 选个位置，之后改动才会自动保存（状态栏会提示一次）。
- **不覆盖外部改动**：如果文件在你打开之后被别的程序改过，自动保存会暂停这个标签（状态栏提示），
  用 `Ctrl+S` 手动保存会覆盖外部内容并解除暂停。
- 切走工具 / 关闭程序前会把还没到防抖时间的改动补写掉；关掉「自动保存」开关后，改动保留未保存标记，照旧手动保存。
- 开关在「偏好设置」面板里（默认开），配置键 `AutoSave`。

## 查找与替换

- `Ctrl+F` 打开查找条（就在标签条下面），`Ctrl+H` 打开并展开替换行；`Esc` 或 ✕ 关闭。
- 边输入边统计：查找框右边显示当前有几个匹配；回车 = 下一个，`Shift+回车` = 上一个，
  `F3` / `Shift+F3` 也一样（查找条没开时按 `F3` 会先把它打开）。
- 勾「区分大小写」再搜；「替换」替换当前匹配并跳到下一个；「全部替换」一次替换所有匹配
  （全部替换可以一步撤销，`Ctrl+Z` 就整体回退）。
- 查找是**纯文本字面匹配**，不支持正则 / 通配符。

## 界面缩放

- 25%~400%，一步 10%；菜单「查看」、`Ctrl+滚轮`、偏好设置里的滑块都能调，`Ctrl+0` 回 100%。
- 只缩放**编辑区文字**（菜单栏 / 标签条 / 状态栏不跟着变），就是「编辑区字号独立」的做法。
- 实际字号 = 基础字号（8~36）× 缩放百分比；缩放值存配置，下次打开还是这个比例。

## 偏好设置面板

- 右侧浮层（工具页内，不是独立窗口）：字体（`Cascadia Mono` / `Consolas` / `Courier New` /
  `Microsoft YaHei UI` 预设）、字号滑块、**行距滑块（1.0× ~ 2.0×，默认 1.2×）**、界面缩放滑块、
  背景颜色、文字颜色、**自动保存（默认开）**、显示行号、自动换行、Tab 键输入空格、缩进宽度（2 / 4 / 8）。
- 背景颜色有 **5 个预设色板**（深空 / VSCode 深色 / 纯黑 / 纸白 / 护眼米黄）：点一下**同时**换背景和文字色，
  之后还可以各自手填 `#RRGGBB` / `#AARRGGBB` 微调。
- 面板和编辑区之间有分隔条：宽度自己拖；双击缝隙只恢复这一处；
  全部恢复默认在 **主页设置 → 界面布局 → 恢复默认布局**。`Esc` / 面板右上角 ✕ 关闭。
- 所有改动即时生效并自动落盘，没有保存按钮。

## 设置（`Config\text-editor\config.json`）

改一下自动落盘，没有保存按钮；写坏的值会按下面的范围夹回合法值。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `FontName` | `Cascadia Mono` | 等宽字体；预设 `Cascadia Mono` / `Consolas` / `Courier New` / `Microsoft YaHei UI`，系统里没装的自动回退 |
| `FontSize` | `14` | 基础字号（8~36），实际显示字号 = 字号 × 缩放 |
| `LineSpacing` | `1.2` | 行距系数（1.0 = 编辑器默认紧凑行高，2.0 = 行高翻倍；一步 0.05）；拖滑块即时生效 |
| `ZoomPercent` | `100` | 界面缩放百分比（25~400，一步 10） |
| `BackgroundHex` | `#16181D` | 编辑区背景色，`#RRGGBB` 或 `#AARRGGBB` |
| `ForegroundHex` | `#E8EAF0` | 编辑区文字颜色，格式同上 |
| `ShowLineNumbers` | `true` | 是否显示行号列 |
| `WordWrap` | `false` | 自动换行 |
| `ShowMarkdownPreview` | `true` | 打开 Markdown 文件时自动显示「实际效果预览」窗 |
| `AutoSave` | `true` | 自动保存：停手约 0.9 秒把改动写回原文件（无标题标签要先 Ctrl+S 选位置） |
| `ConvertTabsToSpaces` | `true` | Tab 键输入空格（关掉 = 输入真正的制表符） |
| `IndentSize` | `4` | 缩进宽度（预设 2 / 4 / 8） |
| `RecentFiles` | `[]` | 最近打开的文件（新开的排最前，最多 10 条） |
| `SessionFiles` | `[]` | 上次打开着的标签（只记有文件路径的，会话恢复用） |
| `SessionActiveIndex` | `0` | 上次激活的标签序号（越界自动夹回） |
| `RememberSession` | `true` | 是否恢复上次的标签会话 |

## 编码与换行

- 打开时**自动探测编码**：先看 BOM（UTF-8 BOM / UTF-16 LE / UTF-16 BE）→ 没有 BOM 就按严格 UTF-8 校验 →
  再不行按 **GB18030** 兜底，所以中文 Windows 上的传统 ANSI（GBK）文件也不会乱码；状态栏显示当前编码。
- **保存按原编码写回**；新建文件用 **UTF-8 无 BOM**。保存是先写临时文件再替换，
  中途失败不会留下写了一半的坏文件。
- 换行风格也自动探测（CRLF / LF / CR，按文件里出现的第一个换行判断），**保存按原风格写回**；
  编辑器内部统一 CRLF，所以不会把 LF 文件偷偷改成 CRLF。
- 单个文件超过 **32MB** 会拒绝打开并提示，避免把界面卡死。

## 语法高亮

支持 **26 种语言 / 格式**（AvalonEdit 内置定义 + 项目自带的 13 个 `.xshd` 定义），
按**文件名 / 扩展名**自动识别，认不出的就是纯文本：

| 语言 / 格式 | 扩展名 / 识别方式 |
| --- | --- |
| C | `.c` `.h` |
| C++ | `.cpp` `.cxx` `.cc` `.hpp` `.hxx` `.hh` `.h++` `.inl` |
| C# | `.cs` |
| Java | `.java` |
| Python | `.py` `.pyw` |
| Go | `.go` |
| Rust | `.rs` |
| Ruby | `.rb` `.rake` `.gemspec`，以及 `Gemfile` / `Rakefile` / `Vagrantfile` |
| F# | `.fs` `.fsi` `.fsx` |
| Kotlin | `.kt` `.kts` |
| PHP | `.php` `.php3` `.php4` `.php5` `.phtml` |
| JSON | `.json` `.jsonc` |
| HTML / HTML5 | `.html` `.htm` `.xhtml` |
| JavaScript | `.js` `.mjs` `.cjs` `.jsx` |
| TypeScript | `.ts` `.tsx` `.mts` `.cts` |
| CSS | `.css` |
| Vue 单文件组件 | `.vue` |
| Batch / CMD | `.bat` `.cmd` |
| INI | `.ini` `.cfg` `.inf` |
| YAML | `.yml` `.yaml` |
| Markdown | `.md` `.markdown` `.mdown` `.mkd` |
| PowerShell | `.ps1` `.psm1` `.psd1` |
| Lua | `.lua` |
| XML | `.xml` `.xaml` `.csproj` `.config` `.svg` 等（AvalonEdit 内置那批） |
| SQL | `.sql`（项目自带的通用 SQL 定义，MySQL / PostgreSQL 风格关键字） |
| Dockerfile | 无扩展名，按文件名 `Dockerfile` 识别 |

配色统一套 **Visual Studio 2022 深色主题**的 C# 风格：关键字蓝、**方法名淡黄**、类型青绿、字符串橙、
数字浅绿、注释绿、属性 / 字段浅蓝、标点浅灰。AvalonEdit 内置定义原本是给白底设计的深蓝 / 藏青，
深色底上根本看不清，所以按 xshd 的**颜色名**整体重配（自定义的 13 个语言定义沿用同一套颜色名，
自然套上同一套配色）；没归类的颜色也统一回退成浅灰，不会留下看不清的深色；顺便去掉背景色、
把字体粗细归到 Normal（VS 的深色配色不加粗）。另存为换了扩展名会立即换高亮。

## Markdown 实际效果预览

- 打开 `.md` / `.markdown` / `.mdown` / `.mkd` 文件时，编辑区**下方**自动出现「实际效果预览」窗
  （设置里的 `ShowMarkdownPreview` 关掉就不自动出）。
- 这些语法会**真渲染**：ATX 标题（`#` ~ `######`）、段落与硬换行、**加粗 / 斜体 / 删除线 / 行内代码**、
  `[链接](url)`、**本地相对路径图片**、有序 / 无序列表（含嵌套）、引用、表格（含 `:---:` 对齐）、
  围栏代码块（``` / ~~~）、分割线。
- 图片只认**本地文件**：相对路径按当前 Markdown 文件所在目录解析；`http(s)://` / `data:` 这类
  **不联网**，显示 `[图片：说明]` 占位文字。链接点一下交给系统（`http/https/mailto` 走默认浏览器 / 邮件，
  本地文件用资源管理器打开），打不开也不影响编辑。
- 刷新是**合并延迟**的：敲字后 **260ms** 才重排一次（预览不能拖慢打字），重排后**保持原来的滚动位置**；
  文档超过 **40 万字符**会暂停实时预览（显示提示，编辑照常）。
- 预览的**背景色 / 文字色 / 字体 / 字号 × 缩放**都跟着编辑器和偏好设置走，改颜色 / 缩放会立刻重排。
- 显隐：`Ctrl+Shift+M` 或菜单「查看 → Markdown 预览」开关（写进配置）；预览窗右上角 **×** 也是关掉这个开关
  （之后 `.md` 不再自动出预览，想再看从菜单打开）。
- 高度：编辑区和预览窗之间的**分隔条**拖动调整，双击缝隙恢复默认高度（**360 px**）；
  全部恢复默认在 **主页设置 → 界面布局 → 恢复默认布局**。

## 状态栏

底部一条信息栏：**行:列**、**字符数**、**编码**（UTF-8 BOM / UTF-8 / UTF-16 LE / BE / GB18030）、
**状态消息**（打开 / 保存结果、错误提示），右侧是当前**文件路径**和**缩放百分比**。

## 数据放在哪

- 配置：`Config\text-editor\config.json`——字体 / 字号 / 缩放 / 颜色 / 行号 / 换行 / 缩进、最近文件、会话
  全在这一份里。
- 本工具不写 `Data\` / `Cache\`；想清掉「最近文件 / 会话」直接删这个 `config.json`，下次打开会按默认值重新生成。

## 依赖与许可

- NuGet：**AvalonEdit 6.3.1.120**（MIT License，Copyright (c) 2014-2024 AlphaSierraPapa / ICSharpCode），
  登记在 [THIRD-PARTY-NOTICES](../../THIRD-PARTY-NOTICES.md)。
- 从源码构建时**首次需要联网还原一次** NuGet 包，之后走本机 NuGet 缓存；
  运行时**不需要额外的系统组件**（不像网页浏览器那样依赖 WebView2 运行时）。
- 本项目整体仍是 Unlicense；AvalonEdit 只作为 NuGet 依赖引用，本项目**没有修改**它。

## 已知限制（后面可以继续加）

- **不是 IDE**：没有代码补全、多文件工程、终端、调试、Git 集成。
- 高亮配色按深色底调；切到浅色背景（纸白 / 护眼米黄）时部分高亮颜色对比度一般。
- 自定义语言的高亮是**基于正则的近似高亮**（没有语义分析）；Vue 是基础单文件组件高亮
  （`template` / `script` / `style` 分段），不做模板表达式 / 组件类型检查。
- Markdown 预览是 **FlowDocument 渲染**：覆盖常用语法，**不是完整 CommonMark**——
  不支持脚注、内嵌 HTML、远程图片（远程图片显示占位文字）。
- 会话只恢复有文件路径的标签；无标题草稿关了不保留。
- **自动换行开启时行距可能不完全均匀**：AvalonEdit 是一条文档行折成多条文本行来排版的，行距靠每行行尾的
  撑高元素实现，只会作用于折行后的最后一段；写代码时默认关自动换行，一般不受影响。
- 单文件 32MB 上限，超大日志 / 数据文件请用别的方式看。
- 查找替换是纯文本匹配，不支持正则。

## 实现位置

| 内容 | 文件 |
| --- | --- |
| 工具入口 / 主页卡片信息 | `src/YuLiangToolkit.Tools.TextEditor/TextEditorTool.cs` |
| 配置项与默认值（纯函数夹取，自检断言） | `src/YuLiangToolkit.Tools.TextEditor/TextEditorConfig.cs` |
| 编码探测 / 换行 / 读写（纯逻辑） | `src/YuLiangToolkit.Tools.TextEditor/Core/TextFileCodec.cs` |
| 查找 / 替换 / 环绕查找（纯逻辑） | `src/YuLiangToolkit.Tools.TextEditor/Core/TextSearch.cs` |
| 语法高亮（暗色配色覆写） | `src/YuLiangToolkit.Tools.TextEditor/Core/EditorHighlighting.cs` |
| 多语言高亮注册（内置 + 自定义 xshd 的注册 / 解析） | `src/YuLiangToolkit.Tools.TextEditor/Core/EditorSyntaxDefinitions.cs` |
| 13 个新语言定义（Go / Rust / Ruby / F# / Kotlin / TS / Vue / Batch / INI / YAML / Dockerfile / Lua / SQL，嵌入资源） | `src/YuLiangToolkit.Tools.TextEditor/Highlighting/*.xshd` |
| Markdown 解析（纯逻辑，自检断言） | `src/YuLiangToolkit.Tools.TextEditor/Core/Markdown/MarkdownParser.cs` |
| Markdown → FlowDocument 渲染 | `src/YuLiangToolkit.Tools.TextEditor/Core/Markdown/MarkdownPreviewRenderer.cs` |
| 最近文件 / 会话路径清洗 | `src/YuLiangToolkit.Tools.TextEditor/Core/RecentFileList.cs` |
| 单个标签的状态（路径 / 脏标记 / 编码 / 换行） | `src/YuLiangToolkit.Tools.TextEditor/ViewModels/EditorTab.cs` |
| ViewModel（标签 / 文件与编辑命令 / 缩放 / 会话恢复） | `src/YuLiangToolkit.Tools.TextEditor/ViewModels/TextEditorViewModel.cs` |
| 界面（菜单 / 标签条 / 查找条 / 设置面板 / Markdown 预览 / 快捷键） | `src/YuLiangToolkit.Tools.TextEditor/Views/TextEditorView.xaml(.cs)` |

> 菜单模板、标签条横向滚动、宿主快捷键重挂、会话落盘顺序、高亮注册 / Markdown 预览这些坑：
> [docs/dev/pitfalls.md](../dev/pitfalls.md) 的「文本编辑器（AvalonEdit）」小节。
