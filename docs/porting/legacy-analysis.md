# HeavenUsurperToolkit-master 旧项目移植分析

> **用途**：把旧项目 `HeavenUsurperToolkit-master/`（osu!mania 三件套 + 粒子效果）彻底读懂，
> 产出「不看旧代码也能照着重写」的规格。本文只做分析，不改旧项目、不改新项目源码。
> **参考项目文件夹现已从仓库删除，本文档保留作移植存档（文中行号指 2026-10 快照）。**
>
> **阅读约定**
> - 文中所有 `文件:行号` 都指 `HeavenUsurperToolkit-master/` 下的文件（2026-10 快照）。
> - 旧项目用 **.NET 8 + WPF**（`net8.0-windows`，`UseWPF=true`，`Nullable=enable`，无 NuGet 依赖），
>   新项目是 .NET 9 WPF + 手写 MVVM（见项目 `AGENTS.md`），**不能直接拷 UI，只有算法/公式/常量可复用**。
> - 「旧实现」指 **as-is 行为**；凡是明显 bug，统一标 ⚠️，并在第 8 章给出「复刻 / 修掉」的建议。
> - 第 9 章是风险清单，§ 附录 C 列出对任务描述里几处措辞的澄清。

---

## 0. TL;DR（先看这 10 条）

1. **旧项目规模很小**：26 个文件全在仓库根目录、没有分层、没有第三方依赖、没有任何持久化/配置/设置；
   三个工具是三个 WPF `Page`，套在一个 `Frame + 滑动动画 + 窗口级粒子叠加层` 的自绘壳里。
2. **三个工具的入口完全一样**：把 `.osu` 文件或**文件夹**拖到页面上 → 递归收集 `.osu` → 后台线程**串行**逐个处理；
   没有「选择文件」按钮、没有取消、没有失败列表、没有预览。Release 下失败**静默**（只在 DEBUG 弹 MessageBox）。
3. **不是原地修改**：两个改写工具都按 `Metadata` 重新拼文件名，在**原目录新建/覆盖**一个 `.osu`；
   原文件不动、**无备份**、**同名直接覆盖**；写回是**固定模板全量重写**（UTF-8 无 BOM、CRLF），
   `[Editor]`、未知 `[General]/[Metadata]` 键、除 `[Events]` 外的注释全部丢失。
4. **KeyConverter**：低 k → 高 k（只支持升 key）。按 `Gap`（≈0.4–30 秒）把谱面切成若干块，
   **每块重新随机**决定「复制哪些列、插到哪些列、哪些新列留空（Blank）」；原音符按插入位置右移；
   最后按列用 50ms / 10ms 阈值做合并去重。**完全不改 `[TimingPoints]`**（任务里问「如何改 TimingPoints」——答案是：不改）。
5. **LNTransformer**：Level 滑条 **-3 ~ 10** 共 14 档（见 §4.5）。`-3` 是 Hold off、`-1` 随机 1/n 倍、`0` 短 LN、
   `1..9` 按 `11% × Level` 的高斯分布、`10` 反向几乎满 LN；再经 `AfterTransform` 按 `Column` 限制受影响的轨道、
   按 `Gap` 每隔若干行重抽轨道。默认 `OverallDifficulty=0` 会把谱面 OD 写成 0（很容易踩）。
6. **LevelCalculator**：星数来源是 `OsuFileV14` 构造函数里自动算的 mania 星数（`StarRating.cs`）。
   Level 公式是三段拟合：`2.76257856739498 ≤ SR ≤ 10.5541834716376` 用 `6.9615·e^(0.1374·SR)`；
   `0<SR<下界` 用 `3.6198·SR`；`上界<SR<12.3456789` 用 `2.791·SR+0.5436`；其余 **NaN**。
   DataGrid 7 列，只进不出（无导出/无保存/无删除）。
7. **StarRating.cs 不是 osu!lazer 源码**：它是第三方 Python「Star Rating Rebirth」（`srcalc`）思路的
   C# 扁平重写（`StarRating.cs:13` 的注释点名 `StarRatingRebirthCalculateForLazer.CalculateStarRatingForLazer`），
   算法与 lazer 的 mania 难度计算同源（x/p/a/r/j/c/ks/anchor/keyUsage 体系 + 0.975 难度系数），
   但 **875 行静态方法 + 中文注释 + 中文变量解释**，依赖面很窄（见 §6.3）。
8. **粒子效果**是**窗口级**的（不属于某个工具）：Canvas 盖在最上层、不参与命中测试；
   鼠标移动更新位置、**左键加速+加量、右键只加量**、鼠标离开立即关闭；
   主页面 `Loaded` 和「返回导航」各 `Toggle` 一次（两次相抵）；`CompositionTarget.Rendering` 每帧更新，
   **按帧固定增量、与刷新率耦合**，每帧新建 `Ellipse`/`SolidColorBrush`，移植前必须改造。
9. **OsuFileV14 是「v14 专用、只读一次、全量重写」的简易解析器**：忽略实际 `osu file format vN`、
   `double.Parse` 受系统区域设置影响、`[TimingPoints]` 必须 8 列、`[HitObjects]` 必须 ≥6 列、
   无任何容错/校验/回滚；对象模型是**可变 public 字段 + struct 按值复制 + 静态 `KeyX` 表副作用**。
10. **建议**：解析器、三套算法、星数计算全部**按本文规格重写为纯逻辑类**（挂到新项目 `Core/` + 自检），
    UI 用新项目 MVVM/深色主题重做；§8.3 有「必须复刻 vs 强烈建议修复」清单，§9 是风险表。

---

## 1. 旧项目总览

### 1.1 工程与技术栈

| 项 | 值 | 证据 |
| --- | --- | --- |
| 工程文件 | `HeavenUsurperToolkit.csproj`（16 行，根目录） | `HeavenUsurperToolkit.csproj:1-16` |
| 目标框架 | `net8.0-windows`，`UseWPF=true`，`Nullable=enable`，`ImplicitUsings=enable` | 同上 `:6-8` |
| 发布 | `PublishSingleFile=true`、`DebugType=None` | 同上 `:9-11` |
| 依赖 | **无任何 PackageReference**，只用 WPF + `Microsoft.Win32` + BCL | 全工程 |
| 文档 | `README.md` 只有 5 行（已归档） | `README.md:1-5` |
| 许可证 | **仓库里没有 LICENSE 文件** | 文件清单 |
| 壳 | `App.xaml` 合并 `SharedResources.xaml`，`StartupUri=MainWindow.xaml` | `App.xaml:5-10` |
| 状态 | 无配置、无 Data 目录、无 registry、无进程退出清理 | `App.xaml.cs` 空类 |

> **移植第 0 个决策**：旧代码**没有任何许可证声明**（`Utils.cs` 开头标了 ppy Pty Ltd MIT，见 §1.5）。
> 新项目要合并这些代码，需要先确认授权（见 §9 风险 R1）。

### 1.2 文件清单与规模（全部源码都在根目录）

| 文件 | 行数/大小 | 作用 |
| --- | --- | --- |
| `KeyConverter.xaml` / `.xaml.cs` | 100 行 / 490 行 | 转键器页面（低 k → 高 k） |
| `LNTransformer.xaml` / `.xaml.cs` | 159 行 / 719 行 | LN 转换器页面（14 档模式） |
| `LevelCalculator.xaml` / `.xaml.cs` | 44 行 / 130 行 | 8k 难度评级表（拖 .osu 显示 Level） |
| `OsuFileV14.cs` | 1117 行 | .osu 解析/写回 + mania 对象模型 + `KeyX` 常量 |
| `StarRating.cs` | 875 行 | mania 星数（SR）计算 + `EasyObject` |
| `Helper.cs` | 394 行 | 拖放收集（`GetDroppedFiles`）、随机抽列（`SelectRandom`）、`PreciseTime` 等 |
| `Utils.cs` | 125 行 | 二分/累加和/平滑/插值/`RescaleHigh`（**ppy MIT 头**） |
| `ParticleEffectManager.cs` | 157 行 | 粒子系统 |
| `MainWindow.xaml` / `.xaml.cs` | 44 行 / 402 行 | 自绘窗口壳 + Frame 导航 + 滑动动画 + 粒子叠层 |
| `MainPage.xaml` / `.xaml.cs` | 51 行 / 56 行 | 主页（3 个入口按钮，Test 按钮被 Collapsed） |
| `SharedResources.xaml` | 477 行 | 紫色主题：按钮/文本框/滑条/复选框/进度条/DataGrid/ListView/ComboBox |
| `App.xaml` / `App.xaml.cs` | 13 行 / 14 行 | 空壳（只合并资源字典） |
| `WidthHeightToBooleanConverter.cs` | 24 行 | 判断控件宽高是否相等（按钮 hover 旋转动画用） |
| `AssemblyInfo.cs` / `.sln` / `.gitattributes` / `.gitignore` | — | 忽略 |

### 1.3 壳与导航（MainWindow / MainPage）

**窗口**（`MainWindow.xaml:9-19`）：
- `Title="HeavenUsurper's Toolkit"`、`Height=850`、`Width=800`、`WindowStyle=None`、背景 `#FFD1BFEA`；
- `WindowChrome`：`GlassFrameThickness=0`、`CornerRadius=0`、`CaptionHeight=0`（标题栏完全自绘）、`ResizeBorderThickness=5`；
- 内容 `Grid x:Name="ContentArea"`（`ClipToBounds=True`）层级（从下到上）：
  1. `Canvas x:Name="ParticleCanvas"`（`IsHitTestVisible=False`、`Panel.ZIndex=999`，**盖住整个窗口**）；
  2. `Frame x:Name="MainFrame"`（`NavigationUIVisibility=Hidden`，所有页面都塞这里）；
  3. 顶部 65px 高的标题栏 `Rectangle Fill="#FFB9A6D2"`；
  4. 右上角三个 45×45 按钮：`X`（关闭）/`□`（最大化）/`一`（最小化），`Margin` 分别为 `0,10,10/60/110,0`；
  5. 左上角标题文字 `"HeavenUsurper's Toolkit"`，20px SemiBold，`#FF8E6CDC`，`Margin=20,20,0,0`。

**代码**（`MainWindow.xaml.cs`）：
- `IResizablePage` 接口（`:13-17`）：`double DesiredWidth/DesiredHeight`，页面声明自己想要的窗口尺寸；
  四个 Page 的值：`MainPage 800×850`、`KeyConverter 800×850`、`LNTransformer 800×850`、`LevelCalculator 1281×850`。
- `NavigateToPage(Page)`（`:54-177`）：若正在导航则忽略；首屏或同类型页面直接 `MainFrame.Content=newPage`；
  否则新建一个 `Frame`，两层 `ThicknessAnimation` 0.5s（`ExponentialEase`，`Exponent=6`）做「旧的往左滑出、新的从右滑入」，
  同时 0.6s 动画改 `Window.Width/Height` 到目标页的 `DesiredWidth/Height`；`Completed` 里移除旧 Frame、替换 `MainFrame`。
- `NavigateBack(Page)`（`:242-356`）：反向动画（从左边滑入），**结尾调用一次 `ToggleParticleEffect()`**（`:355`）。
- 窗口控制（`:358-400`）：关闭/最大化/最小化；最大化时把 `MaxWidth/MaxHeight` 钳到 `SystemParameters.WorkArea`；
  `MouseLeftButtonDown` 时若 `e.GetPosition(this).Y < 65` 则 `DragMove()`。
- 粒子接线见 §7。
- ⚠️ `MainFrame = newFrame;`（`:128`、`:308`）改的是 `x:Name` 生成的字段，可编译但语义混乱；新项目别照抄这种动画壳。

**主页**（`MainPage.xaml:16-49`）：左侧一列 120×40 按钮 `LN` / `Key` / `Level`（`ModernButton` 样式，间距 20）；
`Test` 按钮被 `Visibility="Collapsed"`（原本给 PatternGenerator 用）。
`Page_Loaded` 里调 `mainWindow.ToggleParticleEffect()`（`MainPage.xaml.cs:48-54`）。

### 1.4 主题资源（SharedResources.xaml，移植时只需要「行为 + 颜色」）

新项目已有自己的深色主题（`AGENTS.md` 2.9 节），**不建议搬 XAML，只要知道旧主题长什么样**：

| Key | 用途 | 关键属性 | 行号 |
| --- | --- | --- | --- |
| `ModernButton` | 所有按钮 | 透明底、紫边 `#FF8E6CDC`、`BorderThickness=3`、悬停反白；**宽=高时 hover 旋转 360°**（用 `WidthHeightToBooleanConverter`） | `:5-63` |
| `ModernTextBox` | 输入框 | 透明底、紫边 3、`Padding=10,5`、聚焦换边框色 | `:66-97` |
| `ModernSlider` | 所有滑条 | `TickBar` 顶部 4px、轨道 4px、Thumb 16×16 圆点（`MediumPurple`/`#FFA78EE8`/`#FFB9A6D2`） | `:328-387` |
| `ModernCheckBox` | 复选框 | 20×20 方框 + 手写路径勾 `M3,10 L8,15 L17,4`，选中填充 `#FF8E6CDC` | `:390-444` |
| `ModernProgressBar` | 进度条 | 10px 高、边框 2、填充 `#FF8E6CDC` | `:447-476` |
| `ModernDataGrid` | Level 表 | 表头紫底白字；行 hover `#FFF0F0F0`、选中 `#FFE3D7FF`；交替行 `#FFF8F6FF` | `:267-312` |
| `ModernListView` / `ModernComboBox` | 定义但**这三个工具没用** | 白底浅色、小字号 12 | `:100-153`、`:156-264` |

主要颜色：主紫 `#FF8E6CDC`，浅紫 `#FFA78EE8`，窗口底 `#FFD1BFEA`，标题栏 `#FFB9A6D2`，
拖放区/导航区 `#FFCFAEFD`，文字紫 `#FF8E6CDC`。整个 UI 是「浅紫底 + 白底内容 + 紫色粗描边」的 2010 年代风格；
新项目是深色主题，**只按语义重画，不要搬颜色**。

### 1.5 Helper.cs / Utils.cs 里真正被这三个工具用到的部分

**Helper.cs（静态工具类）**

| 成员 | 行号 | 是否被这三个工具用 | 用途 |
| --- | --- | --- | --- |
| `DIVIDE_NUMBER = [2,4,8,3,6,9,5,7,12,16,48,35,64]` | `:11` | ✅（经 `PreciseTime`） | LN 时值吸附的优先分母顺序（先粗后细；含 35/48/64 这种非常规分母） |
| `ERROR = 2.0` | `:12` | ✅（经 `PreciseTime`） | 吸附接受误差（毫秒） |
| `GetDroppedFiles(e, filter, onFileFound)` | `:33-59` | ✅ 三个工具都用 | 从 `DragEventArgs` 拿 `FileDrop`，文件按扩展名过滤、目录**递归**展开；`onFileFound(path,1)` 回调（用于进度），返回 `List<string>` |
| `IsDirectory(path)` | `:123-127` | ✅（被 `GetDroppedFiles` 用） | `new FileInfo(path).Attributes & Directory` |
| `AddFilesInDirectory`（4 参版） | `:106-121` | ✅ | 递归目录；**无 try/catch**（另一个 2 参版 `:374-392` 未使用） |
| `SelectRandom(enumerable, rng, times=1, duplicate=false)` | `:290-324` | ✅ LNTransformer | 随机选 `times` 个**不重复**元素；`times<=0` 返回空；`times>Count` 抛 `InvalidOperationException` |
| `PreciseTime(time, bpm, offset)` | `:266-278` | ✅ LNTransformer | 把时间吸附到 1/t 拍网格（详见 §4.11） |
| `Show/ DragEnter / DragDrop / DirectionFunc / SelectColumn / SelectManyColumn / GenerateRandomHitObjects / CleanObject / CheckOverlap(WithoutLN) / GetAudioDuration / ExtractDuration / SelectRandomNumber` | `:16-127`、`:129-197`、`:149-179`、`:221-264`、`:326-372` | ❌ 未使用（早期 PatternGenerator 的遗留） | 不要移植 |

**Utils.cs（**`Utils.cs:1-3` 开头就是 ppy Pty Ltd 的 MIT 版权头**）**——全部成员都被 `StarRating.cs` 用到：

| 成员 | 行号 | 用途（在 SR 里） |
| --- | --- | --- |
| `SearchSortedLeft(array, value)` | `:10-14` | `Array.BinarySearch`，未命中返回 `~index`（=插入点） |
| `SearchSortedRight(array, value)` | `:16-28` | 命中时**跳到最后一个相等元素的下一格** |
| `CumulativeSum(x, f)` | `:30-38` | 分段常量函数在断点 `x` 上的累计积分 |
| `QueryCumsum(q, x, F, f)` | `:40-47` | 查询累计积分（q 落在分段内则线性补） |
| `SmoothOnCorners(x, f, window, scale=1.0, mode="sum")` | `:49-72` | 在断点坐标上做 ±window 的**滑动窗口和/平均**（窗口长度按时间截断） |
| `InterpValues(newX, oldX, oldVals)` | `:74-95` | 线性插值（区间外取端点值） |
| `StepInterp(newX, oldX, oldVals)` | `:97-107` | 阶梯插值（取 `newX` 左侧最近值） |
| `RescaleHigh(sr)` | `:109-113` | 高星压缩：`sr<=9 ? sr : 9+(sr-9)/1.2` |
| `FindNextNoteInColumn(note, times, noteSeqByColumn)` | `:115-123` | 同列下一个音符；找不到返回 `new EasyObject(0, int.MaxValue, int.MaxValue)`（注意 `Head=0`） |

> ⚠️ `Utils.cs` 的 MIT 头意味着：**如果照搬这 125 行，新项目需要在文档/关于页保留 ppy MIT 声明**。
> `Helper.cs`、`OsuFileV14.cs`、`StarRating.cs` 本身没有版权头，来源需按 §9 R1 处理。

### 1.6 持久化 / 设置 / 数据目录

**没有**。旧项目不写配置、不写日志、不读注册表（`OsuFileProcessor.GetOsuPath()` 会读注册表找 osu! 安装目录，但**三个工具都没调用**，见 §2.4）。
所以移植时不存在「迁移配置/数据」的问题；唯一需要考虑的是新项目是否要加「上次使用的参数」等配置项（建议加，见 §8.4）。

---

## 2. OsuFileV14：mania `.osu` 解析器与对象模型（**移植时建议重写，不照抄**）

### 2.1 类型一览（全部在 `OsuFileV14.cs`）

| 类型 | 行号 | 种类 | 说明 |
| --- | --- | --- | --- |
| `OsuFileProcessor` | `:7-268` | class（静态方法为主） | 门面：`ReadFile(FileInfo/string)`、`WriteOsuFile(osu, path)`、`GetOsuPath()` |
| `OsuFileV14` | `:270-788` | class | 一张谱面：`HitObjects / TimingPoints / Colours / Metadata / General / Events / OriginalFile / path / StarRating` + 解析/写回/列操作 |
| `TimingPoint` | `:790-812` | **struct** | `Time:int, BeatLength:double, Meter/SampleSet/SampleIndex/Volume/Uninherited/Effects:int` + 8 参构造 |
| `ManiaHitObject` | `:814-969` | **struct** | `X,Y,StartTime,Type,HitSound,CircleSize:int, HitSample:string, actualColumn:int` + 计算属性 `EndTime/Column/IsLN` + 4 个构造 + `ToNote()` |
| `Event` | `:971-979` | struct | 只有一个 `string eventString`（把 `[Events]` 原文整段存起来） |
| `Colour` | `:981-1006` | struct | `Name + R/G/B/A`（**A 恒为 255**，读入时忽略原 alpha） |
| `Metadata` | `:1008-1048` | struct | `Title/TitleUnicode/Artist/ArtistUnicode/Creator/Difficulty/Source/Tags/BeatmapID/BeatmapSetID`（默认 `BeatmapID="0"`、`BeatmapSetID="-1"`） |
| `General` | `:1050-1116` | struct | `Version="v14"` + 10 个字符串 General 字段 + 6 个 double Difficulty 字段 + 2 个未使用的事件字符串 |

