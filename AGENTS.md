# AGENTS.md — YuLiangToolkit（凉雨工具集）开发入口

> **入口地图**，不是手册：动手前读本文件；实现细节看源码 XML 注释，排错查 [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md)。
> 本文件 ≤120行/≤5KB，**别塞细节**；注释 / 提交 / 文档 / 回答一律用**中文**。

## 项目速览

| 项 | 内容 |
| --- | --- |
| 技术栈 | Windows 10 2004+ + .NET 9（主程序 / 自检 `net9.0-windows10.0.19041.0`，工具库 `net9.0-windows`）+ WPF + 手写 MVVM；唯一第三方包 `OsuParsers`（MIT）；PDF 渲染用系统 `Windows.Data.Pdf`，文字搜索 / 书签目录是手写 PDF 解析器 |
| 主程序 | 根目录 `YuLiangTookit.csproj`（少一个 `l` 是历史拼写）：标题栏 / 主页 / 类别页 / 面包屑、两套主题、粒子 |
| 核心库 | `src/YuLiangToolkit.Core/`：工具契约 / MVVM / 存储 / 配置 / 维护（缓存清理·配置重建）/ FFmpeg / 粒子 / TCP 房间 |
| 功能 | 16 个（13 个 `Tools.*`）：视频转音频 / 播放器 / 图片阅读器 / PDF 阅读器 / 重命名 / Flash / 聊天室 / osu! 下图器 / osu!mania 三件套 / 棋牌 / KPS 按键统计 / Spug 验证码测试 / 示例模板 |
| 自检 | `tests/YuLiangToolkit.FrameworkSelfCheck/`：2847 项，退出码 0 = 通过（按分组跑） |
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
2. **别再引入第三方 NuGet 包**；新轮子优先手写进 `Core/`（唯一例外 `OsuParsers`）。
3. **带 setter 的计算属性必须 `[JsonIgnore]`**；配置自动落盘（无保存按钮）；要「一敲键就刷新」用 `Set(ref 字段, 值)`。
4. **后台线程改界面走 `Ui(...)` / `UiAsync(...)`**；切走时在 `NotifyClosed()` 里摘缓存对象上的事件；绕过 `ToolConfigStore` 的持久化入口自己接 `FlushAll`。
5. **数据目录里的相对路径不能带 `..`、不能是绝对路径**（`ToolStorage` 故意抛异常防逃逸）。
6. **改主题 token 三处一起改**：`DefaultTheme.xaml` + `LiquidGlassTheme.xaml` + `ThemeManager.RequiredTokenKeys`；界面颜色一律 `{DynamicResource}`。
7. **新增顶层目录**时同步改主项目 `DefaultItemExcludes` **和** `.gitignore`。
8. **别用 PowerShell 改 XAML / 源码**（5.1 按 GBK 读会乱码）；`.ps1` 纯 ASCII 或 UTF-8 with BOM。
9. **`ParticleOverlay` 必须放 Window 内 Grid 铺满 + `IsHitTestVisible="False"`**。
10. **缓存写 `Cache\<工具Id>\`**（`ToolCache`），「主页设置 → 清理缓存 / 重建配置」按已发现工具自动覆盖新工具；缓存混在 Config 里就实现 `IToolCacheSource` 报告清理项（详见 `Core/Maintenance/` 注释）。
11. **改完必须提交 git**；其他别做 / 先问用户的见踩坑清单。
12. **有多个区块的工具页必须可拖拽**（v0.41.0 起）：区块之间用 `core:PaneSplitter` + `App.xaml` 的分隔条样式（蓝线 / ↔ ↕ 光标 / 自动记住 / 双击复位），**别再把区块大小写死**；新页面照 `docs/dev/layout.md` 做，布局恢复默认在「主页设置 → 界面布局」。

## 找东西

| 我要… | 看 |
| --- | --- |
| 全貌 / 流程 / 硬性规则 | 本文件 |
| 验证纪律（本轮该跑哪些组 / 全量例外 / 预算） | [`docs/dev/verification.md`](docs/dev/verification.md) |
| 轮子（工具契约 / MVVM / 存储 / 配置 / 粒子 / TCP 房间）、目录与新功能 | `src/YuLiangToolkit.Core/` 源码注释；新功能复制 `src/YuLiangToolkit.Tools.Sample/` 模板 |
| 工具页区块可拖拽 / 布局记忆 / 恢复默认 | [`docs/dev/layout.md`](docs/dev/layout.md) |
| 某个功能的实现细节 / 踩过的坑 | 对应源码 + [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md) |
| 面向用户的文档 | [`docs/tools/`](docs/tools/)（索引 [`docs/README.md`](docs/README.md)） |
| 版本改动 | `git log`（旧版手册在 git 历史里） |
