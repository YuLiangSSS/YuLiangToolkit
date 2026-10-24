# StarRating 新旧实现数值对齐报告

> 对象：旧项目 `HeavenUsurperToolkit-master/StarRating.cs`（875 行）+ `Utils.cs`（125 行）
> ↔ 新实现 `src/YuLiangToolkit.Tools.OsuMania/Core/Difficulty/StarRatingCalculator.cs`（+ `EasyObject.cs`、`Utils.cs`）
>
> 参考值表（旧实现基准、生成方法）：`docs/porting/star-rating-reference.md`
> 相关选型文档：`docs/porting/osu-parser-selection.md`

---

## 0. 结论（TL;DR）

| 项 | 结果 |
| --- | --- |
| 主对照（10 组确定性合成谱：4/7/10K、OD 0/8.0/9.5、稀疏/中/高密度、含 LN、rate 1.0/1.5） | **10/10 完全一致**；绝对差全部 = `0.0000000000E+00`，最大相对差 = `0.0000000000E+00` |
| 双入口一致性 | `CalculateStarRating(List, od, keys, rate)` 与 `CalculateStarRating(OsuManiaBeatmap, rate)` 结果差 ≤ 1e-12，全部一致 |
| 边界用例（空谱面 / 单 note / 2K / rate 0.75 / 零长度 LN） | 4/5 完全一致；空谱面是**预期差异**（见 §3） |
| 端到端（`.osu` → OsuParsers 解析桥 → 新实现，10 组） | **10/10 一致**（加载时算出的星数 = 旧实现同对象数值，差 0） |
| 是否需要 osu-core 修 bug | **不需要**。当前版本（快照见 §1）与旧实现逐位一致，无需定位差异 |
| 建议 | 把 §4 的 10 个参考值（+ 端到端对照）加进 FrameworkSelfCheck 的 `StarRatingChecks`，防止后续改动漂移；本次报告只代表 §1 快照版本，之后若 osu-core 再改 `StarRatingCalculator.cs` 需重跑本 harness |

---

## 1. 环境与被测实现快照

| 项 | 值 |
| --- | --- |
| 日期 | 2026-10-24 16:33（本地时区） |
| 运行时 | .NET 9.0.21，Windows |
| 旧实现 | `HeavenUsurperToolkit-master/StarRating.cs` / `Utils.cs` **逐字节原样拷贝**到 harness，仅 namespace 改 `YltSrParity`（算法零改动） |
| 新实现 | 仓库项目 **ProjectReference 直引**（跑的是 osu-core 当前源码，不复制、不修改） |
| 实现快照（sha256 前 16 位） | `StarRatingCalculator.cs` **d1365739fa93d674**（33,117 B，16:32:06）<br>`EasyObject.cs` **1d8730f6c0fe5d8c**（2,171 B）<br>`Utils.cs` **2f0da85fae0dff48**（5,090 B）<br>`OsuManiaBeatmap.cs`（ManiaNote 定义）**4435773ca895cb7a**（14,396 B） |
| 运行时说明 | 本报告生成时 osu-core 仍在并行开发；若上述文件后续有改动，重跑 §5 命令即可自动复测 |

---

## 2. 对齐方法

1. **同一批输入**：用确定性生成器（`Random(1000 + id*37)`）生成 10 组合成谱的对象列表，每组的 `(StartTime, EndTime, Column)` 逐对象相同；
2. **旧实现**：转成旧 `ManiaHitObject` 最小等价物（`StartTime/EndTime/Column`）→ `YltSrParity.StarRatingCalculator.CalculateStarRating(...)`；
3. **新实现**：同一批对象转成 `ManiaNote`（`Column/StartTime/EndTime`）→
   `YuLiangToolkit.Tools.OsuMania.Core.Difficulty.StarRatingCalculator.CalculateStarRating(...)`；
4. 两边的 `(od, keys, rate)` 完全一致；**不经过 `.osu` 解析**，避免解析层差异干扰星数对比；
5. 额外做**端到端**：把同一批对象写成 v14 mania `.osu`，再用 `OsuBeatmapFile.TryLoad()`（OsuParsers 桥）读回来，
   比较“加载时算出的星数”与“旧实现同对象、rate=1.0 的星数”，验证列号 `floor(x*keys/512)` 往返与解析桥无偏差。