> 设计问题：**所有实体都是可变 struct、字段 public**，`OsuFileV14` 把它们当可变容器到处改；
> `ManiaHitObject` 每次从 `List` 取出来都是**副本**，改完必须写回列表（代码里 `osu.HitObjects[i] = obj;` 到处都是）。
> 新项目建议改成 `class/record` + 只读属性 + 明确的不变量（列号、时间、LN 尾时间）。

### 2.2 ManiaHitObject（struct）——字段、构造、计算属性（**移植必读**）

字段（`OsuFileV14.cs:814-824`）：

```
X:int            // osu 列坐标 0..512（mania 按列中心取整）
Y:int            // mania 恒 192
StartTime:int    // 毫秒
Type:int         // osu 位掩码；mania LN = 128，普通 note = 1
HitSound:int
CircleSize:int   // 这张谱的键数（int）
HitSample:string // 普通 note: "normalSet:additionSet:index:volume:filename"（5 段，"0:0:0:0:" 是默认）
                 // LN:          "endTime:normalSet:additionSet:index:volume:filename"（6 段）
actualColumn:int // 0..CircleSize-1（真正参与算法的列号）
```

计算属性：

- `EndTime`（`:829-874`）：
  - **get**：`Type != 128` → 返回 `StartTime`；否则 `int.Parse(HitSample.Split(':')[0])`；解析失败兜底 `StartTime`。
  - **set**（把 note ↔ LN 互换就靠它）：
    - `value != StartTime`：`Type != 128` 时置 128；若 `HitSample` 恰好 5 段（普通 note）→ `value + ":" + HitSample`；
      否则（已经 6 段）→ 把第一段换成 `value`（`Skip(1)` 再拼）。
    - `value == StartTime`：若 `Type == 128` → 置 `Type=1` 并去掉 `HitSample` 第一段；否则什么都不做。
- `IsLN`（`:876`）：`StartTime != EndTime || Type == 128`。
- `Column`（`:881-896`）：
  - **get** → `actualColumn`；
  - **set** → `actualColumn = value; X = OsuFileV14.KeyX[CircleSize][value];`
    ⚠️ 这是**静态表副作用**：只要 `CircleSize` 不在 `KeyX` 里（1k、2k、11k…）就 `KeyNotFoundException`；
    `KeyX[12/14/16/18]` 是**空列表** → `IndexOutOfRange`（见 §2.3）。
    另外 set 会把 `X` **规范化**到标准列坐标，哪怕列号没变。
- `ToNote()`（`:963-968`）：副本，`EndTime = StartTime`（→ 去掉尾时间、Type 回 1）。
- 四个构造函数（`:898/915/931`，以及 4 参 `:898`）：
  1. `(x, y, circleSize, startTime, type, hitSound=0, hitSample="0:0:0:0:", endTime=int.MinValue)`：解析文件用；
     `actualColumn = floor(x*circleSize/512.0)`；若给了 `endTime` 则走 `EndTime` setter。
  2. `(column, circleSize, startTime, type, hitSound=0, hitSample="0:0:0:0:", endTime=int.MinValue)`：`X = (int)((column+0.5)*512/circleSize)`。
  3. `(column, circleSize, startTime, hitSound=0, hitSample="0:0:0:0:", endTime=int.MinValue)`（**KeyConverter 收尾和 LN 生成实际用的是这个**）：
     不给 `endTime` → `Type=1`；给了 → `Type = (endTime != startTime) ? 128 : 1`，`X = (int)((column+0.5)*512/circleSize)`、`Y=192`。
     注意 `X` 公式与 `KeyX`（§2.3）在 3k–10k 上**每个列都恰好相等**（对 0..keys-1 逐一验算过），所以两条路径等价。

> **移植建议**：新实现里 `ManiaHitObject` 用 class，显式存 `Column`、`StartTime`、`EndTime`（LN 才有）、
> `HitSample` 原样保留，不再用 `Type==128` 反推尾时间；`X` 只在写文件时由列号换算。

### 2.3 常量：`KeyX` 列坐标表（`OsuFileV14.cs:284-298`）

```
KeyX = {
  3  : [85, 256, 426]
  4  : [64, 192, 320, 448]
  5  : [51, 153, 256, 358, 460]
  6  : [42, 128, 213, 298, 384, 469]
  7  : [36, 109, 182, 256, 329, 402, 475]
  8  : [32,  96, 160, 224, 288, 352, 416, 480]
  9  : [28,  85, 142, 199, 256, 312, 369, 426, 483]
  10 : [25,  76, 128, 179, 230, 281, 332, 384, 435, 486]
  12 / 14 / 16 / 18 : []      // 占位空表，用了就崩
}
```

公式等价：`X = floor((column + 0.5) * 512 / keys)`（对 3–10k 全列成立）。
**没有 1、2、11、13、15、17** → 1k/2k 用 `Column` setter 会抛异常；**12k+ 是空表**（保留给 12k+ 谱面但没填）。

其他常量：
- `FileExtension = ".osu"`（`:280`）；
- `ForbiddenChar = { '\\','/',':','*','?','"','<','>','|' }`（`:281`）——只用于生成文件名时**删除**这些字符（不是替换成 `_`）；
- `StarRating` public 字段，默认 0（`:282`）；
- **没有** `Keys` 枚举、没有 key 名映射、没有 `Difficulty` 枚举。

### 2.4 `OsuFileProcessor` / `OsuFileV14` 公开 API 清单

`OsuFileProcessor`（静态门面）：

| API | 行号 | 行为 | 谁用 |
| --- | --- | --- | --- |
| `static OsuFileV14 ReadFile(FileInfo file)` | `:14-226` | 逐行解析 + 构造对象（构造时**自动算 SR**） | `OsuFileV14(string)`、`OsuFileProcessor.ReadFile(string)` |
| `static OsuFileV14 ReadFile(string fileName)` | `:228-231` | 转 `FileInfo` 调上面 | **三个工具都用** |
| `static void WriteOsuFile(OsuFileV14 osu, string path="")` | `:233-241` | 转发 `osu.WriteFile(path)` | ❌ 未使用 |
| `string GetOsuPath()` | `:243-267` | 读注册表 `HKCR\osu!\shell\open\command` 猜 osu! 安装目录（写死 `Substring(1, len-16)`，失败弹 MessageBox） | ❌ 未使用（且是实例方法却没字段，写了 `return` 后又 `MessageBox`，死代码） |

`OsuFileV14`（实例）：

| 成员 | 行号 | 说明 | 谁用 |
| --- | --- | --- | --- |
| 字段 `HitObjects` | `:272` | `List<ManiaHitObject>` | 三个工具 |
| 字段 `TimingPoints` | `:273` | `List<TimingPoint>` | LNTransformer（`.First()`）、写回 |
| 字段 `Colours` | `:274` | `List<Colour>` | 写回 |
| 字段 `Metadata` / `General` / `Events` | `:275-277` | struct | 三个工具 |
| 字段 `OriginalFile` | `:278` | `FileInfo` | 写回目录依据（另一处用 `path`） |
| 字段 `path` | `:279` | 原文件目录（不是完整路径！） | 写回 |
| 字段 `StarRating` | `:282` | 构造时算好的 SR | LevelCalculator |
| `FileName` | `:300-306` | `OriginalName` 删除 `ForbiddenChar` 后的名字（**不含扩展名**） | 写回 |
| `OriginalName` | `:308-314` | `"Artist - Title (Creator) [Difficulty]"`（**注意 Creator 在括号里，Difficulty 在方括号里**） | `FileName` |
| 构造 `OsuFileV14(hitObjects, timingPoints, colours, metadata, general, events, file)` | `:316-348` | 赋字段 + 重算 `actualColumn` + 算 `StarRating`（异常则 0） | 解析器内部 |
| 构造 `OsuFileV14(string FileName)` | `:350-381` | `ReadFile` → 拷贝 `HitObjects/TimingPoints/Metadata/General`（**漏了 `Colours`/`Events`**）→ 重算 `actualColumn` → **再算一遍 SR**（内层已算过） | LevelCalculator |
| `TimingPointAt(double time)` | `:383-406` | 只在**红点**（`BeatLength >= 0`）里按 `Time` 找；`Time` 落在两红点间取左；早于第一个/晚于最后一个取端点；**没有红点会崩**（`RedPoints[-1]`） | KeyConverter、LNTransformer |
| `ManiaToKeys(int Keys)` | `:408-420` | `General.CircleSize=Keys`；每个音符 `X = KeyX[Keys][obj.Column]`（**原列号**）并改 `CircleSize` | KeyConverter |
| `SelectColumn(int)` / `SelectManyColumn(int[])` / `RemoveManyColumn(int[])` | `:422-450` | 按列取/重排（`SelectManyColumn` 会把列重映射到 `KeyX[Columns.Length]`，`RemoveManyColumn` 改 `CircleSize`） | ❌ 未使用 |
| `Copy()` | `:452-464` | **浅拷贝**：列表里是 struct 副本，但 `Metadata/General/Events` 是值复制、`OriginalFile` 共享 | ❌ 未使用 |
| `ReadFile(FileInfo)`（实例） | `:466-693` | 与静态解析**重复**的一份实现（且不算 SR、不重算列） | ❌ 未使用 |
| `WriteFile(string path="")` | `:696-787` | 全量重写（见 §2.6） | KeyConverter、LNTransformer |
| `TimingPoint` 8 参构造 | `:801-811` | — | 解析器 |
| `ManiaHitObject` 4 个构造 / `ToNote` | `:898-968` | 见 §2.2 | 三个工具 + SR |
| `Colour/Metadata/General/Event` 构造 | `:981-1116` | 默认值（`Metadata.BeatmapID="0"`、`General.CircleSize=8`、`General.OverallDifficulty=0`、`General.ApproachRate=5`、`General.Version="v14"`） | 解析器 |

### 2.5 解析流程（`OsuFileProcessor.ReadFile`，`:14-226`）

逐步：

1. **编码**：`new StreamReader(file.FullName)` → .NET 默认 UTF-8 + BOM 自动识别（UTF-8/UTF-16 BOM 会剥掉）。
2. 新建四个列表 + `Metadata/General/Event` 默认结构体 + 4 个 section 标志（`Events/TimingPoints/Colours/HitObjects`）。
3. 逐行 `ReadLine().Trim()`；**空行直接跳过**（所有空行丢失）。
4. 若以 `"osu file format"` 开头 → **直接 continue，什么也不做**（注意 `:39` 的注释：`Version` 解析被注释掉了）。
   所以 **`General.Version` 永远是默认 `"v14"`，与文件实际版本无关**；写回也永远写 `v14`。
5. 按**固定顺序**做前缀匹配（顺序很重要，都是 `StartsWith`）：
   - General：`AudioFilename / AudioLeadIn / PreviewTime / Countdown / SampleSet / StackLeniency / Mode /
     LetterboxInBreaks / SpecialStyle / WidescreenStoryboard` → 取 `:` 后 `Trim()` 存字符串；
   - Metadata：`TitleUnicode / ArtistUnicode / Title / Artist / Creator / Version / Source / Tags /
     BeatmapID / BeatmapSetID` → 取 `:` 后 `Trim()`（`Version` 映射到 `Metadata.Difficulty`）；
   - Difficulty（数值）：`HPDrainRate / CircleSize / OverallDifficulty / ApproachRate / SliderMultiplier /
     SliderTickRate` → `double.Parse(...)`；
   - 段头：`[Events]` / `[TimingPoints]` / `[Colours]` / `[HitObjects]` → 设对应标志位（各自把其它标志清 0）。
   - 注意这些判断**先于** section 标志执行：如果 [Events] 里某行恰好以 `Title`/`Mode` 等开头会被误判（实际罕见）。
6. 按当前 section 解析：
   - `[Events]`：原文追加 `line + Environment.NewLine`（**只保留行内容，不解析**；段内空行已在第 3 步丢弃）；
   - `[TimingPoints]`：`line.Split(',')` 必须 **8 段**，全部 `double.Parse` 再 `(int)`：
     `time, beatLength, meter, sampleSet, sampleIndex, volume, uninherited, effects`；
     **少列/多列/非数字 → 直接抛异常**（文件级 try/catch 在外层）；
   - `[Colours]`：`Split(':')` → `Name: R,G,B`（用 `double.Parse` 再 `(byte)`，**只取前 3 个数，alpha 丢弃**）；
   - `[HitObjects]`：`Split(',')`，要求至少 6 段：
     `x,y,time,type,hitSound,hitSample` → `double.Parse` 后 `(int)`；`hitSample = elements[5]`（**后面的段全部丢弃**，
     所以 `endTime:hitSample` 中对 mania LN 是完整的 6 段，没丢东西；但如果谱面某行有额外字段会丢）。
   - 其它 section / 其它行：`if (!HitObjects) continue;` —— **未知段落的内容全部忽略**。
7. 构造 `OsuFileV14(...)`（见 §2.4），构造里：
   - 对每个音符重算 `actualColumn = floor(X * CircleSize / 512.0)`；
   - **立即算 `StarRating`**（`StarRatingCalculator.CalculateStarRating(hitObjects, OD, (int)CS, 1.0)`），异常吞掉置 0（DEBUG 弹窗）。

**解析器支持/不支持的段落总结**

| 段落/字段 | 支持 | 备注 |
| --- | --- | --- |
| `[General]` 10 个字段 | ✅ | 其余 General 键（`EpilepsyWarning`、`UseSkinSprites`、`SampleVolume`、`SamplesMatchPlaybackRate` 等）**丢失** |
| `[Editor]` | ❌ **整段丢失**（书签、`DistanceSpacing` 等） | 段头被忽略，行内容在 `[HitObjects]` 之前 → 全部跳过 |
| `[Metadata]` 10 个字段 | ✅ | 其余键丢失 |
| `[Difficulty]` 6 个数值 | ✅ | 其余（如 `SliderTickRate` 已有；无 `ApproachRate` 时默认 5）丢失 |
| `[Events]` | ✅ 原文保留 | 段内空行丢失；段外注释丢失；`[Events]` 之后的 `[Storyboard]` 行（如果有）会被当成 Events 原文保留 |
| `[TimingPoints]` | ✅ 8 列 | 少列/旧版本格式即崩；**空行被跳过后时间线仍可用** |
| `[Colours]` | ✅ | 只读 RGB 3 值（alpha 忽略），写回补 `,255` |
| `[HitObjects]` | ✅ 6 列 | 只认 6 列 |
| `[Variables]`（v3 老格式） | ❌ | 会被当作未知行丢弃 |
| 文件 BOM | ✅ 自动识别 | 写回一律 UTF-8 无 BOM |

### 2.6 写回流程（`WriteFile`，`:696-787`）——**固定模板全量重写**

**写到哪**：

```
目标文件 = (path 参数非空 ? path : this.path) + DirectorySeparatorChar + FileName + ".osu"
this.path = 原文件所在目录（ReadFile 时 file.DirectoryName）
FileName  = "Artist - Title (Creator) [Difficulty]" 删除 ForbiddenChar 后（不含扩展名）
```

- 所以：**默认写到原文件所在目录**，文件名来自元数据；**原文件不改**、**无备份**、
  目标同名文件被 `StreamWriter` 直接截断覆盖（`FileMode.Create` 语义）。
- 因为两个工具都会在 `Metadata.Difficulty` 前缀 `[... ]`、`Metadata.Creator` 追加 `& xxx`，
  所以目标文件名通常与原文件名不同；**重复转换已转换过的文件时，目标路径会等于输入路径**（此时先读后写，等于原地重写）。
- 两个不同的输入文件可能映射到同一个目标文件名 → 后者静默覆盖前者。
- 文件名清理只删除 9 个 Windows 非法字符；**不处理**结尾空格/点、保留设备名（`CON` 等）、超长路径 → 可能抛异常。

**写回格式（精确模板，`\r\n` = `Environment.NewLine`）**：

```
osu file format v14
<空行>                                  ← WriteLine 里又拼了 Environment.NewLine，多出一个空行
[General]
AudioFilename:<值>
AudioLeadIn:<值>
PreviewTime:<值>
Countdown:<值>
SampleSet:<值>
StackLeniency:<值>
Mode:<值>
LetterboxInBreaks:<值>
SpecialStyle:<值>
WidescreenStoryboard:<值>
<空行>
[Metadata]
Title:<值>
TitleUnicode:<值>
Artist:<值>
ArtistUnicode:<值>
Creator:<值>
Version:<值>            ← Metadata.Difficulty
Source:<值>
Tags:<值>
BeatmapID:<值>
BeatmapSetID:<值>
<空行>
[Difficulty]
HPDrainRate:<double>
CircleSize:<double>
OverallDifficulty:<double>
ApproachRate:<double>
SliderMultiplier:<double>
SliderTickRate:<double>
<空行>
[Events]
<Events.eventString 原文（每行已带 \r\n）>
<空行>                  ← WriteLine(eventString) 再补一个换行
[TimingPoints]
time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects    ← 每个红/绿点一行
<空行>
[Colours]               ← 仅当 Colours.Count > 0
<Name>: R,G,B,255
<空行>
[HitObjects]
x,y,startTime,type,hitSound,hitSample                                     ← 每行一个
```

细节与坑：
- `string.Join(",", point.Time, point.BeatLength, ...)`（`:764`）与 `string.Join(",", obj.X, ...)`（`:783`）
  走的是 **`object.ToString()` = 当前区域设置**：`BeatLength:double` 在 zh-CN 下是 `"500"` / `"333.333333333333"`（15 位有效数字），
  **小数精度/格式会与原文不同**；在逗号作小数点的区域设置下会直接写出非法文件。
- 行尾统一 `\r\n`；编码 UTF-8 **无 BOM**（.NET Core `StreamWriter` 默认）。
- `General.Version` 永远是 `"v14"` → 原文是 v14 的谱面版本号不变；如果是别的版本会被**伪装成 v14**。
- `Events.eventString` 末尾有换行（每行追加过），`WriteLine` 再加一个 → 固定多一空行；
  `[TimingPoints]` 若是空列表 → 直接一个空行；`[Colours]` 空则整段不写（**但它的那个空行仍然写**）。
- 写回后**丢失**：`[Editor]` 全段、未知 General/Metadata/Difficulty 键、非 `[Events]` 注释、
  `[Colours]` 原 alpha、`[Events]` 段内空行、`TimingPoint.BeatLength` 原始文本精度。
- `HitObjects` 写 `obj.X, obj.Y, StartTime, Type, HitSound, HitSample`：
  KeyConverter 收尾重建对象时 `Y` 固定 192、`HitSound` 固定 0（**全部打击音丢失**，见 §3.10）；
  LNTransformer 大多数情况保留原对象 → `HitSound` 保留，但 `Column` setter 会规范化 `X`。

### 2.7 三个工具 + StarRating 实际用到的成员（依赖矩阵）

| 成员 | KeyConverter | LNTransformer | LevelCalculator | StarRating |
| --- | --- | --- | --- | --- |
| `OsuFileProcessor.ReadFile(string)` | ✅ | ✅ | ✅（经 ctor） | — |
| `new OsuFileV14(string)` | — | — | ✅ | — |
| `HitObjects` 读/写 | ✅ | ✅ | ✅（读进 SR） | —（只用 `ManiaHitObject`） |
| `TimingPoints` | ❌（不读不写，写回原样） | ✅ `.First().Time`、写回原样 | 写回（经 ctor 不拷贝，Level 不写回） | — |
| `General.Mode` | ✅ 判断 `"3"` | ✅ 判断 `"3"` | ❌（不检查模式） | — |
| `General.CircleSize` | ✅ 读+写（`ManiaToKeys`） | ✅ 读 | 读（算 SR） | — |
| `General.OverallDifficulty` | 写回（不变） | ✅ 读 `osu.TimingPoints` 后**用 UI 值覆盖** | 读（算 SR） | 输入参数 |
| `Metadata.Difficulty` | ✅ 前缀 | ✅ 前缀 | 显示/文件名 | — |
| `Metadata.Creator` | ✅ 读/改 | ✅ 读/改 | 显示/文件名 | — |
| `Metadata.Title/Artist` | 写回 | 写回 | 显示 | — |
| `TimingPointAt` | ✅（clean 用） | ✅（到处用） | — | — |
| `ManiaToKeys` | ✅ | — | — | — |
| `WriteFile()` | ✅ | ✅ | ❌ | — |
| `StarRating` 字段 | ❌ | ❌ | ✅ | — |
| `ManiaHitObject.StartTime/EndTime/Column/IsLN/ToNote/构造` | ✅ | ✅ | 经 SR 间接 | `FromManiaObjects` 只用 `StartTime/EndTime/Column` |
| `KeyX` | 经 `ManiaToKeys`/`Column` setter 间接 | 经 `Column` setter 间接 | ❌ | ❌ |
| `Colours/Events/FileName/OriginalName/ForbiddenChar/Copy/Select*` | 写回间接 | 写回间接 | — | — |

