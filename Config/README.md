# Config —— 功能配置（说明文件，不是运行时目录）

> ⚠️ **真正的运行时 `Config` 在 exe 旁边**，不在仓库里：
> `bin\Debug\net9.0-windows\Config\`、`bin\Release\net9.0-windows\Config\`，或 `publish.ps1` 出来的 `dist\YuLiangToolkit\Config\`。
> 这个仓库文件夹只保留说明，方便你（和以后接手的人）知道约定长什么样。

这个目录记录**每个功能的每个选项**：Checkbox 勾没勾、Textbox 填了什么、下拉框选了哪一项……
界面上一改就自动存进来（最多滞后 400ms），下次打开功能自动恢复。

## 目录约定（运行时那份）

```
<exe 所在目录>\Config\
├─ 怎么用这两个文件夹.md      ← 程序首次运行时自己写的说明
├─ shell/config.json          ← 外壳（主页/导航）：排序偏好 + 各功能上次使用时间
├─ <功能Id>/
│  ├─ config.json             ← 强类型配置（ToolConfig / ToolConfigStore.Load<T>）
│  └─ options.json            ← 动态键值配置（DynamicToolConfig，可选）
├─ batch-rename/config.json   ← 批量重命名：整套改名规则 + 排序偏好 + 上次用过的文件夹
├─ flash-downloader/
│  ├─ config.json             ← Flash 下载器选项
│  └─ folders.json            ← 游戏目录（动态配置的第二份文件）
└─ sample-framework/
   ├─ config.json
   └─ options.json
```

* **小文件夹名 = 工具 Id**（`ITool.Id`）。
* 每个功能想拆多份配置就多放几个 json（`ToolConfigStore.For(id, "网络.json")`）。
* 文件是 **camelCase、带缩进、中文不转义** 的，可以直接用记事本改；改完重启功能即生效。

## 配置文件长什么样

```json
{
  "saveFolder": "D:\\Flash",
  "autoRename": true,
  "timeoutSeconds": 30,
  "mode": "Quality"
}
```

枚举以字符串保存（`"mode": "Quality"`），改起来不会因为枚举顺序变化而错位；
给配置类**新增属性不会影响老文件**，缺失的字段自动用类里的默认值。

## 代码里怎么用（两种写法）

```csharp
// ① 强类型（推荐）：声明一个类，属性一改就自动保存
public sealed class FlashDownloaderConfig : ToolConfig
{
    public string SaveFolder { get; set; } = "";
    public bool AutoRename { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;

    [JsonIgnore] public bool HasFolder => SaveFolder.Length > 0;   // 计算属性记得标注
}

Cfg = LoadConfig<FlashDownloaderConfig>();          // 在 ToolViewModelBase 子类里一行搞定
```

```xml
<!-- XAML：改一下 = 存一次，无需写任何保存代码 -->
<TextBox  Text="{Binding Cfg.SaveFolder, UpdateSourceTrigger=PropertyChanged}" />
<CheckBox IsChecked="{Binding Cfg.AutoRename}" />
```

```csharp
// ② 动态键值（不想建类时用）：Config\<功能名>\options.json
var dyn = DynamicToolConfig.Load("flash-downloader");
dyn.SetValue("lastUrl", "https://…");
int timeout = dyn.GetInt("timeoutSeconds", 10);
```

推荐用带默认值的加载（键缺失时补齐并落盘，配置文件因此自带“有哪些选项”的说明）：

```csharp
Dyn = LoadDynamicConfigWithDefaults(new Dictionary<string, object?>
{
    ["autoRename"] = true,
    ["lastUrl"] = "",
});
```
```xml
<CheckBox IsChecked="{Binding Dyn[autoRename], TargetNullValue=False}" />
```

> 动态配置的 CheckBox 记得加 `TargetNullValue=False`（或用上面的默认值加载）：
> 键不存在时读取返回 `null`，`IsChecked=null` 的复选框点一下会变成 `false`，看起来像“点了没反应”。

## 注意

* 删除某个 json = 该功能选项恢复默认值（下次打开会重新生成默认文件）。
* 文件损坏（手改坏了）会被自动改名备份成 `xxx.bad-20250101-120000.json` 并回退默认值，不会崩。
* 写入是**原子**的（先写 `.tmp` 再替换），断电不会留下半个文件。
* 程序退出、切换功能时都会强制落盘一次未保存的改动（`ToolConfigStore.FlushAll`）。
* **`dotnet clean` / 手删 `bin`/`dist` = 删掉这些配置**（下次打开回到默认值），重要设置自己留个备份。
* 本目录已从主项目编译项里排除，往这里丢 `.cs` / `.xaml` 不会被编译。
