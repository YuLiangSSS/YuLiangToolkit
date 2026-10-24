# osu!mania 三件套移植复核（新实现 vs 旧项目）

> 复核人：legacy-analyst（task-7）；日期：2026-10-24。
> 复核对象：`src/YuLiangToolkit.Tools.OsuMania/`（Core 算法 + 解析桥 + UI 三工具参数），旧参考：`HeavenUsurperToolkit-master/`。
> 结论一句话：**未发现功能性 Critical/Major 缺陷，可以提交**。独立探针 396/396 项通过；
> 6 项计划修复全部验证正确；仅发现 **1 处用户可见文案错误（Level -3 说明）** 和几处可选改进（见 §7）。

---

## 1. 复核方法与证据（独立于 osu-core 的 golden 对照）

为免「自证」，本次没有复用 osu-core 的对照工程，而是在 `%TEMP%\ylt-review\` 另建**独立探针**：

| 侧 | 内容 |
| --- | --- |
| 旧实现 | **旧项目源码原样**拷入探针：`StarRating.cs`、`Utils.cs`、`OsuFileV14.cs`（仅去掉 WPF 专属的 `GetOsuPath`/`using System.Windows`，解析/写回/结构体/`TimingPointAt`/`KeyX` 全未动）；`KeyConverter.xaml.cs` / `LNTransformer.xaml.cs` 的算法方法**逐行原文抽出**（只去掉 UI 进度/读文件/写文件，换成内存模型）；`Helper.cs` 的 `PreciseTime`/`SelectRandom`/`DIVIDE_NUMBER`/`ERROR` 原样。 |
| 新实现 | 用 `Compile Include` 把仓库 `Core/**/*.cs`（含 `Difficulty/`）**直接编进同一个进程**（不复制、不改仓库源码）。 |
| 输入 | 自建 11 个确定性合成 `.osu`（4K/7K/2K/1K、含 LN、零长度 LN、双押、变速红绿线、自定义 HitSound/Extras、含非法文件名字符、非 mania、11K、无红线、垃圾/空文件）+ researcher 的 `case01~10.osu`。 |
| 对比口径 | KeyConverter/LN：输出 `HitObjects` 的 `(列,开始,结束)` **序列逐项相等**，以及键数 / 难度名 / 谱师 / OD；SR：`double` 差 ≤ 5e-10（实际多为 0）；解析：对象序列 / HitSound / Extras / TimingPoints / 元数据逐项相等。 |
| 结果 | **PASS 396 / FAIL 0**（旧版会死循环的组合单独只验新实现）。探针在 `%TEMP%\ylt-review\probe`（`dotnet run -c Release`），日志 `%TEMP%\ylt-review\probe-run.log`，不入库。 |
| 快照 | 新：`StarRatingCalculator d1365739fa93`、`Utils 2f0da85fae0d`、`EasyObject 1d8730f6c0fe`、`OsuManiaBeatmap 4435773ca895`、`OsuBeatmapFile 98c0d1e8d4b9`、`KeyConverterEngine a7bfa3d616c5`、`LnTransformerEngine ca864a77b0d1`、`LevelCalculator b5d2e6950e1f`、`OsuTransformUtils cc6cb0bc4dae`、`OsuFileBatch ffb803cc99ea`（SHA256 前 12 位）。 |

**一个直接证据**：旧版 KeyConverter 的复制列循环在「目标键数 > 2×源键数」（如 4K→10K）时会死循环
（第一次试跑探针 10 分钟无返回，按任务中止）；新版改为「源列用完后允许重复复制」，同输入毫秒级完成
（探针 `kc-hangfix`：4K→9/10、2K→5、1K→3/4 全部成功；旧实现无法给出对照，属预期修复）。

---

## 2. 逐方法映射与一致性结论

### 2.1 StarRating / Utils / EasyObject / Level（**逐行级一致**）

用「去注释/去空行/去命名空间与访问修饰符」的规范化文本对比 + 探针实测：

| 旧 | 新 | 结论 |
| --- | --- | --- |
| `StarRating.cs:11 CalculateStarRating(List<ManiaHitObject>,od,keys,rate)` | `Difficulty/StarRatingCalculator.cs:35 CalculateStarRating(IReadOnlyList<ManiaNote>,od,keys,rate)` | 主体 1:1；新增最外层 try/catch → 失败返回 0（旧版是 `OsuFileV14` 构造器处 catch，产品行为等价） |
| `ComputeCAndKs/Pbar/Abar/Rbar/LNBodies…/Xbar/Jbar/Anchor/GetKeyUsage400/LNSum/GetKeyUsage/GetCorners/PreProcess` | 同名函数（`internal static`） | 公式、常量、数组维度、循环顺序、浮点运算顺序**无差异**（规范化 diff 仅类型/限定名差异） |
| `Utils.cs` 9 个方法 | `Difficulty/Utils.cs` 9 个方法 | 逐行一致（仅 `Utils.` 限定名与 `static class` 形式差异） |
| `EasyObject`（旧文件底部） | `Difficulty/EasyObject.cs` | 逐行一致（`List<ManiaHitObject>`→`IReadOnlyList<ManiaNote>`，其余含 `FromRate` 原地修改一致） |
| `LevelCalculator.CalculateLevel/FittingFormula` + 两常量 | `Core/LevelCalculator.cs` | 公式/常量/NaN 分支逐位一致；探针用独立写出的分段公式对 19 个点（含边界/NaN/负值）比对全等 |

**实测覆盖**：参考表 `case01~08` 旧/新都精确复现 `docs/porting/star-rating-reference.md` 的 10 位小数；
rate=1.5 分支旧/新在 `case02/case07` 上完全相等（注意参考表 #09/#10 是“内存对象”的 rate 值，
`cases/09、10.osu` 文件本身与内存对象不同，**不能拿文件直接对表**——已按“旧/新交叉相等”验）；OD=8.2 时
旧 `double 8.2` vs 新库 `float→8.199999809265137`，但 `ceil(OD*3)` 相同 → **SR 完全一致（差 0）**。

### 2.2 KeyConverter（算法一致；差异都是计划内修复）

| 旧（`KeyConverter.xaml.cs`） | 新（`Core/KeyConverterEngine.cs`） | 结论 |
| --- | --- | --- |
| `ProcessDroppedFiles`+`ApplyToBeatmap` 的读文件/写文件/进度 | `OsuBeatmapFile.TryLoad` + `Apply(OsuBeatmapFile, opts)`（保存并在结果里带 OutputPath） | 拆分正确；逐文件串行；单文件失败不影响整批 |
| `ApplyToBeatmap` 判定段（extension→由桥；Mode=3；seed；keys>9；目标≤源；Blank 夹取；CheckKeys） | `Apply`：顺序相同；另加“目标键数 1~10”兜底 | 一致（桥负责 mania/extension） |
| `ManiaToKeys`（改 CS + `X=KeyX[Keys][列]`） | `Apply` 里 `beatmap.KeyCount=目标`，保存时按 `ColumnX` 重算 X | 3~10K 输出列相同；1K/2K 由旧崩溃 → 新公式（计划修复） |
| 分段循环（`sumTime/Gap` 公式、area、checkList） | 同名逻辑、同常量 | 一致（18/18 组参数输出序列全等） |
| `ProcessArea`（复制列/空白列/插入列/右移/跨段清子弹） | `ProcessArea` 同结构、同 RNG 调用顺序 | 一致；额外：复制列循环死循环兜底、RNG 调用次数在可行输入上不变 |
| 收尾按列合并（LN 区间/50ms/10ms） | 同结构 | 一致；额外：重建时带回源对象的 HitSound/Extras/连击（计划修复） |
| 难度名前缀 / Creator / `WriteFile` | 同文案；`OsuBeatmapFile.Save`（同命名规则） | 一致（前缀 `[4To7C]` / `[4To7C{Blank}B]`、` & KeyConverter`） |

**RNG 顺序**：seed 计算 → `ProcessArea` 内「复制源列（含重试）→ 标空白（含重试）→ 插入位置（含重试）」；
映射与清理不耗 RNG。探针固定 seed 的 18 组参数（Gap 0/2/10/20、Clean 0/4/8/16、Blank 0/1/2/3、4→7/8、7→9/10、2→4/3、零长 LN、Creator、CheckKeys）
输出**逐项全等**，可证明 seed→结果可复现、顺序未变。

### 2.3 LNTransformer（算法一致；差异都是计划内修复）

| 旧（`LNTransformer.xaml.cs`） | 新（`Core/LnTransformerEngine.cs`） | 结论 |
| --- | --- | --- |
| `ApplyToBeatmap`（读文件/进度/写文件） | `Apply(OsuBeatmapFile, opts)` + `ApplyCore` | 拆分正确 |
| 判定：Mode=3、CheckKeys、`IsConverted && Ignore` | 同顺序 | 一致 |
| Level -3 分支 | 同代码（含第二次抽列用 raw Column 的旧行为） | 一致；输出确实**全部普通键**（见 §7 文案问题） |
| `Transform` / `Invert` / `TrueRandom` / `GetDurationByDistribution` / `AfterTransform` / `RandDistribution` / `TimeRound` / `IsConverted` | 同名公开/内部方法 | 公式、常量、RNG 顺序、`PreciseTime` 两处 offset 差异（Transform 用当前红点 Time、Invert 用第一条 Time）**全保留** |
| `originalLNObjects = Transform(...)`（每列覆盖） | `originalLNObjects.AddRange(...)` | **计划修复**：Original LN 对全部列生效 |
| `General.OverallDifficulty = 参数值`（默认 0，写坏 OD） | `KeepOverallDifficulty` 默认 true，勾选“修改 OD”才覆盖 | **计划修复** |
| `TimingPointAt` 无红线抛 `ArgumentOutOfRangeException` 冒到 UI | 桥/引擎 try/catch → “跳过 + 中文原因”；`TimingPointAt` 对只有绿线抛带说明的 `InvalidOperationException` | **计划修复**，探针 `ln-no-red-timingpoint` 通过 |

**RNG 顺序**：-3 分支（初始转普通键→按时间组 `Next(100)`→每 Gap 组重抽列）、Transform（每音 `RandDistribution` 2 次
`NextDouble`（Level -1 再加 `Next(1,timenum)`）→ 概率 `Next(100)`→最后音再 `Next(100)`）、Invert、TrueRandom、
AfterTransform（初始抽列 + 每 Gap 组重抽）均与旧版一致。探针 23 组（Level -3/-2/-1/0/3/8/9/10、Percentage 40/50/60/100、
Divide 2/3/4/8/16、Column 0/4/10、Gap 0/3/12/20、FixError 开/关、OriginalLN 开/关）：**19 组非 OriginalLN 输出逐项全等**；
4 组 OriginalLN 按“新版保留更多列”的预期差异断言通过；Level -2 的“每列最后音符丢失”等旧 bug **按计划原样保留**。

### 2.4 解析 / 批量层（重写，非逐行）

| 旧 | 新 | 结论 |
| --- | --- | --- |
| `OsuFileProcessor.ReadFile`（手写 v14） | `OsuBeatmapFile.TryLoad`（OsuParsers 1.7.2） | 6 个 fixture 上对象序列/HitSound/Extras/TimingPoints/元数据**逐项相等**（73 项） |
| `OsuFileV14.TimingPointAt` | `OsuManiaBeatmap.TimingPointAt/BeatLengthAt` | 逐行一致（只认 `BeatLength>=0` 红线、区间匹配、兜底最后一条、空列表抛异常） |
| `KeyX` / `ManiaToKeys` / `Column` setter | `KeyX` + `ColumnX` + 保存时重算 X | 3~10K 与旧表逐列相等（探针全列比对）；1/2K 公式兜底（计划修复） |
| `WriteFile`（固定模板、强制 v14） | `OsuParsers.Beatmap.Save` | 命名规则一致；编码 UTF-8 无 BOM、CRLF、往返后列/时间/HitSound/TimingPoints 不变（26 项） |
| `Helper.GetDroppedFiles` | `OsuFileBatch.CollectOsuFiles` | 递归/大小写不敏感/去重正确（3 项）；目录内固定排序（旧版依赖枚举顺序，可复现性更好） |
| 三页面各自逐文件循环 + 进度 | `OsuFileBatch` + VM | 串行、可取消、单文件失败不中断；进度/日志/统计齐全 |

**OsuParsers 相关加固（已验证）**：解码/编码 static 非线程安全 → `OsuBeatmapFile` 内静态 `IoLock` 串行；
探针并发 16 次 `TryLoad` 全部成功（`bridge-lock-parallel`）。非法文件/非 mania/键数 0/12 均被拒绝并给中文原因。

---

## 3. 常量 / 公式 / 取整 / 排序 / 边界核对（全部通过）

| 项 | 旧 | 新 | 结果 |
| --- | --- | --- | --- |
| `INTERVAL / LN_INTERVAL / ERROR`（KC） | 50 / 10 / 1.5 | 同 | ✅ |
| Gap 公式（KC） | `29998.8584·e^(-0.3176·Gap)+347.7248`，0→∞ | 同 | ✅ |
| `DIVIDE_NUMBER / ERROR`（LN） | `[2,4,8,3,6,9,5,7,12,16,48,35,64]` / 2.0 | 同 | ✅ |
| LN `mu/sigma` | -1→(-1,1)、0→(1,100)、L→(L*11, 0.85；8→0.9、9→1) | 同 | ✅ |
| `TimeRound` / `RandDistribution`（Box-Muller） | 原式 | 原式 | ✅ |
| `Invert` 的 `fullDuration-3` 边界、`Transform` 最后音符分支 | 原式 | 原式 | ✅ |
| `AfterTransform` 第二次抽列无 `Column==0→keys` 兜底 | 有 bug | **原样保留** | ✅ |
| -2 `TrueRandom` 丢每列最后一个音符 | 有 bug | **原样保留** | ✅ |
| 稳定排序 / 同刻输出顺序 | `OrderBy(StartTime)` 链 | 同 | ✅（序列逐项相等） |
| 难度名 / Creator 文案 | `[4To7C]`、`[4To7C2B]`、`[LN-Lv{L}-C{C}]`、` & …` | 同 | ✅ |
| 输出文件名 | `Artist - Title (Creator) [Version]` 删 9 非法字符 + 原目录 | 同 | ✅（含非法字符 fixture） |
| 零长度 LN（type=128 end=start） | 读入 `IsLN`，KC 最终压回普通键 | 同 | ✅ |
| Level 公式 | 三段 + NaN | 同 | ✅（19 点 + 文档表抽查） |

---

## 4. 计划修复项验收（Lead 列表逐项）

| # | 计划修复 | 复核结论 | 证据 |
| --- | --- | --- | --- |
| 1 | KeyX 1k/2k 越界 | ✅ 修对：`ColumnX` 3~10K 与旧表逐列相等，1/2K 走公式；1K 谱面可加载/可转 | `keyx-*`、`onek-*` 全过 |
| 2 | KeyConverter HitSound 丢失 | ✅ 修对：复制源音符后 HitSound/Extras/连击保留；旧实现输出全 0，新输出值 ⊆ 源值且含非零 | `kc#…/hitsound-fix` 全过 |
| 3 | Clean=0 误删 | ⚠️ **旧版无此 bug**：外层已判 `clean > 0`，`clean==0` 分支是死代码，旧版实际就是“不清理”。新版保持同一可达行为 → **无需修复，未引入回归**（我此前 `legacy-analysis.md` §3.9 K2 已更正） | 旧/新 18 组含 Clean=0 输出全等 |
| 4 | LN OriginalLN 只最后一列 | ✅ 修对：`AddRange` 累计所有列；4 组 OriginalLN 断言“差异只发生在旧=普通键/新=LN（同列同起点）”通过 | `ln#…/seq-originalLnFix` |
| 5 | LN OD 默认覆盖 | ✅ 修对：默认 `KeepOverallDifficulty=true` 保留原 OD；关闭后写参数值（旧版行为） | `ln-od-fix-default` |
| 6 | TimingPointAt 无红点崩溃 | ✅ 修对：只有绿线时 `TimingPointAt` 抛带说明异常，引擎 catch 成“跳过+原因”，不冒到 UI | `ln-no-red-timingpoint` |
| +a | （复核发现）KC 目标 > 2×源键数死循环 | ✅ 已修：允许重复复制源列；4K→9/10、2K→5、1K→3/4 全部成功 | `kc-hangfix` ×5 |
| +b | （复核发现）1K 谱面 ManiaToKeys/列坐标依赖 KeyX | ✅ 同上 #1 | `onek-convert` |