`StarRating.cs` 对 `OsuFileV14` 的**唯一依赖**是 `ManiaHitObject` 这个 struct（`StartTime`/`EndTime`/`Column` 三个成员），
**不依赖** `OsuFileV14` 类本身（详见 §6.3）。这意味着新项目可以给 SR 计算喂一个**独立的最小音符结构**，不必先有解析器。

### 2.8 OsuFileV14 明显缺陷（**移植时不要照抄**）

| # | 缺陷 | 证据 | 影响 |
| --- | --- | --- | --- |
| P1 | 忽略实际文件版本，永远按 v14 列格式解析/写回 `v14` | `:37-41`、`:709` | 非 v14 文件可能解析失败或字段错位；写回"伪装版本" |
| P2 | 无容错：`double.Parse`/数组下标/字段缺失都直接抛；只有调用方 per-file try/catch | 全文件 | 一个坏谱面 = 整次转换少一个文件，Release 下无提示 |
| P3 | 区域设置敏感：`double.Parse`/`ToString` 都用当前 culture | `:133` 等、`:764`、`:783` | 非 zh-CN/en-US 系统上小数点解析/输出错误 |
| P4 | 未知字段/段落不保留（`[Editor]`、部分 General/Metadata 键、注释） | §2.6 | 写回是"重建"不是"编辑"，**不可逆丢数据** |
| P5 | `[Events]` 用字符串整段保存 → 段内空行丢失、段外注释丢失；`Events` 是 struct 字段，`Copy()` 共享 | `:190`、`:749`、`:461` | 写回格式变化 |
| P6 | 写回文件名由元数据拼出 → 同名覆盖、可能越界/非法；**无备份** | `:696-706` | 用户数据可被静默覆盖 |
| P7 | struct 可变 + `Column` setter 静态副作用（`KeyX`），1k/2k/12k+ 直接崩 | `:881-896` | KeyConverter/LNTransformer 在某些键数上不可用 |
| P8 | `TimingPointAt` 没有红点（全是绿点）时 `RedPoints[-1]` 崩溃 | `:383-406` | 罕见但会炸 |
| P9 | 构造时同步算 SR（875 行算法）→ **加载慢**；10k+ 或空谱面 SR=0（异常被吞） | `:334-347` | LevelCalculator 拖入大批文件时卡 UI，且 10k+ 显示 NaN |
| P10 | `OsuFileV14(string)` 二次计算 SR（内层构造已算过）+ 漏拷 `Colours/Events` | `:350-381` | 性能减半；若拿它写回会丢颜色/事件（当前 Level 不写回，仅隐患） |
| P11 | 静态 `ReadFile` 与实例 `ReadFile` 两份重复实现（后者不算 SR/不重算列） | `:14-226`、`:466-693` | 维护双份，易分叉 |
| P12 | `Colour.A` 恒为 255（读入丢弃 alpha） | `:989-1005` | 写回颜色 alpha 变化 |
| P13 | 无 unit test / 无样本；算法正确性完全靠作者手测 | 仓库 | 移植时没有"参考答案"，必须自己补基线 |

### 2.9 新项目建议的解析器 API（不照抄旧实现）

```
ManiaBeatmapFile
  ├─ Metadata / General / Events(原文) / TimingPoints / HitObjects / Colours
  ├─ static Read(path)            // UTF-8（BOM/无 BOM 均可，拒绝 UTF-16 或转码）
  └─ Write(path)                  // 固定模板（或保留原文 + 只替换目标段，更好）

ManiaNote (class/record)
  ├─ int Column; int StartTime; int? EndTime; int HitSound; string HitSample
  └─ bool IsLongNote => EndTime is int e && e != StartTime
```

- 解析用 `CultureInfo.InvariantCulture`；写回也用它（或 `double.ToString("R", InvariantCulture)`）。
- 建议**保留原文的行**（`[Editor]`、未知键、[Events] 空行）并在写回时按 section 替换，
  这样能避免 P4 的不可逆丢数据；如果决定不保留，至少要在 UI 里明确提示「会丢弃 X」。
- 列号不再从 `X` 反推：mania 的列号在解析时就用 `floor(x*keys/512)` 一次算好；
  但注意**非标准 X 的谱面**（有些图的 x 是 0/1/511 之类）按这个公式会得到合法列，和旧行为一致。

---

## 3. KeyConverter（转键器）

> 源文件：`KeyConverter.xaml`（100 行）、`KeyConverter.xaml.cs`（490 行）。
> 一句话：**把低键数 mania 谱面「扩」成高键数**——随机挑选原列复制/插入到新列里，
> 可以故意留若干空白列降低密度，并按时间做子弹清理；**只支持升 key**，**不改 `[TimingPoints]`**。

### 3.1 UI 布局与交互（`KeyConverter.xaml`）

Grid 7 行 × 2 列，从上到下：

| 行 | 控件 | 取值/默认 | 说明 |
| --- | --- | --- | --- |
| 0 | 空 `Rectangle` 高 65 | — | 给自绘标题栏让位 |
| 1 | `Slider KeyValue` | **2–10，默认 8**，`IsSnapToTickEnabled`，`TickFrequency=1` | 目标键数（`int` 截断） |
| 2 | `Slider GapValue` | **0–20，默认 10** | 转换「块」的时间跨度（公式见 §3.4） |
| 3 左 | `Slider CleanValue` | **0–16，默认 4** | 子弹清理粒度（换算成 `beatLength/Clean` 毫秒阈值） |
| 3 右 | `Slider BlankValue` | **0–9，默认 0** | 留空列数（实际会被 clamp 到 `目标键数-源键数`） |
| 4 | `TextBox SeedText` | 默认 `"114514"` | 随机种子（空 = 每次随机） |
| 5 | `CheckBox CreatorCheckBox` + `TextBox CreatorText` | 默认不勾 / 空 | 勾了且非空 → **替换**谱面作者，否则**追加** `" & KeyConverter"` |
| 6 | 拖放区：`Rectangle Fill="#FFCFAEFD"` + 居中大字 `"Drop Folder or .osu File"` | — | 两个控件各自挂 `Drop` 事件 |
| 6 | `Button BackButton`（左下 `←`）、`Button InstructionButton`（右下 `Instruction`） | — | 返回主页 / 弹说明 |
| 6 | `WrapPanel`：`C1k … C9k` 复选框 + 一个**没人读的** `AnotherAlgorithm` 复选框 | 全不勾 | 只转换勾选键数的谱面；不勾 = 全部键数都转 |
| 6 | `ProgressStackPanel`（`ProgressBar` + `"x/y"` 文本） | 初始 `Collapsed` | 找到第一个文件后才显示 |

**没有任何**「选择文件」按钮 / 输出目录设置 / 取消按钮 / 结果列表 / 预览 / 回滚。
整个页面唯一的输入方式是把文件或文件夹拖到紫色区域或中间文字上。
`InstructionButton_Click`（`:476-488`）弹出的原文（移植时可作为工具说明文案）：

```
本转K器只支持低转高 不支持高转低
Key：目标key数
Gap：转换间隔（参数越小转换切换越快）
Clean：清除参数，用来清除子弹使用，例如设置为4，则会清除时长为包括1/4之内的子弹（二连音）（1/5 1/6都会清除）
Blank：空白列数，为0则不会生成空白列（用来减轻高k密度压力使用）
勾选Creator之后可更改谱面作者便于上传
选择对应k数之后可以转换指定k数的谱面，不勾选则默认转换全部k数的谱面

请注意：未转换完成前请不要切换页面，否则需要重新转换
```

### 3.2 输入 / 输出

| 项 | 行为 |
| --- | --- |
| 输入 | 拖入 `.osu` 文件或文件夹；文件夹**递归**收集所有扩展名（小写后）为 `.osu` 的文件（`Helper.GetDroppedFiles`，`Helper.cs:33-59`） |
| 读取 | `OsuFileProcessor.ReadFile(path)` → UTF-8（BOM 自动识别），只认 `[General]/[Metadata]/[Difficulty]/[Events]/[TimingPoints]/[Colours]/[HitObjects]` |
| 读后判断 | `Mode` 必须 `"3"`（mania）；`CircleSize` 取整为源键数；`Path.GetExtension(name) == ".osu"`（**大小写敏感**） |
| 输出 | **同目录**新文件：`Artist - Title (Creator) [Version].osu`（元数据拼名、删 9 个非法字符；`Difficulty` 会被加上前缀） |
| 覆盖 | 目标存在则 `StreamWriter` **直接覆盖**；**无备份**；原文件不改（除非目标名恰好等于输入名 → 原地重写） |
| 编码 | 输出 UTF-8 **无 BOM**，行尾 `\r\n`；输入编码交给 `StreamReader` 默认（UTF-8/UTF-16 BOM 可识别） |
| 写回 | 固定模板**全量重写**（见 §2.6）：`[Editor]`、未知字段、非 Events 注释全部丢失 |
| 进度 | 只显示 `已处理/总数`；没有取消；切页面后后台仍在跑（多个 drop 会并发） |

### 3.3 参数与常量（`KeyConverter.xaml.cs:14-20`）

| 常量/参数 | 值 | 含义 |
| --- | --- | --- |
| `INTERVAL` | **50.0** | 同列相邻音符「起点差 ≤ 50ms」→ 视为同一串，丢弃后一个（收尾合并） |
| `LN_INTERVAL` | **10.0** | 同列相邻音符「后一个起点 - 前一个尾点 ≤ 10ms」→ 视为接续，丢弃后一个 |
| `ERROR` | **1.5** | 跨块清理时的额外容差（`timeDivide + 1.5`） |
| `BlankColumn` | 1 | **定义了但从未使用**（死常量） |
| `blank`（UI） | `(int)BlankValue.Value`，再 `min(blank, 目标键数-源键数)` | 留空列数 |
| `clean`（UI） | `(int)CleanValue.Value` | 子弹清理：阈值 `beatLength / clean` 毫秒 |
| `gapValue`（UI） | `(int)GapValue.Value`（0–20） | 分块时间跨度，公式：`gap_ms = 29998.8584 * e^(-0.3176 * gapValue) + 347.7248`；**`gapValue==0` → `double.MaxValue`（整张谱一个块）** |
| `keyValue`（UI） | 2–10 | 目标键数 |

`Gap → 块跨度`数值表（用于文档/UI 提示；四舍五入到 0.01ms）：

| Gap | 块跨度 ms | Gap | 块跨度 ms | Gap | 块跨度 ms |
| --- | --- | --- | --- | --- | --- |
| 0 | ∞（不切块） | 7 | 3595.46 | 14 | 699.33 |
| 1 | 22183.71 | 8 | 2711.73 | 15 | 603.66 |
| 2 | 16242.01 | 9 | 2068.47 | 16 | 534.02 |
| 3 | 11917.07 | 10 | 1600.25 | 17 | 483.33 |
| 4 | 8768.98 | 11 | 1259.43 | 18 | 446.43 |
| 5 | 6477.51 | 12 | 1011.35 | 19 | 419.57 |
| 6 | 4809.56 | 13 | 830.77 | 20 | 400.02 |

### 3.4 主流程（`ApplyToBeatmap`，`:167-364`，含 `ProcessDroppedFiles` 外层）

```
ProcessDroppedFiles(e):                      // :57-147
  if 无 FileDrop: return
  Task.Run:                                  // ⚠️ 在后台线程调 Helper.GetDroppedFiles(e,...) 读拖放数据
    UI: processedFiles=0; 进度条=0
    allFiles = GetDroppedFiles(e, 过滤 ext(".osu"), 每找到一个文件: UI { totalFiles++; 显示进度面板; 刷进度 })
    totalFiles = allFiles.Count             // ⚠️ 后台线程直接写，和 callback 里的 UI 线程 totalFiles++ 有竞态
    if totalFiles == 0: return
    UI 一次性读参数: seedText / blank / clean / gap / keyValue / creator / creatorChecked / checkKeys(勾选的 C1k..C9k)
    foreach file in allFiles:
      try ApplyToBeatmap(file, 参数...)
      catch: UpdateProgress(false);                  // DEBUG 下另起线程弹 MessageBox
```

```
ApplyToBeatmap(name, seedText, blankValue, cleanValue, gapValue, keyValue, creator, creatorChecked, checkKeys):
  canBeConverted = true
  if Path.GetExtension(name) != ".osu": canBeConverted = false      // ⚠️ 大小写敏感，.OSU 会被判失败
  osu = OsuFileProcessor.ReadFile(name)          // 可能抛异常 → 外层 catch
  if osu.General.Mode != "3": canBeConverted = false                // 必须 mania

  seed = 空种子 ? Guid.NewGuid().GetHashCode()
                : BitConverter.ToInt32(SHA256(UTF8(seedText)), 0)  // 取摘要前 4 字节（小端）
  Rng = new Random(seed)

  keys   = (int)osu.General.CircleSize
  blank  = (int)blankValue
  if keys > 9 || (int)keyValue <= keys: canBeConverted = false      // 只支持低→高；10k 源被拒
  blank  = min(blank, (int)keyValue - keys)
  if checkKeys.Count > 0 && !checkKeys.Contains(keys): canBeConverted = false

  if !canBeConverted: UpdateProgress(false); return

  osu.ManiaToKeys((int)keyValue)      // General.CircleSize=目标键数；每个音符 X = KeyX[目标][原列]

  locations = osu.HitObjects.OrderBy(StartTime).ToList()
  area = []; checkList = []; sumTime = 0; lastTime = 0
  tempObjects = locations                      // 再排一次序，无实际作用

  foreach group in tempObjects.GroupBy(h => h.StartTime):      // 按键时间升序分组
     // 1) 把该时间点、该块内的音符重建成「目标键数」下的对象（按列号升序）
     newLocations = group.Select(n => (n.Column, n.StartTime, n.EndTime)).OrderBy(Column)
     line = [ for each: new ManiaHitObject(note.Column, 目标键数, note.StartTime, endTime: note.EndTime) ]
            // ⚠️ 这里就把 HitSound 清成 0、HitSample 重置为默认（见 §3.10）
     // 2) 累计时间跨度
     sumTime += group.Key - lastTime; lastTime = group.Key
     area.AddRange(line)
     // 3) 判断是否切块
     gap = 29998.8584 * exp(-0.3176 * gapValue) + 347.7248
     if gapValue == 0: gap = double.MaxValue
     if sumTime >= gap:
        sumTime = 0
        (result, checkList2) = ProcessArea(osu, Rng, area, keys, 目标键数, blank, (int)cleanValue, ERROR, checkList)
        newObjects.AddRange(result); checkList = checkList2.ToList(); area.Clear()

  if area.Count > 0:                           // 处理最后一块（注意：这次返回的 checkList 不再保存）
     (result, _) = ProcessArea(...同上...); newObjects.AddRange(result)

  newObjects = newObjects.OrderBy(StartTime)   // 只按时间稳定排序
  cleanObjects = 按列合并去重(newObjects)       // §3.6
  osu.HitObjects = cleanObjects.OrderBy(StartTime).ToList()

  // 元数据（§3.7）
  prefix = blank == 0 ? $"[{keys}To{keyValue}C]" : $"[{keys}To{keyValue}C{blank}B]"
  osu.Metadata.Difficulty = osu.Metadata.Difficulty.Insert(0, prefix)
  if creatorChecked && creator != "": osu.Metadata.Creator = creator
  else                               : osu.Metadata.Creator += " & KeyConverter"

  osu.WriteFile()                              // 新文件名见 §3.2
  UpdateProgress()
```

### 3.5 `ProcessArea`：**列映射算法**（`:366-474`，核心）

每个「块（area）」独立做一次随机映射；一个块 = 连续若干个时间点、总跨度刚 ≥ `gap_ms` 的一组音符。
**同一个块内所有音符共用同一套映射**，所以同一块内是「整块复制 + 整块右移」，跨块会换映射（这就是 Gap 的听感作用）。

```
ProcessArea(osu, Rng, hitObjects /*=一块*/, fromKeys, toKeys, blankNum, clean, error, checkList):
  # ── 1. 随机决定：复制哪些源列、哪些复制列留空、插到哪些新列 ──
  copyColumn = []                          # List<(int column, bool isBlank)>
  n = toKeys - fromKeys
  while n > 0:
     c = Rng.Next(fromKeys)
     if (c,false) ∉ copyColumn: copyColumn.Add((c,false)); n--
  n = blankNum
  while n > 0:
     c = Rng.Next(fromKeys)
     if (c,false) ∈ copyColumn && (c,true) ∉ copyColumn:
         copyColumn[IndexOf((c,false))] = (c,true); n--
  n = toKeys - fromKeys
  insertColumn = []
  while n > 0:
     p = Rng.Next(toKeys)
     if p ∉ insertColumn: insertColumn.Add(p); n--
  insertColumn.Sort()                      # 升序；copyColumn[j] ↔ insertColumn[j] 一一对应

  # ── 2. 逐时间点做映射 ──
  for each timeGroup in hitObjects.GroupBy(StartTime):     # 组内顺序 = 上一阶段按列排好的顺序
     locations = timeGroup.ToList()        # List<ManiaHitObject>（struct 副本；下面会写回）
     tempObjects = []
     for i in 0 .. locations.Count-1:
        column = locations[i].Column       # 原始列号（0..fromKeys-1）
        for j in 0 .. copyColumn.Count-1:
           if column == copyColumn[j].column && !copyColumn[j].isBlank:
              temp = locations[i]          # 复制一份
              temp.Column = insertColumn[j]        # ← Column setter 会按 KeyX[toKeys] 重算 X
              tempObjects.Add(temp)                # 先加「新列上的副本」
           if locations[i].Column >= insertColumn[j]:     # ← 注意：比较的是「当前（可能已右移）」的列号
              loc = locations[i]; loc.Column += 1; locations[i] = loc
        tempObjects.Add(locations[i])      # 最后加「被右移后的原音符」

     # ── 3. 跨块清理（只作用于本块第一个时间点）──
     if isFirst && checkList != null && checkList.Count > 0 && clean > 0:
        checkC = checkList 的列号列表；checkS = checkList 的 StartTime 列表
        for i in 0 .. tempObjects.Count-1:
           if tempObjects[i].Column ∈ checkC:
              if clean != 0:
                 beatLength = osu.TimingPointAt(tempObjects[i].StartTime).BeatLength
                 timeDivide = beatLength / clean
                 index = checkC.IndexOf(tempObjects[i].Column)   # 第一次出现的下标
                 if tempObjects[i].StartTime - checkS[index] < timeDivide + error:
                     tempObjects.RemoveAt(i); i--
              else:
                 tempObjects.RemoveAt(i); i--        # ⚠️ clean==0 时无条件删（bug，见 §3.10）
        isFirst = false

     newObjects.AddRange(tempObjects)
     checkColumn = tempObjects 的副本   # 作为下一块的 checkList（=本块最后一个时间点的结果）

  return (newObjects, checkColumn)
```

**插入/右移语义**（把上面两重循环翻译成一句话）：

