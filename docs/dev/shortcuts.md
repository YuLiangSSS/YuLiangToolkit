# 全局快捷键与通知体系

> 给「以后的工具」看的约定页：工具怎么声明自己的快捷键、怎么发通知。
> 秒表是第一个用它的工具，Kps / 录屏 / Live2D / 播放器随后各接了一份。
>
> **要给新工具接快捷键？直接看「[一·补：给一个新工具接快捷键的完整接线清单](#一补给一个新工具接快捷键的完整接线清单)」**
> —— 从配置字段到引擎注册到自检，照着一步步做就行（播放器 `MediaPlayerTool` 是按它做的样板）。

[← 文档索引](../README.md)

## 一、快捷键

### 1. 一条快捷键长什么样

`KeyCombo`（`Core/Input/KeyCombo.cs`）= **一个按键集合**：若干修饰键 + 若干主键，要求**同时按下**。

- 文本形式 `Ctrl+U+K` ⇄ 对象，**往返一致**（配置里存的是给人看的文字）。
- 修饰键固定顺序 Ctrl → Alt → Shift → Win；**主键保留录入顺序**（录的是 `Ctrl+U+K` 就显示 `Ctrl+U+K`）。
- 左右修饰键归一（`左Ctrl` == `Ctrl`），相等比较与顺序无关。
- 上限 **4 个键**（再多按不过来，也容易和别的软件撞）。
- 合法性：单字母 / 数字 / 空格必须带修饰键；`F1~F24` 可以单独用。

### 2. 两种形态

| 形态 | 存什么 | 触发 | 界面上 |
| --- | --- | --- | --- |
| **同时** | 一个 `KeyCombo` | 所有键同时按住 | 一个大框 +「拆分」 |
| **分步** | 两个 `KeyCombo` | 先按完第一步，再按第二步（VS 的 chord） | 两个小框 +「合并」 |

- **录制永远是「同时按」**：所有键必须真的同时按住，且**全部松开后才定下来**。
- **分步是拆出来的**：点「拆分」→ 把第二步录一遍。拆分时第二步先填成第一步的副本（不然「空第二步」会被判回单步，拆了等于没拆）。
- **合并**：两段按键取并集（`Ctrl+U` + `Ctrl+K` → `Ctrl+U+K`）。合并后超过 4 个键就拒绝并说明原因。
- **分步的吞键规则**：第一步被完整按下后**吞掉**（不传给前台），否则在编辑器里会既触发快捷键又打出一个字符。
  - 等第二步时按对了 → 触发并吞掉；
  - **按了不匹配的键 → 也吞掉**，同时弹一条「未被识别的快捷键：xxx（这里应该按 xxx）」——
    那一下已经不会传给前台，不给提示用户会以为键盘失灵；
  - 只按修饰键（Ctrl/Shift/Alt/Win）不算按错，继续等；
  - 超过等待时长 → 作废。
- **等待时长是全程序统一的一项设置**（主页设置 → 快捷键），默认 **5 秒**，可调 0.5~30 秒，
  也可以勾「不限时」（按下第一步后一直等到按对或按错为止）。不按工具分开设 ——
  用户按第一步时不会去想「这是哪个工具的等待时长」。
- **按完第一步有提示**：屏幕角落弹一条「已识别 Ctrl+U，请按第二步」（用该工具自己的通知设置），
  作废 / 按错时提示会被新的那条替换。

### 3. 引擎怎么注册（`GlobalShortcutEngine`）

混合式，按组合决定走哪条通道：

| 组合 | 通道 | 代价 |
| --- | --- | --- |
| 修饰键 + 1 个主键（含单独 F 键） | Win32 `RegisterHotKey` | 无 —— 前台是管理员窗口也生效，也不误吞键 |
| 多主键 / 分步 | 低级键盘钩子 `WH_KEYBOARD_LL` | **前台是管理员权限窗口时收不到**（Windows UIPI 限制） |

钩子只在「确实存在这类快捷键」时安装，没有就立刻卸掉（不长期挂着拖慢系统键盘）。

### 4. 工具怎么接进来

实现 `IShortcutProvider`（`Core/Input/IShortcutProvider.cs`）四个成员：

```csharp
public sealed class MyTool : ITool, IShortcutProvider
{
    public IReadOnlyList<ShortcutDefinition> GetShortcuts();   // 每次按当前配置现生成
    public bool ShortcutsEnabled { get; set; }                 // 整工具总开关
    public event EventHandler? ShortcutsChanged;               // 改了要通知汇总页
    public void ApplyShortcuts();                              // 按当前配置重新注册
}
```

改键 / 启停的回调写在每个 `ShortcutDefinition` 的 `SetCombo` / `SetEnabled` 里，工具自己写回配置。

> 手头只有「几个字符串配置项」的工具可以用 `ShortcutProviderAdapter`（`Core/Input/ShortcutProviderAdapter.cs`）少写一遍。

注册时机：**程序启动就注册**（不要等页面被打开），否则「软件在后台按快捷键」不成立。
外壳在 `App.OnStartup` 里把所有 `IShortcutProvider` 登记进 `ShortcutHub`，汇总页读的就是它。

### 5. 冲突与启停

- **冲突**：同一组合（含某条分步的第一步）被两个启用中的快捷键占用 → 两条都标红、**都不注册**（避免「谁生效看运气」）。
- **单条禁用**：只关这一条，设置保留。
- **整工具禁用**：一键全关，单条状态保留。两者正交。

### 6. 汇总页在哪

主页设置 → **快捷键**：分类（▼/▶）→ 工具（▼/▶）→ 每条一行，改键 / 拆分 / 合并 / 禁用 / 状态都在行里。默认全部收起，有冲突的工具自动展开。

这一块顶部还有**分步快捷键的等待时长**：全程序统一一项，存在 `ShellConfig.ShortcutSequenceTimeoutMs`
（0 = 不限时），改一下立即写进引擎。工具不再自己定义这个值。

### 7. 分步的反馈事件（给界面用）

引擎抛三个事件，工具按自己的通知设置去弹提示：

| 事件 | 时机 | 建议提示 |
| --- | --- | --- |
| `SequenceStarted` | 第一步被识别，开始等第二步 | 「已识别 Ctrl+U，请按第二步」 |
| `SequenceRejected` | 等第二步时按了不匹配的键（**那一下已被吞掉**） | 「未被识别的快捷键：Ctrl+X（这里应该按 Ctrl+K）」 |
| `SequenceCancelled` | 超时作废 | 收起提示即可 |

## 一·补：给一个新工具接快捷键的**完整接线清单**

> 光看第 4 节还不够：从「工具有个功能」到「在别的窗口按一下就触发」，中间有几处必须都接上，
> 少一处就是「设置页能看到快捷键、但按了没反应」。**照这份清单一步步做**（播放器 `MediaPlayerTool` 就是按它做的）。

### 第 0 步：先想清楚「触发时谁干活」

- 快捷键的回调是**在 UI 线程**由引擎调的，它只会调 **实现了 `IShortcutActionHost.InvokeShortcut(id)` 的那个对象**（见第 3 步）。
- 播放器的做法：`MediaPlayerTool` 自己实现 `IShortcutActionHost`，`InvokeShortcut` 转发到它持有的**常驻 ViewModel**
  （`_viewModel.TogglePlayPause()` / `_viewModel.MoveTrack(±1)`）。
- ⚠ **页面是懒加载、还会被缓存淘汰**：如果触发逻辑写在页面 ViewModel 上、而 VM 又只在 `CreateView()` 里 `new`，
  那么「工具页没打开」时根本按不动。所以**要么让工具持有常驻 ViewModel**（播放器/秒表这么做），
  要么把触发逻辑写成工具的静态/无状态方法。**推荐前者**：见第 4 步的常驻 VM 坑。

### 第 1 步：配置里加「组合文本 + 第二步 + 启用位」，外加一个总开关

```csharp
// MyConfig : ToolConfig
private string _shortcutNext = "Ctrl+Alt+Right";
public string ShortcutNext { get => _shortcutNext; set => Set(ref _shortcutNext, value ?? ""); }

private string _shortcutNextSecond = "";       // 分步第二步，空 = 单步
public string ShortcutNextSecond { get => _shortcutNextSecond; set => Set(ref _shortcutNextSecond, value ?? ""); }

private bool _shortcutNextEnabled = true;       // 单条启用位
public bool ShortcutNextEnabled { get => _shortcutNextEnabled; set => Set(ref _shortcutNextEnabled, value); }

private bool _shortcutsEnabled = true;          // 整工具总开关
public bool ShortcutsEnabled { get => _shortcutsEnabled; set => Set(ref _shortcutsEnabled, value); }
```

- 组合文本用 `KeyCombo` 的文本形式（`Ctrl+Alt+Right` / `Ctrl+Alt+Space`…），**别存成对象**——配置是给人看 / 手改的。
- 默认值要给：找一个**避开系统常用单键**的组合（别默认 `F1`/`Alt+Tab`/`Ctrl+C` 这种）。

### 第 2 步：工具实现 `IShortcutProvider`（声明 + 回写）

最省事的是用 `ShortcutProviderAdapter`，把三条槽位的「读 / 写」委托给它，`GetShortcuts()` 里 `adapter.Build(Id)`：

```csharp
public class MyTool : ITool, IShortcutProvider, IShortcutActionHost
{
    private readonly MyViewModel _viewModel;                 // 常驻 VM（第 4 步）
    private readonly ShortcutProviderAdapter _adapter;

    public MyTool()
    {
        _viewModel = new MyViewModel();
        _adapter = new ShortcutProviderAdapter(
            slots: () => new[]
            {
                new ShortcutProviderAdapter.Slot(
                    "next", "下一首",
                    ReadPrimary: () => _viewModel.Cfg.ShortcutNext,
                    WritePrimary: v => _viewModel.Cfg.ShortcutNext = v,
                    ReadSecondary: () => _viewModel.Cfg.ShortcutNextSecond,
                    WriteSecondary: v => _viewModel.Cfg.ShortcutNextSecond = v,
                    ReadEnabled: () => _viewModel.Cfg.ShortcutNextEnabled,
                    WriteEnabled: v => _viewModel.Cfg.ShortcutNextEnabled = v,
                    CanSplit: true, Description: "播放器：下一首"),
                // ……其余两条同构
            },
            apply: ApplyShortcuts,
            notifyChanged: () => ShortcutsChanged?.Invoke(this, EventArgs.Empty));
    }

    public string ShortcutToolId => Id;
    public string ShortcutToolName => Name;
    public IReadOnlyList<string> ShortcutCategoryPath => new[] { "媒体" };
    public bool ShortcutsEnabled { get => _viewModel.Cfg.ShortcutsEnabled; set { _viewModel.Cfg.ShortcutsEnabled = value; ApplyShortcuts(); } }
    public event EventHandler? ShortcutsChanged;
    public IReadOnlyList<ShortcutDefinition> GetShortcuts() => _adapter.Build(Id);
    public void ApplyShortcuts() { ShortcutHub.ApplyAll(); ShortcutsChanged?.Invoke(this, EventArgs.Empty); }
}
```

- **`ApplyShortcuts()` 里就是 `ShortcutHub.ApplyAll()`** —— 引擎由外壳持有，工具只能通过 Core 的静态入口 `ShortcutHub` 拿它。
  不要自己去 `new GlobalShortcutEngine`。
- 还必须在配置 `PropertyChanged` 里监听上面那几个字段，变了就 `ApplyShortcuts()`（**否则「改了键旧键还生效」极难查**）：
  ```csharp
  _viewModel.Cfg.PropertyChanged += (_, e) => { if (e.PropertyName is ... or nameof(MyConfig.ShortcutNext) or ...) ApplyShortcuts(); };
  ```

### 第 3 步：工具实现 `IShortcutActionHost`（按下时干活）

```csharp
public void InvokeShortcut(string shortcutId)
{
    switch (shortcutId)
    {
        case "next": _viewModel.MoveTrack(+1); break;
        case "previous": _viewModel.MoveTrack(-1); break;
        case "play-pause": _viewModel.TogglePlayPause(); break;
    }
}
```

- 引擎注册时（`ShortcutRegistry.ApplyTo`）就是 `engine.Register(id, kind, primary, secondary, _ => host.InvokeShortcut(shortcutId), out err)`，
  所以**只要 provider 同时是 host，按键就能直接进到你的 `InvokeShortcut`**。
- 分步反馈（第 7 节）由工具自己按需订阅引擎事件弹提示（播放器这几条是单步，可不订）。

### 第 4 步（常驻 VM 工具的坑）：`CreateView` 复用同一个 ViewModel，并补 `NotifyOpened`

- `CreateView()` 返回 `new MyView { DataContext = _viewModel }`（**不是 `new MyViewModel()`**），
  这样页面开没开、视图被缓存淘汰重建，快捷键和界面操作的都是**同一份状态**。
- 但外壳的缓存淘汰会 `DisposeToolView` → `vm.Dispose()`（= `NotifyClosed`，**停掉配置后台自动保存**）；
  外壳只在「复用旧视图」时调 `NotifyOpened`，**新建的视图不会**。
  → 所以在视图的 `Loaded` 里自己补一次 `vm.NotifyOpened();`，否则淘汰重建后**配置改动不落盘**。
  （播放器 `MediaPlayerView.OnLoaded` 第一行就是这个。）
- 常驻 VM 若也做后台任务（播放器的 `IBackgroundTool`），注意 `OnClosed` 里**别**无脑停掉正在后台播放的东西
  （见 `IBackgroundTool` 文档：外壳保活时走的是 `StopBackgroundPlayback`，不是 `NotifyClosed`）。

### 第 5 步：核对「外壳已经替你做了、你不用再做」

- `App.OnStartup` 里已经：`new GlobalShortcutEngine()` → `ShortcutHub.Initialize(...)` →
  `foreach (tool in tools.OfType<IShortcutProvider>()) ShortcutHub.AddProvider(tool)` → `ShortcutHub.ApplyAll()`。
  **所以工具只要实现接口、构造函数里把常驻 VM / 配置建好，启动时就会自动注册**（不用改 `App.xaml.cs`）。
- 工具**不要**自己注册、注销热键，也不要碰 `RegisterHotKey`／键盘钩子——都由 `GlobalShortcutEngine` 统一管。

### 第 6 步：自检（`tests/.../XxxChecks.cs` 里加一段）

无头就能测「接线对不对」（`media-player` 组的 `CheckShortcuts` 就是模板）：

1. `new MyTool()` → 断言 `is IShortcutProvider` 且 `is IShortcutActionHost`；
2. `GetShortcuts()` 条数 / Id / 默认组合文本；
3. 拿一条 `ShortcutDefinition` 调 `SetCombo(...)` / `SetEnabled(false)`，断言配置被回写；
4. 给常驻 VM 接一个**假播放器/假后端**，调 `tool.InvokeShortcut("next")` 等，断言状态真的变了。

> 真按 `RegisterHotKey` 的那一下（系统级）由人工冒烟验收——无头环境注册不了真实热键。


## 二、通知

### 1. 每个工具各配一份

`NotificationSettings`（`Core/Notifications/NotificationCenter.cs`）作为工具配置里的一个子对象：

```csharp
public NotificationSettings Notification { get; set; } = new();
```

界面上放一块 `<notify:NotificationSettingsView DataContext="{Binding Cfg.Notification}" />` 就有完整的设置界面（通道 / 四角 / 停留时长）。

**为什么不集中到主页设置**：不同工具弹在不同角落是个人习惯（秒表弹右下、提醒弹左上），集中配置做不到。

### 2. 发送

```csharp
NotificationCenter.Show(settings, new NotificationRequest("标题", "正文", NotificationKind.Success, "my-tool"));
```

- 通道由 `settings.Mode` 决定：`InApp`（应用内自绘小窗，四个角、可堆叠）/ `Windows`（托盘气泡，进系统通知中心）。
- **兜底**：首选通道不可用（托盘没建起来 / 系统禁了通知）→ 自动换另一条；两条都不行就只写 Debug 输出，不抛异常也不静默丢。

### 3. 加新通道

实现 `INotificationSink`（一个 `Send` 方法）→ 在 `NotificationCenter.SetSinks` 多注一个即可，**所有工具代码一行都不用改**。
