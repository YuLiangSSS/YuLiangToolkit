# osu!mania 核心移植笔记（HeavenUsurperToolkit → YuLiangToolkit.Tools.OsuMania）

> 写给维护者 / 未来的 AI：这里记录 **旧版 HeavenUsurperToolkit 的算法是怎么搬过来的、哪些行为原样保留、
> 哪些旧 bug 被明确修掉、怎么验证的**。改动 `src/YuLiangToolkit.Tools.OsuMania/Core/` 之前先读这一页。
>
> * 旧项目路径：`HeavenUsurperToolkit-master/`（只读参考，**不要修改**）
> * 新项目：`src/YuLiangToolkit.Tools.OsuMania/`（Core = 纯逻辑；ViewModels/Views/Tool = 界面）
> * 解析库：`OsuParsers 1.7.2`（NuGet，MIT，唯一第三方依赖；见 AGENTS.md 第 13 条）

---

## 1. 文件清单 / 旧 → 新映射

| 旧文件 | 旧内容 | 新位置 | 说明 |
| --- | --- | --- | --- |
| `OsuFileV14.cs`（解析器 + `OsuFileV14` + 结构体） | 手写 .osu v14 解析 / 写文件；`TimingPointAt` / `ManiaToKeys` / `KeyX` | `Core/OsuBeatmapFile.cs`（解析 / 保存桥）<br>`Core/OsuManiaBeatmap.cs`（工作模型 + KeyX/ColumnX + TimingPointAt） | 读写交给 OsuParsers；算法只碰工作模型 |
| `OsuFileV14.ReadFile / WriteFile` | 逐行解析 / 拼文本写出 | `OsuBeatmapFile.TryLoad / Load / Save / SaveTo` | 命名规则照旧（见 §4.1） |
| `OsuFileV14.TimingPointAt` | 只认 `BeatLength >= 0` 的红线 | `OsuManiaBeatmap.TimingPointAt / BeatLengthAt` | 逐行照搬，见 §4.3 |
| `OsuFileV14.KeyX`（3~10K 常量表） | 列坐标表 | `OsuManiaBeatmap.KeyX`（3~10 原值）+ `ColumnX(keys, column)` | 1K/2K 用公式兜底（旧版越界，属 bug） |
| `OsuFileV14.ManiaToKeys` | 换 CircleSize + 按 KeyX 改 x | `KeyConverterEngine.Apply` 里 `beatmap.KeyCount = target` | 列号不变，x 由写文件时按新键数重算 |
| `OsuFileV14.Copy` / `SelectColumn` / `SelectManyColumn` / `RemoveManyColumn` | 内部辅助 | 未移植 | 转 K / 转 LN 其实没用到（`KeyConverter` 里的 `SelectColumn` 也是死代码），不需要 |
| `StarRating.cs`（875 行） | 星数计算（Star Rating Rebirth / srcalc 思路的 C# 重写） | `Core/Difficulty/StarRatingCalculator.cs` + `EasyObject.cs` | 逐行照搬（见 §3.1） |
| `Utils.cs` | 8 个数学工具（ppy osu!lazer，MIT） | `Core/Difficulty/Utils.cs` | 逐行照搬，保留版权头 |
| `Helper.cs` | `PreciseTime` / `SelectRandom` / `DIVIDE_NUMBER` / `ERROR` 等 | `Core/OsuTransformUtils.cs` | 只搬被算法用到的部分 |
| `KeyConverter.xaml.cs` `ApplyToBeatmap` + `ProcessArea` | 转 K 全部逻辑 | `Core/KeyConverterEngine.cs` | 逐行照搬（见 §3.2） |
| `LNTransformer.xaml.cs` `ApplyToBeatmap` / `Transform` / `Invert` / `TrueRandom` / `AfterTransform` / `GetDurationByDistribution` / `RandDistribution` / `TimeRound` / `IsConverted` | 转 LN 全部逻辑 | `Core/LnTransformerEngine.cs` | 逐行照搬（见 §3.3） |
| `LevelCalculator.xaml.cs` `CalculateLevel` / `FittingFormula` | 星数 → 等级公式 | `Core/LevelCalculator.cs` | 公式与常量逐位照搬 |
| `Helper.GetDroppedFiles` / `DirectionFunc` / `DragDrop` | WPF 拖放 + 递归找文件 | `Core/OsuFileBatch.cs` `CollectOsuFiles` | 去掉 WPF 依赖，纯路径集合进 |
| 三个页面各自的“逐文件循环 + 进度条” | UI 层 | `Core/OsuFileBatch.cs` `RunKeyConverter` / `RunLnTransformer` / `ConvertOne` | 串行执行 + 进度回调 + 汇总 |

