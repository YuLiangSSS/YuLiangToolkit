# 验证纪律：自检只做「够用」的那部分

> 入口是根 [AGENTS.md](../../AGENTS.md)；本页只管「这轮改动该跑哪些自检」。踩坑细节看 [pitfalls.md](pitfalls.md)。
> 一句话：**用脚本按影响面选组，别每轮全量**；全量是四个明确触发点才配用的「重武器」。

## 总则：映射覆盖 ≠ 断言覆盖

`check.ps1` 只负责「选出该跑哪些组 + 跑 + 汇总」。跑绿只证明**被选中的组里现有断言全部通过**，
不证明「改动的文件被验证了」。以下三类必须额外人工冒烟，`--all` 也替代不了：

1. **没有自检组的项目**：`Tools.VideoToAudio`（视频转音频）、`Tools.Sample`（框架模板）；
2. **有组但无断言的文件**：`App.xaml` / `App.xaml.cs` / `MainWindow.*` / `Converters/**`、
   `Core/Controls/**`、`Core/Services/FFmpeg*`、`ToolDiscovery`、`GomokuViewModel` 等；
3. **自检会 fail-open 跳过的用例**：如 `map-downloader` 假镜像端口被占时整段记 PASS（日志里写「跳过」）。
   绿之前先扫一眼日志里的「跳过」行（`check.ps1` 对网络类组会主动提示）。

## 为什么要这样

- 改文档、改文案、只动一个功能时，全量的**边际收益接近 0**，成本却是分钟级等待 + 几千行输出；
- 真正需要全量的是**跨层改动 / 交付前 / bug 复发区**——这些地方出错代价远高于自检成本；
- **编译是便宜的全局信号**：`build.ps1` 一次编译整个解决方案（含自检工程），跨项目契约被破坏会先在编译暴露。
  所以先 build、再按组跑，别拿自检替代编译；
- 把验证做成「有纪律地调用外部确定性工具」而不是「手艺人反复试错」：脚本负责选组 + 跑 + 汇总，
  AI 只负责看退出码和 FAIL 行。跑过就算数，不手工「再点一遍确认」。

## 四级验证（能低不高，默认 L2）

| 级别 | 什么时候 | 跑什么 | 说明 |
| --- | --- | --- | --- |
| **L0** | 只动 `*.md`、`docs/`、`LICENSE`、IDE 元文件、`scripts/**`（开发脚本） | 不 build、不自检 | 纯文档 / 纯元数据 |
| **L1** | 只动**观感或无断言**文件：工具 XAML、`Converters/**`、`Assets/**`、`MainWindow.*`、`App.xaml.cs`、构建/发布脚本、测试素材 | `build.ps1` + 截图冒烟（或对应 e2e 脚本） | 编译过 + 截图/人工确认；**不跑自检组** |
| **L2** | 单功能内部改动，或一个只被 1~2 组断言的文件（如 `MiniPlayerPanel` / `ShellConfig` / `ShellCategories`） | build + 对应 1~2 组 | **大多数改动都在这级** |
| **L3** | 一次动 3~7 组：多个功能模块 / 改自检断言 / 主题 / 共享壳层资源 | build + **映射组并集** | `check.ps1` 自动取并集 |
| **L4** | 一次动 ≥8 组（脚本自动升级）· 改 `Core/` 公共轮子 · 改自检接线（`Program.cs` / `SelfCheckScratch.cs` / 自检 csproj） · 改主工程 `YuLiangTookit.csproj` · 发布前 · 映射不到 / bug 复发且影响面不清 | build + `--all`（+ 必要的人工清单/截图） | **全量是例外，不是日常** |

> ⚠ 例外：`Views/Themes/**` 与 `App.xaml` **不能**按「XAML 改动」走 L1 —— `theme` 组就是为它们存在的，
> 且 `App.xaml` 全局样式无断言，改完必须截「主页 + 至少一个工具页 + 四套主题」。
> 同理，跑 `shell` 组 ≠ 验过 `MainWindow` / `Converters` / `MainViewModel`（shell 组不断言它们）→ 按 L1 加截图。

## 一条命令：`scripts\check.ps1`

