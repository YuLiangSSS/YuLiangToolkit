# 全局快捷键与通知体系

> 给「以后的工具」看的约定页：工具怎么声明自己的快捷键、怎么发通知。
> 秒表是第一个用它的工具，Kps / 录屏 / Live2D 随后各接了一份。

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
- **分步的第一步会被吞掉**（不传给前台），否则在编辑器里会既触发快捷键又打出一个字符。中途按了不匹配的键或超时 → 这次作废。

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