> 新列的位置集合 `insertColumn` 是升序的 `toKeys-fromKeys` 个随机位置；
> 对每个时间点里的每个原音符：若它的源列 `c` 在 `copyColumn` 里且没被标 Blank，
> 就在 `insertColumn[j]` 处放一个「X 相同的副本」；而原音符自身按 `insertColumn` 顺序依次右移，
> **凡是插入位置 ≤ 该音符当前列号就 +1**。由于升序处理，最终列号满足
> `最终列 = 原列 + |{ p ∈ insertColumn : p ≤ 最终列 }|` 这个不动点（等于「按升序逐个插入」的稳定结果）。

**4k → 7k、Blank=1 的完整手算例子**（便于写单元测试）：
设 `fromKeys=4, toKeys=7`（需要 3 个副本），随机结果：
`copyColumn=[(1,false),(3,false),(0,true)]`（源列 1、3 复制，源列 0 的复制被 Blank 掉），
`insertColumn=[1,4,6]`（排序后），则：

| 源列 | 映射结果 | 说明 |
| --- | --- | --- |
| 0 | → 列 0 | 0<1、0<4、0<6，不右移；源列 0 的副本被 Blank → 不产生副本 |
| 1 | → 副本@列1，原音符→列2 | j=0：命中 copyColumn[0] → 副本列1；再 `1>=1` → 原音符列2；j=1/2 不再右移 |
| 2 | → 列 3 | 2>=1 → 3；3<4/6 不再动 |
| 3 | → 副本@列4，原音符→列5 | j=0：3>=1 → 4；j=1：命中 copyColumn[1] → 副本列4，再 `4>=4` → 原音符 5；j=2：5<6 |
| 合计 | 占用列 0,1,2,3,4,5；**列 6 空** | 7 列里 1 列留空 = Blank=1 ✓ |

结论：**输出列数恒等于目标键数**；`blank` 个新列是空的（对应 insert 位置没有副本产生）。
`blank=0` 时不会产生空白列；`blank` 越大密度越低。每次切块都会重新抽这 3 组随机数，
所以同一张谱不同时间段可能落在不同列上（Gap 越小切换越频繁）。

> **随机数消耗顺序**（复刻种子效果必须完全一致）：先 `toKeys-fromKeys` 次成功 `Next(fromKeys)`（含重试），
> 再 `blankNum` 次成功 `Next(fromKeys)`（含重试），再 `toKeys-fromKeys` 次成功 `Next(toKeys)`（含重试）。
> 每个时间点/块内不再消耗随机数；清理逻辑也不消耗。

### 3.6 收尾合并（列内去重，`:280-337`）

```
for each columnGroup in newObjects.GroupBy(Column):                # 按列分组（顺序 = 首次出现顺序）
   cleanLocations = columnGroup.Select(StartTime, EndTime).OrderBy(StartTime).ToList()
   lastStart = cleanLocations[0].StartTime; lastEnd = cleanLocations[0].EndTime
   for i in 0 .. count-1:
      if i == 0: lastStart/lastEnd = [0]; continue
      if cleanLocations[i].StartTime >= lastStart && <= lastEnd:
          RemoveAt(i); i--; continue                              # 音符落在上一个 LN 体内 → 丢
      if |cleanLocations[i].StartTime - lastStart| <= 50:
          lastStart/lastEnd = cleanLocations[i]; RemoveAt(i); i--; continue   # 同列 50ms 内的串 → 只留第一个
      if |cleanLocations[i].StartTime - lastEnd| <= 10:
          lastStart/lastEnd = cleanLocations[i]; RemoveAt(i); i--; continue   # 尾接下一个头 10ms 内 → 丢下一个
      # 保留上一个
      newColumnObjects.Add(new ManiaHitObject(columnGroup.Key, 目标键数, lastStart, endTime: lastEnd))
      lastStart = cleanLocations[i].StartTime; lastEnd = cleanLocations[i].EndTime
   newColumnObjects.Add(new ManiaHitObject(columnGroup.Key, 目标键数, lastStart, endTime: lastEnd))
   cleanObjects.AddRange(newColumnObjects)
```

行为要点：
- 三个判断是 **else-if 顺序**：先判「在 LN 里」（不改 last），再判 50ms（**更新 last 为被删的音符**），再判 10ms（同样更新 last）→ 类似于「链式合并」，被删的音符可能成为下一轮的比较基准。
- 保留的音符用**新构造**生成（Y=192、HitSound=0、HitSample=默认），所以**全部打击音在这里彻底丢失**。
- 该收尾**只在列内做**：不同列同一时间点的双押保留（正常），但跨列不判断重叠。
- LN 重叠时（后一个 LN 起点落在前一个 LN 体内）→ 后一个 LN 整条被删，**尾段也一起没了**（会改谱，不只是"清理子弹"）。

### 3.7 元数据改写（`:341-357`）

- `Metadata.Difficulty`（写回为 `Version:` 行）→ 在最前面插入：
  - `blank == 0`：`"[{keys}To{keyValue}C]"`，例：`[4To7C]`；
  - `blank > 0`：`"[{keys}To{keyValue}C{blank}B]"`，例：`[4To7C2B]`。
  （`keys` 是源键数 `(int)CircleSize`，`keyValue` 是目标键数滑条值。）
- `Metadata.Creator`：勾了 Creator 且文本非空 → **整体替换**；否则 `旧作者 + " & KeyConverter"`。
- 其余元数据/时间点/事件/颜色：原样写回（但按 §2.6 的固定模板重排、丢未知字段）。
- 目标文件名由 `Artist - Title (Creator) [Difficulty]` 拼出 → 因此文件名会带上 `[4To7C]` 前缀和 `& KeyConverter`。

### 3.8 进度 / 异常 / 取消

- `UpdateProgress(canBeConverted=true, increment=true)`（`:32-55`）在 UI 线程上：
  - 失败调用 `UpdateProgress(false)`：`totalFiles--; processedFiles--;`，再（默认 increment）`processedFiles++` →
    **净效果 = 总数减一、已处理不变**（失败文件不计入分母）；`totalFiles<=0` 时直接 return（进度条停在旧值）；
  - 成功调用 `UpdateProgress()`：`processedFiles++`，刷新 `值 = processed/total*100` 和文本 `"已处理/总数"`。
- 扫描阶段的 callback 每发现一个文件：`totalFiles++; 显示进度面板; 调 UpdateProgress(true,false)`（只刷新不计数）。
- **异常**：`ApplyToBeatmap`/`ReadFile` 抛出的所有异常被 per-file `try/catch` 吞掉，只表现为「该文件失败」；
  DEBUG 下另起线程弹 `MessageBox`（`MessageBoxOptions.DefaultDesktopOnly`），Release 下完全静默。
- **没有取消**；没有完成提示/统计；切走页面后转换继续（说明文字只是提醒不要切）。
  ⚠️ 连续 drop 两次会起两个 `Task.Run` 并发写共享的 `totalFiles/processedFiles/进度条`，并可能同时写同一个输出文件。
- ⚠️ `Helper.GetDroppedFiles(e,...)` 在 `Task.Run` 里执行，`e` 是 WPF `DragEventArgs`（有线程亲和性），
  这里能跑通属于"运气/版本行为"；新项目务必**先在 UI 线程取出 `string[]` 路径**，再丢给后台。

### 3.9 已知缺陷 / 奇怪行为（KeyConverter）

| # | 缺陷 | 证据 | 影响/建议 |
| --- | --- | --- | --- |
| K1 | **1k → 2k 会崩**：`ManiaToKeys(2)`/`Column` setter 查 `KeyX[2]`，表里没有 2 | `OsuFileV14.cs:414`、`:890` | 滑条允许 2；新实现要么补 `KeyX[2]=[128,384]`，要么禁止目标 2k |
| K2 | **（2026-10-24 复核更正：不成立）** 原以为 `Clean=0` 会误删整列；实际 `ProcessArea` 的无条件删除分支被外层 `clean > 0` 挡成**死代码**，旧版 `Clean=0` 就是“不清理” | `KeyConverter.xaml.cs:438`（外层 `clean > 0`）、`:458-462`（死分支） | 旧行为本身正确，**不需要修复**；详见 `osu-core-review.md` §6 |
| K3 | `AnotherAlgorithm` 复选框在 XAML 里但**没有任何代码读取** | `KeyConverter.xaml:97` | 死 UI，移植时删掉 |
| K4 | **全部 `HitSound` 丢失**（重建对象时默认 0；收尾又重建一次） | `:244`、`:325`、`:332` | 转出来的谱面没有 whistle/clap/finish；如需保留需在重建时带上 `HitSound` |
| K5 | LN 体内音符 / 与 LN 尾 ≤10ms 的音符会被直接删除（不是"变短"） | §3.6 | 对特殊谱面可能改坏；建议在新工具里作为显式选项 |
| K6 | 10k 源谱面无法转换（`keys>9` 直接拒） | `:199` | 若要做 10k→? 需扩展 `KeyX` 与逻辑 |
| K7 | 扩展名判断大小写敏感（`.OSU` 收集进来却判失败） | `:170` | 新实现统一 `OrdinalIgnoreCase` |
| K8 | 跨块清理用 `checkList` 只保留了上一块**最后一个时间点**，且只对比列号 + 时间；`checkS[index]` 取第一个出现 | `:438-466` | 语义模糊；建议新实现按"同列最近音符时间差"重写 |
| K9 | 进度计数在失败时把文件移出分母；`totalFiles` 跨线程无锁 | §3.8 | 新实现用 `Interlocked`/UI 线程统一计数 |
| K10 | 输出=输入同名时会原地覆盖；不同源文件同目标名互相覆盖；无备份 | §3.2 | 新项目建议"另存到指定目录 + 重名自动 `(1)`" |
| K11 | 只按 `StartTime` 稳定排序输出，同一时间的音符顺序会变（复制音符在前/原音符在后），文件字节与旧版不同 | `:278`、`:339` | 对玩法无影响；若要逐字节比对需复刻排序 |
| K12 | 没有对 `CircleSize` 作整数校验：CS=7.5 时 `keys=7`，列号由 `floor(x*7/512)` 推出，可能与真实 7.5k 布局不符 | `:196`、`OsuFileV14.cs:330` | 实际 7.5k 谱面罕见；新实现应要求 CS 为整数 |
| K13 | 噪音：`import System.Security.Cryptography`；`osu.TimingPoints.First()` 未用；`Math.Pow(Math.E,...)` 可读性 | — | 重写时清理 |

---

## 4. LNTransformer（LN 转换器）

> 源文件：`LNTransformer.xaml`（159 行）、`LNTransformer.xaml.cs`（719 行）。
> 一句话：**按「密度档位 + 百分比 + 时值分母 + 轨道 + 间隔」把普通音符批量变成 LN，或反过来把 LN 压成音符**；
> 内部是一个「每列独立生成 LN 时长」的随机算法，最后再按轨道集合筛一遍。

### 4.1 UI 布局与交互（`LNTransformer.xaml`）

从上到下一列滑条（全部整数吸附）：

| 控件 | 范围/默认 | 含义 |
| --- | --- | --- |
| `Slider LevelValue` | **-3 ~ 10，默认 3** | 模式/密度档（tooltip 列了 -3/-2/-1/0/3/5/7/10 的说明，见 §4.4） |
| `Slider PercentageValue` | **0–100，默认 100** | 每个音符变成 LN 的概率（%） |
| `Slider DivideValue` | **1–16，默认 4** | LN 时值分母：网格 = `beatLength / Divide`（即 1/4 拍等） |
| `Slider ColumnValue` | **0–10，默认 10** | 只让哪几条轨道保留 LN（0 = 全部；大于键数自动 clamp 到键数） |
| `Slider GapValue` | **0–20，默认 12** | 每隔多少个「时间点」重新随机一次轨道集合 |
| `CheckBox CreatorCheckBox` + `TextBox CreatorText` | 不勾/空 | 同 KeyConverter：替换 or 追加 `" & LNTransformer"` |
| `TextBox SeedText` | `"114514"` | 随机种子（空 = 随机） |
| `TextBox OverallDifficulty` | **`"0"`** | **转换后强制写入谱面 OD**（默认就把 OD 写成 0！） |
| `CheckBox Ignore` | **默认勾选** | 已转换过的谱面跳过（判断见 §4.12） |
| `CheckBox Fix Error` | **默认勾选** | LN 尾时间用 `PreciseTime` 吸附到节拍网格（±2ms） |
| `CheckBox OriginalLN` | **默认不勾** | 保留原谱面的 LN 不动（不重新生成） |
| `CheckBox C1k…C10k` | 全不勾 | 只转换勾选键数的谱面（同 KeyConverter） |
| 拖放区 / Back / Instruction / 进度条 | 同 KeyConverter | 右下 `Instruction` 有彩蛋：**右键**弹出 `"右键点击了Instruction按钮！"`（`:714-717`） |

`Level` 的 tooltip 原文（`:38-56`）：`-3 Hold off`、`-2 Random ms`、`-1 Random divide`、`0 Regular LN`、
`3 Light LN`、`5 Medium LN`、`7 Dense LN`、`10 Full LN`。

### 4.2 输入 / 输出 / 关键提醒

- 输入过滤与 KeyConverter 完全一样（拖 .osu/文件夹递归，只收 `.osu`）；`Mode != "3"` 拒绝；
  `checkKeys` 过滤；`IsConverted && Ignore` 跳过。
- **不检查扩展名**（`Path.GetExtension` 判断被注释掉，`:186-189`）——靠收集阶段过滤。
- 输出同样是「同目录、按元数据拼名、新建/覆盖、UTF-8 无 BOM、CRLF、固定模板全量重写」。
- ⚠️ **OD 默认 0**：只要不改那个文本框，转出来的谱 OD 全部变 0（对可玩性影响很大）。
- ⚠️ 1k/2k 谱面会因 `Column` setter 查 `KeyX[1/2]` 崩溃（`KeyX` 只有 3–10 等），per-file 被吞。
- `CircleSize` 不变（不增删键数）；10k 谱面 `KeyX[10]` 存在，可以处理（前提 `Column` setter 用 10）。

### 4.3 参数结构（`LNTransformParameters`，`:29-44`）

`SeedText / LevelValue / PercentageValue / DivideValue / ColumnValue / GapValue / OverallDifficulty /
CreatorIsChecked / CreatorText / IgnoreIsChecked / OriginalLNIsChecked / FixErrorIsChecked / CheckKeys`。
读取时：`LevelValue = (int)LevelValue.Value` 等全为 int 截断；`OverallDifficulty = double.Parse(OverallDifficulty.Text)`
（⚠️ 文本框非法 → 解析异常发生在 `Dispatcher.Invoke` 里 → 整次转换静默中止）。

### 4.4 主流程（`ApplyToBeatmap`，`:182-376`）

```
ApplyToBeatmap(name, p):
  canBeConverted = true
  osu = ReadFile(name)
  if osu.General.Mode != "3": 拒绝
  if p.CheckKeys 非空 && !包含 (int)osu.General.CircleSize: 拒绝
  if IsConverted(osu) && p.Ignore: 拒绝
  if !canBeConverted: UpdateProgress(false); return

  seed = p.SeedText 为空 ? Guid.NewGuid().GetHashCode()
                        : BitConverter.ToInt32(SHA256(UTF8(p.SeedText)), 0)
  Rng = new Random(seed)
  keys = (int)osu.General.CircleSize
  newObjects = []; oldObjects = osu.HitObjects; originalLNObjects = []

  # ── 分支 A：Level == -3（Hold off）──
  if (int)p.LevelValue == -3:
      for i in 0..osu.HitObjects.Count-1:                 # 1) 先把所有 LN 直接变 note
         if osu.HitObjects[i].IsLN:
             obj = osu.HitObjects[i]; obj.EndTime = osu.HitObjects[i].StartTime
             osu.HitObjects[i] = obj
      cols = min((int)p.ColumnValue, keys)
      randomSet = SelectRandom(0..keys-1, Rng, cols == 0 ? keys : cols).ToHashSet()
      gap = (int)p.GapValue
      foreach timeGroup in oldObjects.GroupBy(StartTime): # 2) 再按百分比"再 ToNote"（实际又是 no-op）
         foreach note in timeGroup:
             if note.Column ∈ randomSet && Rng.Next(100) < p.Percentage: newObjects.Add(note.ToNote())
             else                                                      : newObjects.Add(note)
         gap--
         if gap <= 0: randomSet = SelectRandom(0..keys-1, Rng, cols).ToHashSet(); gap = (int)p.GapValue
         # ⚠️ cols==0 时这里抽 0 个 → 空集合（后续组不再命中）
      osu.HitObjects = newObjects
      osu.General.OverallDifficulty = p.OverallDifficulty
      osu.Metadata.Difficulty = Insert(0, $"[LN-Lv{p.LevelValue:N0}-C{p.ColumnValue:N0}]")
      作者改写；osu.WriteFile(); UpdateProgress(); return

  # ── 分支 B：Level == -2 / -1 / 0 / 1..9 / 10 ──
  foreach column in osu.HitObjects.GroupBy(h => h.Column):     # 按列（顺序=列在文件里首次出现的顺序）
     switch (int)p.LevelValue:
       case -2: TrueRandom(newObjects, Rng, column, p.OriginalLN, p.Percentage)   # 返回值被丢弃
       case 10: originalLNObjects = Invert(osu, newObjects, Rng, column, p.Divide, p.OriginalLN, p.Percentage, p.FixError)
       default:
          # 计算 mu / sigmaDivisor
          if Level == -1:        mu = -1; sigmaDivisor = 1
          elif Level == 0:       mu = 1;  sigmaDivisor = 100
          else:                  mu = Level * 11; sigmaDivisor = 0.85
                                 (Level==8 → sigmaDivisor=0.9; Level==9 → sigmaDivisor=1)
          originalLNObjects = Transform(Rng, mu, sigmaDivisor, p.Divide, osu, newObjects, column,
                                        p.OriginalLN, p.Percentage, p.FixError)
          # ⚠️ originalLNObjects 每次循环被覆盖 → 最终只剩最后一列的原始 LN

  newObjects = newObjects.OrderBy(StartTime)
  originalLNObjects = originalLNObjects.OrderBy(StartTime)
  AfterTransform(newObjects, originalLNObjects, osu, Rng, (int)p.ColumnValue, p.OriginalLN, (int)p.GapValue)

  osu.General.OverallDifficulty = p.OverallDifficulty          # ← 强制 OD
  columnNum = min((int)p.ColumnValue, keys)
  osu.Metadata.Difficulty = Insert(0,
        columnNum != 0 ? $"[LN-Lv{p.LevelValue:N0}-C{columnNum:N0}]" : $"[LN-Lv{p.LevelValue:N0}]")
  作者改写（同 KeyConverter）；osu.WriteFile(); UpdateProgress()
```

### 4.5 模式总表（14 档，全部列出）

| Level | tooltip 名 | 使用的子程序 | 每个「非最后音符」的时长算法 | 备注 |
| --- | --- | --- | --- | --- |
| **-3** | Hold off | 直接转 Note | 无时长：全部 LN → Note | ⚠️ 实际就是"把谱面所有 LN 压成 note"，`Column/Gap/Percentage` 全部无效（§4.6） |
| **-2** | Random ms | `TrueRandom` | `duration = Rng.Next((int)fullDuration) + Rng.NextDouble()`（毫秒级随机；超过 `fullDuration` 就 `duration--`） | ⚠️ 每列**最后一个音符被丢掉**；不吸附网格、不 Fix Error |
| **-1** | Random divide | `Transform`（`mu=-1`） | `duration = rdtime * timeDivide`，`rdtime = Rng.Next(1, round(full/timeDivide))`；≥1 格、<全长 | 吸附到 1/Divide 网格 |
| **0** | Regular LN | `Transform`（`mu=1, sigmaDivisor=100`） | 均值 = `fullDuration*1%` + 高斯 `σ=timeDivide/100`，`TimeRound` 到网格，再夹到 `[timeDivide, full-timeDivide]` | 多数情况下得到「1 格短 LN」 |
| **1–9** | Light/Medium/Dense（3/5/7） | `Transform`（`mu=Level*11, sigmaDivisor=0.85`；8→0.9、9→1） | 均值 = `fullDuration * Level * 11%` + 高斯 `σ=timeDivide/sigmaDivisor`（≈1.18 格），`TimeRound` 到网格，再夹取 | Level 越高 LN 越长、越密 |
| **10** | Full LN | `Invert` | `duration = fullDuration - beatLength/Divide`，夹到 `[beatLength/Divide, ...]`；若 `duration > fullDuration-3` 则本音符不转 LN | 近似"从本音符按到下一个音符前一格" |