---

## 3. 主对照结果（旧 vs 新，10 位小数）

| # | keys | OD | rate | notes | LN | 旧实现 SR | 新实现 SR | 绝对差 | 相对差 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 01 | 4 | 8.0 | 1.0 | 200 | 0 | 1.0478683262 | 1.0478683262 | 0.0000000000E+00 | 0.0000000000E+00 |
| 02 | 4 | 8.0 | 1.0 | 600 | 135 | 4.9352511737 | 4.9352511737 | 0.0000000000E+00 | 0.0000000000E+00 |
| 03 | 4 | 8.0 | 1.0 | 1200 | 205 | 8.1031144462 | 8.1031144462 | 0.0000000000E+00 | 0.0000000000E+00 |
| 04 | 4 | 0.0 | 1.0 | 200 | 0 | 0.9600663396 | 0.9600663396 | 0.0000000000E+00 | 0.0000000000E+00 |
| 05 | 4 | 9.5 | 1.0 | 600 | 147 | 5.1732675209 | 5.1732675209 | 0.0000000000E+00 | 0.0000000000E+00 |
| 06 | 7 | 0.0 | 1.0 | 150 | 18 | 1.8638253317 | 1.8638253317 | 0.0000000000E+00 | 0.0000000000E+00 |
| 07 | 7 | 8.0 | 1.0 | 700 | 224 | 6.2438279397 | 6.2438279397 | 0.0000000000E+00 | 0.0000000000E+00 |
| 08 | 10 | 8.0 | 1.0 | 900 | 167 | 5.7213766701 | 5.7213766701 | 0.0000000000E+00 | 0.0000000000E+00 |
| 09 | 4 | 8.0 | 1.5 | 600 | 135 | 6.4424754489 | 6.4424754489 | 0.0000000000E+00 | 0.0000000000E+00 |
| 10 | 7 | 9.5 | 1.5 | 700 | 224 | 7.7591794804 | 7.7591794804 | 0.0000000000E+00 | 0.0000000000E+00 |

- **完全一致（|差| ≤ 1e-12）：10/10**；最大绝对差 = `0.0000000000E+00`；最大相对差 = `0.0000000000E+00`。
- 全部用例 `new(List)` 与 `new(OsuManiaBeatmap)` 两个入口结果一致（差 ≤ 1e-12）。
- #09 / #10 是“同 #02 / #07 的逐对象相同输入、仅 rate=1.5”，单独验证 `EasyObject.FromRate` 分支。

### 3.1 边界用例

| 用例 | keys | OD | rate | notes | 旧实现 SR | 新实现 SR | 绝对差 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 空谱面 | 4 | 8.0 | 1.0 | 0 | `IndexOutOfRangeException`（旧 `OsuFileV14` 构造器 try/catch → 星数 0） | 0.0000000000 | —（预期差异，见下） |
| 单 note | 4 | 8.0 | 1.0 | 1 | 0.0000000000 | 0.0000000000 | 0.0000000000E+00 |
| 2K 120 notes | 2 | 8.0 | 1.0 | 120 | 1.7929102511 | 1.7929102511 | 0.0000000000E+00 |
| case02 同对象 rate=0.75 | 4 | 8.0 | 0.75 | 600 | 4.3325686479 | 4.3325686479 | 0.0000000000E+00 |
| 零长度 LN（type=128 但 end=start） | 4 | 8.0 | 1.0 | 50 | 0.4858933604 | 0.4858933604 | 0.0000000000E+00 |

**关于空谱面的预期差异**：旧 `CalculateStarRating` 直接对空 `noteSeq` 取 `[0]` 会抛 `IndexOutOfRangeException`；
旧项目是在 `OsuFileV14` 构造器里 `try/catch` 后把 `StarRating = 0`。新实现把 try/catch 收进
`StarRatingCalculator.CalculateStarRating` 内、返回 0（XML 注释里已写明“与旧版行为一致”）。
两者在**产品行为**上等价（都是 0），只是异常边界位置不同；建议保留新实现的做法。

