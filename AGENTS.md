# AGENTS.md — YuLiangToolkit（凉雨工具集）开发入口

> **入口地图**，不是手册：动手前读本文件；实现细节看源码 XML 注释，排错查 [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md)。
> 本文件保持精简（≤120 行），**别塞细节**；注释 / 提交 / 文档 / 回答一律用**中文**。

## 项目速览

| 项 | 内容 |
| --- | --- |
| 技术栈 | Windows 10 2004+ + .NET 9（主程序 / 自检 `net9.0-windows10.0.19041.0`，工具库 `net9.0-windows`）+ WPF + 手写 MVVM；第三方库 / 许可证见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)；PDF 渲染用系统 `Windows.Data.Pdf` |
| 主程序 | 根目录 `YuLiangTookit.csproj`（少一个 `l` 是历史拼写）：标题栏 / 主页 / 类别页 / 面包屑、六套主题、背景图、粒子 |
| 核心库 | `src/YuLiangToolkit.Core/`：工具契约 / MVVM / 存储 / 配置 / 维护 / OOXML / FFmpeg / 粒子 / TCP 房间 |
| 功能 | 62 个（52 个 `Tools.*` 项目，主页 13 个分类：媒体 / 文档 / 文件 / 网络 / 桌面 / 系统 / 时间 / 数学 / 生活 / 音游工具 / 棋牌 / 游戏 / 开发）；完整清单见 [`docs/README.md`](docs/README.md)，分类元数据在 `Models/ShellCategories.cs` |
| 自检 | `tests/YuLiangToolkit.FrameworkSelfCheck/`：6905 项（66 组），退出码 0 = 通过（按分组跑） |
| 数据 | **在 exe 旁边**（`bin\Debug\net9.0-windows10.0.19041.0\Data`、`Config`、`Cache`）；仓库根同名目录只有说明 |

## 开工 / 收尾（硬性）

```powershell
.\build.ps1                          # ① 编译：0 错误
.\scripts\check.ps1                  # ② 按改动自动选组：build + 只跑受影响的自检组
git add -A; git commit -m "<中文提交信息>"   # ③ 提交：每轮改动都要提交（见下节）
```

1. **验证按影响面分级，默认只部分自检**：`check.ps1` 从 `git status` 映射受影响组并跑；别凭猜选组、别每轮全量。`-All` 只给：发布前 · 改 `src/YuLiangToolkit.Core/` · 改自检接线 · 一次动 ≥8 组或映射不到影响面。详见 [`docs/dev/verification.md`](docs/dev/verification.md)。
2. 纯文档可跳 ①②；只改 XAML / 文案 / 样式 → build + 截图冒烟，不跑自检。照样提交；`git push` 由用户决定。
3. 改行为 → 同步自检断言；改用户可见行为 → 更新 `docs/tools/<工具Id>.md` 与 README 工具表。
4. 界面改动 → 截图冒烟；发布前 `.\publish.ps1` 打一次包，确认 Data/Config 在发布目录旁。
5. **验证一次即止**：同轮跑过的组不重跑；偶发失败最多原样重跑一次；全量每轮最多一次。
6. 版本号 / 自检项数只在必要处改（版本唯一来源 `Directory.Build.props`）。

## Git 提交（硬性）

- 每轮改动完成、验证通过后必须提交：`git add -A; git commit -m "<中文提交信息>"`；代码 / XAML / 文档 / 自检一起提交。
- 提交信息用中文写清结果（例：`工具分类：62 个工具归入 13 个分类`），不要只写 `update`。
- `git push` 只在用户明确要求时执行。

## 硬性规则（违反 = 破坏项目或删用户数据；更多细节见踩坑清单）