说明：
- 「完整流程」都还有两层过滤：① `Rng.Next(100) < Percentage`（不中 → 变回普通 note）；
  ② `AfterTransform` 的轨道随机集合（不在集合里的 LN → 变回 note）。所以最终 LN 数量 ≤ 百分比。
- `OriginalLN` 勾选时：**原始就是 LN 的音符保持原样**（`Transform`/`Invert` 里会跳过重新生成），
  但受第 ② 层和「只记录了最后一列」的 bug 影响（§4.14）。
- `Fix Error` 只对 `Transform`/`Invert` 生效（-2 不用，-3 无用）。

### 4.6 Level -3（Hold off）——⚠️ 参数全废

代码实际做的：先把 `osu.HitObjects` 里**所有** `IsLN` 的音符 `EndTime = StartTime`（变 note），
然后循环里 `note.ToNote()` 和 `note` 已经没有区别 → **Column / Gap / Percentage / Divide / Fix Error 都不影响结果**。
唯一有效的是：OD 覆盖、Creator、Difficulty 前缀（且 -3 的分支**不判断 Column 是否 0**，总是写 `-C{ColumnValue}`）。
随机的 `SelectRandom` 仍会消耗随机数、`gap` 仍会变化，但只影响 RNG 状态，不影响输出。移植时建议**重新定义**：
要么做成真正的 Hold off（只压选中的轨道、按百分比），要么直接删掉这一档。

### 4.7 Level -2（Random ms）：`TrueRandom`（`:575-616`）

```
TrueRandom(newObjects, Rng, column, originalLNIsChecked, percentageValue):
  locations = column.OrderBy(StartTime).ToList()
  newColumnObjects = []; originalLNObjects = []
  for i in 0 .. locations.Count-2:                      # ⚠️ 没有处理最后一个音符
     fullDuration = locations[i+1].StartTime - locations[i].StartTime
     duration = Rng.Next((int)fullDuration) + Rng.NextDouble()    # 0 .. fullDuration+1 的实数
     while duration > fullDuration: duration--
     obj = locations[i]; obj.Column = column.Key
     if OriginalLN && locations[i].StartTime != locations[i].EndTime:
         newColumnObjects.Add(obj); originalLNObjects.Add(obj)
     else if Rng.Next(100) < percentageValue:
         obj.EndTime = obj.StartTime + (int)duration
         newColumnObjects.Add(obj)
     else:
         newColumnObjects.Add(obj.ToNote())
  newObjects.AddRange(newColumnObjects)
  return originalLNObjects
```
⚠️ 该子程序**没有把每列最后一个音符加入结果**（`Transform`/`Invert` 都有最后音符处理，只有它漏了）→
每列最后一个音符**从谱面消失**。另外它的返回值在 `ApplyToBeatmap` 里被丢弃（`:300`），
所以 `AfterTransform` 拿到的 `originalLNObjects` 是**空的** → 勾了 OriginalLN 也保不住（还会被轨道集合二次过滤）。

### 4.8 Level -1 / 0 / 1–9：`Transform` + `GetDurationByDistribution`（`:396-502`）

`Transform`（`:396-446`）流程：

```
Transform(Rng, mu, sigmaDivisor, divide, osu, newObjects, column, originalLNIsChecked, percentageValue, fixErrorIsChecked):
  locations = column.OrderBy(StartTime).ToList()
  for i in 0 .. locations.Count-2:
     fullDuration = locations[i+1].StartTime - locations[i].StartTime
     duration = GetDurationByDistribution(Rng, osu, locations[i].StartTime, fullDuration, mu, sigmaDivisor, divide)
                # ⚠️ 即使勾了 OriginalLN 也会先算 duration（消耗随机数）
     obj = locations[i]; obj.Column = column.Key
     if originalLNIsChecked && locations[i].StartTime != locations[i].EndTime:
        newColumnObjects.Add(obj); originalLNObjects.Add(obj)          # 原 LN 不动
     else if Rng.Next(100) < percentageValue && !double.IsNaN(duration):
        endTime = obj.StartTime + duration
        if fixErrorIsChecked:
            point = osu.TimingPointAt(endTime)
            endTime = Helper.PreciseTime(endTime, point.BeatLength, point.Time)   # 吸附到该红点网格
        obj.EndTime = (int)endTime
        newColumnObjects.Add(obj)
     else:
        newColumnObjects.Add(obj.ToNote())                             # 不中百分比 / 放不下 → note
  # 最后音符：
  if |last.StartTime - last.EndTime| <= ERROR(2) || Rng.Next(100) >= percentageValue:
     newColumnObjects.Add(last.ToNote())
  else:
     newColumnObjects.Add(last)                                        # 保留原 LN 原样（不重算长度）
  newObjects.AddRange(newColumnObjects)
  return originalLNObjects
```

`GetDurationByDistribution`（`:448-502`）——**所有模式的时长核心**：

```
GetDurationByDistribution(Rng, osu, startTime, limitDuration /*=fullDuration*/, mu, sigmaDivisor, divide):
  beatLength = osu.TimingPointAt(startTime).BeatLength       # 红点 beatLength（ms/拍）
  timeDivide = beatLength / divide                            # 一格的毫秒数
  flag = true
  sigma = timeDivide / sigmaDivisor
  timenum = (int)Math.Round(limitDuration / timeDivide)
  duration = TimeRound(timeDivide, RandDistribution(Rng, limitDuration * mu / 100, sigma))
       # RandDistribution：d<=0 直接返回 u；否则 Box-Muller 正态（均 u、标准差 sigma）

  if mu == -1:                                                # Level -1 专用覆盖
     if timenum < 1: duration = timeDivide
     else: rdtime = Rng.Next(1, timenum); duration = rdtime * timeDivide; duration = TimeRound(timeDivide, duration)

  if duration > limitDuration - timeDivide:                   # 尾部至少留一格
     duration = limitDuration - timeDivide; duration = TimeRound(timeDivide, duration)
  if duration <= timeDivide: duration = timeDivide            # 至少一格
  if duration >= limitDuration - ERROR(2): flag = false       # 放不下 → 变 note
  return flag ? duration : double.NaN
```

数值直觉（例子，Divide=4、120BPM → `beatLength=500ms`、`timeDivide=125ms`）：

| Level | mu | sigmaDivisor | 相邻音符间隔 full | 期望 duration | 实际（TimeRound+夹取后） |
| --- | --- | --- | --- | --- | --- |
| 0 | 1 | 100 | 250ms | 2.5ms ± 1.25ms | 0 → 夹到 125ms |
| 3 | 33 | 0.85 | 250ms | 82.5ms ± 147ms | 夹到 [125,125] → 125ms |
| 5 | 55 | 0.85 | 500ms | 275ms ± 147ms | 250ms（1/2 拍） |
| 9 | 99 | 1.0 | 1000ms | 990ms ± 125ms | 875ms（7 格） |
| 10 | — | — | 1000ms | 1000 - 125 = 875ms | 875ms |

要点：
- `TimeRound(td, num)`：`r = num % td`；`r < td/2 ? num-r : num + td - r`（就近取整到 `td` 的整数倍；负数也会被取到负网格，随后被夹成 `td`）。
- 高斯由 `d` 控制：`d = timeDivide/sigmaDivisor`（-1 档为 `timeDivide`；0 档为 `timeDivide/100`；1–9 档约 1.18 格）。
- **BPM/offset 关系**：网格只由 **`TimingPointAt(startTime).BeatLength`（当前红点）** 决定，与谱面的 `offset`（红点 Time）**无关**
  （`TimeRound` 是相对 0 的等距网格）；`PreciseTime` 才用 offset（`point.Time`）。变速谱面按相邻音符起点各自的 beatLength 分段处理。
- 精度：LN 尾时间最终 `(int)` 截断到毫秒；`Fix Error` 只做 ±2ms 的"就近吸附"，不会改变 BPM 网格。

### 4.9 Level 10（Full LN）：`Invert`（`:504-573`）

```
Invert(osu, newObjects, Rng, column, divideValue, originalLNIsChecked, percentageValue, fixErrorIsChecked):
  locations = column.OrderBy(StartTime).ToList()
  for i in 0 .. count-2:
     fullDuration = next.StartTime - cur.StartTime
     beatLength = osu.TimingPointAt(next.StartTime).BeatLength       # ← 注意用"下一个音符"处的 BPM
     flag = true
     duration = fullDuration - beatLength / divideValue              # 留一格
     if duration < beatLength / divideValue: duration = beatLength / divideValue
     if duration > fullDuration - 3: flag = false                    # 放不下（差 3ms 也判失败）
     obj = cur; obj.Column = column.Key
     if OriginalLN && cur 是 LN: 保留原对象（同时记录到 originalLNObjects）
     else if Rng.Next(100) < percentageValue && flag:
         endTime = cur.StartTime + duration
         if fixErrorIsChecked:
             endTime = Helper.PreciseTime(endTime, osu.TimingPointAt(endTime).BeatLength,
                                          osu.TimingPoints.First().Time)   # ⚠️ offset 用"第一个时间点"而非该红点
         obj.EndTime = (int)endTime; add
     else: add cur.ToNote()
  # 最后一个音符：原 LN 且 OriginalLN → 保留；否则 ToNote
  newObjects.AddRange(...)
```
- `duration` 是**小数**（不 `TimeRound`）；不勾 Fix Error 时尾时间可能不在任何网格上；勾了也只是 ±2ms 吸附。
- 用 `TimingPointAt(next.StartTime)` 取 BPM 是作者注释里说的"hold note 末尾的 BPM"；变速处与 `Transform` 取法不同（后者用起点）。

### 4.10 最后一道筛子：`AfterTransform`（`:618-660`）

对所有模式（-3 除外）最终都调用它，作用 = **只让随机选中的轨道保留 LN，其它 LN 全压回 note**：

```
AfterTransform(afterObjects, originalLNObjects, osu, Rng, transformColumnNum, originalLNIsChecked, gapValue):
  originalLNSet = HashSet(originalLNObjects)        # struct 值相等：字段完全相同才 Contains
  keys = (int)osu.CircleSize
  if transformColumnNum > keys: transformColumnNum = keys
  maxGap = gapValue; gap = maxGap
  randomSet = SelectRandom(0..keys-1, Rng, transformColumnNum == 0 ? keys : transformColumnNum).ToHashSet()
  foreach timeGroup in afterObjects.OrderBy(StartTime).GroupBy(StartTime):
     foreach note in timeGroup:
        if note ∈ originalLNSet && originalLNIsChecked: add note                       # 原 LN 保留
        else if note.Column ∈ randomSet && note.StartTime != note.EndTime: add note   # 被选中的 LN 保留
        else: add note.ToNote()                                                        # 其它 LN 压成 note
     gap--
     if gap == 0:                       # ⚠️ 用 ==0，所以 gapValue==0 时永不重抽
        randomSet = SelectRandom(0..keys-1, Rng, transformColumnNum).ToHashSet()       # ⚠️ 0 → 空集合
        gap = maxGap
  osu.HitObjects = resultObjects.OrderBy(StartTime).ToList()
```

语义：
- `transformColumnNum == 0` → 第一次集合 = 全部轨道（`keys` 个）；但**第一次重抽（每 Gap 组）会变成空集合** →
  之后所有 LN 都被压成 note（除了 originalLNSet 里的）；这是 bug（`:654`）。
- `GapValue` 数的是「不同的 StartTime 组的个数」：每处理完一个时间点 `gap--`，到 0 重抽。
  `Gap=12` 默认 → 每 12 个时间点换一次轨道集合；`Gap=0` → 永不重抽（等价于整张谱用同一套随机轨道，若 Column>0）。
- `originalLNSet` 只在 `Transform`/`Invert` 路径有内容，而且**只有最后一列**（`originalLNObjects` 被覆盖）；
  -2 路径根本没有内容。所以「Original LN」实际效果 ≈ "只在最后一列（且没被轨道筛掉时）保留"。

### 4.11 数学工具

```
RandDistribution(Rng, u, d):                 # :662-674
  if d <= 0: return u
  u1 = Rng.NextDouble(); u2 = Rng.NextDouble()
  z = sqrt(-2*ln(u1)) * sin(2π*u2)           # Box-Muller 正态
  return u + d*z

TimeRound(timeDivide, num):                   # :676-682
  r = num % timeDivide
  return r < timeDivide/2 ? num - r : num + timeDivide - r

Helper.PreciseTime(time, beatLength, offset): # Helper.cs:266-278
  foreach t in [2,4,8,3,6,9,5,7,12,16,48,35,64]:      # 先粗后细
     tem = time
     time = offset + round((time-offset) / (beatLength/t)) * (beatLength/t)
     if |time - tem| < 2.0: return time
     else: time = tem
  return time
```
`PreciseTime` 是「按优先分母顺序找第一个能让时间落进 ±2ms 网格的分母」；例如 120BPM 的 1/4 网格 `beatLength/4 = 125ms`。
`Helper.ERROR = 2.0` 与 `LNTransformer.ERROR = 2.0` 是同一个值的两份拷贝。

### 4.12 元数据 / 写回 / IsConverted

- `General.OverallDifficulty` = UI 文本框值（**默认 0**）。
- `Metadata.Difficulty` 前缀：
  - 主分支：`columnNum = min(ColumnValue, keys)`；`columnNum != 0` → `"[LN-Lv{Level:N0}-C{columnNum:N0}]"`；
    `columnNum == 0` → `"[LN-Lv{Level:N0}]"`（`N0` 是千分位格式，负号正常，如 `-2`）。
  - -3 分支：总是 `"[LN-Lv{LevelValue:N0}-C{ColumnValue:N0}]"`（不判断 0）。
- `Metadata.Creator`：勾选且非空 → 替换；否则追加 `" & LNTransformer"`。
- `IsConverted(osu)`（`:684-696`）：`Creator.Contains("LNTransformer")` **或** `Regex.IsMatch(Difficulty, @"\[LN.*\]")`；
  ⚠️ `\[LN.*\]` 会误判任何名字里带 `[LN...]` 的普通谱面（例如版本名 `[LN Version]`）。
- 写回行为同 §2.6；原文件不动、新文件按元数据命名、可被后续转换再次读入（Ignore 默认勾选可避免重复转）。

### 4.13 进度 / 异常 / 取消

与 KeyConverter 完全相同的实现（两份代码是复制的）：`UpdateProgress` 语义、per-file `try/catch`、
DEBUG MessageBox、无取消、无完成提示、切页继续、并发 drop 共享计数、`e` 在后台线程被读。

### 4.14 已知缺陷 / 奇怪行为（LNTransformer）

| # | 缺陷 | 证据 | 影响/建议 |
| --- | --- | --- | --- |
| L1 | **Level -3 的 Percentage/Column/Gap 全部无效**（先把 LN 全变 note，再 ToNote 是 no-op） | `:233-291` | 新实现要么重定义，要么删除该档 |
| L2 | **Level -2 丢掉每列最后一个音符** | `:582-611` | 数据丢失，必须修 |
| L3 | `originalLNObjects` 每列被覆盖 → 只有最后一列的原始 LN 被记录 | `:331`、`:336` | OriginalLN 基本失效；应用 `AddRange` |
| L4 | `TrueRandom` 返回值被丢弃 → -2 档 OriginalLN 完全失效 | `:300` | 同上 |
| L5 | `AfterTransform` 重抽时 `SelectRandom(..., 0)` 返回空集合 → Column=0 时后续 LN 全被压平 | `:654` | 应写成 `transformColumnNum == 0 ? keys : transformColumnNum` |
| L6 | -3 分支重抽同样用 `transformColumnNum`（0 → 空集合），与主线不一致 | `:271` | 同上（虽然 -3 已无效） |
| L7 | `Invert` 的 Fix Error offset 用 `TimingPoints.First().Time`，不是该红点的 `Time` | `:543` | 多红点谱面吸附错位；应统一 |
| L8 | `AfterTransform` 用 `gap == 0` 判断重抽 → GapValue=0 时永不重抽（主线），而 -3 分支是 `gap <= 0`（每时间点重抽） | `:652`、`:269` | 语义不一致；建议统一为"每 N 个时间点"并允许 0=不重抽 |
| L9 | `Transform` 的最后一个音符：若它是原 LN 且百分比命中，**保留原长度**而不是重新生成 | `:432-441` | 谱尾行为与其它音符不一致 |
| L10 | `Transform`/`Invert`/`TrueRandom` 里 `obj.Column = column.Key` 会把 `X` 规范化为 `KeyX[keys][col]`；最后一个音符有的路径没做 | 各分支 | 输出 X 不一致（不影响声音，影响文件字节） |
| L11 | `GetDurationByDistribution` 的 `mu1Dmu2 / divide2 / mu2` 分支从未被调用（死代码） | `:448-469` | 重写时删掉 |
| L12 | `FixErrorAfterTransform` 整个方法**从未被调用**（调用处被注释） | `:347`、`:378-391` | 死代码 |
| L13 | `Transform` 里 `offset` 计算后未使用 | `:404` | 死代码 |
| L14 | **OD 默认写 0**（文本框默认 `"0"`，无校验、无联动） | `:107`、`:349` | 很容易把用户谱面 OD 改坏；新 UI 应默认取原 OD |
| L15 | `double.Parse(OverallDifficulty.Text)` 在 `Dispatcher.Invoke` 内 → 非法输入会抛到后台 Task（未观察）→ **整批转换静默中止** | `:107` | 新实现应校验输入并给出提示 |
| L16 | `IsConverted` 正则误判 + 只认 Creator 字符串 | `:684-696` | 新实现建议在文件里写自定义标记（如 `[YLT]`）或记录转换历史 |
| L17 | `TimingPointAt` 每次调用线性扫红点；长谱面 `Transform` 每音符调两次 → O(音符数×红点数) | `:451`、`:420`、`:516` | 重写时建红点索引/二分 |
| L18 | 1k/2k 谱面 `Column` setter 崩溃 | `OsuFileV14.cs:890` | 同 K1 |
| L19 | 多 BPM 时 `Transform` 用起点 BPM、`Invert` 用下个音符 BPM，同一首歌不同档的"网格感"不一致 | §4.8/§4.9 | 新实现先定义清楚（建议"以本音符起点所在红点为准"） |
| L20 | 每个时间点的"横向密度"没考虑：同列相邻音符间隔太小时会算出 `timeDivide` 后被夹到 1 格，等于强制造串 | `:491-494` | 可加"间隔不足则跳过"选项 |
| L21 | `RandDistribution` 的 `u1=0`（概率 2^-53）会产生 NaN → 该音符变 note；`NextDouble` 不会返回 1 | `:669` | 理论边界，注明即可 |
| L22 | `IsConverted` 用正则 `\[LN.*\]` 且 `.*` 贪婪，Difficulty 里任意位置出现 `[LN...]` 都命中 | `:691` | 低风险误判 |

### 4.15 移植建议：给"新 LN 转换器"定义清晰语义

旧实现是「作者自用工具」，很多参数语义靠感觉。新项目若要复刻，建议：

1. **保留**：-2/-1/0/1–9/10 的时长公式（§4.8/§4.9）与 `PreciseTime` 吸附；`Percentage`、`Divide`、`Column`、`Gap` 的作用；
2. **修掉**：L1/L2/L3/L4/L5/L7/L14（见 §8.3）；
3. **重新命名/解释 UI**：旧 tooltip 的 "Light/Medium/Dense" 不够直观，建议改成「LN 占间距的百分比」直观滑条 +
   模式下拉（Hold off / 随机毫秒 / 随机 1/n / 短 LN / 按百分比 / 满 LN），并在预览里显示平均 LN 长度；