---

## 5. 与旧版的行为差异（均属改善/已接受；**建议写进工具文档**）

| # | 差异 | 旧 | 新 | 判定 |
| --- | --- | --- | --- | --- |
| D1 | `[Editor]` 段 | 整段丢失 | **保留**（OsuParsers 认识该段） | 更好，可接受 |
| D2 | `osu file format` 版本头 | 永远写 `v14` | **保留输入版本**（v9→v9） | 更好，可接受；修复工具不会再“伪造 v14” |
| D3 | `[Events]` 注释/空行 | 原文整段回写 | 解析成对象后按规范重写（注释被规范化，背景/Break/Storyboard 保留） | 可接受（选型文档已记录） |
| D4 | 未知自定义段 / 未知键 | 丢失 | 丢失（OsuParsers 同样只认已知段） | 与旧一致 |
| D5 | OD 精度 | `double.Parse("8.2")` = 8.2 | 库是 `float` → 8.199999809…；**SR 实测完全一致**（ceil 取整后无差）；界面显示 1 位小数看不出 | 可接受；如后续做“OD 精确编辑”需注意 |
| D6 | 加载时计算 SR 次数 | 旧 Level 页每张算 2 次（构造 + 页面再算）且 UI 线程 | 新加载只算 1 次，后台线程 | 更好 |
| D7 | 非 mania 谱面 | KC/LN 拒绝；Level **不检查**（会算无意义 SR） | 统一在桥层拒绝并给原因 | 更好 |
| D8 | 批量文件顺序 | 依赖系统枚举顺序 | 目录内固定排序 | 更好（可复现） |
| D9 | 文件覆盖/备份 | 同名输出直接覆盖、无备份 | 同（命名规则一致；转谱会改 Creator/难度名，通常生成新文件） | 与旧一致，已在 UI 提示 |