1. **运行时数据在 exe 旁边**；仓库根 `Data/`、`Config/`、`Cache/` 只放说明。绝不用 `<None CopyToOutputDirectory>` 拷 Data/Config；`dotnet clean` / 删 `bin`、`dist` = 删用户数据。
2. **NuGet 包可自由引入**：引入后在 `THIRD-PARTY-NOTICES.md` 登记来源 / 许可证，并在功能文档说明用途与运行时要求。
3. **带 setter 的计算属性必须 `[JsonIgnore]`**；配置自动落盘；要「一敲键就刷新」用 `Set(ref 字段, 值)`。
4. **后台线程改界面走 `Ui(...)` / `UiAsync(...)`**；切走时在 `NotifyClosed()` 里摘缓存对象上的事件；绕过 `ToolConfigStore` 的持久化入口自己接 `FlushAll`。
5. **数据目录里的相对路径不能带 `..`、不能是绝对路径**（`ToolStorage` 会抛异常）。
6. **改主题 token 要一起改**：四套深色字典 + `ThemeManager.RequiredTokenKeys`（几何 key 在 `RequiredRadiusKeys`）；新增颜色要保证 `ThemePaletteBuilder` 覆盖；界面颜色一律 `{DynamicResource}`。
7. **新增顶层目录**时同步改主项目 `DefaultItemExcludes` **和** `.gitignore`。
8. **别用 PowerShell 改 XAML / 源码**（5.1 按 GBK 读会乱码）；`.ps1` 纯 ASCII 或 UTF-8 with BOM。
9. **`ParticleOverlay` 必须放 Window 内 Grid 铺满 + `IsHitTestVisible="False"`**。
10. **缓存写 `Cache\<工具Id>\`**（`ToolCache`）；缓存混在 Config 里就实现 `IToolCacheSource` 报告清理项。
11. **改完必须提交 git**（见「Git 提交（硬性）」）；其他别做 / 先问用户的见踩坑清单。
12. **多区块工具页必须可拖拽，相邻区块边界都要有 `core:PaneSplitter` 分隔条**；别把区块大小写死，新页面照 [`docs/dev/layout.md`](docs/dev/layout.md) 做，恢复默认在「主页设置 → 界面布局」。
13. **右键菜单必须套 `App.xaml` 的 `DarkMenu` 样式**（叶子 / 分隔线 / 父项分别用 `DarkMenuLeafItemStyle` / `DarkMenuSeparatorStyle` / `DarkMenuParentItemStyle`），别只设 Background（深色主题会留白图标列，详见踩坑清单）。

## 新功能要接的「主页设置」关联点（漏了功能就是残的）

主页设置（⚙ 页面）集中管理跨工具的东西；工具**按需实现接口 / 加约定**，外壳自动收进去，**别去改设置页**：

| 关联点 | 工具要做什么 | 底层 / 约定 |
| --- | --- | --- |
| **清理缓存** | 缓存写 `Cache\<工具Id>\` = 自动清；混在 Config / 别处的实现 `IToolCacheSource` 报告 `ToolCacheItem` | `Core/Maintenance/IToolCacheSource.cs`、`CacheCleaner` |
| **重建配置 / 导入导出** | 配置走 `ToolConfig` 就自动出现、回默认、进 zip；**凭据类**登记进 `ConfigArchive.CredentialConfigToolIds`（整目录不导出）；随包 `Data\` 文件加进 `ExportableDataPrefixes` | `Core/Maintenance/` |
| **打开方式关联** | 只写扩展名分流；开关统一在主页设置（`ShellConfig.OpenWithAssociationEnabled`），工具页只做状态 / 修复 | `Core/Shell/OpenWith*.cs`、[`docs/tools/open-with.md`](docs/tools/open-with.md) |
| **开机自启 / 联动启动** | 实现 `IStartupTask`（在**工具实例**上；立刻返回、不抛异常、想清重启去重） | `Core/Tools/IStartupTask.cs` |
| **后台保活** | 实现 `IBackgroundTool`（`KeepAliveOnLeave` / `ShowMiniPanel` / `StopBackgroundPlayback`） | `Core/Tools/IBackgroundTool.cs` |

> 快捷键 / 布局 / 主题 token 见硬性规则 6/12 与 [`docs/dev/shortcuts.md`](docs/dev/shortcuts.md)。原则：外壳**不认识具体工具**——工具实现 `Core` 接口、写自己的配置，设置页只负责渲染。

## 找东西

| 我要… | 看 |
| --- | --- |
| 全貌 / 流程 / 硬性规则 | 本文件 |
| 验证纪律 | [`docs/dev/verification.md`](docs/dev/verification.md) |
| 轮子 / 目录 / 新功能 | `src/YuLiangToolkit.Core/` 源码注释；复制 `src/YuLiangToolkit.Tools.Sample/` |
| 工具分类 / 类别图标描述 | 工具里的 `ITool.CategoryPath` + `Models/ShellCategories.cs` |
| 工具页区块可拖拽 / 布局记忆 | [`docs/dev/layout.md`](docs/dev/layout.md) |
| 给新工具接**全局快捷键** | [`docs/dev/shortcuts.md`](docs/dev/shortcuts.md) 的「一·补：完整接线清单」 |
| 实现细节 / 踩过的坑 | 对应源码 + [`docs/dev/pitfalls.md`](docs/dev/pitfalls.md) |
| Windows「打开方式」/ 文件关联 | [`docs/tools/open-with.md`](docs/tools/open-with.md) + `Core/Shell/OpenWith*.cs` |
| Live2D 桌宠 | [`docs/dev/live2d.md`](docs/dev/live2d.md) |
| 面向用户的文档 | [`docs/tools/`](docs/tools/)（索引 [`docs/README.md`](docs/README.md)） |
| 版本改动 | `git log`（旧版手册在 git 历史里） |