默认模式**不做全量**：读 `git status`（含未跟踪文件）→ 每个改动文件映射到组 → build 一次 → 只跑这些组。

```powershell
.\scripts\check.ps1              # 日常唯一需要的命令：自动选组 + build + 定向自检
.\scripts\check.ps1 -DryRun      # 只看这次会跑什么，不动手（改前规划 / 解释给用户）
.\scripts\check.ps1 -List        # 看 17 个组 + 完整「路径 → 组」映射表
.\scripts\check.ps1 -SelfTest    # 单独校验映射/接线/工程/文档是否漂移（退出码 2 = 漂移）
.\scripts\check.ps1 -Files a.cs,b.md   # 假装这些文件改了（提交后复查 / 指定影响面）
.\scripts\check.ps1 -Groups theme      # 强制指定组（少数场景；优先让脚本自动选）
.\scripts\check.ps1 -SkipBuild         # 已经 build 过、想省一次编译
.\scripts\check.ps1 -All               # 全量（只允许下面「四个例外」）
.\scripts\check.ps1 -FullAt 10         # 调整「多少组自动升级全量」的阈值（默认 8）
```

行为约定：

- **每次运行都会先跑一遍 SelfTest**（毫秒级，纯静态解析）；映射/接线/文档漂移会直接退出码 2，不会“带病选组”；
- **选组**：多文件取并集；映射不到 → 自动升级 `--all`（打印原因）；选到 ≥8 组 → 直接 `--all`；
- **输出**：只回显 `FAIL` 行、PASS/FAIL 计数、以及网络类组的「跳过」提示；完整输出在
  `.artifacts/selfcheck-last.log`（UTF-8）。失败时用**文件读取工具**看日志，别用 PowerShell 读中文日志（会乱码）；
- **YLT_HOME 隔离**：脚本强制把自检数据根指到 `.artifacts/selfcheck-home-*`，**不会碰你真实的 Data/Config**；
- **退出码**：`0` = 本轮所选组全过；`1` = 编译或自检失败；`2` = 映射漂移 / 参数错误。

截图冒烟（L1 界面改动；无头、不抢焦点）：

```powershell
$env:YLT_SHOT_TOOL="<工具Id>"; $env:YLT_SHOT="shots/check.png"
Start-Process .\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_SHOT_TOOL, Env:YLT_SHOT
```

其余 `YLT_*` 调试开关在源码里 grep；截图放 `shots/`（不入库）。
工具 Id ≠ 自检组 id：`osu-mania` 对应 3 个工具（`osu-key-converter` 等）、`board-games`/`board-net`/`board-room`
对应 `gomoku`/`xiangqi`、`spug` 对应工具 `spug-captcha`。

## 自检的已知盲区（重要）

- **`--all` ≠ 全覆盖**：见「总则」三类；全绿只说明现有断言都过了；
- **fail-open**：网络/端口类断言在环境不满足时会「跳过并记 PASS」，日志里写「跳过」。
  `check.ps1` 只对网络类组（`map-downloader` / `flash-downloader` / `chat-room` / `board-net` / `board-room` / `kps`）
  提示，其他组请自己扫日志；
- **内联固定成本**：`Program.cs` 里 `core` / `shell` / `batch-rename` 的大量内联断言，无论选了什么组，
  代码体都会执行（只有 `Check` 记录被跳过），所以「只跑一组」仍有固定耗时；`batch-rename` 的 E2E 尤其慢；
- **并发**：同一个工作区**不要并发跑 `check.ps1`**（共享 `bin/obj`，会互相锁/串扰）；团队多 agent 时串行。

## 路径 → 自检组映射（source of truth 是 `scripts/check.ps1`）