命名空间：`YuLiangToolkit.Tools.OsuMania.Core`（模型 / 桥 / 引擎 / 批量）与
`…Core.Difficulty`（星数 + 数学工具）。

---

## 2. 引擎 API 速览（界面层用法）

```csharp
// ① 加载（失败不抛异常，给中文原因）
if (!OsuBeatmapFile.TryLoad(path, out var file, out var error)) { /* error 给用户看 */ }
var bm = file!.Beatmap;          // 键数 / OD / 元数据 / TimingPoints / HitObjects / StarRating

// ② 转 K（纯内存，不写盘）
var kr = KeyConverterEngine.Apply(bm, new KeyConverterOptions { TargetKeys = 7, Gap = 10, Clean = 4 });
if (!kr.Converted) { /* kr.SkipReason */ }
// 转换 + 保存（旧版命名规则，返回 OutputPath）
var kr2 = KeyConverterEngine.Apply(file, new KeyConverterOptions { TargetKeys = 7 });

// ③ 转 LN
var lr = LnTransformerEngine.Apply(file, new LnTransformerOptions { Level = 3, KeepOverallDifficulty = true });
if (lr.Converted) Console.WriteLine(lr.OutputPath);

// ④ 批量（拖入文件 / 文件夹 → 递归找 .osu → 串行转换 → 汇总）
var files = OsuFileBatch.CollectOsuFiles(droppedPaths);             // 预览列表
var result = OsuFileBatch.RunKeyConverter(droppedPaths, options, progress);
Console.WriteLine(result.SummaryText);                              // 共 N 个：成功 / 跳过 / 失败

// ⑤ 等级 / 星数
double level = LevelCalculator.CalculateLevel(bm.StarRating);        // NaN = 超出公式范围
double sr = StarRatingCalculator.CalculateStarRating(bm.HitObjects, bm.OverallDifficulty, bm.KeyCount);
```

* `OsuManiaBeatmap.StarRating` 在**加载时算一次**（与旧版 `OsuFileV14` 构造时算一次一样）；**转谱后不会自动重算**，
  要显示“转换后星数”请自己调 `bm.CalculateStarRating()`。
* 引擎的 `Apply(OsuManiaBeatmap, …)` **只改内存**；`Apply(OsuBeatmapFile, …)` 才会保存并回填 `OutputPath`。
* 结果类里 `Converted=false` 一定带 `SkipReason`（中文，可直接显示）。

---

## 3. 移植方式与验证

### 3.1 StarRating / Utils / EasyObject

* `StarRating.cs` 逐行照搬（只改命名空间、`List<ManiaHitObject>` → `IReadOnlyList<ManiaNote>`、
  在入口加 `try/catch` 返回 0，旧版是调用处 catch 的）。
* 来源标注：StarRating 是第三方 “Star Rating Rebirth / srcalc” 思路的 C# 重写（旧文件 :13 有注释），
  **不是 osu!lazer 源码直拷**；`Utils.cs` 来自 ppy osu!lazer（MIT，保留版权头）。