---

## 6. UI 三工具参数复核（对照旧 XAML）

| 参数 | 旧默认/范围 | 新默认/范围 | 结论 |
| --- | --- | --- | --- |
| KC 目标键数 | 8；2~10 | 8；2~10 | ✅ |
| KC Gap / Clean / Blank | 10；0~20 / 4；0~16 / 0；0~9 | 同 | ✅ |
| KC Seed / Creator | `114514` / 不勾 | 同 | ✅ |
| KC 源键数过滤 | 1k~9k 复选，全不勾=全部 | 同（`BuildCheckKeys`） | ✅ |
| LN Level | 3；-3~10 | 3；-3~10 | ✅ |
| LN LN% | 100；0~100（旧步进 1） | 100；0~100（**步进 5**） | ⚠️ 见 §7 P2-1 |
| LN Divide / Column / Gap | 4；1~16 / 10；0~10 / 12；0~20 | 同 | ✅ |
| LN Seed / Ignore / FixError / OriginalLN | `114514` / 勾 / 勾 / 不勾 | 同 | ✅ |
| LN OD | 旧：数值框默认 `"0"`，**总是写回** | 新：“修改 OD”默认不勾=保留原 OD；数值默认 8 | ✅ 计划内修复；默认 8 略随意，见 P2-2 |
| LN 源键数过滤 | 1k~10k | 同 | ✅ |
| Level 表列 | Level/SR/OD/Title/Difficulty/Artist/Creator | 同 + Key数 + 状态列 | ✅ 增加信息，可接受 |
| 工具元数据 | — | Id `osu-key-converter`/`osu-ln-transformer`/`osu-level-calculator`，Order 51/52/53，CategoryPath「音游工具」，emoji 🎹/🍜/⭐ | ✅ 与任务约定一致 |
| 文案 | 旧 Instruction 弹窗 | 整理成折叠「参数说明」；KC/LN/Level 主说明准确 | ✅（除 §7 P1） |