| 改动路径 | 自检组 | 级别 |
| --- | --- | --- |
| `docs/**`、任意 `*.md`、`Data/README.md`、`Config/README.md`、`LICENSE`、IDE 元文件、`scripts/**` | 无 | L0 |
| `build.ps1`、`publish.ps1`、`Directory.Build.*`、`*.sln`、`global.json`、`Assets/**`、`testdata/**` | 无 | L1 |
| `src/**/Views/*.xaml`（工具 XAML，仅样式/布局） | 无（build + 该工具截图） | L1 |
| `Converters/**`（无断言） | 无（build + 截图） | L1 |
| `Views/MiniPlayerPanel.*` | `shell` + `media-player` | L2 |
| `Models/ShellConfig.cs` | `shell` + `theme` | L2 |
| `Models/ShellCategories.cs` | `shell` + `board-games` | L2 |
| `Views/Themes/**`（含 `ThemeManager.cs`） | `theme`（XAML 也不能跳过） | L2 |
| `Views/**`（除上面）、`ViewModels/**`、`Models/**`（除上面）、`SingleInstance.cs` | `shell` | L2 |
| `App.xaml` | `theme` + `shell` + **必须截图（四套主题）** | L3 |
| `App.xaml.cs`、`MainWindow.*` | `shell` + 截图（无直接断言） | L2 |
| `src/YuLiangToolkit.Core/**` | **ALL**（跨层公共轮子） | L4 |
| `src/YuLiangToolkit.Tools.BatchRename/**` | `batch-rename` | L2 |
| `src/YuLiangToolkit.Tools.FlashDownloader/**` | `flash-downloader` | L2 |
| `src/YuLiangToolkit.Tools.ChatRoom/**` | `chat-room` | L2 |
| `src/YuLiangToolkit.Tools.MapDownloader/**` | `map-downloader` | L2 |
| `src/YuLiangToolkit.Tools.MediaPlayer/**` | `media-player` | L2 |
| `src/YuLiangToolkit.Tools.OsuMania/**` | `osu-mania`（含星数） | L2 |
| `src/YuLiangToolkit.Tools.BoardGames/**` | `board-games` + `board-net` + `board-room` | L3 |
| `src/YuLiangToolkit.Tools.PictureViewer/**` | `picture-viewer` | L2 |
| `src/YuLiangToolkit.Tools.PdfReader/**` | `pdf-reader` | L2 |
| `src/YuLiangToolkit.Tools.Kps/**` | `kps` | L2 |
| `src/YuLiangToolkit.Tools.SpugCaptcha/**` | `spug` | L2 |
| `src/YuLiangToolkit.Tools.VideoToAudio/**`、`src/YuLiangToolkit.Tools.Sample/**` | **无专属组** → build + 人工冒烟 | L1 |
| `tests/.../XxxChecks.cs` | 该模块对应的组（`ScrollChainingChecks.cs` → `core`；`PdfTextChecks.cs` → `pdf-reader`…） | L3 |
| `tests/.../MaintenanceChecks.cs` | `maintenance`（缓存清理 / 重建配置） | L3 |
| `tests/.../Program.cs`、`SelfCheckScratch.cs`、自检 csproj | **ALL**（接线/公共脚手架） | L4 |
| `YuLiangTookit.csproj`（⚠ 少一个 `l`） | **ALL** + 人工清单（引用数 / `DefaultItemExcludes` / 无 `CopyToOutputDirectory` / 启动截图） | L4 |
| 其他任何路径（含新工具目录、新顶层目录） | **ALL**（映射不到） | L4 |

注意反直觉点：

1. **BoardGames 一个项目对应 3 个组**（规则 / TCP 协议 / 房间 VM 同工程）；脚本刻意保持整工程 3 组（安全优先）。
   改纯 XAML 会命中 L1 的 `src/**/Views/*.xaml` 规则只 build+截图；改代码则 3 组都跑；
2. **VideoToAudio / Sample 没有自检组**——改它们只 build + 人工冒烟（`scripts/e2e-convert.ps1` / 示例页截图），
   不要把「没跑自检」误当成「不用验」；
3. **组 id ≠ 项目名 ≠ 工具 Id**：`SpugCaptcha` → 组 `spug`；`OsuMania` → 组 `osu-mania`；`BoardGames` → 三个 `board-*` 组。

## 验证预算（硬性，防「验证通胀」）

1. **每轮 1 次 build + 1 轮所选组**；同一组不重复跑（跑过 = 有结论）。
2. **全量每轮最多 1 次**，且只允许下面的四个例外；「顺手全量保险一下」不算例外。
3. 偶发时序/网络失败（`board-room`、`board-net`、`chat-room`、`flash-downloader`、`map-downloader`、`kps`）
   允许**原样重跑一次**；第二次还挂就是真失败，别继续重试，去修。**日志里的「跳过」行 ≠ 通过行**。
