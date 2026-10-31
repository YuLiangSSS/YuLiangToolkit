# 🧪 框架示例（开发用，可删）

> 这不是给用户玩的功能，而是**给开发者看的模板**：把 Data / Config 两个轮子跑给你看，
> 新功能直接复制这个项目就行。

[← 工具一览](../../README.md) ｜ [文档索引](../README.md)

## 能做什么

- 左侧选项（TextBox / CheckBox / ComboBox / Slider）改一下，右侧**实时显示** `config.json`、`options.json` 的变化 ——
  这就是「选项改一下就自动保存」到底是怎么存的。
- 下方演示 `Data\sample-framework\` 的列文件、导入外部文件、写入、删除、在资源管理器中显示。
- 还有一份通用交互演示（忙碌状态 / 进度 / 日志 / 取消）。

## 区块布局（可拖拽示例）

v0.42.0 起，这一页是**可拖拽区块布局的模板**（三处都示范了，新功能照抄）：**每个相邻区块之间都有一条缝**，
同一列里上下堆叠的卡片也各算一块、各有一条缝（不会把两块合成一个可拖块）：

- **① 强类型配置 | ② 动态配置**：左列里上下两张卡之间的横缝（↕），两块都是固定像素行；
- **主设置区 | 配置文件预览**：中间竖缝左右拖（↔），预览列固定像素、默认 390 宽，主设置区 `*` 随窗口缩放；
- **③ 数据文件夹（文件列表） | ④ 通用交互演示（日志）**：页面下方那条横缝（↕），两块也都是固定像素行；
- 分隔条平时只有一条**很淡的提示线**（默认 18%，可在主页设置 →「界面布局 → 分隔条提示线」调：0 = 完全看不见、
  100 = 一直显示实心蓝线）；鼠标移到区块之间的缝隙上会亮起蓝色竖线 / 横线、光标变 ↔ / ↕，按住就能拖；
- 拖完自动记住（`Config\layout.json`），下次打开还是调好的样子；**双击**某条缝只恢复这一处；
- 想全部回默认：主页设置 →「界面布局 → 恢复默认布局」；
- 区块内部能伸缩：列表 / 日志 / 预览框都放在 `*` 行里，不再写死高度（拖大拖小内容都跟着变）。

照抄要点：复制本项目时把那几条 `core:PaneSplitter`（连同 `App.xaml` 里的两个样式）一起带走 ——
**相邻两块之间就要有一条**，别只给整页做一条大缝；`Key` 改成 `"<工具Id>.<区域名>"`（同一页别重复），
相邻两块记得给 `MinWidth` / `MinHeight`，分隔条自己独占一行 / 一列（12 宽），
卡片被拖得比内容矮时加 `ClipToBounds="True"` 别让内容溢出。完整约定见
[docs/dev/layout.md](../dev/layout.md)。

## 当模板用

```powershell
# 1) 复制整个文件夹并改名
Copy-Item -Recurse src\YuLiangToolkit.Tools.Sample src\YuLiangToolkit.Tools.MyTool
# 2) 改 csproj 的 AssemblyName / RootNamespace，再改 4 个文件
#    （Tool / Config / ViewModel / View，里面的注释都写好了要改哪）
# 3) 接入解决方案
dotnet sln YuLiangToolkit.sln add src\YuLiangToolkit.Tools.MyTool\YuLiangToolkit.Tools.MyTool.csproj
#    并在 YuLiangTookit.csproj 里加一条 ProjectReference
.\build.ps1
```

要点：实现 `ITool`（`Id/Name/Description/EmojiIcon/Category/Order` + `CreateView()`）就会被自动发现；
选项继承 `ToolConfig`，ViewModel 继承 `ToolViewModelBase`，Data/Config 路径与持久化框架全包了。
也可以把编译好的 dll 丢进程序目录的 `plugins\`（外部插件，不用改主程序）。

## 删掉它

整个 `src\YuLiangToolkit.Tools.Sample` 目录可以删，删之前记得把主程序 `YuLiangTookit.csproj` 里的
那条 `ProjectReference` 与 `YuLiangToolkit.sln` 里的条目一起去掉。

## 相关代码

- 项目：`src/YuLiangToolkit.Tools.Sample/`（`SampleTool.cs` / `SampleConfig.cs` /
  `ViewModels/SampleViewModel.cs` / `Views/SampleView.xaml`）
- 新增功能的完整步骤与轮子速查：[AGENTS.md](../../AGENTS.md)（入口）；轮子源码在 `src/YuLiangToolkit.Core/`，注释即文档