4. **默认 OD 取原值**（勾选"覆盖 OD"才写）；
5. **给出「转换前/后统计」**（LN 数、总时长、最大尾偏差），因为旧工具完全无反馈。

---

## 5. LevelCalculator（8k 难度评级表）

> 源文件：`LevelCalculator.xaml`（44 行）、`LevelCalculator.xaml.cs`（130 行）。
> 一句话：**把若干 `.osu` 拖进 DataGrid，显示每张谱的 mania 星数、推算「等级 Level」、OD 和元数据**；
> 纯展示工具，**没有导出、没有保存、没有持久化**。

### 5.1 UI 布局与交互

- 页面窗口大小 `DesiredWidth=1281, DesiredHeight=850`（`:16-17`；四个页面里唯一超宽的，因为 DataGrid 有 7 列）。
- 顶部 65px 留给标题栏；`DataGrid x:Name="MapDataGrid"`（`Margin=10,75,10,65`，`AllowDrop=True`，`Drop=MapDataGrid_Drop`）：
  - `AutoGenerateColumns=False`、`CanUserAddRows=False`、`FontSize=15`、`Style={StaticResource ModernDataGrid}`；
  - 7 列（`LevelCalculator.xaml:15-34`）：

| 列 | 绑定 | 宽度 | 说明 |
| --- | --- | --- | --- |
| `Level` | `MapInfo.Level` | Auto | **唯一带自定义表头样式**的列（LightBlue 底、SlateGray 粗体、居中） |
| `StarRating` | `MapInfo.StarRating` | Auto | 显示 3 位小数 |
| `OD` | `MapInfo.OD` | Auto | 显示 1 位小数 |
| `Title` | `MapInfo.Title` | * | |
| `Difficulty` | `MapInfo.Difficulty` | * | 即 `Version:` |
| `Artist` | `MapInfo.Artist` | * | |
| `Creator` | `MapInfo.Creator` | * | |

- 底部：左下 `BackButton`（`←`）；右下 `TextBlock "⬑Drop .osu File"`（提示）；右侧 `Recalculate` 按钮（170×45）。
- **拖入行为**：拖到 DataGrid 上（`MapDataGrid_Drop` → `ProcessDroppedFiles`），支持文件+文件夹递归，只收 `.osu`；
  对每个文件 `new OsuFileV14(path)` → 加一行；**不去重**（同一文件拖两次两行）；**不检查 Mode**（osu!std/taiko/catch 谱也会被当成 mania 算 SR，结果无意义）；
- **没有任何交互**：不能选择/删除行、不能排序、不能清空、不能导出 CSV/JSON、关闭即丢。

### 5.2 星数来源（关键链路）

```
MapDataGrid_Drop (UI 线程)
  → Task.Run → Helper.GetDroppedFiles(...)                     // 后台递归
     → 每找到一个文件：Dispatcher.Invoke(() => {               // ← 注意：解析+算 SR 全在 UI 线程！
            osu = new OsuFileV14(filePath)
            row = { StarRating = Round(osu.StarRating, 3),
                    Level     = Round(CalculateLevel(osu.StarRating), 2),
                    OD        = Round(osu.General.OverallDifficulty, 1),
                    Title/Difficulty/Artist/Creator = osu.Metadata.* }
            MapList.Add(row)
        })
```

- `OsuFileV14(string)` 构造函数（`OsuFileV14.cs:350-381`）会：
  1. `OsuFileProcessor.ReadFile`（内层构造**已经算过一次 SR**）；
  2. 只拷贝 `HitObjects/TimingPoints/Metadata/General`（漏 `Colours/Events`）；
  3. 重算 `actualColumn`；
  4. **再算一次 SR**：`StarRating = StarRatingCalculator.CalculateStarRating(HitObjects, General.OverallDifficulty, (int)General.CircleSize, 1.0)`（`:369`）。
  → 一次拖入 = **每张谱算 2 次 875 行 SR**，并且都在 UI 线程上（`Dispatcher.Invoke` 内），大量拖入会卡死界面。
- 星数算法细节见第 6 章；**Level 公式的系数是针对这套 SR 拟合的**，所以想复刻 Level 必须原样复刻 SR（不能换别的 SR 算法）。

### 5.3 Level 公式（`LevelCalculator.xaml.cs:13-118`）

常量：

```
LOWER_BOUND = 2.76257856739498
UPPER_BOUND = 10.5541834716376
```

三段分段函数（`CalculateLevel`，`:94-112`）：

```
if SR >= LOWER_BOUND && SR <= UPPER_BOUND:   Level = 6.9615 * e^(0.1374 * SR)          // 主拟合段
else if SR < LOWER_BOUND && SR > 0:          Level = 3.6198 * SR                          // 低星线性段
else if SR > UPPER_BOUND && SR < 12.3456789: Level = 2.791 * SR + 0.5436                 // 高星线性段
else:                                        Level = double.NaN                          // SR<=0 或 SR>=12.3456789
```

数值表（可直接当自检基准；`Round(...,2)` 为界面显示值）：

| SR | Level | SR | Level | SR | Level |
| --- | --- | --- | --- | --- | --- |
| 0 | **NaN** | 4 | 12.06 | 8 | 20.90 |
| 0.5 | 1.81 | 5 | 13.84 | 9 | 23.97 |
| 1 | 3.62 | 6 | 15.88 | 10 | 27.51 |
| 2 | 7.24 | 7 | 18.21 | 10.5541834716376 | 29.68 |
| 2.76257856739498 | 10.18 | — | — | 12.3456789 | **NaN** |

细节与坑：
- **边界处有约 0.18–0.32 的不连续**：低星段在 `LOWER_BOUND` 处 = `3.6198×2.76258 ≈ 10.000`，而主拟合段在边界 = `6.9615·e^(0.1374×2.76258) ≈ 10.175`（代码先判主段，所以显示 10.18）；
  高星段在 `UPPER_BOUND` 处 = `2.791×10.55418+0.5436 ≈ 30.000`，主拟合段 = 29.68。**这是 as-is 行为，复刻 UI 时数值会一致**。
- 计算用**未四舍五入的 SR**；界面显示 `SR` 保留 3 位、`Level` 保留 2 位。
- `Recalculate` 按钮（`:120-128`）用**已四舍五入到 3 位的 SR** 重算 Level → 与初次显示可能有 ±0.01 的差异（精度丢失）。
- `MapInfo` 是 **struct**（`:28-37`），放进 `ObservableCollection<MapInfo>`：`MapList[i] = mapInfo` 替换整项刷新；
  DataGrid 若允许编辑单元格会改到 boxed 副本（实际上没有编辑需求）。
- **SR 的边界**会直接变成 `NaN`：10k+ 谱面（SR 算法只支持 ≤10 键，异常被吞 → SR=0）→ Level 显示 `NaN`；
  空谱面 / 0 音符 → 同样 NaN。界面没有提示，用户会看到 "NaN"。
- 公式的来源：旧 README 称该工具为 "8k Level Rating Tool"，Level 是社区自定义的「等级」经验拟合，
  **不是 osu! 官方数值**（官方只有 SR/pp）；系数是作者自己拟合的，新工具若沿用需要在文档里说明。

### 5.4 导入 / 导出 / 持久化

| 项 | 旧行为 |
| --- | --- |
| 导入 | 拖 `.osu` 文件/文件夹 → 一行/文件；只按扩展名过滤；**不校验 Mode / 版本** |
| 导出 | **无**（没有保存 CSV/JSON/剪贴板，也没有截图） |
| 持久化 | **无**（关程序即清空） |
| 刷新 | `Recalculate` 用当前列表里存的（3 位小数的）SR 重算 Level |
| 删除/清空 | **无** |

### 5.5 缺陷（LevelCalculator）

| # | 缺陷 | 证据 | 影响/建议 |
| --- | --- | --- | --- |
| C1 | 解析 + 2 次 SR 都在 UI 线程（`Dispatcher.Invoke` 里） | `:63-90` | 拖入多张就假死；新实现应后台算、进度条、批量取消 |
| C2 | 每张谱算 2 次 SR（`OsuFileV14(string)` 内层已算一次） | `OsuFileV14.cs:350-381` | 性能减半；新解析器应惰性算 SR |
| C3 | 不校验 `Mode`，std/taiko 谱也算 mania SR | `:67` | 结果无意义；新实现应提示"仅 mania" |
| C4 | 10k+ / 空谱面 SR=0 → Level 显示 `NaN`，无提示 | §5.3 | UI 显示"—"并标注原因 |
| C5 | `MapInfo` 是 struct + 无 `INotifyPropertyChanged` | `:28-37` | 新项目用 class/record + 只读视图 |
| C6 | `Recalculate` 用 3 位小数的 SR 重算，和初始值不完全一致 | `:120-128` | 保存/重算应统一用原始 double |
| C7 | 无导出/排序/删除/持久化 | §5.4 | 新工具建议至少支持导出 CSV、清空、按列排序 |
| C8 | 窗口 1281 宽，小屏（1024）放不下 | `:16` | 新项目按内容自适应/可滚动 |
| C9 | 异常静默（Release） | `:80-88` | 应显示失败原因列表 |

---

## 6. StarRating.cs（mania 星数计算）

> 源文件：`StarRating.cs`（875 行）。这是整套移植里**最重、最难验证**的一块。

### 6.1 它是从哪来的？（结论）

- **不是 osu!lazer 的源码直接拷贝**。文件里没有 osu!lazer 的命名空间/类型（`DifficultyCalculator`、`Skill`、`Mods` 都没有），
  所有算法被压成 `StarRatingCalculator` 的**一堆静态方法 + 中文注释**；`StarRating.cs:13` 的注释直接写着：
  `// return StarRatingRebirthCalculateForLazer.CalculateStarRatingForLazer(objects, od, keys, rate);`
