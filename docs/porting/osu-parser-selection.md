# osu! `.osu` 解析库选型与可行性验证（YuLiangToolkit 移植用）

> 目标：为 YuLiangToolkit 移植旧 `HeavenUsurperToolkit` 的三个 osu!mania 工具（KeyConverter / LNTransformer / LevelCalculator），
> 替换手写的 `HeavenUsurperToolkit-master/OsuFileV14.cs`，改用更成熟的第三方解析库/源码。
>
> 实测环境：Windows 10/11（`Microsoft Windows NT 10.0.26300`）、.NET SDK **9.0.319**、目标框架 **`net9.0-windows`（WPF）**；
> NuGet 源 `nuget.org`（+ 华为云镜像）。所有探针工程均在 `%TEMP%\ylt-osu-probe\`，**没有写进仓库、没有加进解决方案**。
> 查证/实测日期：2026-10-24。

---

## 0. 结论（TL;DR）

| 项 | 结论 |
| --- | --- |
| **首选** | **`OsuParsers` 1.7.2**（NuGet；MIT；netstandard2.0；唯一传递依赖 `System.Numerics.Vectors` 4.5.0，nupkg 合计 ≈ 443 KB）。已在本机**真还原、真解码、真改对象、真写回、再解码**通过；与 `net9.0-windows` 兼容实测通过。**项目已按它接入**（见 4.1）。 |
| **备选（NuGet 不可用 / 必须零依赖时）** | **vendor `OsuParsers` 的 MIT 源码子集**（69 个 `.cs` / 3,266 行 / 120 KB），打 3 个小补丁（static 解码器改实例、写回保真、mania 列工具）。见 4.2。 |
| **否决** | `ppy.osu.Game`（最新只支持 net10.0，net9 实测 **NU1202**；net8 旧版能跑但发布要多 **135.9 MB / 189 个依赖**，且只读不能写、离线构建不可行）；`OsuSharp`（只是 osu!api v2 客户端，不解析 `.osu`）；`MapWizard.BeatmapParser`（只支持 v14/v128，写回强制升到 v14）；其余小众包（见 2）。 |
| **星数（LevelCalculator）** | 三个解析库**都不提供**星数。`ppy.osu.Game` 能算官方 mania SR，但与旧 `StarRating.cs` **不是同一套算法**（同图实测 2.353491 vs 3.546690）；LevelCalculator 的等级公式是按旧 SR 拟合的 → **保留旧 `StarRating.cs` 的移植**，不要换官方 SR。详见 `docs/porting/star-rating-reference.md` / `star-rating-parity-report.md`。 |
| **关键风险（必须遵守）** | ① `OsuParsers.BeatmapDecoder` 是 **static 状态、非线程安全**：并发实测 100 次出现 **56 次异常 + 40 次结果串台** → 解析必须**串行/加锁**，禁止并行解析。② 它的 `Save()` 是**全量重新序列化**（未知字段、未知 section、注释、事件注释注释会丢/被规范化），与旧实现「`[Events]` 原样回写」不同。③ 空文件/非 osu 文本**静默返回空谱**（不抛异常），桥层必须自己校验 `Mode=3`、对象数、CS。 |

---

## 1. 需求梳理：旧实现实际用到哪些能力

| 能力 | 旧代码位置 | 说明 |
| --- | --- | --- |
| 读 8 个区段：General / Editor / Metadata / Difficulty / Events / TimingPoints / Colours / HitObjects | `OsuFileV14.cs:29-226`、`:466-693` | 手写 line-by-line；`[Editor]` 实际**没有解析**（读了也没存） |
| mania 普通 note（`type=1`）、LN（`type=128`，结束时间在 `hitSample` 首段） | `OsuFileV14.cs:814-968` | `EndTime` getter/setter 直接操作 `HitSample` 字符串 |
| 列号 = `floor(x * CircleSize / 512)`；写回用中心坐标 `KeyX` 表（3~10K） | `OsuFileV14.cs:284-298`、`:881-896` | 4K 中心 x = 64/192/320/448 |
| TimingPoint 8 列（Time, BeatLength, Meter, SampleSet, SampleIndex, Volume, Uninherited, Effects）；`TimingPointAt()` 只看红线 | `OsuFileV14.cs:790-812`、`:383-406` | 写回时固定 8 列 |
| 写回 `.osu`（全量重写，`[Events]` 原文保留） | `OsuFileV14.cs:696-787` | 版本头写死 `v14`（`General.Version` 默认 `"v14"`，**从不解析真实版本**） |
| 改名：`Version` 前缀 `[4To7C]`、`Creator += " & KeyConverter"` 之类 | `KeyConverter.xaml.cs:343-357`、`LNTransformer.xaml.cs:357-371` | 关系到写回字段 |
| 星数 → 等级 | `OsuFileV14` 构造 → `StarRating.cs`；`LevelCalculator.xaml.cs:94-118` | 875 行 mania SR 算法 + 等级拟合公式 |

**旧手写解析器的硬伤（这就是要换的理由）**：
1. TimingPoint 固定 `split[0..7]`、HitObject 固定 `split[0..5]` → 老谱面少一列就 `IndexOutOfRangeException`；
2. 完全不读 `osu file format vN`，**任何输入写回都变成 v14**；
3. 不解析/不保留 `[Editor]`；未知字段、未知区段全部丢；
4. `StartsWith("Title")` 这种前缀匹配写法脆弱（靠判断顺序兜住）；
5. 没有 Mode 校验（manzia 判断在调用方做）。

**新库必须满足**：读 v3~v14 兼容（至少不崩且字段容错）、mania note/LN 对象模型、timing/BPM、可写回、`net9.0-windows` 可用、离线构建可控。

---

## 2. 候选清单（NuGet / 源码元数据，2026-10-24 查证）

| 候选 | 最新版 / 发布 | TFM | License | 依赖 / nupkg 体积 | 读 | 写回 | mania LN | timing/BPM | 星数 | 维护/下载 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **OsuParsers** | 1.7.2（2025-01-11） | `netstandard2.0` | **MIT**（仓库 LICENSE，Copyright 2021 mrflashstudio） | 仅 `System.Numerics.Vectors 4.5.0`；nupkg 68.9 KB（传递包 373.8 KB） | ✅ | ✅ `Beatmap.Save()` | ✅ `ManiaNote` / `ManiaHoldNote` | ✅ `TimingPoints` / `BeatLengthAt` | ❌ | 25,148 下载；最新发布 2025-01，之后无新版（功能已稳定） |
| ppy.osu.Game | 2026.916.0 / 早期 2025.905.0（net8） | 最新 **net10.0**；2025.905.0 为 `net8.0` | MIT | 24 个直接依赖（Realm/SQLite/SignalR/Sentry/TagLib…）+ `ppy.osu.Framework` + `ppy.osu.Game.Resources`（**109 MB nupkg**） | ✅ `LegacyBeatmapDecoder` | ❌ 无 encoder | 经 ruleset 转换 | ✅ | ✅ `ManiaDifficultyCalculator` | 官方活跃维护 |
| ppy.osu.Game.Rulesets.Mania | 2026.916.0（net10）/ 2025.905.0（net8） | 同上 | MIT | 只依赖 `ppy.osu.Game` | — | — | 提供 mania 规则集/难度计算 | — | ✅ | 官方活跃 |
| **osu!lazer `LegacyBeatmapDecoder`（源码）** | master | 源码 | MIT | 依赖整个 `osu.Game` + `osu.Framework` 栈 | ✅ | ❌ | 需 mania 规则集包 | ✅ | 需 ruleset | 官方活跃 |
| MapWizard.BeatmapParser | 1.0.6 | `net8.0;net9.0;net10.0` | MIT | **0 依赖**；449 KB 缓存 | ✅ | ✅ `Beatmap.Encode()` | ✅ `ManiaHold` | ✅ | ❌ | 1,404 下载；新项目 |
| OsuSharp | 6.2.0 | `net6.0` | MIT | 5 个依赖（OsuSharp.Domain/JsonModels、Microsoft.Extensions.*） | ❌ | ❌ | ❌ | ❌ | ❌ | 只是 **osu!api v2 客户端** |
| LAsOsuBeatmapParser | 1.0.4 | `net8.0` | MIT | 0 依赖 | 未实测 | 未实测 | 未实测 | 未实测 | ❌ | 1,323 下载；单维护者 |
| cz.lukynka.OsuBeatmapParser | 1.1.0 | `net8.0` | **未知**（nuspec 无 license） | 0 依赖 | 未实测 | 未实测 | 未实测 | 未实测 | ❌ | 465 下载；自述 WIP |

---

## 3. 实测记录

### 3.1 环境与 NuGet 可达性

```
dotnet --version           -> 9.0.319
dotnet nuget list source   -> nuget.org (https://api.nuget.org/v3/index.json)
                              Huawei   (https://repo.huaweicloud.com/repository/nuget/v3/index.json)
Invoke-WebRequest https://api.nuget.org/v3/index.json -> 200
dotnet add package OsuParsers --version 1.7.2
  -> 已还原（用时 2.13 秒）；包与项目所有框架均兼容
```

> 结论：**NuGet 可达**，不需要走「完全离线手工投放」路线。

### 3.2 OsuParsers 1.7.2 探针（`%TEMP%\ylt-osu-probe\probe-osuparsers`）

**PackageReference（实测可用；先用 net9.0 跑，再改 `net9.0-windows` 复跑，两次 exit 0）：**

```xml
<ItemGroup>
  <PackageReference Include="OsuParsers" Version="1.7.2" />
</ItemGroup>
```

**测试素材（自造，纯离线）**：最小 4K mania `.osu`（1,059 B，v14）——`[General]`（含 Mode:3、未知键 `SampleVolume: 60`）、
`[Editor]`、`[Metadata]`（含中文 TitleUnicode、未知键 `CustomKey`）、`[Difficulty]`（CS=4, OD=8）、
`[Events]`（背景图 + Break + 一条 Storyboard Sprite + 注释）、`[TimingPoints]`（500 红线 + 1500 绿线）、
`[Colours]`、`[HitObjects]`（4 个普通 note + 1 个 LN `320,192,1500,128,0,2500:0:0:0:0:`）。

**解码实测输出（节选）：**

```
Version          = 14
General.Mode     = Mania (ModeId=3)
TitleUnicode     = YLT 解析器探针
Tags[]           = [ylt | probe | 测试]
CS/OD/HP/AR      = 4 / 8 / 8 / 5
Background       = bg.jpg
Breaks           = [5000-8000]
Storyboard BG 层 = 1 个对象
TimingPoints     = 2
   offset=500  beatLength=500 meter=SimpleQuadruple sampleSet=Soft vol=60 inherited=False effects=None
   offset=1500 beatLength=-50 ... inherited=True effects=Kiai
BeatLengthAt(1600) = 250   ← 注意：该 API 会乘绿线 SV，取 BPM 要自己过滤 Inherited
HitObjects       = 5
   ManiaNote     start=1000 end=1000 x=64  column=0
   ManiaNote     start=1250 end=1250 x=192 column=1
   ManiaHoldNote start=1500 end=2500 x=320 column=2   ← LN 正确
   ManiaNote     start=1750 end=1750 x=448 column=3   (hitsound=Whistle)
   ManiaNote     start=2000 end=2000 x=64  column=0
```

**写回实测**：改 Title/Creator/OD、把 LN 结束时间 2500→2600、改红线音量，`beatmap.Save(path)` 成功；
输出 **UTF-8 无 BOM、CRLF**；`osu file format v14` 头保留；LN 行变成 `320,192,1500,128,0,2600:0:0:0:0:`。

**写回保真度 diff（原始 vs roundtrip）**：

```
- 丢失: SampleVolume: 60          （未知 [General] 键被丢）
- 丢失: CustomKey: should-be-lost （未知 [Metadata] 键被丢）
- 丢失: 原始 [Events] 注释行       （改成规范的 //Storyboard Layer 0..4 等）
+ 新增: //Storyboard Layer 0 (Background) ... (7 层注释)
+ 新增: [TimingPoints] 后多一个空行（osu!stable 习惯）
（HitObjects / Colours / Editor / Break / Storyboard Sprite 均正确保留；对象顺序不变）
```

**版本兼容**：把 header 改成 `v9` 后仍能解码（Version=9），`Save()` 首行**保留 v9**。

**边界/容错实测：**

| 输入 | 行为 |
| --- | --- |
| 空文件 | **不抛异常**，Version=14、objects=0（静默成功，危险） |
| 非 osu 文本 | **不抛异常**，objects=0（静默成功，危险） |
| `[TimingPoints]` 只有 2 列 | 不抛异常，objects 正常 |
| `[HitObjects]` 只有 3 列 | `IndexOutOfRangeException` |
| 没有 `[HitObjects]` 区段 | 不抛异常，objects=0 |

**线程安全实测（源码里 `Beatmap` / `currentSection` / `sbLines` 都是 `static`）：**

```
并发 Decode（两个线程各解一个不同标题的文件）x100：
  异常 = 56 次  结果串台 = 40 次
  首个异常：FormatException: The input string 'Combo2 : 0' was not in a correct format.
```

> ⚠ **必须串行/加锁**。三个工具当前是逐个文件串行处理，天然安全；但新实现若用 `Parallel.ForEachAsync` 会随机崩。

**真实 4K mania 谱面实测**（`real-mania.osu`：26,968 B，v14，860 行；`himmel - Ever Dream [Ran's Dream]` by `__Ag`；
取自公开 MIT 仓库 [maotovisk/BeatmapParser](https://github.com/maotovisk/BeatmapParser) 的测试资源）：

```
文件原始行统计：objects=795（其中 LN=482）、TimingPoints=11
OsuParsers 解析：objects=795、ManiaNote=313、ManiaHoldNote=482、TimingPoints=11
列分布 = 0:163  1:241  2:235  3:156；时间升序 = True
写回 27,829 B → 再解析 objects=795 / LN=482 / TPs=11 / Title='Ever Dream'（语义一致）
```

**体积影响（net9.0-windows 控制台探针）**：

| 项 | 数值 |
| --- | --- |
| `OsuParsers` nupkg | 68.9 KB |
| `System.Numerics.Vectors 4.5.0` nupkg | 373.8 KB |
| 输出目录新增 dll | `OsuParsers.dll` 125,952 B（System.Numerics.Vectors **不复制**，.NET 9 框架自带） |
| 全局 NuGet 缓存占用（两包） | ≈ 2.8 MB |

### 3.3 MapWizard.BeatmapParser 1.0.6 探针（同一探针工程）

- v14 自造谱：`objects=5 / holds=1 / TPs=2 / CS=4 / Title OK`；改 LN 结束时间后 `Encode()` 成功。
- v9 谱：`Decode` 后 `Version=9`，但 `Encode()` 首行 = **`osu file format v14`（强升，旧谱面被改头）**。
- 真实 mania 谱：`objects=795 / holds=482 / TPs=11`（与 OsuParsers 一致），`Encode()` 27,785 chars。
- 缺 section（只有 `[General]`）：抛 `Exception: Error while parsing General section`（容错弱于 OsuParsers）。
- 结论：**只支持 v14/v128**（README 明说，编码只保证 14/128）。不满足本项目旧谱面兼容要求 → 否决；若将来只处理 v14，可作为「零依赖、API 现代」的备选。

### 3.4 ppy.osu.Game 探针（`%TEMP%\ylt-osu-probe\probe-ppygame-net8`）

**最新版直接不可用（真实错误）**：net9.0 工程执行 `dotnet add package ppy.osu.Game`：

```
error: NU1202: 包 ppy.osu.Game 2026.916.0 与 net9.0 (.NETCoreApp,Version=v9.0) 不兼容。
       包 ppy.osu.Game 2026.916.0 支持: net10.0 (.NETCoreApp,Version=v10.0)
```

**net8 时代版本（2025.905.0）在 net9 工程可引用、可 headless 跑通**：

```xml
<ItemGroup>
  <PackageReference Include="ppy.osu.Game" Version="2025.905.0" />
  <PackageReference Include="ppy.osu.Game.Rulesets.Mania" Version="2025.905.0" />
</ItemGroup>
```

```csharp
// 关键步骤（探针实测可运行；完整代码见 %TEMP% 探针工程）
Assembly.Load("osu.Game.Rulesets.Mania");                 // AssemblyRulesetStore 只扫已加载程序集
using var reader = new osu.Game.IO.LineBufferedReader(File.OpenRead(path));
var beatmap = new LegacyBeatmapDecoder(14).Decode(reader); // BeatmapInfo.Ruleset = mania
var ruleset = new ManiaRuleset();
var attrs = ruleset.CreateDifficultyCalculator(new SimpleWorkingBeatmap(beatmap)).Calculate();
Console.WriteLine(attrs.StarRating);
// SimpleWorkingBeatmap 需实现 GetBeatmap()/GetBackground()/GetBeatmapTrack()/GetSkin()/GetStream()
```

**实测输出：**

```
ppy.osu.Game assembly = 2025.905.0.0
decode OK: ruleset=mania (onlineID=3) objects=795 CS=4 title='Ever Dream'
ManiaDifficultyCalculator: Version=20241007 StarRating=2.353491 MaxCombo=2282
```

**代价（实测）**：

| 项 | 数值 |
| --- | --- |
| restore 用时 | 12.7 s（两个包，含传递依赖；已有部分缓存） |
| 直接+传递库数量 | **189** |
| `dotnet build` 输出目录 | **135.9 MB / 84 个文件**，其中 `osu.Game.Resources.dll` **104.6 MB** |
| 能力 | 只读（`LegacyBeatmapDecoder`）；**没有 `.osu` 写出器** |
| 离线构建 | 需要 `ppy.osu.Game.Resources`（109 MB nupkg）+ 20+ 依赖，与「离线 build.ps1 + 零依赖」约定冲突 |

> 结论：**技术上能 headless 解码 mania 谱、也能直接算官方 SR**，但依赖/体积/离线/写回四项都不满足本项目 → 否决。
> 仅当未来升级到 **net10**、且只需要「官方 SR 校验」、且能接受发布体积 +100 MB 时再考虑。

### 3.5 osu!lazer `LegacyBeatmapDecoder`（MIT 源码）移植评估

`LegacyBeatmapDecoder.cs` 本身约 550 行，但 `using` 横跨 20+ 命名空间，依赖：

- `osu.Game.Rulesets` 的 `RulesetStore` / `AssemblyRulesetStore`（扫描 AppDomain 已加载程序集，需要 ruleset 包，且 `RulesetStore` 字段是 `internal static`，外部无法注入）；
- `osu.Game.Rulesets.Objects.Legacy.ConvertHitObjectParser`（约 600 行，osu!standard 的 hitobject 解析/转换）；
- `LegacyControlPointInfo` + `TimingControlPoint` / `DifficultyControlPoint` / `EffectControlPoint` / `SampleControlPoint`；
- `Beatmap` / `BeatmapInfo` / `BeatmapMetadata` / `HitObject` / `IBeatmap` / `BreakPeriod` 等模型；
- `osu.Framework` 的 `Logger`、`LineBufferedReader`、扩展方法等。

估算要原样搬动 **≥ 30 个文件 / 8,000+ 行**（还要裁剪 `osu.Framework`），工作量接近重写一套；且它**只读、没有写出器**。
→ 不推荐「为了省一个包而移植 lazer 源码」；如果目标是与官方行为 100% 一致，直接引 ppy 包（见 3.4）更省事，但那又是另一套代价。

### 3.6 OsuSharp（否决）

NuGet 描述：`An osu!api v2 wrapper`。只提供 osu! 官方 API 的 HTTP 客户端（谱面搜索/下载等），**不含 `.osu` 文件解析与写回**，
与本次「替换本地解析器」需求无关。（本项目已有 osu! 下图器的 `OsuApiClient`，也不需要它。）

---

## 4. 推荐方案（可直接执行）

### 4.1 首选：`OsuParsers` 1.7.2（NuGet 包）

**接入位置（已落地，仓库当前状态）：**
`src/YuLiangToolkit.Tools.OsuMania/YuLiangToolkit.Tools.OsuMania.csproj`

```xml
<ItemGroup>
  <!-- osu! 谱面（.osu）解析 / 保存：只在这一层出现，算法层不直接依赖第三方类型。 -->
  <PackageReference Include="OsuParsers" Version="1.7.2" />
</ItemGroup>
```

**隔离依赖的三条纪律：**
1. **只在 `Tools.OsuMania` 层引用**，`YuLiangToolkit.Core` 保持零依赖；算法/UI 层只依赖自己定义的 `OsuBeatmap` DTO 或包装类型，不直接 `using OsuParsers.*`；
2. **解析入口必须串行**：解码器是 static 状态（3.2 实测 56% 异常率），建议在解析桥里集中加 `lock`（或单线程队列），禁止 `Parallel.ForEach` / `Task.WhenAll` 并发解码；
3. 写回只走 **「解码 → 改对象 → `Save()`」** 一种通道；不要多处自拼 `.osu` 文本。

**最小示例（API 与实测一致）：**

```csharp
using OsuParsers.Beatmaps;
using OsuParsers.Beatmaps.Objects;
using OsuParsers.Beatmaps.Objects.Mania;
using OsuParsers.Decoders;

public static class OsuBeatmapBridge
{
    private static readonly object DecodeGate = new();   // OsuParsers 解码器是 static 的，必须串行

    public static Beatmap Load(string path)
    {
        lock (DecodeGate) return BeatmapDecoder.Decode(path);
    }

    public static void Example(string inPath, string outPath)
    {
        var bm = Load(inPath);

        if (bm.GeneralSection.Mode != OsuParsers.Enums.Ruleset.Mania || bm.DifficultySection.CircleSize is < 1 or > 18)
            throw new InvalidDataException("不是合法的 mania 谱面");

        int keys = (int)bm.DifficultySection.CircleSize;

        // 遍历：普通 note / LN 分开处理（LN 的结束时间就是 EndTime）
        foreach (var h in bm.HitObjects)
        {
            if (h is ManiaHoldNote ln)
            {
                ln.EndTime = Math.Max(ln.EndTime, ln.StartTime + 1);  // 改 LN 时长
            }
            else if (h is ManiaNote note)
            {
                int column = note.GetColumn(keys);                     // 列号 = x / (512/keys)
                note.SetColumn(keys, column);                          // 会写成列左边界 x=column*512/keys
                // 若要和旧实现一样写中心坐标，自己按旧 KeyX 表赋 Position：
                // note.Position = new System.Numerics.Vector2(KeyX[keys][column], 192);
            }
        }

        // 红线（BPM）要自己过滤 Inherited；BeatLengthAt() 会乘绿线 SV 倍率
        var red = bm.TimingPoints.Where(p => !p.Inherited).OrderBy(p => p.Offset).ToList();
        double bpmAt1000 = 60000 / red.Last(p => p.Offset <= 1000).BeatLength;

        bm.MetadataSection.Version = "[4To7C]" + bm.MetadataSection.Version;  // 旧工具改名规则
        bm.MetadataSection.Creator += " & KeyConverter";

        bm.Save(outPath);   // 全量重写：UTF-8 无 BOM、CRLF；原版本号保留
    }
}
```

**写回保真度与旧实现的差异（务必知晓）：**

| 行为 | 旧 `OsuFileV14.WriteFile` | `OsuParsers.Save()` |
| --- | --- | --- |
| `[Events]` | **原文整段回写**（注释/未知事件保留） | 解析成对象后按规范重写（会重排/改写注释，Storyboard 对象保留） |
| 未知字段/未知区段 | 丢弃 | **丢弃** |
| 版本头 | 永远写 `v14` | **保留输入版本号** |
| 数值格式 | 直接 `ToString` | 规范化格式（例如 `7.5`） |

> 三个工具本来就是「整体重写」的工作方式，所以**功能上可用**；但若产品要求「尽量保留未知字段/原始格式」，写回需要改为
> 「读原文件行 → 只替换 `[General]/[Metadata]/[Difficulty]/[HitObjects]` 中变化的行」的自写 Writer（约 150~250 行，可作为后续增强）。
> 当前建议：**先接受 OsuParsers 的全量重写**，在工具文档里注明「处理后会规范化 `[Events]`、丢弃未知字段」。

**对离线构建 / 发布的影响：**

- `build.ps1` **不用改**：它走本机 NuGet 缓存；开发机联网 `dotnet restore` 一次后，缓存里就有
  `OsuParsers 1.7.2` + `System.Numerics.Vectors 4.5.0`（合计 nupkg ≈ 443 KB），之后离线可构建。
- 新机器 / CI：要么联网一次预热缓存，要么手工把这两个 nupkg 放进 `%USERPROFILE%\.nuget\packages`。
- 发布产物：只多 `OsuParsers.dll` ≈ **123 KB**；`System.Numerics.Vectors` 不会进发布目录（.NET 9 自带）。
- 与「零 NuGet 依赖」约定的冲突：**仅此一个包（+1 个框架内传递依赖）**；换来 v14 完整解析、容错、写回、跨版本读取，
  相比继续维护 1,117 行手写解析器更划算。是否值得：**值得**。

### 4.2 备选：vendor `OsuParsers` 的 MIT 源码（NuGet 不可用时）

- 从 `https://github.com/mrflashstudio/OsuParsers`（LICENSE = MIT，Copyright 2021 mrflashstudio）取**解析+写回所需子集**：
  `Beatmaps/`（22 文件）、`Decoders/`（4）、`Encoders/`（4）、`Enums/`（24）、`Helpers/`（4）、`Storyboards/`（11），
  合计 **69 个 `.cs` / 3,266 行 / 120 KB**；`Database/`、`Replays/`、`LZMA/`、`SevenZip/`、`Serialization/` 等 28 文件 / 3,869 行**删掉**。
- 必须打的补丁：
  1. **解码器 static 状态 → 实例类**（线程安全；或保留 static 但对外只暴露串行 wrapper —— 不推荐）；
  2. **写回保真**：`Encoder` 增加「未知行/未知字段」透传（或在项目里写行级 Writer）；
  3. （可选）`ManiaNote` 列工具、旧 `KeyX` 中心坐标表、v<5 的 24 ms timing offset 兼容。
- 保留 MIT 版权头 + LICENSE 文件；放进 `src/YuLiangToolkit.Tools.OsuMania/Core/Osu/` 或 `Core/Osu/`。
- 工作量估算：裁剪/移植 0.5 天 + 补丁 0.5~1 天 + 回归 0.5 天。

### 4.3 否决清单

| 候选 | 否决理由 |
| --- | --- |
| `ppy.osu.Game` / `ppy.osu.Game.Rulesets.Mania` | 最新只支持 **net10**（net9 实测 NU1202）；net8 旧版虽可跑，但 189 个依赖、发布 +135.9 MB、**只读无写回**、离线构建不可行。只有「将来升 net10 + 只要官方 SR」才值得重新评估。 |
| `OsuSharp` | 是 osu!api v2 客户端，不解析 `.osu`。 |
| `MapWizard.BeatmapParser` | 只支持 v14/v128；写回强制把老版本谱面改头成 v14。若产品只处理 v14 可再议。 |
| `LAsOsuBeatmapParser` / `cz.lukynka.OsuBeatmapParser` | 下载量小、单人维护、写回/mania LN 未验证；`cz.lukynka` 包甚至没有 license 元数据。不用于生产。 |
| 移植 lazer `LegacyBeatmapDecoder` 源码 | 需要 ≥30 文件/8k+ 行 + 整个 ruleset/control-point/模型栈，且只读无 encoder；工作量远大于引包或 vendor OsuParsers。 |

### 4.4 星数（LevelCalculator）单独结论

- **三个解析库都不提供星数**（OsuParsers / MapWizard / OsuSharp）。`ppy.osu.Game` 的 `ManiaDifficultyCalculator` 可以算（3.4 实测），但代价见上。
- **旧 `StarRating.cs` 与 ppy 官方算法不是一套**：同一张真实 4K 谱（Ever Dream，OD=7）：
  - 旧 `StarRatingCalculator.CalculateStarRating(...)`（原样拷贝跑，rate=1.0）= **3.546690**；
  - ppy.osu.Game 2025.905.0 `ManiaDifficultyCalculator`（Version=**20241007**，无 mod）= **2.353491**；
  - 差 **+1.193199**（旧实现明显更高，属于不同算法/校准版本；旧实现是社区 StarRatingRebirth 的 C# 移植）。
- **结论：保留旧 `StarRating.cs` 的移植**（875 行 + `Utils.cs` 125 行 + 极简 `ManiaHitObject`），放进 `Tools.OsuMania/Core/Difficulty/`。
  LevelCalculator 的等级拟合公式（`LevelCalculator.xaml.cs:13-14` 的 `LOWER_BOUND=2.76257856739498`、`UPPPER_BOUND=10.5541834716376`，
  `CalculateLevel()` :94-118）是按旧 SR 数值拟合的，**换成官方 SR 会让等级整体漂移**。
- **License 注意**：`StarRating.cs` 文件头没有版权声明；其算法与社区项目 `sunnyxxy/Star-Rating-Rebirth`（Python，仓库**没有 LICENSE 文件**）高度对应；
  但其算法的数学描述源自 ppy/osu（MIT）。移植时建议在文件头注明「算法来源 osu!lazer（MIT）/ StarRatingRebirth 复刻」并保留相应声明；
  `Utils.cs` 已带 ppy MIT 头，保留即可。
- 新旧实现数值对齐工作见配套文档：`docs/porting/star-rating-reference.md`（旧实现参考答案，10 组）、`docs/porting/star-rating-parity-report.md`（对齐报告）。
  **对齐结论（2026-10-24）**：新实现（快照 `StarRatingCalculator.cs` sha256 `d1365739…`）对 10 组确定性合成谱（4/7/10K、OD 0/8/9.5、稀疏/中/高密度、含 LN、rate 1.0/1.5）与旧实现**10/10 完全一致（差 0）**，
  且 `.osu → OsuParsers 解析桥 → 新实现` 端到端 10/10 一致；唯一差异是空谱面（旧实现抛异常、新实现返回 0，与旧 `OsuFileV14` 构造器 try/catch 的产品行为等价）。

---

## 5. 复现步骤（探针）

```powershell
# 1) 建探针工程（%TEMP%，绝不进仓库/解决方案）
$probe = "$env:TEMP\ylt-osu-probe\probe-osuparsers"
New-Item -ItemType Directory -Force $probe | Out-Null; Set-Location $probe
dotnet new console -n OsParsersProbe -o . -f net9.0-windows
dotnet add package OsuParsers --version 1.7.2
dotnet run -c Release     # 输出：解码字段、roundtrip、diff、真实谱面统计、并发探针

# 2) 想复跑 MapWizard 对比：再 dotnet add package MapWizard.BeatmapParser --version 1.0.6

# 3) ppy.osu.Game 对比（可选，占 ~136 MB 输出）：
#    dotnet add package ppy.osu.Game --version 2025.905.0
#    dotnet add package ppy.osu.Game.Rulesets.Mania --version 2025.905.0
```

探针工程位置（本机，可随时重跑）：
`%TEMP%\ylt-osu-probe\probe-osuparsers\`（OsuParsers + MapWizard）、
`%TEMP%\ylt-osu-probe\probe-ppygame-net8\`（ppy.osu.Game）、
`%TEMP%\ylt-osu-probe\probe-oldstar\`（旧 StarRating.cs 对照）、
日志：`%TEMP%\ylt-osu-probe\probe-osuparsers.log` 等。

---

## 6. 风险清单（接入后要盯的）

| 风险 | 影响 | 处置 |
| --- | --- | --- |
| `BeatmapDecoder` static 非线程安全 | 并发解码随机异常/串数据 | 解析桥统一 `lock` + 串行；自检里加并发用例（预期必须是「安全」或明确串行） |
| `Save()` 全量重写丢未知字段/注释、`[Events]` 规范化 | 用户谱面被「清洗」 | 在工具文档写明；需要保真时改行级 Writer（4.1 表） |
| 空文件/非 osu 文本静默成功 | 误判为「0 个对象」继续处理 | 桥层校验：Mode==Mania、CS 合法、`HitObjects.Count>0`；失败给中文错误 |
| v<5 谱面的 24 ms timing offset 不处理 | 极老谱面 timing 偏 24 ms | 本次三个工具主要面向 v14 mania 谱；在文档标注限制 |
| `BeatLengthAt()` 含 SV 倍率 | BPM 显示错误 | 统一用 `TimingPoints.Where(!Inherited)` 自己算 BPM |
| Tags 被重排（逗号/空格→空格） | meta 格式变化 | 可接受；或写回前把 Tags 原样保留 |
| 上游 2025-01 后无新版 | 无新特性/修复 | MIT 可自行 fork；当前功能面足够 |
| 旧手写解析器仍在仓库 | 双实现漂移 | 三个工具接入完成后删除/停用 `OsuFileV14` 的解析职责，只保留算法需要的 DTO |

---

## 附录 A：查证链接

- OsuParsers NuGet：<https://www.nuget.org/packages/OsuParsers>（1.7.2，2025-01-11）
- OsuParsers 源码/License：<https://github.com/mrflashstudio/OsuParsers>
- ppy.osu.Game：<https://www.nuget.org/packages/ppy.osu.Game> ；ruleset：<https://www.nuget.org/packages/ppy.osu.Game.Rulesets.Mania>
- osu!lazer `LegacyBeatmapDecoder` 源码：<https://github.com/ppy/osu/blob/master/osu.Game/Beatmaps/Formats/LegacyBeatmapDecoder.cs>
- MapWizard.BeatmapParser：<https://www.nuget.org/packages/MapWizard.BeatmapParser> ；源码：<https://github.com/maotovisk/BeatmapParser>
- OsuSharp：<https://www.nuget.org/packages/OsuSharp>（osu!api v2 wrapper）
- LAsOsuBeatmapParser：<https://www.nuget.org/packages/LAsOsuBeatmapParser>
- cz.lukynka.OsuBeatmapParser：<https://www.nuget.org/packages/cz.lukynka.OsuBeatmapParser>
- StarRatingRebirth（旧 SR 算法来源之一）：<https://github.com/sunnyxxy/Star-Rating-Rebirth>