* **验证**：
  * `docs/porting/star-rating-parity-report.md`：researcher 用旧实现做基准，10 组谱面 + 边界（单 note / 2K /
    rate 0.75 / 零长 LN）新旧完全一致（差 0）。
  * 本次移植另外做了 golden 对照：**384 组**（3~10K × 4 张随机谱 × OD 6 档 × rate 1.0/1.5）
    **旧实现 vs 新实现差 0**（逐位相等）。
  * `OsuManiaChecks` 里还固化了几条不依赖具体参考值的断言：SR>0 且有限、换 OD 会变、rate=1.5 会变、
    空谱返回 0、1K 不越界。
* 空谱面：旧版抛异常（被 `OsuFileV14` 构造器吞掉 → SR=0），新版入口直接 catch → 0（产品行为等价）。

### 3.2 KeyConverter

`OsuFileV14.ReadFile → ApplyToBeatmap → ProcessArea → WriteFile` 拆成
`OsuBeatmapFile.TryLoad → KeyConverterEngine.Apply → OsuBeatmapFile.Save`。算法本体逐行照搬：

* 种子：种子文本为空 → `Guid.NewGuid().GetHashCode()`；否则 SHA256(UTF8)·前 4 字节（小端 int）→ `new Random(seed)`。
* 有效条件（顺序同旧版）：extension/mania 由桥负责；`源键数 > 9` 或 `目标 ≤ 源` 拒绝；
  `Blank` 夹到 `目标 - 源`；`CheckKeys` 不命中则拒绝。
* 分段：Gap 公式 `(29998.8584 * e^(-0.3176 * Gap)) + 347.7248`（Gap=0 → MaxValue），
  用 `sumTime` 跨过阈值切段；上一段最后的 `checkList` 会带进下一段（跨段去重，旧版如此）。
* `ProcessArea`：随机复制列（`copyColumn`）/ 随机空白列 / 随机插入列 → 同时刻对象复制到插入列、原列右移 →
  清子弹（`BeatLength / Clean` + `ERROR=1.5` 对比上一段同列对象）。
* 收尾：同列按 `INTERVAL=50` / `LN_INTERVAL=10` / “在 LN 区间内” 合并；难度名插入 `[4To7C]` / `[4To7C2B]`；
  谱师替换或追加 `" & KeyConverter"`。
* 随机数调用顺序、常量、稳定排序（`OrderBy`）全部照旧；**固定种子两次结果逐位一致**（自检断言）。

### 3.3 LNTransformer

逐行照搬，Level 分支：

| Level | 行为 |
| --- | --- |
| -3 | Hold off：把所有 LN 压成普通键（**输出不含 LN**）；LN% / Column / Gap 都不生效（旧代码两个分支都只输出普通键，随机只影响走哪条分支、不改变结果） |
| -2 | `TrueRandom`：`Rng.Next((int)fullDuration) + Rng.NextDouble()` 的随机长度 |
| -1 | mu=-1 / sigma=1：随机等分（`Rng.Next(1, timenum)`） |
| 0 | mu=1 / sigma=100：常规 LN（很短） |
| 1~9 | mu=Level*11，sigma=0.85（8→0.9、9→1.0） |
| 10 | `Invert`：反相（`fullDuration - beat/divide`，太贴边则取消） |

* `AfterTransform`：按 Gap 重抽“保留 LN 的轨道”，不在轨道上的 LN 压成普通键（第二次抽列用 raw `Column`，照旧）。
* `FixError`：`PreciseTime` 依次尝试 `DIVIDE_NUMBER`（2,4,8,3,6,9,5,7,12,16,48,35,64）、误差 2ms 内对齐。
  注意两处 offset 不同（Transform 用当前时间点的 `Time`；Invert 用第一条时间点的 `Time`），照旧。
* 难度名前缀：`[LN-Lv{level:N0}-C{col:N0}]`（Column>0）或 `[LN-Lv{level:N0}]`；谱师同样替换/追加 `" & LNTransformer"`。
* `IsConverted`：Creator 含 `"LNTransformer"` 或难度名匹配 `\[LN.*\]`。

### 3.4 golden 对照（开发期验证，不进仓库）