**UI 编译**：`dotnet build src\YuLiangToolkit.Tools.OsuMania\...csproj -c Debug --no-restore` → 0 警告 0 错误（本机实测）。

---

## 7. 问题清单

### P0（Critical / Major，功能）
**无。** 未发现会改错输出、丢数据（相对旧版）、崩溃、死循环或不可复现的功能性问题。

### P1（Major，用户可见文案错误 —— 建议提交前顺手改）
**LN Level -3 的说明与实际行为矛盾**（唯一需要改的用户可见错误）：

- 实际行为（旧/新一致）：**把所有 LN 压成普通键，输出不含 LN；LN% / Column / Gap 全部无效**。
- 错误文案位置 1：`src/YuLiangToolkit.Tools.OsuMania/OsuLnTransformerConfig.cs:127`（`LevelHint`，显示在 Level 滑条 ToolTip）
  现写「…再按 Column / Gap 随机抽轨道变回 LN…」——**不会变回 LN**。
  建议改为：`-3 = Hold off：把所有 LN 压成普通键（输出不含 LN）；这一档 LN% / 转换轨道 / 间隔都不生效`。
- 错误文案位置 2：`docs/tools/osu-ln-transformer.md:37` 同样写了“随机抽一部分变回 LN”，建议同步改成上面这句。
- 正确版本已有：`OsuLnTransformerViewModel.HelpText`（`:20`）写的是对的，可照抄。
- 最小复现：拖一张含 LN 的 4K 谱 → Level=-3 → 输出谱面 `grep ',128,'` 为 0 行（探针 `ln-lv3-holdoff-semantics` 已证明，旧版同样如此）。

