# 🧪 框架示例（开发用，可删）

> 这不是给用户玩的功能，而是**给开发者看的模板**：把 Data / Config 两个轮子跑给你看，
> 新功能直接复制这个项目就行。

[← 工具一览](../../README.md) ｜ [文档索引](../README.md)

## 能做什么

- 左侧选项（TextBox / CheckBox / ComboBox / Slider）改一下，右侧**实时显示** `config.json`、`options.json` 的变化 ——
  这就是「选项改一下就自动保存」到底是怎么存的。
- 下方演示 `Data\sample-framework\` 的列文件、导入外部文件、写入、删除、在资源管理器中显示。
- 还有一份通用交互演示（忙碌状态 / 进度 / 日志 / 取消）。

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