4. 自检失败**只修相关组 + 重跑该组**；除非失败根因指向公共轮子，才升级 L4。
5. 交付证据 = 命令 + 退出码 + PASS/FAIL 计数（失败附 `.artifacts/selfcheck-last.log`；无组/视觉改动附截图）。
6. 纯文档 / 纯样式改动**不跑自检**；别拿自检当编译保险——编译就是 `build.ps1`。
7. 脚本不实现「同轮去重 / 自动重试」的状态缓存，这些靠人/AI 按本表执行；同一工作区串行跑。

## 全量的四个例外（写死，别扩展）

1. **发布前**。发布证据链：`build.ps1` → `check.ps1 -All` → `publish.ps1` → 发布目录启动截图 + 检查 Data/Config 在 exe 旁；
2. **改 `src/YuLiangToolkit.Core/` 公共轮子**（跨层改动，影响所有工具）；
3. **改自检接线 / 主工程清单**（`Program.cs`、`SelfCheckScratch.cs`、自检 csproj、`YuLiangTookit.csproj`），
   或**选到 ≥8 个组**（脚本自动升级；一次动多个功能由脚本判断，不靠人拍脑袋）；
4. **影响面判断不了**（新目录、映射不到、bug 复发区且无法界定波及面）。

`check.ps1` 已把这几类自动升级并打印原因；**不要自己额外加码**，也不要因为「怕」而绕过脚本手动全量。

## 防漂移（新增工具 / 自检组 / 接线时）

`check.ps1` 每次运行（含 `-DryRun`）都会自动跑 SelfTest，检查：

- `Program.cs` 的组 id 是否重复；所有 `RunGroupModule` / `RunGroup` / `EnterGroup` 接线 id 是否都在组清单里；
- 每个组是否至少被接线一次；每个 `*Checks.cs` 是否既在映射表里、又真的被 `Program.cs` 调用（防「写了断言永远不跑」）；
- 跨组共享文件映射是否符合期望（`MiniPlayerPanel`→`shell`+`media-player`、`ShellConfig`→`shell`+`theme`、
  `ShellCategories`→`shell`+`board-games`、`App.xaml`→`theme`+`shell` 等）；
- 每个 `Tools.*` 工程是否同时被主 csproj、`YuLiangToolkit.sln`、自检 csproj（VideoToAudio/Sample 白名单）引用；
- 无组项目是否仍显式标为 build-only；`docs/dev/verification.md` 是否提到全部 17 个组 id。

新增功能 / 组的步骤：

1. 建 `src\YuLiangToolkit.Tools.X\` 工程 + `ITool` 实现（可复制 `Tools.Sample`）；
2. 在自检 `Program.cs` 的 `selfCheckGroups` 加组、并写 `RunGroupModule`/`EnterGroup` 接线（新 `*Checks.cs` 必须被调用）；
3. 在 `scripts/check.ps1` 映射表加一条 `New-Rule`（没有专属组就显式映射 L1 build-only，别让它落到 ALL）；
4. 同步主 csproj 的 `ProjectReference`、`YuLiangToolkit.sln`、自检 csproj 引用（有组才加自检引用）；
5. 跑 `.\scripts\check.ps1 -SelfTest`（应退出 0）；
6. 更新 `docs/tools/<工具>.md`、README 工具表、本页映射表；截图冒烟用**工具 Id**（不是组 id）。

## 反例（这些行为在浪费 token / 时间）

- 每轮 `--all`，或「改前先全量跑一遍垫底」；
- build 已过、自检已过还手点界面「再确认一次」；
- 同一组跑三遍等它自己通过；
- 为了保险把纯文档改动也跑全量；
- 不看 `.artifacts/selfcheck-last.log` 就整组重跑；
- 把「自检全绿」当成发布依据——发布前还要 `publish.ps1` + 发布目录启动 + 截图冒烟；
- 只看 PASS/FAIL 计数，不看「跳过」行（fail-open 会让绿变得比实际乐观）。