- 结合名字与公式，它是 **osu!mania 社区「Star Rating Rebirth」类 SR 重算工具**的 C# 移植
  （第三方 Python 项目，例如 [sunnyxxy/Star-Rating-Rebirth](https://github.com/sunnyxxy/Star-Rating-Rebirth)，
  发布物叫 `srcalc`）。算法体系与 osu!lazer 的 mania 难度计算同源（x/p/a/r/j/c/ks/anchor/keyUsage + 0.975 难度系数 +
  分位数加权），但**代码是重写版**，不能按 "lazer 源码" 对待。
- **授权状态**：`StarRating.cs`、`OsuFileV14.cs` 无版权头；`Utils.cs` 有 ppy Pty Ltd MIT 头；
  第三方 Python 项目未在仓库内声明许可。→ 移植前必须确认授权（见 §9 R1）。

### 6.2 入口签名与调用链

```csharp
// StarRating.cs:11
public static double CalculateStarRating(List<ManiaHitObject> objects, double od, int keys, double rate)
```

- `objects`：mania 音符（只用到 `StartTime`、`EndTime`、`Column` 三个成员，经 `EasyObject.FromManiaObjects` 转成 `EasyObject[]`）；
- `od`：`OverallDifficulty`（0–10 的谱面 OD，或用户自定值）；
- `keys`：键数，**算法只支持 1–10**（`crossMatrix[K]`，见 §6.3）；
- `rate`：倍速（mods）。`rate != 1` 时 `EasyObject.FromRate` 会把 `Head/Tail` 整除 `rate`（**原地改数组**）；
- 返回：最终 SR（double，含 0.975 系数）；无 mods、rate 恒 1。

调用点只有两处，都在 `OsuFileV14` 构造函数：`OsuFileV14.cs:336`、`:369`，参数 `(HitObjects, General.OverallDifficulty, (int)General.CircleSize, 1.0)`。
**接口本身是干净的纯函数**（除了 `FromRate` 会改输入），新项目可以直接包成：
`double ManiaStarRating.Calculate(IReadOnlyList<ManiaNote>, double od, int keys)`。

### 6.3 依赖清单（精确到成员）+ 移植配套类型

| 依赖 | 用到的成员 | 说明 |
| --- | --- | --- |
| `ManiaHitObject`（`OsuFileV14.cs:814`） | `StartTime`、`EndTime`（计算属性）、`Column`（getter） | **唯一**用到的 3 个成员（`EasyObject.FromManiaObjects`，`:852-861`）。`EndTime` 内部依赖 `Type/HitSample`，`Column` 返回 `actualColumn` |
| `EasyObject`（`StarRating.cs:837-873`） | 字段 `Head/Tail/Key`、`IsLong`、构造、`FromManiaObjects`、`FromRate` | 与 SR 同文件，建议原样保留（`Head/Tail` 是 int ms） |
| `Utils`（`Utils.cs`，**ppy MIT**） | `SearchSortedLeft`、`SearchSortedRight`、`CumulativeSum`、`QueryCumsum`、`SmoothOnCorners`、`InterpValues`、`StepInterp`、`RescaleHigh`、`FindNextNoteInColumn` | 9 个方法，**全部**是 SR 的底层数学；`FindNextNoteInColumn` 额外依赖 `EasyObject` |
| `System` | `Math`、`Array`、`Enumerable`、`List/Dictionary/HashSet` | 无第三方 |
| 其余 | — | **不依赖** `OsuFileV14` 类、**不依赖** `KeyX`、**不依赖** 文件 IO |

**移植需要的新配套类型（最小集）**：

```csharp
public sealed class ManiaNote            // 替代 ManiaHitObject（SR 视角）
{
    public int Column;      // 0..keys-1
    public int StartTime;   // ms
    public int EndTime;     // ms；普通 note 时 == StartTime
    public bool IsLong => EndTime != StartTime;
}
// 适配：解析器 → ManiaNote（EndTime 由解析器直接给出，不再从 Type/HitSample 反推）
// SR：  StarRatingCalculator.Calculate(List<ManiaHitObject>) 改为 Calculate(IReadOnlyList<ManiaNote>)
//       EasyObject.FromManiaObjects 内部直接用 (StartTime, EndTime, Column)
```

- 若不想改 `StarRating.cs` 的签名，可以保留 `ManiaHitObject` struct（但要连带 `OsuFileV14.KeyX/Column setter` 一起搬，牵连大）；
- 建议：**新项目里把 `Utils.cs` 的 9 个方法复制进 `ManiaSrMath.cs`（保留 MIT 声明），把 `StarRating.cs` 的静态方法改签名后整体搬入**，
  并删掉 `EasyObject.FromRate`（无 mods 需求）或保留。
- `internal static` 的 12 个子函数（`ComputeXbar` 等）改为 `public`/`internal` 便于自检直接断言。

### 6.4 算法总览（按代码顺序）

> 记号：`x` 是 OD 换算的「时间尺度」（越小越难）；`K` 键数；`T` 谱面总时长（ms）；
> `noteSeq` 按 `(Head, Key)` 排序的所有音符；`noteSeqByCol` 每列音符；`lnSeq` 所有 LN（按起点）；
> `tailSeq` 所有 LN 按尾时间排序；`baseCorners/aCorners/allCorners` 是时间轴断点。

```
1.  EasyObject 化 + rate 缩放（rate=1 无操作）
2.  PreProcess：
      x = 0.3 * sqrt((64.5 - ceil(od*3)) / 500)
      x = min(x, 0.6*(x-0.09)+0.09)
      按 Head/Key 排序；拆 noteSeqByCol / lnSeq / tailSeq；T = max(最后 head, 最后 LN tail)+1
3.  GetCorners：baseCorners = 所有 head/tail 及其 +501/-499/+1 偏移（截到 [0,T]）；aCorners = head/tail ±1000；
      allCorners = 两者并集
4.  keyUsage = GetKeyUsage(noteSeq,K,T,baseCorners)：每个音符把 [head-150, (LN? tail+150 : head+150)] 覆盖的断点标 true
    activeColumns[i] = 在 baseCorners[i] 处有使用的列（升序）
5.  keyUsage400 = GetKeyUsage400(...)：每音符给 [head, end] 内断点 +3.75+min(dur,1500)/150；
      head 前 400ms / end 后 400ms 内按抛物线 3.75*(1-(dist/400)^2) 衰减
6.  anchor = ComputeAnchor(K, keyUsage400, baseCorners)（按各列 400ms 用量的分布「走步」）
7.  ComputeJbar → deltaKs（每列相邻音符间隔）与 jBar（各列 jack 强度平滑后的加权 5 次均值）
8.  ComputeXbar（相邻两列的交叉/轮指强度，含 crossMatrix 系数）
9.  ComputePbar（流（stream）强度：相邻音符时间差 + LN 体量 + streamBooster）
10. ComputeAbar（同时间点活跃列之间的切换强度；A_step 平滑平均）
11. ComputeRbar（释放/换手强度：LN 尾到下一个头的时间差）
12. ComputeCAndKs（c = 每个断点 ±500ms 内音符数；ks = 该断点活跃列数，最小 1）
13. 在 allCorners 上算 S、T、D：
       term1 = aBar^(3/ks) * min(jBar, 8 + 0.85*jBar)
       term2 = aBar^(2/3) * (0.8*pBar + rBar*35/(c+8))
       s = (0.4*term1^1.5 + 0.6*term2^1.5)^(2/3)
       t = aBar^(3/ks) * xBar / (xBar + s + 1)
       d = 2.7*sqrt(s)*t^1.5 + 0.27*s
14. 每个断点的有效权重 w = c * gap（gap = 相邻断点间距的中点差分）；按 d 升序排序
15. 8 个分位点（0.945/0.935/0.925/0.915 与 0.845/0.835/0.825/0.815）各取一个 d 的平均 = p93/p83
16. 加权 5 次均值 = (Σ d^5*w / Σw)^(1/5)
17. sr = (0.88*p93)*0.25 + (0.94*p83)*0.2 + weightedMean*0.55
18. sr /= 8 * 8         // 代码里 Pow(sr,1)/Pow(8,1)*8，恒等（死代码）
19. totalNotes = noteSeq.Length + 0.5 * Σ min(LN 尾-头, 1000)/200
     sr *= totalNotes/(totalNotes+60)
20. sr = RescaleHigh(sr)  // sr<=9 ? sr : 9+(sr-9)/1.2
     sr *= 0.975          // 难度系数
21. return sr
```

### 6.5 关键常量表（移植必须逐个对上）

| 位置 | 常量/公式 | 值 |
| --- | --- | --- |
| `PreProcess`（`:777`） | `x` | `0.3*sqrt((64.5-ceil(od*3))/500)`，再 `x=min(x, 0.6x+0.036)`（即 `0.6(x-0.09)+0.09`） |
| `GetCorners`（`:736-739`） | base 断点偏移 | `+501`、`-499`、`+1`；两端补 `0`、`T` |
| `GetCorners`（`:758-759`） | a 断点偏移 | `+1000`、`-1000` |
| `GetKeyUsage`（`:711-712`） | 使用窗口 | 头 `-150`，尾/头 `+150`（LN 用 tail） |
| `GetKeyUsage400`（`:665-676`） | 400ms 窗口权重 | 区间内 `3.75+min(dur,1500)/150`；两侧 `3.75-3.75/400²·dist²` |
| `ComputeAnchor`（`:631-647`） | 走步 | `walk/maxWalk` 后 `1+min(a-0.18, 5(a-0.22)^3)` |
| `ComputeJbar`（`:565-566`） | 间隔强度 | `(1/δ)*(1/(δ+0.11*x^0.25))`；`jackNerfer(δ)=1-7e-5*(0.15+|δ-0.08|)^-4` |
| `ComputeJbar`（`:608-609`） | 聚合 | 每列平滑（win 500，sum×0.001）后按 `1/deltaKs` 加权 5 次均值 |
| `ComputeXbar`（`:430-442`） | crossMatrix | 见下（按 K 取行的常数矩阵） |
| `ComputeXbar`（`:487`） | 单列对强度 | `0.16*max(x,δ)^-2`（δ>0） |
| `ComputeXbar`（`:495`） | 跨列折减 | `val *= 1-crossCoeff[k]`（两列都不活跃时） |
| `ComputeXbar`（`:502`） | fastCross | `max(0, 0.4*max(δ, max(0.06,0.75x))^-2 - 80)` |
| `ComputeAbar`（`:274`） | dks | `|Δk0-Δk1| + 0.4*max(0, max(Δk0,Δk1)-0.11)` |
| `ComputeAbar`（`:304-311`） | aStep 折减 | `d<0.02`：`min(0.75+0.5*maxΔ,1)`；`d<0.07`：`min(0.65+5d+0.5*maxΔ,1)`；平滑 win 250 平均 |
| `ComputeRbar`（`:345-348`） | 释放指数 I | `iH=0.001*|tail-head-80|/x`，`iT=0.001*|nextHead-tail-80|/x`，`I=2/(2+e^(-5(iH-0.75))+e^(-5(iT-0.75)))` |
| `ComputeRbar`（`:366-367`） | rStep | `0.08*δ^-0.5*(1/x)*(1+0.8*(I_i+I_{i+1}))` |
| `LNBodies…`（`:384-389`） | LN 体量 | `head+60` 加 1.3；`min(head+120,tail)` 加 -0.3；`tail` 减 1；值 `min(curr, 2.5+0.5*curr)` |
| `ComputePbar`（`:198-202`） | streamBooster | `ratio=7.5/δ`；`160<ratio<360` 时 `1+1.7e-7*(ratio-160)*(ratio-360)^2` |
| `ComputePbar`（`:215`） | 同时刻尖峰 | `1000*(0.02*(4/x-24))^(1/4)` |
| `ComputePbar`（`:233`） | LN 体量项 | `v = 1 + 6*0.001*LNSum(hL,hR)` |
| `ComputePbar`（`:237-244`） | inc | `δ<2x/3` 用 `(δ-x/2)²`，否则用 `(x/6)²`；`(1/δ)*(0.08/x*(1-24/x*...))^(1/4)*max(bVal,v)` |
| `ComputePbar`（`:248`） | 叠加 | `+= min(inc*anchor, max(inc, inc*2-10))`；平滑 win 500 sum×0.001 |
| `ComputeCAndKs`（`:165-172`） | c | `SearchLeft(s+500)-SearchLeft(s-500)`（±500ms 内音符数） |
| `ComputeCAndKs`（`:181-187`） | ks | `max(活跃列数, 1)` |
| 最终（`:115-125`） | 分位点 | `{0.945,0.935,0.925,0.915,0.845,0.835,0.825,0.815}` |
| 最终（`:138`） | 加权 | `0.88*p93*0.25 + 0.94*p83*0.2 + weightedMean*0.55` |
| 最终（`:141-142`） | 音符数惩罚 | `totalNotes=noteSeq.Length+0.5*Σ min(LN长度,1000)/200`；`sr*=totalNotes/(totalNotes+60)` |
| 最终（`:144`） | RescaleHigh | `sr<=9?sr:9+(sr-9)/1.2` |
| 最终（`:145`） | 难度系数 | `sr *= 0.975` |
| 平滑窗口 | pBar/ rBar / jBar / xBar | 500（scale 0.001，sum）；aBar 250（avg） |

`crossMatrix`（`StarRating.cs:430-442`，按 K 取行，K=1..10 有效；K=0/11+ 会越界）：

```
K=1 : [0.075, 0.075]
K=2 : [0.125, 0.05, 0.125]
K=3 : [0.125, 0.125, 0.125, 0.125]
K=4 : [0.175, 0.25, 0.05, 0.25, 0.175]
K=5 : [0.175, 0.25, 0.175, 0.175, 0.25, 0.175]
K=6 : [0.225, 0.35, 0.25, 0.05, 0.25, 0.35, 0.225]
K=7 : [0.225, 0.35, 0.25, 0.225, 0.225, 0.25, 0.35, 0.225]
K=8 : [0.275, 0.45, 0.35, 0.25, 0.05, 0.25, 0.35, 0.45, 0.275]
K=9 : [0.275, 0.45, 0.35, 0.25, 0.275, 0.275, 0.25, 0.35, 0.45, 0.275]
K=10: [0.325, 0.55, 0.45, 0.35, 0.25, 0.05, 0.25, 0.35, 0.45, 0.55, 0.325]
（数组第 0 行是 K=0 的 [-1]，没有键数为 0 的谱，实际用不到）
```

### 6.6 子函数一览（可直接对照移植）

| 函数 | 行号 | 作用 |
| --- | --- | --- |
| `CalculateStarRating` | `:11-148` | 总入口（上表流程） |
| `ComputeCAndKs` | `:150-189` | c（音符密度）/ ks（同时按键数） |
| `ComputePbar` | `:191-255` | 流强度（含 streamBooster、LN 体量、anchor 加权） |
| `ComputeAbar` | `:257-319` | 列间切换强度 |
| `ComputeRbar` | `:321-374` | 释放（换手）强度 |
| `LNBodiesCountSparseRepresentation` | `:376-416` | LN 体量分段稀疏表示 + 累计积分 |
| `ComputeXbar` | `:427-531` | 交叉/轮指强度（crossMatrix） |
| `ComputeJbar` | `:533-611` | 同列连打（jack）强度 + deltaKs |
| `ComputeAnchor` | `:613-650` | 各列使用分布的不均衡度 |
| `GetKeyUsage400` | `:652-679` | 400ms 加权使用量 |
| `LNSum` | `:681-704` | 分段积分查 LN 体量 |
| `GetKeyUsage` | `:706-721` | ±150ms 布尔使用量 |
| `GetCorners` | `:723-771` | 三种时间断点 |
| `PreProcess` | `:773-834` | x/排序/按列拆分/T |
| `EasyObject` | `:837-873` | `Head/Tail/Key/IsLong` + `FromManiaObjects/FromRate` |
| `contains(array,value)` | `:418-426` | 线性查找（`ComputeXbar` 用） |

### 6.7 边界 / 缺陷 / 性能

| # | 问题 | 证据 | 说明 |
| --- | --- | --- | --- |
| S1 | **只支持 1–10 键**：`crossCoeff = crossMatrix[K]` | `:451` | 11k+ 抛 `IndexOutOfRange` → 被 `OsuFileV14` 构造函数 catch → SR=0（Level 显示 NaN） |
| S2 | 空音符表/全无红点等会在 `PreProcess` 或 `GetCorners` 抛异常 | `:781`、`:828` | 同上 → SR=0 |
| S3 | `od` 太大（`ceil(od*3)>64.5`，即 OD>21.5）→ `sqrt(负数)` = NaN → 整个 SR NaN | `:777` | 旧工具 OD 输入来自文件，mania 一般 0–10；新实现应 clamp OD |
| S4 | `rate != 1` 时会**原地改** `EasyObject` 的时间（`FromRate`） | `:863-872` | `OsuFileV14` 恒传 1.0；新接口建议去掉 rate 或做拷贝 |
| S5 | `sr = Pow(sr,1)/Pow(8,1)*8` 是恒等运算（死代码），说明是从 Python 移植时留下的 | `:139` | 可删 |
| S6 | `ComputeCAndKs` 参数 `T` 未使用；`ComputeRbar` 的 `iArr` 赋值后从未读取 | `:150`、`:324`、`:363` | 死代码，可删 |
| S7 | 同一列同头时间的重复音符会让 `delta=0` → `1/δ` = ∞ → 结果可能 NaN | `:564-566`、`:590` | 旧工具转换出的谱可能有重复；新实现应先去重 |
| S8 | `FindNextNoteInColumn` 用 `Array.BinarySearch(Head)`，同头重复时"下一个"可能还是自己 | `Utils.cs:115-123` | 同上，建议先去重 |
| S9 | 无法确认与线上 osu! 的 SR 完全一致：这是第三方 rework 的移植，不同版本公式会差 | — | **必须用旧程序产出基准值**再断言，不要拿"官方 SR"当基准 |
| S10 | 无任何单元测试/基准数据 | 仓库 | 移植最大风险（§9 R2） |

**性能**：主循环基本是 `O(断点数 × K)` + 每个断点多层平滑（每次 `QueryCumsum` 二分），
典型 5 分钟 4k 谱（~3000 音符、断点几千个）应在几十到几百毫秒量级，但**没有实测**；
旧代码在 UI 线程串行算，拖 100 张谱会明显卡顿。新项目建议后台线程 + 进度 + 缓存。

### 6.8 移植难度评估与策略

- **代码量**：`StarRating.cs` 875 行 + `Utils.cs` 125 行 + 一个 `ManiaNote` 适配（~30 行）≈ 1000 行。
- **难点不在写，而在「验证」**：没有基准值，必须先在旧环境跑出若干张谱的 SR 作为 golden 数据
  （或允许时编译旧工程/旧 exe 跑；本次分析按任务要求没有运行任何旧程序）。
- **建议步骤**：
  1. 先搬 `Utils`（9 方法，MIT 头保留）→ 自检（二分/插值/平滑可用小数组断言）；
  2. 搬 `StarRating` 静态方法，`internal` 改 `public`；`ManiaHitObject` → `ManiaNote` 适配；
  3. 对 **1k–10k 各一张** 简单合成谱（手写 2–3 个音符）断言不抛异常 + 结果有限；
  4. 用旧工具对 `testdata/` 里的真实谱面生成 SR 基准表，逐条断言（容差 1e-6 或 3 位小数）；
  5. 性能基准（一张大谱 < 200ms），必要时缓存 `TimingPointAt`、把平滑/插值循环改为数组复用。
- **替代方案（不推荐）**：换成简单公式（如 `SR = f(音符密度)`）——注意 **Level 公式是按这套 SR 拟合的**，
  换 SR 会让 Level 全部失真，除非同时重新拟合 Level；而 Level 本身是社区经验值，没有公开标准。

---

## 7. 粒子效果（窗口级叠加层）

> 源文件：`ParticleEffectManager.cs`（157 行）+ `MainWindow.xaml` 的 `ParticleCanvas` + `MainWindow.xaml.cs` 的事件接线。

### 7.1 架构与坐标系

- `MainWindow.xaml:21`：`<Canvas x:Name="ParticleCanvas" IsHitTestVisible="False" Panel.ZIndex="999"/>`
  —— 放在 `Grid ContentArea` 的**第一个子元素**，靠 `ZIndex=999` 盖在所有 UI（包括 Frame/标题栏/按钮）之上；
  `IsHitTestVisible=False` 保证不挡鼠标（点击穿透到下面的按钮）。
- `Canvas` 没有显式宽高 → 拉伸填满 `Grid`（即整个窗口内容区）。窗口 `WindowStyle=None` + `CaptionHeight=0`，
  所以 **`e.GetPosition(Window)` ≈ `Canvas` 坐标**（没有标题栏偏移）。
- 粒子 = `Ellipse` 直接 `Children.Add` 到 Canvas，`Canvas.SetLeft/SetTop` 定位。
- 坐标系原点是窗口左上角；粒子**不会**随窗口大小变化做裁剪/重定位（Canvas 拉伸，坐标是绝对像素）。

### 7.2 开关状态机与触发时序

两个状态字段（`ParticleEffectManager`）：
- `_particleEffectActivatedByButton`（MainWindow 私有，`:32`）：是否"已激活"；
- `IsParticleEffectEnabled`（`ParticleEffectManager.IsParticleEffectEnabled`）：本帧是否真的画粒子。

| 触发 | 代码 | 效果 |
| --- | --- | --- |
| `MainWindow.ToggleParticleEffect()` | `MainWindow.xaml.cs:188-192` | `activated = !activated; enabled = activated;`（**翻转**） |
| 主页 `Page_Loaded` | `MainPage.xaml.cs:48-54` | 调 `ToggleParticleEffect()` |
| `NavigateToPage`（进入某个工具页） | `MainWindow.xaml.cs:54-177` | **不碰**粒子状态 |
| `NavigateBack`（返回主页） | `MainWindow.xaml.cs:242-356`，`:355` | 结尾调一次 `ToggleParticleEffect()` |
| `Test` 按钮（被 `Collapsed`，不可见） | `MainPage.xaml.cs:23-30` | 调 `ToggleParticleEffect()`（原本用于开关粒子） |
| 鼠标进入窗口 `MouseEnter` | `:194-200` | `if (activated) enabled = true` |
| 鼠标离开窗口 `MouseLeave` | `:202-205` | `enabled = false`（**强制关**，不改变 activated） |
| 鼠标移动 `MouseMove` | `:207-213` | `if (enabled) SetMousePosition(e.GetPosition(this))` |
| 左键按下（隧道 `PreviewMouseLeftButtonDown`） | `:215-226` | `if (enabled) { SetMousePosition; TriggerParticleSpeedBoost(); TriggerParticleCountBoost(); }` |
| 右键按下（隧道 `PreviewMouseRightButtonDown`） | `:228-234` | `if (enabled) TriggerParticleCountBoost();`（只加量，不加速） |
| 每帧渲染 `CompositionTarget.Rendering` | `:236-239` | `UpdateParticles()`（**不管开关都会调**） |

**实际时序**（为什么"粒子几乎一直开着"）：
1. 启动：构造函数先 `NavigateToPage(new MainPage())`（`:39`），之后主页触发 `Loaded` → `Toggle` → `activated=true, enabled=true`；
   所以**启动后粒子就是开的**（此时 `_mousePosition=(0,0)`，粒子在左上角出现，直到第一次鼠标移动）。
2. 进入工具页：不 toggle → 仍开，粒子继续画在工具页上（鼠标移动时跟着走）。
3. 返回主页：`NavigateBack` 先 toggle（→off），随后新主页 `Loaded` 又 toggle（→on）→ **净状态不变**；
   中间动画期间会有一小段关闭（粒子被清空）。
4. 如果以后把 `NavigateBack` 的目标改成非主页（不触发 `Loaded`），则返回后粒子**会永久关掉**。
5. 鼠标离开窗口 → 关；回来 → 若 activated 仍为 true 则重新开。
6. 颜色/速度/数量三个静态参数没有 UI，也没有配置持久化；**测试按钮隐藏后用户无法关闭粒子**。

### 7.3 每帧更新算法（`UpdateParticles`，`:67-132`）

```
UpdateParticles():
  if (!IsParticleEffectEnabled):
     移除所有粒子的 Shape；_particles.Clear(); return

  # 1. 全局速度衰减
  if _particleSpeed > 0.15:
     _particleSpeed -= 0.1
     if _particleSpeed < 0.15: _particleSpeed = 0.15

  # 2. 全局数量衰减：int 截断 → 每帧 -1
  if _particleCount > 1:
     _particleCount = (int)Math.Max(1, _particleCount - 0.5)

  # 3. 每帧在鼠标位置生成 _particleCount 个新粒子
  for i in 0.._particleCount-1: CreateParticle(_mousePosition)

  # 4. 更新/移除已有粒子（倒序遍历）
  for i = _particles.Count-1 .. 0:
     p = _particles[i]; p.Age += 1          # 按帧计龄（不是按毫秒！）
     if p.Age > p.LifeSpan:
         Canvas.Children.Remove(p.Shape); _particles.RemoveAt(i)
     else:
         currentSpeed = p.Speed * _particleSpeed
         x = Canvas.GetLeft(p.Shape) + cos(p.Angle) * currentSpeed
         y = Canvas.GetTop(p.Shape)  + sin(p.Angle) * currentSpeed
         Canvas.SetLeft/SetTop
         progress = p.Age / p.LifeSpan
         p.Shape.Fill = new SolidColorBrush(Color.FromArgb(255*(1-progress), 255,255,255))
         size = 5 * (1 - progress); p.Shape.Width = p.Shape.Height = size

CreateParticle(position):
  shape = new Ellipse { Width=5, Height=5, Fill=SolidColorBrush(White) }
  Canvas.Children.Add(shape)
  Canvas.SetLeft(shape, position.X - 2.5); Canvas.SetTop(shape, position.Y - 2.5)
  _particles.Add({ Shape=shape, Angle=Rng.NextDouble()*2π, Speed=Rng.NextDouble()*2+1,
                   Age=0, LifeSpan=Rng.Next(30, 90) })
```

### 7.4 参数常量与行为数值

| 参数 | 值 | 位置 | 说明 |
| --- | --- | --- | --- |
| `_particleSpeed` 初始 | `1.2` | `:26` | 全局速度倍率 |
| `DefaultSpeed` | `0.15` | `:27` | 衰减下限 |
| `MaxSpeed` | `3` | `:28` | 左键加速目标值 |
| `SpeedDecay` | `0.1` /帧 | `:29` | 从 3 衰减到 0.15 约 29 帧（60fps 下 ≈0.48s） |
| `_particleCount` 初始 | `1` | `:31` | 每帧生成数 |
| `DefaultParticleCount` | `1` | `:32` | 衰减下限 |
| `MaxParticleCount` | `10` | `:33` | 左/右键加量目标 |
| `ParticleCountDecay` | `0.5` /帧 + `(int)` 截断 | `:34`、`:84` | 实际每帧 -1，10→1 约 9 帧 |
| 单个粒子速度 | `Rng.NextDouble()*2+1` ∈ [1,3) | `:151` | 实际位移 = 该值 × 全局速度（默认 1.2 → 1.2–3.6 px/帧） |
| 角度 | `Rng.NextDouble()*2π` | `:150` | 均匀圆周 |
| 寿命 | `Rng.Next(30,90)` ∈ [30,89] 帧 | `:153` | 60fps ≈ 0.5–1.5s |
| 粒子大小 | 5px 起始，线性缩到 0 | `:139`、`:119-121` | |
| 颜色 | 白色，alpha 线性淡出 | `:140`、`:117-118` | 每帧新建 `SolidColorBrush` |
| 生成位置 | 当前鼠标位置（居中偏移 2.5） | `:144-145` | 鼠标没动过时是 `(0,0)`（左上角） |
| 稳态粒子数（默认） | ≈ 1×平均寿命 ≈ 60 个 | — | 加速时 10×30 ≈ 300 个，且刷新率越高越多 |

### 7.5 移植到 WPF（新项目）的注意点

1. **按帧计龄 → 必须改成按时间**：旧实现 `Age += 1`、`Speed` 是 px/帧，导致 **144Hz 屏幕上粒子快 2.4 倍、寿命短 2.4 倍**。
   新实现用 `Stopwatch`/`RenderingEventArgs.RenderingTime` 求 `dt`，速度乘 `dt/16.67`，年龄累加毫秒。
2. **渲染开销**：每个粒子一个 `Ellipse` + 每帧一个 `SolidColorBrush`（还有 `Canvas.GetLeft/SetLeft` 的依赖属性读写、布局失效）。
   几百个粒子时 UI 线程会抖。建议：
   - 用 **一个 `DrawingVisual`/`FrameworkElement.OnRender`** 的 `DrawingContext.DrawEllipse` 画全部粒子（一个视觉对象）；
   - 或 `WriteableBitmap` 直接写像素（最省，但要处理 DPI/清除）；
   - 画笔用 **`Freeze()` 的缓存画刷**（按 alpha 分档），不要每帧 new；
   - 粒子数上限（例如 400）+ 低于阈值时降频生成。
3. **`CompositionTarget.Rendering` 是静态事件**：旧代码 `+=` 后**从不取消订阅**（`MainWindow.xaml.cs:46`），
   窗口关掉后仍持有窗口 → 泄漏。新实现必须在 `Unloaded`/窗口关闭时 `-=`，并在 `IsVisible=false` 时暂停。
4. **坐标**：用 `e.GetPosition(ParticleCanvas)`（相对画布），而不是相对 Window；新项目标题栏高度/DPI 缩放不同，
   相对 Window 会偏。窗口缩放时旧粒子留在原地（旧行为），新实现可选"按画布裁剪"或"随窗口平移"。
5. **命中测试**：Canvas 必须 `IsHitTestVisible=False`；同时新项目的按钮是自定义模板，注意不要被 Canvas 盖住（ZIndex 要大但不抢事件）。
6. **鼠标离开**：旧实现是 `MouseLeave` 直接 `enabled=false`（下一帧清空）。新实现建议**渐隐**而不是瞬清，
   并区分"离开窗口"与"鼠标不动"（旧实现只有移动才更新位置，鼠标停住时粒子在原地持续生成，效果是正确的）。
7. **多显示器/DPI**：WPF 坐标是 DIP，Canvas 自动按 DPI 缩放；若改用位图渲染要按 `VisualTreeHelper.GetDpi` 缩放。
8. **可访问性/性能模式**：建议提供开关（设置项）+ 尊重系统"减少动画"（`SystemParameters.ClientAreaAnimation` 之类），
   不要像旧版一样只能靠隐藏的 Test 按钮。
9. **线程**：`CompositionTarget.Rendering` 在 UI 线程；粒子集合不要跨线程改。
10. **窗口最小化/非激活**：旧实现照样每帧算（浪费）。新实现可在 `Window.Deactivated`/`IsVisibleChanged` 暂停生成，
   回到前台再恢复。

**建议的新实现形态**（给新项目用）：
```
ParticleOverlay : FrameworkElement     // 放在 MainWindow 的 Grid 最上层，IsHitTestVisible=false
   ├─ Enabled (bool, DP)
   ├─ AddBurst(Point p, int count, double speedFactor)   // 左键/右键调用
   ├─ MousePosition 更新
   ├─ 内部 List<Particle>（struct；位置/速度/年龄/寿命）
   ├─ 挂在 CompositionTarget.Rendering（Unloaded 时摘掉；后台暂停）
   └─ OnRender 里 DrawEllipse（冻结画刷按 alpha 分档）
MainWindow：接 MouseMove/PreviewMouseLeftButtonDown/PreviewMouseRightButtonDown/MouseEnter/MouseLeave；
            开关放到 AppSettings 或 shell 配置（例如 "显示粒子效果"），默认开，可在设置里关。
```

---

## 8. 移植决策与拆分建议

### 8.1 新项目落点建议

旧项目 = 3 个独立页面 + 1 个全局粒子层。新项目已有「一工具一卡片」的框架（`ITool`），两种方案：

| 方案 | 结构 | 优点 | 缺点 |
| --- | --- | --- | --- |
| **A. 一个工具「osu!mania 工具箱」**（推荐） | `src/YuLiangToolkit.Tools.OsuMania/`，内部 3 个 Tab（转键 / LN / 评级） | 三个功能共享同一个解析器/设置/拖放区；主页只多一张卡；符合"工具集"定位 | 单 View 较复杂，需要 Tab 样式 |
| B. 三个独立工具 | `Tools.KeyConverter` / `Tools.LnTransformer` / `Tools.LevelCalculator` | 每个都简单、可独立删 | 解析器/拖放/进度代码三份；主页三张卡；与现有 7 工具风格略重复 |

无论哪种，**纯逻辑都建议放在同一个 `Core/` 子目录**：

```
Core/
├─ Mania/  ManiaBeatmapFile.cs      // .osu 读写（UTF-8/BOM、[Events] 原文、Unknown 段保留）
│          ManiaNote.cs            // class：Column/StartTime/EndTime?/HitSound/HitSample
│          ManiaTimingPoint.cs     // class/record
│          ManiaKeyLayout.cs       // KeyX 表 + X↔Column 换算（含 1/2k 兜底）
├─ KeyConverterEngine.cs           // 纯函数：输入 beatmap + 参数 → 输出 beatmap（不碰磁盘）
├─ LnTransformerEngine.cs          // 纯函数；内部 Transform/Invert/TrueRandom/AfterTransform
├─ ManiaStarRating.cs              // SR 计算（纯函数）
├─ ManiaSrMath.cs                  // Utils.cs 的 9 个方法（MIT 声明）
└─ ManiaLevelFormula.cs            // Level 三段公式（纯函数）
```

UI 层用新项目的 `ToolViewModelBase`：
- 拖放：View 的 Drop 事件里**在 UI 线程取路径数组**，再 `Task.Run` 处理（修旧实现的线程问题）；
- 进度/取消：用 `IProgress<T>` + `CancellationToken`（旧版无取消）；
- 设置：把「默认目标键数/Gap/Clean/Blank/Seed/OD 覆盖/粒子开关」存到该工具的 `ToolConfig`（`Config\<toolId>\config.json`）；
- 文件输出：建议**新建到指定目录 + 重名自动编号 + 可选备份原文件**（旧版直接覆盖同目录）。

### 8.2 纯逻辑 vs UI 拆分

| 模块 | 类型 | 可自检 | 说明 |
| --- | --- | --- | --- |
| `.osu` 解析/写回 | 纯逻辑 | ✅ | 给定字符串 → 对象；对象 → 字符串 |
| KeyConverter 映射 | 纯逻辑 | ✅ | 输入 beatmap + 参数 + seed → 新 beatmap；**RNG 需可注入**（`Random` 种子） |
| LN 转换（14 档） | 纯逻辑 | ✅ | 同上 |
| SR 计算 | 纯逻辑 | ✅ | 输入音符数组+OD+keys → double |
| Level 公式 | 纯逻辑 | ✅ | 输入 SR → double（含 NaN 边界） |
| 文件名清洗/重名策略 | 纯逻辑 | ✅ | |
| 进度/取消/串行队列 | 服务 | 部分 | 可 mock 文件 IO |
| 粒子模拟（位置/寿命） | 纯逻辑（可抽 `ParticleSimulator`） | ✅ | 用固定 dt 断言轨迹/淡出 |
| 渲染（Canvas/DrawingVisual） | UI | ❌ | 只做视觉冒烟 |
| 页面布局/主题/动画 | UI | ❌ | 新项目深色主题重画 |

### 8.3 「必须复刻」vs「强烈建议修复」

**必须复刻（否则老用户结果对不上）**：
- KeyConverter：`gap_ms` 公式（§3.3）、`ProcessArea` 的复制/插入/右移/Blank 语义（§3.5）、收尾 50ms/10ms/在 LN 内 三条规则（§3.6）、
  元数据前缀格式、seed 生成方式（空=随机 / SHA256 前 4 字节）。
- LNTransformer：`GetDurationByDistribution` 公式与 `TimeRound`/`RandDistribution`/`PreciseTime`（§4.8/§4.11）、
  各 Level 的 `mu/sigmaDivisor`、`Invert` 的时长、`AfterTransform` 的 `Column/Gap` 语义、元数据前缀格式。
- LevelCalculator：三段公式与边界（§5.3）、`Round(...,2)` 显示。
- StarRating：**整份算法 + 0.975 系数**（否则 Level 全变）。
- 粒子：默认参数与"左键加速+加量、右键加量、离开即关"的手感（§7.4）。

**强烈建议修复（旧 bug/数据风险）**：
- KeyConverter：K4（打击音丢失）、K1（1k→2k 崩溃）、K5（删音符策略应可选）、K10（覆盖无备份）；**另：旧版“目标 > 2×源键数”会死循环**（复核时发现，替代 K2 的位置）。
  （K2「Clean=0 误删」经复核**不成立**：旧版外层已用 `clean > 0` 挡住，见 §3.9 更正。）
- LNTransformer：L2（丢最后音符）、L1/L3/L4/L5/L7（Original LN 与 Column=0 失效）、L14（OD 默认写 0）、L15（OD 非法输入静默中止）。
- 解析器：P1–P7（版本、culture、未知字段、备份、KeyX 越界、TimingPointAt 空红点）。
- LevelCalculator：C1/C3/C4/C6。
- 粒子：按帧→按时间、取消静态事件订阅、性能（§7.5）。

### 8.4 建议的测试/自检清单（新项目风格）

1. **KeyConverter 映射单测**：用 §3.5 的 4k→7k 例子（`copyColumn=[(1,false),(3,false),(0,true)]`、`insertColumn=[1,4,6]`）
   断言每个源列的输出列；再测 blank=0 时无空列、`toKeys=fromKeys` 被拒、2k 目标的处理策略。
2. **收尾合并单测**：同列 40ms 两音 → 只留一个；LN[100,500] + 音符 400 → 音符被删；LN[100,500] + 音符 505 → ≤10ms 尾接 → 音符被删。
3. **seed 复现**：同 seed + 同输入 → 输出字节/音符序列稳定；空 seed → 每次不同。
4. **LN 各档单测**：固定 beatLength（如 500ms/Divide=4）、固定 full=250/500/1000ms，断言各 Level 的时长（按公式手算）；
   断言 -2 的最后音符问题修复后的行为、-3 的语义。
5. **PreciseTime**：`time=126, beatLength=500, offset=0, divide 网格 125` → 返回 125；`time=190` → 不吸附（保持 190 附近）。
6. **Level 公式**：§5.3 的表逐行断言（含边界 NaN）。
7. **SR**：先用旧工具生成 golden SR（1k–10k 各一张），断言误差 <1e-6；再测 OD=0/10、空谱面、11k 抛异常/兜底。
8. **写回**：输出能被本解析器重新读入且关键字段一致；`[Events]` 原文、`[Colours]`、红/绿 timing point 不变（除非工具本来改）。
9. **文件安全**：目标重名自动编号、原文件不被改、可选备份、失败不留半个文件。
10. **粒子**：固定 dt 模拟 100 帧，断言粒子数上限、alpha 单调、寿命移除；冒烟截图（新项目 `YLT_SHOT`）。

---

## 9. 移植风险清单

| # | 风险 | 证据/来源 | 影响 | 建议 |
| --- | --- | --- | --- | --- |
| R1 | **授权不明**：旧仓库无 LICENSE；`Utils.cs` 是 ppy MIT；`StarRating.cs` 源自第三方 rework 且无声明 | §1.1、§1.5、§6.1 | 直接拷代码到新仓库有合规风险 | 移植前确认作者授权；保留 ppy MIT 声明；必要时重写/换算法并注明来源 |
| R2 | **SR 无法验证**（无基准、非官方实现、第三方版本可能漂移） | §6.7 S9/S10 | Level 全部数值都依赖它，错了没人发现 | 先用旧程序产出 golden 数据；把 SR 版本/来源写进工具文档 |
| R3 | 解析器重写工作量大（要保留未知段/编码/culture/备份） | §2.8 | 拖慢工期，或退化成"照抄旧坑" | 先按最小 v14 + 保留原文实现，再逐步补 |
| R4 | **文件覆盖/数据丢失**：旧版无备份、按元数据拼名、同名覆盖、失败无提示 | §2.6 | 用户谱面被覆盖 = 事故 | 新实现默认"另存 + 自动编号 + 可选备份" |
| R5 | **确定性**：随机结果依赖 .NET `Random(seed)` 实现与 RNG 消耗顺序 | §3.5、§4.4 | 与旧版逐字节对比会失败；用户"同 seed 结果应该一样"的预期 | 复刻旧 RNG 消耗顺序，或自实现 RNG；文档说明"新旧版本同 seed 结果可能不同" |
| R6 | **区域设置**：旧版 `double.Parse/ToString` 用当前 culture | §2.6/P3 | 非 zh-CN 环境解析错/写坏文件 | 新实现全部 `InvariantCulture` |
| R7 | **UI 线程卡死**：LevelCalculator 在 `Dispatcher.Invoke` 里算 SR；Key/LN 在后台但 UI 更新频繁 | §5.5 C1、§3.8 | 大批文件卡界面、看起来像崩溃 | 全部后台 + 可取消 + 限频刷新进度 |
| R8 | **线程安全**：`DragEventArgs` 在后台读、计数跨线程无锁、并发 drop | §3.8 | 偶发异常/计数错乱 | UI 线程取路径；`Interlocked`；禁止并发任务或加队列 |
| R9 | **键数覆盖不全**：`KeyX` 缺 1/2/11+，SR 只支持 1–10，KeyConverter 目标 2k 崩 | §2.3、§6.7 S1 | 1k/2k/10k+ 用户无法使用 | 明确支持的键数范围并在 UI 提示；补 2k 表或禁用 |
| R10 | **语义误解**：Level 是作者经验拟合，不是官方；用户可能以为是 osu! 官方等级 | §5.3 | 数值被质疑/误用 | 工具内注明公式/版本/非官方 |
| R11 | **粒子性能/泄漏**：每帧新建对象、静态事件不摘、刷新率耦合 | §7.3、§7.5 | 高刷屏效果过快；关窗泄漏 | 按时间步进 + DrawingVisual + 摘事件 + 上限 |
| R12 | **范围/交互设计**：旧工具无取消/无预览/无失败列表，新项目若照搬会显得难用 | 全文 | 移植完成但体验差 | 按 AGENTS.md 的工具规范补：设置持久化、取消、统计、失败列表 |
| R13 | **旧 bug 被用户当功能**：Clean=0/Original LN 等行为可能有人依赖 | §3.9、§4.14 | "修了 bug"反而被说改坏了 | 修复项写进版本说明；关键行为（如 OD 覆盖）给"保留旧行为"开关 |
| R14 | **无测试素材**：仓库没有 `.osu` 样本/自检 | 仓库 | 回归靠人肉 | 新项目 `testdata/` 放 3–5 张覆盖 4k/7k/LN/变速/10k 的谱面 |
| R15 | **解析器兼容性**：只认 v14 列格式；旧谱/新版本可能失败 | §2.5/P1 | 用户拖入即失败 | 至少给"不支持的原因"提示（版本/列数/缺段） |

---

## 附录 A：关键行号索引

| 主题 | 位置 |
| --- | --- |
| KeyConverter 常量/参数 | `KeyConverter.xaml.cs:14-20` |
| KeyConverter 主流程 | `:167-364` |
| KeyConverter 列映射 `ProcessArea` | `:366-474` |
| KeyConverter 收尾合并 | `:280-337` |
| KeyConverter 说明文案 | `:476-488` |
| LNTransformer 参数结构 | `LNTransformer.xaml.cs:29-44` |
| LNTransformer 主流程 | `:182-376` |
| LNTransformer -3 分支 | `:233-291` |
| LNTransformer `Transform` | `:396-446` |
| LNTransformer `GetDurationByDistribution` | `:448-502` |
| LNTransformer `Invert` | `:504-573` |
| LNTransformer `TrueRandom` | `:575-616` |
| LNTransformer `AfterTransform` | `:618-660` |
| LNTransformer `RandDistribution/TimeRound/IsConverted` | `:662-696` |
| LevelCalculator 公式 | `LevelCalculator.xaml.cs:13-118` |
| LevelCalculator 拖放/DataGrid | `:47-92` |
| OsuFileV14 解析器门面 | `OsuFileV14.cs:7-268` |
| OsuFileV14 类型/字段/KeyX | `:270-314` |
| OsuFileV14 构造函数 | `:316-381` |
| OsuFileV14 `TimingPointAt/ManiaToKeys` | `:383-420` |
| OsuFileV14 `WriteFile` | `:696-787` |
| ManiaHitObject（struct） | `:814-969` |
| Helper `PreciseTime` | `Helper.cs:266-278` |
| Helper `GetDroppedFiles` | `Helper.cs:33-59` |
| Helper `SelectRandom` | `Helper.cs:290-324` |
| Utils 全部数学工具 | `Utils.cs:10-123` |
| StarRating 入口 | `StarRating.cs:11-148` |
| StarRating 各子函数 | `StarRating.cs:150-834` |
| EasyObject | `StarRating.cs:837-873` |
| 粒子管理器 | `ParticleEffectManager.cs:20-157` |
| 粒子接线（开关/鼠标/渲染） | `MainWindow.xaml.cs:188-239` |
| 导航（前进/返回 + 动画） | `MainWindow.xaml.cs:54-356` |
| 主页粒子 toggle | `MainPage.xaml.cs:48-54` |
| 主题资源 | `SharedResources.xaml:5-476` |

## 附录 B：`.osu` 写回模板（可直接当 `golden` 对比用）

```
osu file format v14
<blank>
[General]
AudioFilename:{General.AudioFilename}
AudioLeadIn:{General.AudioLeadIn}
PreviewTime:{General.PreviewTime}
Countdown:{General.Countdown}
SampleSet:{General.SampleSet}
StackLeniency:{General.StackLeniency}
Mode:{General.Mode}
LetterboxInBreaks:{General.LetterboxInBreaks}
SpecialStyle:{General.SpecialStyle}
WidescreenStoryboard:{General.WidescreenStoryboard}
<blank>
[Metadata]
Title:{Metadata.Title}
TitleUnicode:{Metadata.TitleUnicode}
Artist:{Metadata.Artist}
ArtistUnicode:{Metadata.ArtistUnicode}
Creator:{Metadata.Creator}
Version:{Metadata.Difficulty}
Source:{Metadata.Source}
Tags:{Metadata.Tags}
BeatmapID:{Metadata.BeatmapID}
BeatmapSetID:{Metadata.BeatmapSetID}
<blank>
[Difficulty]
HPDrainRate:{General.HPDrainRate}
CircleSize:{General.CircleSize}
OverallDifficulty:{General.OverallDifficulty}
ApproachRate:{General.ApproachRate}
SliderMultiplier:{General.SliderMultiplier}
SliderTickRate:{General.SliderTickRate}
<blank>
[Events]
{Events.eventString}          // 原文（每行自带 CRLF），末尾再补一个空行
[TimingPoints]
{Time},{BeatLength},{Meter},{SampleSet},{SampleIndex},{Volume},{Uninherited},{Effects}
<blank>
[Colours]                     // 仅当 Colours.Count > 0
{Name}: {R},{G},{B},{A=255}
<blank>
[HitObjects]
{X},{Y},{StartTime},{Type},{HitSound},{HitSample}
```

## 附录 C：对任务描述里的几处澄清 / 修正

1. **旧项目是 .NET 8 WPF，不是 .NET 9**（`HeavenUsurperToolkit.csproj:6`）。.NET 9 是新项目；旧代码如果直接搬，需要留意
   `Random`/`StreamWriter`/`nullable` 等行为差异（本分析里已经按"行为"而不是"API"来写规格）。
2. **KeyConverter 不改 `[TimingPoints]`**：它只改 `HitObjects`、`General.CircleSize`、`Metadata.Difficulty/Creator`，
   TimingPoints/Events/Colours 原样写回（但会因为固定模板重排而丢未知字段）。任务描述里"如何改 TimingPoints/HitObjects"
   → 答案是：**不改 TimingPoints，只重建 HitObjects**。
3. **LevelCalculator 没有"导入/导出格式"**：只有"拖入 `.osu` 文件/文件夹"，没有文件导出、没有剪贴板、没有持久化；
   任务里的"导入/导出"应理解为"拖入 / 无导出"。
4. **StarRating.cs 不是直接移植自 osu!lazer**：它是第三方 "Star Rating Rebirth / srcalc" 思路的 C# 重写（见 §6.1），
   与 osu!lazer 的 mania 难度算法同源但不保证数值一致；移植验证必须用旧程序产出的 SR，不能拿官方 SR 当基准。
5. **"manio 对象模型"** 应为 **mania 对象模型**：核心是 `ManiaHitObject`（struct）、`TimingPoint`（struct）、
   `Metadata/General/Event/Colour`（struct）+ `OsuFileV14`（class），不是分层 DTO。
6. **粒子效果不是工具页面功能**：它是 `MainWindow` 上的全局 `Canvas` 叠加层；"导航返回时开关"实际是
   `NavigateBack` 与 `MainPage.Loaded` **各 toggle 一次**（净状态不变），并受 `MouseLeave` 强制关闭影响（见 §7.2）。
7. **LNTransformer 的 Level -3 不是"关闭 LN"**：它把谱面里所有 LN 强制压成 note，且该档其它参数全部无效（§4.6）；
   若要保留"Hold off"语义，新实现需要重定义。
8. **三个工具都不写回原文件**：输出 = 按 `Metadata` 拼文件名、写到原目录、同名覆盖、无备份（§3.2/§4.2/§2.6）。
   任务里的"是否备份"→ **不备份**。
9. **README 说 "8k Level Rating Tool"**：LevelCalculator 实际不限 8k（任何键数都套同一 SR 公式），
   只是历史命名；另外 10k+ 因 SR 只支持 1–10 键会显示 NaN（§5.3）。