在 `%TEMP%` 里建了一个**不污染仓库**的对照工程：把旧版 `StarRating.cs` / `Utils.cs` / `OsuFileV14` 的
结构体与 `KeyConverter`/`LNTransformer` 的算法段**原文抽出**（只去掉 WPF 进度 / 写文件 / 文件读取，换成内存模型），
和新引擎在相同的合成谱面上跑，比较输出（列 / 开始 / 结束 / 键数 / 难度名 / 谱师 / OD）。

结果（共 **1515 项断言全过**）：

| 组 | 覆盖 | 结果 |
| --- | --- | --- |
| StarRating | 3~10K × 4 张随机谱 × OD {0,2.5,5,7.2,8.5,10} × rate {1,1.5} = 384 组 | 差 0（逐位相等） |
| KeyConverter | `(4→7,3→5,4→8,5→9,7→10,2→4,9→10,6→10,3→6,5→10)` × Gap {0,5,10,20} × (Blank,Clean) 5 组 = 200 组 + Creator 分支 | 输出逐条相等（含难度名/谱师/键数） |
| LNTransformer | Level {-3,-2,-1,0,1,2,3,5,7,8,9,10} × 百分比/Divider/Column/Gap/FixError 共 22 组 | 输出逐条相等（OriginalLN=false 时） |
| Level 公式 | sr ∈ [-1,15] 每 0.01 一点 | 新旧完全一致 |

> 新引擎与旧版**预期不同**的几项（见 §4）在 golden 里单独断言：
> OriginalLN 新版保留全部列、旧版只保留最后一列；KeyConverter 新版保留 HitSound/Extras、旧版全丢。
> KeyConverter 旧版会死循环的输入（目标 > 2×源）无法拿旧实现对照，只用自检断言“能跑完且结果合法”。

### 3.5 仓库内自检

`tests/YuLiangToolkit.FrameworkSelfCheck/OsuManiaChecks.cs`（`Run(check, plain)` 签名，由 `Program.cs` 调用）
—— **158 项**，覆盖：解析 / 保存往返（含零长 LN / HitSound / Extras / 连击标记）、命名规则与非法字符、
非 mania / 键数越界拒绝、时间点语义、列坐标常量、星数、等级公式数值、转 K（含死循环输入、CheckKeys、Blank 夹取、
Creator、可复现）、转 LN（各 Level、Percentage / Level 方向、OriginalLN、FixError、OD 开关、Ignore、可复现、
保存往返）、批量（收集 / 去重 / 单文件 / 整批 / 取消）、以及 **OsuParsers 解码/编码线程安全**（并行加载 / 保存 0 失败）。

---

## 4. 与旧版的行为差异（**都已和 Lead 确认**）

### 4.1 修掉的旧 bug

| # | 旧版 bug | 新版行为 |
| --- | --- | --- |
| 1 | `KeyX` 表只有 3~10K，1K/2K 取值会 `KeyNotFoundException` | `ColumnX`：3~10K 查旧表（逐列相等），其它用 `floor(512*(column+0.5)/keys)`；1K/2K 可用 |
| 2 | KeyConverter 收尾重建对象时固定 `HitSound=0` / 默认 HitSample，**全谱打击音效丢失** | 复制源音符（含 `HitSound` / `Extras` / `IsNewCombo` / `ComboOffset`），复制列与区间合并时都保留 |
| 3 | KeyConverter：目标键数 > 2×源键数（如 2K→4K、4K→9K）时 `while` 永远凑不齐不重复的源列 → **死循环卡死** | 可复制源列用完后**允许重复复制随机源列**；仍保持同一随机数消费顺序（可行输入的结果与旧版逐位一致） |
| 4 | LNTransformer `originalLNObjects` 在按列循环里被覆盖，只保留最后一列 → OriginalLN 只对最后一列生效 | 用 `AddRange` **累加所有列**，OriginalLN 对全部列生效 |
| 5 | LNTransformer 永远把 OD 写成界面输入框的值（默认 0，**把谱面 OD 写坏**） | `LnTransformerOptions.KeepOverallDifficulty` 默认 **true（保留原 OD）**；关掉才写 `OverallDifficulty` |
| 6 | `TimingPointAt` 在“没有红线 / TimingPoints 为空”时抛 `ArgumentOutOfRangeException` / `InvalidOperationException`，会冒到 UI 线程 | 桥 / 引擎外层 catch 成“跳过 + 中文原因”；`TimingPointAt` 自身对“只有绿线”抛**带说明**的 `InvalidOperationException` |
| 7 | 1K 谱面的 `ManiaToKeys` / 列坐标同样依赖 KeyX 表 | 同 #1，1K 可加载、可转 |