### 3.2 端到端：`.osu` → OsuParsers 解析桥 → 新实现

（同一批对象写成 v14 `.osu`，用 `OsuBeatmapFile.TryLoad` 读回；比较“加载时算出的星数”与旧实现同对象、rate=1.0 的值）

| # | keys | OD | notes（解析后） | 加载时星数（新） | 旧实现同对象 rate=1.0 | 差 | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 01 | 4 | 8.0 | 200 | 1.0478683262 | 1.0478683262 | 0.0000000000E+00 | 一致 |
| 02 | 4 | 8.0 | 600 | 4.9352511737 | 4.9352511737 | 0.0000000000E+00 | 一致 |
| 03 | 4 | 8.0 | 1200 | 8.1031144462 | 8.1031144462 | 0.0000000000E+00 | 一致 |
| 04 | 4 | 0.0 | 200 | 0.9600663396 | 0.9600663396 | 0.0000000000E+00 | 一致 |
| 05 | 4 | 9.5 | 600 | 5.1732675209 | 5.1732675209 | 0.0000000000E+00 | 一致 |
| 06 | 7 | 0.0 | 150 | 1.8638253317 | 1.8638253317 | 0.0000000000E+00 | 一致 |
| 07 | 7 | 8.0 | 700 | 6.2438279397 | 6.2438279397 | 0.0000000000E+00 | 一致 |
| 08 | 10 | 8.0 | 900 | 5.7213766701 | 5.7213766701 | 0.0000000000E+00 | 一致 |
| 09 | 4 | 8.0 | 600 | 4.9352511737 | 4.9352511737 | 0.0000000000E+00 | 一致 |
| 10 | 7 | 9.5 | 700 | 6.4200886776 | 6.4200886776 | 0.0000000000E+00 | 一致 |

> #09/#10 的 `.osu` 文件内容与 #02/#07 相同（只是文件名/元数据不同），所以端到端核对的是 rate=1.0 的数值；
> rate=1.5 的差异已在主对照表中单独覆盖。

---

## 4. 结论与后续建议

1. **数值对齐通过**：当前快照（§1）下新实现与旧实现**逐位一致**（连 1e-12 以内的误差都没有，差值为 0），
   包括 4/7/10 键、OD 0/8/9.5、含 LN、和弦、高密度、rate 1.5 / 0.75、2K、零长度 LN 等分支。
2. **解析桥没有引入偏差**：`.osu` 写出→OsuParsers 读回→新实现，10/10 与旧值一致（列号往返正确）。
3. **唯一差异是空谱面的异常边界**（§3.1 末尾），属预期且产品行为等价，**不需要修**。
4. 建议把 `docs/porting/star-rating-reference.md` §3 的 10 个数值作为回归断言写进自检
   （`tests/YuLiangToolkit.FrameworkSelfCheck` 新加 `StarRatingChecks`），否则以后没人能发现算法漂移。
5. 本报告只对 §1 快照负责；osu-core 若在 16:32:06 之后继续改 `StarRatingCalculator.cs` / `EasyObject.cs` / `Utils.cs`，
   需重跑 §5 的 harness 并更新本报告的哈希与表格。

---

## 5. 复现方式

```powershell
# harness 位置（只在 %TEMP%，不在仓库）
$h = "$env:TEMP\ylt-sr-parity"
Set-Location $h
dotnet run -c Release
# 输出：控制台对照表 + parity.md（本报告 §2/§3 的表格来源）+ reference.md/reference.csv + cases/*.osu
```

harness 组成：
- `Program.cs`：确定性谱面生成器（与 `star-rating-reference.md` §2 参数一致）+ 新旧对比 + 端到端检查；
- `StarRating.cs` / `Utils.cs`：旧实现原样副本（namespace `YltSrParity`）；
- `ManiaHitObject.cs`：旧结构体最小等价物（只含 `StartTime/EndTime/Column`）；
- `YltSrParity.csproj`：`net9.0-windows` + `ProjectReference` 到仓库 `Tools.OsuMania`（因此跑的一定是仓库最新源码）；
- `cases/case01.osu … case10.osu`：与 §2 参数一致的 v14 mania 谱面（可单独用于手工回归）。