### P2（Minor / 可选，不影响提交）
1. `OsuLnTransformerView.xaml:185`：LN% 滑块 `TickFrequency=5`，旧版是 1（旧版可设 1% 精度）；建议改回 1（引擎对 double 百分比本身无限制）。
2. `OsuLnTransformerConfig.cs:73`：「修改 OD」勾选后的默认值 8 略随意；建议默认沿用“原谱 OD”或 0，并在 ToolTip 说明。
3. `LevelRowItem.cs:149`：StarRating 显示 2 位小数；旧 DataGrid 实际显示 3 位（`Math.Round(SR,3)`）。想完全对齐旧版可改 `"0.000"`。
4. `OsuLevelCalculatorViewModel.cs:195-206`：「重新计算」会**重新读盘**；旧版是用已存的 3 位小数 SR 内存重算。新行为更正确，但属差异，建议文档一句话说明。
5. 文档补充：§5 的 D1/D2/D3（`[Editor]` 保留、版本头保留、Events 规范化）与 D5（OD float）建议写进 `docs/tools/osu-*.md` 的“注意”里，避免用户以为输出与旧版逐字节一致。

---

## 8. 覆盖范围与限制

- 探针共 **396 项断言**：参考值 25、解析桥 73、KeyConverter 103（18 组参数 × 逐项 + 修复/拒绝/死循环）、
  LNTransformer 138（23 组 × 逐项 + 修复/拒绝）、Level 5、写回往返 26、拒绝/边界 18、确定性与批量/并发 8。
