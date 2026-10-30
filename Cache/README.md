# Cache —— 功能缓存（说明文件，不是运行时目录）

> ⚠️ **真正的运行时 `Cache` 在 exe 旁边**，不在仓库里：
> `bin\Debug\net9.0-windows10.0.19041.0\Cache\`、`bin\Release\...\Cache\`，或 `publish.ps1` 出来的 `dist\YuLiangToolkit\Cache\`。
> 这个仓库文件夹只保留说明，方便你（和以后接手的人）知道约定长什么样。

## 目录约定（运行时那份）

```
<exe 所在目录>\Cache\
├─ 怎么用这个文件夹.md    ← 程序首次运行时自己写的说明（RuntimeReadme）
└─ <功能Id>\              ← 每个功能一个同名小文件夹（首次真正写缓存时才创建）
```

* **里面的东西随时可以删**：丢了最多下次重新生成（变慢），**不会丢用户数据 / 设置**。
  用户数据在 `Data\`，设置在 `Config\`。
* 小文件夹名 = 工具 Id（`ITool.Id`），和 `Data\` / `Config\` 保持一致。
* 想手动清理：直接删某个功能的小文件夹，或用「主页设置 → 清理缓存」（可勾选 / 全选，
  图片 / PDF 阅读器的「最近浏览」、聊天室的「最近会话」也列在那里）。

## 代码里怎么读写（新功能请走这个）

```csharp
var cache = ToolCache.For("picture-viewer");   // → <exe 旁边>\Cache\picture-viewer\
var thumb = cache.PathOf("thumbs", "001.png"); // 只拼路径；写之前 cache.EnsureCreated()
cache.Clear();                                 // 清空（保留文件夹本身）
cache.TotalSize();                             // 当前占用
```

* 相对路径禁止 `..` / 绝对路径（和 `ToolStorage` 一样抛 `ArgumentException` 防越界）。
* 「清理缓存」按工具 Id **自动认出** `Cache\<功能Id>\`，新工具不用登记、不用改设置页；
  缓存如果混在 `Config` 里（例如「最近浏览」列表），让工具类实现
  `YuLiangToolkit.Core.Maintenance.IToolCacheSource` 报告清理项即可。

## 路径是怎么定的

`YLT_HOME` → **exe 所在目录**（默认）→ `%AppData%\YuLiangToolkit`（exe 目录不可写时兜底），
和 `Data` / `Config` 三个文件夹永远在同一个根目录下。实现见 `src/YuLiangToolkit.Core/Storage/AppPaths.cs`。

## 注意

* 本目录已从主项目编译项里排除（`YuLiangTookit.csproj` 的 `DefaultItemExcludes`），
  往仓库这份里丢 `.cs` / `.xaml` 也不会被编译。
* ⚠️ 不要在 csproj 里用 `<None CopyToOutputDirectory>` 往 exe 旁边拷这里的文件（原因见 `Data/README.md`）。