### 4.2 有意改进（不算 bug，但结果与旧版可能不同）

* **Clean=0 的语义**：旧版 `ProcessArea` 里“`clean == 0` 就整列删掉”的分支被外层 `clean > 0` 挡成**死代码**，
  所以旧版实际行为就是“**clean=0 不做去子弹**”。新版保持同一可达行为（并保留代码结构便于对照），
  自检断言“Clean=0 转换成功、不做去子弹”。
* **写文件命名规则照旧**：输出到「原目录 + `{Artist} - {Title} ({Creator}) [{Difficulty}]` 去掉 9 个 Windows 非法字符 + `.osu`」。
  转谱会改 Creator / Difficulty，所以正常生成新文件；但**同一输入重复转换会覆盖同一个输出文件**（旧版行为），
  结果里带 `OutputPath` 让界面提示。
* **顺序**：`OsuFileBatch.CollectOsuFiles` 对目录内文件 / 子目录做了固定排序（旧版依赖系统枚举顺序），
  只为让批量结果可复现；不改变转换算法本身。
* **列号越界夹取**：加载时若旧谱面的 x 算出越界列（坏谱面），旧版会在 KeyX 取值处崩；新版夹到 `0..keys-1`
  （一个坏对象不至于让整批文件失败）。
* **保存会丢掉 OsuParsers 不认识的未知段**（旧版自己写的文件同样只保留已知段），属于已接受的行为；
  写回会重排段落顺序 / 重新生成注释行。
* **OD 精度**：OsuParsers 的 `DifficultySection.OverallDifficulty` 是 `float`，加载后模型里是 float→double 的精确值
  （例如文本 `8.2` 读成 `8.199999809…`），旧版是 `double.Parse` 的 `8.2`。对 0.5 步进 / 整数 OD 无差异；
  对 SR 的影响极小（同 OD 组 golden 已对齐 0 差异，因为这些谱面 OD 都是整数值）。
* **Level 计算流程**：旧版 `LevelCalculator` 页面每张谱**算两次 SR**（`OsuFileV14` 构造一次 + 页面再调一次）
  且在 UI 线程；新版加载只算一次（`OsuBeatmapFile.StarRating`），界面在后台线程扫文件。公式不变。
* **批量处理串行**：OsuParsers 解码 / 编码是 **static 状态、非线程安全**（实测多线程 Decode 会串台、
  抛 `FormatException: 'Combo2 : 0'`），`OsuBeatmapFile` 内部用同一把静态锁串行化解码与保存，
  `OsuFileBatch` 也保持逐个处理。**不要**把批量改成 `Parallel` / `Task.WhenAll`；界面也不要多线程同时加载谱面。

### 4.3 原样保留、别“顺手改”的行为

* Gap 公式与分段、随机数调用顺序 / 次数；同一 seed 的输出必须可复现（自检断言）。
* `AfterTransform` 的 Column/Gap 重抽（包括第二次抽列没有 `Column==0 → keys` 兜底）。
* KeyConverter 的 `INTERVAL=50` / `LN_INTERVAL=10` / `ERROR=1.5` 合并阈值与“在 LN 区间内就删”的判定。
* Difficulty / Creator 文案：`[4To7C]`、`[4To7C2B]`、`[LN-Lv{level:N0}-C{col:N0}]`、` & KeyConverter`、` & LNTransformer`。
* 旧版“只支持低转高”的校验（目标键数必须大于源键数）。
* 零长度 LN（`type=128` 且 end==start）语义：加载后 `IsLongNote=true` 且两端相等；保存写回 `type=128`；
  KeyConverter 收尾重建时会被压回普通键（旧版行为）。
