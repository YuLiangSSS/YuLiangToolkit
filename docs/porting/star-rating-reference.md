# osu!mania StarRating 参考值（旧实现基准）

> 配套文档：`docs/porting/osu-parser-selection.md`（选型）、`docs/porting/star-rating-parity-report.md`（新旧实现对齐报告）。
>
> 本文是**新实现（`src/YuLiangToolkit.Tools.OsuMania/Core/Difficulty/StarRatingCalculator.cs`）的参考答案**：
> 用旧项目 `HeavenUsurperToolkit-master/StarRating.cs` + `Utils.cs` 对同一批**确定性合成谱面**算出的星数，
> 保留 **10 位小数**。新实现只要逐项一致（≤ 1e-12），即视为移植正确。

---

## 1. 旧实现来源与保真说明

| 项 | 内容 |
| --- | --- |
| 算法文件 | `HeavenUsurperToolkit-master/StarRating.cs`（875 行，`StarRatingCalculator`）+ `Utils.cs`（125 行） |
| 算法来源 | 社区项目 **StarRatingRebirth** 的 C# 移植（其数学描述源自 ppy/osu 的 mania 难度算法，MIT）；`Utils.cs` 文件头为 ppy Pty Ltd MIT 声明 |
| 本次用法 | **逐字节原样拷贝**到 `%TEMP%\ylt-sr-parity\`，**只改了命名空间**（`HeavenUsurperToolkit` → `YltSrParity`）；算法/常量/循环顺序零改动 |
| 依赖补齐 | 旧算法只经 `EasyObject.FromManiaObjects` 读取 `StartTime` / `EndTime` / `Column`；所以用旧 `OsuFileV14.cs:814-968` 的等价最小结构体 `ManiaHitObject`（见 `ManiaHitObject.cs`）即可，不需要旧项目的 WPF / 手写解析器 |
| 入口 | `StarRatingCalculator.CalculateStarRating(List<ManiaHitObject> objects, double od, int keys, double rate)` |

> ⚠️ 这些是「旧工具当年的星数」，**不是 osu! 官网/stable 当前显示的星数**。
> 旧实现（StarRatingRebirth 复刻）与 ppy 官方 `ManiaDifficultyCalculator`（Version 20241007）**不是同一套数值**：
> 同一张真实 4K 谱（Ever Dream，OD=7）官方实测 **2.353491**、旧实现 **3.546690**（详见选型文档 4.4 节）。
> LevelCalculator 的等级拟合公式是按旧实现数值拟合的，所以**参考值是旧实现这一套**。

---

## 2. 合成谱面设计（确定性）

- 生成器：`%TEMP%\ylt-sr-parity\Program.cs` 的 `BuildCases()` / `Generate()`；
- 随机种子：`Random(1000 + id * 37)` —— 每个用例的**谱面对象（时间/列/是否 LN）完全可复现**；
- 每个用例只由参数决定：`keys`（键数）、`OD`、`rate`、`notes`（目标对象数）、`chord‰`（同刻多键概率）、`LN‰`（长条概率）、`gap`（相邻时间点毫秒范围）；
- LN 长度：100~1000 ms，50 ms 步进；列号在 `0..keys-1` 内随机；
- 覆盖：4/7/10 键；OD 0 / 8.0 / 9.5；稀疏 / 中等 / 高密度和弦；无 LN / 含 LN；rate 1.0 / 1.5 / 0.75（边界用例）。

| # | keys | OD | rate | notes | chord‰ | LN‰ | gap(ms) | 设计意图 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 01 | 4 | 8.0 | 1.0 | 200 | 0 | 0 | 120~380 | 4K 稀疏纯 note（基准） |
| 02 | 4 | 8.0 | 1.0 | 600 | 150 | 250 | 70~220 | 4K 中等密度含 LN |
| 03 | 4 | 8.0 | 1.0 | 1200 | 300 | 150 | 45~150 | 4K 高密度和弦 |
| 04 | 4 | 0.0 | 1.0 | 200 | 0 | 0 | 120~380 | OD 下界（0） |
| 05 | 4 | 9.5 | 1.0 | 600 | 150 | 250 | 70~220 | 高 OD |
| 06 | 7 | 0.0 | 1.0 | 150 | 50 | 100 | 120~380 | 7K + OD0 |
| 07 | 7 | 8.0 | 1.0 | 700 | 150 | 300 | 60~200 | 7K 中等含 LN |
| 08 | 10 | 8.0 | 1.0 | 900 | 200 | 200 | 55~180 | 10K 高密度 |
| 09 | 4 | 8.0 | 1.5 | （同 #2 对象） | — | — | — | **同 #2 完全相同对象，仅 rate=1.5** |
| 10 | 7 | 9.5 | 1.5 | （同 #7 对象） | — | — | — | **同 #7 完全相同对象，仅 rate=1.5** |

> #09 / #10 复用 #02 / #07 的**逐对象相同输入**，只改 rate，用来单独验证 rate 分支（`EasyObject.FromRate`）。

---

## 3. 参考答案（旧实现输出，10 位小数）

<!-- 表格由 harness 的 reference.md 自动生成，数值与 parity-report 完全对应 -->

| # | keys | OD | rate | 谱面特征 | notes | LN 数 | 旧实现 SR（10 位小数） |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 01 | 4 | 8.0 | 1.0 | 4K 稀疏纯 note | 200 | 0 | **1.0478683262** |
| 02 | 4 | 8.0 | 1.0 | 4K 中等密度含 LN | 600 | 135 | **4.9352511737** |
| 03 | 4 | 8.0 | 1.0 | 4K 高密度和弦 | 1200 | 205 | **8.1031144462** |
| 04 | 4 | 0.0 | 1.0 | 4K OD0 稀疏纯 note | 200 | 0 | **0.9600663396** |
| 05 | 4 | 9.5 | 1.0 | 4K OD9.5 中等含 LN | 600 | 147 | **5.1732675209** |
| 06 | 7 | 0.0 | 1.0 | 7K OD0 稀疏 | 150 | 18 | **1.8638253317** |
| 07 | 7 | 8.0 | 1.0 | 7K 中等含 LN | 700 | 224 | **6.2438279397** |
| 08 | 10 | 8.0 | 1.0 | 10K 高密度 | 900 | 167 | **5.7213766701** |
| 09 | 4 | 8.0 | 1.5 | 同 #2 相同对象，仅 rate=1.5 | 600 | 135 | **6.4424754489** |
| 10 | 7 | 9.5 | 1.5 | 同 #7 相同对象，仅 rate=1.5 | 700 | 224 | **7.7591794804** |

**边界用例（信息性，旧实现行为）：**

| 用例 | keys | OD | rate | notes | 旧实现 SR | 说明 |
| --- | --- | --- | --- | --- | --- | --- |
| 空谱面 | 4 | 8.0 | 1.0 | 0 | `IndexOutOfRangeException` | 旧 `OsuFileV14` 构造器用 try/catch 兜底 → 星数记 0 |
| 单 note | 4 | 8.0 | 1.0 | 1 | 0.0000000000 | 退化输入 |
| 2K 120 notes | 2 | 8.0 | 1.0 | 120 | 1.7929102511 | 2 键也支持 |
| case02 同对象 rate=0.75 | 4 | 8.0 | 0.75 | 600 | 4.3325686479 | rate < 1 分支 |
| 零长度 LN（type=128 但 end=start） | 4 | 8.0 | 1.0 | 50 | 0.4858933604 | 算法按普通键处理（只看 head/tail） |

---

## 4. 怎么用这些参考值

1. **直接调用新实现**（推荐，见 parity-report 的复现方式）：
   ```csharp
   var notes = /* 按 §2 参数生成、或从 cases/*.osu 读入后转成 ManiaNote */;
   double sr = StarRatingCalculator.CalculateStarRating(notes, od, keys, rate);
   // 与 §3 的对应行比较，要求 |diff| <= 1e-12
   ```
2. **完整 .osu 输入**：`%TEMP%\ylt-sr-parity\cases\case01.osu ... case10.osu` 是同样 10 组对象的 v14 mania 谱面，
   可用 `OsuBeatmapFile.TryLoad()` 读入后直接 `file.Beatmap.CalculateStarRating(rate)` 对表；
   **注意**：读回再算会多经过解析/列号换算（`column = floor(x*keys/512)`），生成的 x 已按列中心写，理论上列号不变。
3. 建议把 §3 的 10 个数作为**回归基准**加进自检（FrameworkSelfCheck 里加一个 `StarRatingChecks`），以后改算法立刻发现漂移。

---

## 5. 复现命令（本机）

```powershell
# 旧算法 reference harness（本文件数值的来源）
$h = "$env:TEMP\ylt-sr-parity"
Set-Location $h
dotnet run -c Release          # 输出 reference.md / reference.csv / cases/*.osu + 控制台表格

# 若仓库里新实现改了，重跑同一 harness 即会同时打印旧/新对比（它通过 ProjectReference 引用仓库项目）
```

> harness 文件：`%TEMP%\ylt-sr-parity\`：`Program.cs`（生成器 + 对比）、`StarRating.cs`/`Utils.cs`（旧算法原样副本）、
> `ManiaHitObject.cs`（旧结构体最小等价物）、`cases/`（10 个 v14 .osu）、`reference.md`/`reference.csv`/`parity.md`。
>
> 这些文件**只在 %TEMP%，不在仓库**（若需长期回归，把 §3 表格抄进自检即可，不要拷贝旧算法源的那份临时副本）。
