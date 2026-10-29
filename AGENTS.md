# AGENTS.md — YuLiangToolkit（凉雨工具集）开发入口

> 给 AI 编码代理 / 维护者的**入口地图**，不是手册。动手前读本文件；实现细节看源码 XML 注释，
> 排错 / 改危险代码时再查 [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md)。本文件刻意压到 ≤120 行 / ≤5KB，**不要往这里塞细节**。
> 版本唯一来源：`Directory.Build.props` 的 `<Version>`，别在别处写死；语言：注释 / 提交 / 文档 / 回答一律用**中文**。

## 项目速览

| 项 | 内容 |
| --- | --- |
| 技术栈 | Windows + .NET 9（`net9.0-windows`）+ WPF + 手写 MVVM；唯一第三方包 `OsuParsers`（MIT） |
| 主程序 | 根目录 `YuLiangTookit.csproj`（少一个 `l`，历史拼写）：自定义标题栏、主页 / 类别页 / 面包屑、两套主题、窗口级粒子 |
| 核心库 | `src/YuLiangToolkit.Core/`：工具契约、MVVM 基类、存储、配置、FFmpeg、粒子、TCP 房间底座 |
| 功能 | 13 个（10 个 `Tools.*`）：视频转音频 / 播放器 / 图片阅读器 / 批量重命名 / Flash 下载器 / 聊天室 / osu! 下图器 / osu!mania 三件套 / 棋牌 / 框架示例（模板，可删） |
| 自检 | `tests/YuLiangToolkit.FrameworkSelfCheck/`：1816 项行为逐条真跑，退出码 0 = 全通过 |
| 数据 | **在 exe 旁边**（`bin\Debug\net9.0-windows\Data`、`Config`）；仓库根 `Data/`、`Config/` 只有说明文件 |

## 开工 / 收尾（硬性）

```powershell
.\build.ps1                          # ① 编译：0 错误（顺手清警告）
dotnet run --project tests\YuLiangToolkit.FrameworkSelfCheck\YuLiangToolkit.FrameworkSelfCheck.csproj   # ② 自检：退出码 0
git add -A; git commit -m "<中文提交信息>"   # ③ 提交：每轮改动都要提交（硬性）
```

1. 顺序 ①② → ③；纯文档改动（只动 `*.md`）可跳 ① ②，但照样提交；`git push` 由用户决定。
2. 改行为 → 同步自检断言；改用户可见行为 → 更新 `docs/tools/<工具Id>.md` 与 README「工具一览」表。
3. 界面改动 → 跑截图冒烟；发布前 `.\publish.ps1` 打一次包，确认 Data/Config 建在发布目录旁。
4. 版本号 / 自检项数只在必要处改。

### 截图冒烟（无头、不抢焦点）

```powershell
$env:YLT_SHOT_TOOL="<工具Id>"; $env:YLT_SHOT="shots\check.png"
Start-Process .\bin\Debug\net9.0-windows\YuLiangTookit.exe -Wait
Remove-Item Env:YLT_SHOT_TOOL, Env:YLT_SHOT
```

其他调试开关（`YLT_PICTURE_*` / `YLT_PLAYER_*` / `YLT_BOARD_*`…）在源码里 grep `YLT_`；截图放 `shots/`（不入库）。

## 硬性规则（违反 = 破坏项目或删用户数据；更多细节见踩坑清单）

1. **运行时数据在 exe 旁边**；仓库根 `Data/`、`Config/` 只放说明。绝不用 `<None CopyToOutputDirectory>` 往数据目录拷东西；`dotnet clean` / 手删 `bin`、`dist` = 删用户数据。
2. **别再引入第三方 NuGet 包**；新轮子优先手写进 `Core/`（唯一例外 `OsuParsers`）。
3. **配置类带 setter 的计算属性必须 `[JsonIgnore]`**；配置靠后台内容比对自动落盘（没有保存按钮）；要「一敲键就刷新」用 `Set(ref 字段, 值)`。
4. **后台线程改界面走 `Ui(...)` / `UiAsync(...)`**；切走工具时在 `NotifyClosed()` 里摘掉缓存对象上的事件；绕过 `ToolConfigStore` 的持久化入口自己接 `FlushAll`。
5. **数据目录里的相对路径不能带 `..`、不能是绝对路径**（`ToolStorage` 故意抛异常防逃逸）。
6. **改主题 token 三处一起改**：`DefaultTheme.xaml` + `LiquidGlassTheme.xaml` + `ThemeManager.RequiredTokenKeys`；界面颜色一律 `{DynamicResource}`。
7. **新增顶层目录**时同步改主项目 `DefaultItemExcludes` **和** `.gitignore`。
8. **别用 PowerShell 的 `Get-Content` / `Set-Content` 改 XAML / 源码**（5.1 按 GBK 读会乱码）；`.ps1` 保持纯 ASCII 或 UTF-8 with BOM。
9. **`ParticleOverlay` 必须放在 Window 内的 Grid 里铺满 + `IsHitTestVisible="False"`**。
10. **改完必须提交 git**（见上，用户明确要求过）；其他别做 / 先问用户的见踩坑清单。

## 找东西

| 我要… | 看 |
| --- | --- |
| 项目全貌 / 流程 / 硬性规则 | 本文件 |
| 轮子（工具契约 / MVVM / 存储 / 配置 / 粒子 / TCP 房间）、目录约定、新增功能 | `src/YuLiangToolkit.Core/` 源码注释；新功能复制 `src/YuLiangToolkit.Tools.Sample/` 模板（复杂功能参考 `Tools.BatchRename` / `Tools.FlashDownloader`） |
| 某个功能的实现细节 / 踩过的坑 | 对应源码（XML 注释很全）+ [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md) |
| 面向用户的文档（每工具一页） | [`docs/tools/`](docs/tools/)（索引 [`docs/README.md`](docs/README.md)） |
| 某版本改了什么 | `git log`（本文件不再维护变更记录；v0.25.0 之前的完整手册在 git 历史里） |