- 未覆盖：真实 osu! 客户端行为（只验到文件读写层面）；Windows 之外的区域设置（新代码整体用 OsuParsers + Invariant 输出，
  旧版 `double.Parse` 的 culture 隐患已被换库消除）；UI 截图（由 osu-tools-ui/Lead 的冒烟负责）。
- 旧实现对照使用的是**旧源码原样**，但为编译剔除了 WPF 专属门面（`GetOsuPath`/MessageBox/Dispatcher/进度条）；
  被剔除部分不参与算法。
- 参考值交叉核对：`docs/porting/star-rating-reference.md` 的 10 行中 8 行可直接由 `.osu` 文件复现（旧/新都命中 10 位小数）；
  #09/#10 是内存对象的 rate 值，已用“旧/新交叉相等 + rate≠rate1”验证。

## 9. 复现命令（探针在 %TEMP%，不入库）

```powershell
# 旧算法原样 + 新 Core 源码同进程编译的独立探针
cd "$env:TEMP\ylt-review\probe"
dotnet build -c Release
dotnet run -c Release --no-build        # 期望：PASS 396 / FAIL 0，退出码 0

# UI 项目编译
dotnet build src\YuLiangToolkit.Tools.OsuMania\YuLiangToolkit.Tools.OsuMania.csproj -c Debug --no-restore
```

> 结论：**算法移植可信、修复项全部修对、UI 参数与旧版一致；除 §7 P1 两处文案外无需修改，可提交。**
> 另：`legacy-analysis.md` §3.9 K2（Clean=0 误删）已在复核后更正为“不成立”。