* `TimingPointAt` 只认 `BeatLength >= 0` 的红线（绿线是负数）；区间匹配不到就兜底最后一条红线。
* 种子文本判定用 `string.IsNullOrEmpty`（**纯空格不算空**，会当真种子 / 真 Creator）。

### 4.4 已知限制

* UI 层不应并行加载 / 保存（见 §4.2 串行说明）。
* 同名输出会覆盖（旧版行为）；没有自动加序号。
* `OsuParsers` 写文件会重排整份谱面，未知自定义段丢失（可接受）。
* StarRating 只按旧实现的键数上限建表（1~10K）；`README`/UI 已按 1~10K 限制。
* 转谱不更新 `StarRating` 字段（旧版也不更新）；需要新星数请显式调用。

---

## 5. 旧代码里的坑（给未来的自己，都是这次逐行读出来的）

1. **旧版到处是可变结构体（struct）**：`ManiaHitObject` 是 struct，`var obj = locations[i]; obj.EndTime = …`
   只改副本，`locations[i] = obj` 才写回。新版是 class，所有“旧版会复制一份”的位置都显式 `Clone()` /
   `ToNote()`，移植时千万别把某处 clone 删了。
2. `KeyConverter.ProcessArea` 里 `locations[i]` 在 `for j` 循环里会被多次 `Column += 1`，而克隆判定用的是
   **循环开始前**的 `column` 局部变量 —— 顺序很绕，改一行结果就变。
3. `KeyConverter` 的 `checkList` 是**跨段共享**的上一段末尾对象，`isFirst` 标志只在特定分支里复位；
   看起来像 bug 的行为可能正是它的“去重子弹”效果。
4. `LNTransformer.TrueRandom` 的**每列最后一个对象没有处理**（只循环 `Count-1`）→ Level=-2 时每列最后一个对象
   会丢（旧版行为，原样保留）。
5. `Transform`/`Invert` 的 `locations[^1]`（每列最后一个对象）有单独处理：`FixError`、OriginalLN 都会影响它。
6. 旧版 `LevelCalculator` 页面会重复算 SR；现在只在桥里算一次。
7. `OsuFileProcessor.ReadFile` 用 `StreamReader` 默认编码（会识别 BOM）；新版桥目前用 OsuParsers 的路径解码，
   带 BOM 的 UTF-8 文件也能读（自检有断言）。
8. `ManiaHitObject.EndTime` 的 setter 会“改类型 / 改 HitSample 字符串”；新版模型用 `IsLongNote` 布尔 +
   `EndTime` setter 还原同一语义（赋值 == StartTime 就变回普通键）。
9. 旧版写文件时把 `HPDrainRate` 等统一从内存值重写；新版只回写工具会改的字段（键数 / OD / 元数据 / 打击物），
   其余段落沿用解析结果 —— 所以**不要**指望新版会“修正”原文件里其它段的问题。

---

## 6. 验证命令

```powershell
# 只编译新项目（最快，UI 队友在改 ViewModels/Views 时也能确认 Core 没坏）
dotnet build src\YuLiangToolkit.Tools.OsuMania\YuLiangToolkit.Tools.OsuMania.csproj -c Debug

# 全项目编译 + 全量自检（含本工具的 158 项）
.\build.ps1
dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj
```

> golden 对照工程（旧实现原文 vs 新引擎）是开发期一次性验证，放在 `%TEMP%`、**不进仓库**：
> 见 §3.4 的覆盖表。若以后大改引擎，建议按同样方法临时重建一次对照（旧项目源码是只读参考，可随时抄出）。
