# 可拖拽布局约定（工具页区块调整大小）

> 入口是根 [AGENTS.md](../../AGENTS.md)。本页只管「工具页里的区块怎么做到可拖拽、可记住、可恢复默认」。
> 一句话：**有多个区块的工具页，区块之间一律放 `core:PaneSplitter`，别把大小写死在 XAML / 代码里。**

## 为什么要有这套东西

用户报过好几轮「布局不合适、每次都要找 AI 调」：每个工具页的区块大小（左列表 / 右设置、上内容 / 下日志…）
都是写死的，窗口一换、内容一多就挤。现在统一成「用户自己拉」：

- 鼠标移到区块之间的缝隙上 → 亮起一条**蓝色竖线 / 横线**（平时**完全透明、看不见**，只有鼠标在缝隙上才出现）；
- 光标变成 ↔ / ↕，按住拖动就是改大小（跟画图软件拉图形一样）；
- 松手自动记住（`Config\layout.json`），下次打开同一个页面还是调好的样子；
- **双击**某一条分隔条：只恢复这一处的默认大小；
- **主页设置 →「界面布局 → 恢复默认布局」**：所有页面一次恢复默认（`LayoutMemory.ResetAll()`）。

## 三样东西（都在 Core，别在工具工程里重复造）

| 东西 | 位置 | 干什么 |
| --- | --- | --- |
| `PaneSplitter` | `Core/Mvvm/PaneSplitter.cs` | 分隔条控件：拖拽 / 双击复位 / 套用记忆；**不要**再用 `GridSplitter` |
| 两个样式 | `App.xaml` 的 `PaneSplitterVerticalStyle` / `PaneSplitterHorizontalStyle` | 蓝线、光标（SizeWE / SizeNS）、拖拽高亮；连 `Direction` 都设好了 |
| `LayoutMemory` | `Core/Storage/LayoutMemory.cs` | 尺寸落盘 `Config\layout.json`；键名、比例、恢复默认都在这 |

## 标准写法

### 1. 左右分栏（竖线、↔）

```xml
<!-- 页面顶部加 xmlns（多数工具页已经有）：xmlns:core="clr-namespace:YuLiangToolkit.Core.Mvvm;assembly=YuLiangToolkit.Core" -->
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" MinWidth="320" />      <!-- 左：随窗口缩放 -->
        <ColumnDefinition Width="12" />                    <!-- 分隔条独占一列 -->
        <ColumnDefinition Width="560" MinWidth="380" />    <!-- 右：固定宽度、可拖 -->
    </Grid.ColumnDefinitions>

    <Border Grid.Column="0" Style="{StaticResource CardStyle}">…左区块…</Border>
    <core:PaneSplitter Grid.Column="1" Style="{StaticResource PaneSplitterVerticalStyle}"
                       Key="工具Id.左区块与右区块" />
    <Border Grid.Column="2" Style="{StaticResource CardStyle}">…右区块…</Border>
</Grid>
```

### 2. 上下分块（横线、↕）

上下两块要**都是固定像素行**（`Height="700"` 这种，不是 `*`），这样拖其中一条缝才会一块变高一块变矮：

```xml
<Grid.RowDefinitions>
    <RowDefinition Height="Auto" />
    <RowDefinition x:Name="主区域行" Height="700" MinHeight="420" />
    <RowDefinition Height="12" />   <!-- 分隔条独占一行 -->
    <RowDefinition x:Name="日志行" Height="200" MinHeight="120" />
</Grid.RowDefinitions>
…
<core:PaneSplitter Grid.Row="2" Style="{StaticResource PaneSplitterHorizontalStyle}"
                   Key="工具Id.主区域与日志" />
```

> 整页还包在 `ScrollViewer` 里时（大多数工具页都是）：上下两块用固定像素行没问题，
> 拖完页面总高度会变，`ScrollViewer` 自己出滚动条。上方 `Auto` 行（标题 / 工具条）不用动。

### 3. 一侧固定、一侧 `*`（最常用、最稳）

- 左 `*` + 右像素：拖右边，左边吸收窗口变化 —— **推荐**；
- 左像素 + 右 `*`：拖左边；
- 两侧都 `*`：按比例分（窗口缩放比例不变），但**内容自己会撑宽时可能不准**，能不用就不用；
- 两侧都像素：一块大多少另一块就小多少，总宽 / 总高不变（滚动页里的上下两块就是这种）。

`PaneSplitter` 会根据 XAML 里写的 `*` / 像素自动选择存法（`.px` / `.a`+`.b` / `.r`），
工具页不用管；**只要给相邻两块写 `MinWidth` / `MinHeight`**（防止被拖没，实测最小 40）。

## 区块内部要能伸缩

只加分隔条还不够：区块内容要**撑得住**拖出来的空间，否则文字被裁掉 / 露出一大片空白。

- 列表、日志框这类「能滚的」放 `*` 行 / 列，删掉写死的 `Height="110"` / `MaxHeight="330"`
  （改成 `MinHeight` 兜底）；
- 卡片里内容比区块高（例如右侧一列设置）→ 给该列套一个 `ScrollViewer`
  （`VerticalScrollBarVisibility="Auto"` + `core:ScrollChaining.Enabled="True"`，滚到头会交给整页）；
- 卡片被拖得比内容还矮时，Border 上加 `ClipToBounds="True"`，别让内容溢到下一块上面；
- 固定像素行里的 Grid：内部用 `Auto` + `*` 分配（标题 Auto、主体 `*`、底部按钮 Auto）。

## 硬性规矩（违反会出大问题）

1. **键名**：`"<工具Id>.<区域名>"`，工具 Id 里不能有 `.`（设置页按第一个 `.` 分组显示「涉及哪些页面」）。
   同一个页面里不要重复键名。
2. **分隔条独占一行 / 一列**（宽度 12 左右），不要和内容挤在同一格里；`Direction` 已由样式设好，别在页面里改。
3. 不要再写 `GridSplitter`，也不要在页面里另写 Thumb / 分隔条模板 —— 样式统一在 `App.xaml`。
4. 相邻两块必须给 `MinWidth` / `MinHeight`；否则窗口很小时会被拖到 0，内容直接消失。
5. **别保存到工具自己的 `Config\<工具Id>\`**：布局统一走 `LayoutMemory`（`Config\layout.json`），
   这样「主页设置 → 界面布局 → 恢复默认布局」能一次清掉；「重建配置」不会误删布局。
6. 布局只影响大小，**不要顺手改行为**：绑定、命令、`x:Name`（代码后置要用）、单位 / 边框都不动。
7. 新工具页 / 改工具页时：先看 `FlashDownloaderView.xaml`（左右 3 栏 + 上下主区/日志，最全的参考）
   和 `SampleView.xaml`（模板）。

## 自检与验收

- `PaneSplitter.ComputeDrag(...)` 是纯函数（拖拽换算 + 夹取）：`MaintenanceChecks` 里有断言（`maintenance` 组）。
- `LayoutMemory` 的落盘 / 比例 / 分组 / `ResetAll` 也在 `maintenance` 组。
- 改完 XAML：`.\build.ps1` + 该工具页截图冒烟（`YLT_SHOT_TOOL=<工具Id> YLT_SHOT=shots\xxx.png`），
  确认默认尺寸不挤、区块内容没被裁。
- 想验证「记忆」：手动写 `Config\layout.json`（键名见上）再截图，页面应按记录的大小显示 ——
  别为了试一下去改代码。
