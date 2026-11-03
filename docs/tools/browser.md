# 🌐 网页浏览器（Chromium 内核 · 多标签页）

> 主页卡片「🌐 网页浏览器」（分类：网络）。用**微软官方 WebView2** 承载 **Chromium 内核**（和 Edge 同源），
> 浏览器外壳（**多标签页** / 标签栏上方或左侧 / 地址栏 / 前进后退 / 刷新 / 主页 / 页内右键刷新）由本工具自己实现。

## 一句话

在工具集里直接上网：**标签条上点 + 开新标签、× 或中键关标签**，标签栏可以放**上方**也可以放**左侧**；
地址栏输入**网址**或**搜索词**，前进 / 后退 / 刷新 / 主页按钮，网页里**右键也能刷新**；
关闭时记住打开着的一组标签页，下次打开接着看（可关）。

## 怎么用

1. 打开工具（主页「🌐 网页浏览器」卡片）→ 自动恢复上次的**一组标签页**（第一次 / 关了会话记忆则进入主页 `https://www.bing.com/`）。
2. **标签页**：
   - 点标签条右边的 **+**（或按 **Ctrl+T**）开新标签，去主页；
   - 点标签切换，**× / 中键**关标签（或 **Ctrl+W**），**Ctrl+Tab / Ctrl+Shift+Tab** 循环切换；
   - 关掉最后一个标签会自动补一个主页标签，页面永远有内容；
   - 双击标签条空白处也能开新标签；
   - 标签在加载中会显示一个**主题色小圆点**，标题加载出来前显示网址。
3. **标签栏位置**：点工具条右侧的 **⇄** 按钮在「上方 / 左侧」之间切换（写进配置，下次打开还是这个位置）。
4. **地址栏输入**（作用于当前激活标签）：
   - `https://example.com/a?b=1` → 直接用（带 `http://` / `https://` 的地址原样打开）；
   - `example.com` / `www.bing.com/search?q=1` → 自动补 `https://`；
   - `localhost:8080` / `127.0.0.1:5000` / `192.168.1.5` → 自动补 `http://`（本地服务通常没证书）；
   - `file:///C:/temp/a.html`、`about:blank` 这类协议地址原样交给内核；
   - **其它内容当搜索词**（`天气 上海`、`hello world`）→ 用配置里的搜索模板（默认 Bing）搜。
5. 回车或点「前往」导航；**Alt+← / Alt+→** 前进后退，**F5 / Ctrl+R** 刷新（网页内核自带快捷键）。
6. **网页里右键** → 自定义菜单：**刷新**、后退、前进、复制、全选、开发者工具（F12）。
   这是本工具自己画的菜单（收到 `ContextMenuRequested` 后接管掉 WebView2 默认菜单），所以第一条就是你要的「刷新」。
7. `target=_blank` / `window.open` 的链接**在新标签页里打开**（后台自动激活）。

> 快捷键 Ctrl+T / Ctrl+W / Ctrl+Tab 在焦点位于地址栏 / 标签条时可靠；焦点在网页内部时 WebView2（原生窗口）
> 可能自己吃掉按键——按钮永远可用。

## 设置（`Config\browser\config.json`）

改一下自动落盘，没有保存按钮；改坏了会自动备份并回退默认值。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `HomePage` | `https://www.bing.com/` | 主页：点工具条 ⌂ / 新建标签来到这里 |
| `SearchUrl` | `https://www.bing.com/search?q={0}` | 地址栏输入不是网址时的搜索模板；`{0}` = 关键词。模板为空或没写 `{0}` 会回退默认 Bing |
| `RememberLastPage` | `true` | 是否恢复上次关闭时的标签页会话 |
| `LastPage` | 空 | 上次激活的页面（自动写；兼容保留，会话恢复优先用 `SessionTabs`） |
| `TabBarPosition` | `Top` | 标签栏位置：`Top`（上方一行）/ `Left`（左侧一列）；写坏自动回退 `Top` |
| `SessionTabs` | `[]` | 关闭时打开着的标签列表（地址 + 标题，最多记 20 个；`RememberLastPage=false` 时不恢复但仍随手记录） |
| `ActiveTabIndex` | `0` | 恢复会话时激活第几个标签（越界自动夹回） |

## 数据 / 缓存放在哪

- 配置：`Config\browser\config.json`
- **网页用户数据**：`Cache\browser\WebView2\`（Cookie / 登录态 / 网页缓存 / IndexedDB…，所有标签共享一份）
  - 属于「随时可以删」的缓存：**主页设置 → 清理缓存**会把它一起清掉（登录态也会没）；
  - 内核正在运行时部分文件被占用，清理可能清不干净，切走再清 / 下次清理即可。

## 依赖与要求

- 需要系统装有 **Microsoft Edge WebView2 Runtime**（Chromium 运行时）。Windows 10 2004+ 一般随 Edge 自带；
  没有的话工具会提示「没有找到 WebView2 运行时」，到微软官网装一次即可（不随本项目打包，避免 100+MB 体积）。
- 本项目新增了 NuGet 包 `Microsoft.Web.WebView2`（见 [THIRD-PARTY-NOTICES](../../THIRD-PARTY-NOTICES.md)）：
  从源码构建时**首次需要联网还原一次**，之后走本机 NuGet 缓存（`build.ps1` 离线也能命中）。
- 网页需要联网；打不开时看工具条右边的状态文字（域名解析失败 / 拒绝连接 / 证书问题等）。

## 已知限制（后面可以继续加）

- **每个标签一个 WebView2 实例**：同一时刻只有激活标签的内核在显示（其余隐藏但仍占内存，和正常浏览器的
  后台标签一致）；工具页**切走 / 切回会重建内核**（为了不留隐藏的 Edge 进程），页面内容能接上（会话恢复），
  但每页的**历史记录从当前页重新开始**、后台标签的网页不实时刷新；
- WebView2 是**原生窗口**：主页的粒子 / 鼠标特效这类 WPF 覆盖层在网页区域看不见（这是 Windows 混合渲染的
  限制，不是 bug）；`YLT_SHOT` 的 `RenderTargetBitmap` 截图也抓不到网页内容（见下）；
- 网页里的右键菜单是我们自己画的，所以只有上面那几项，不是 Chromium 的全套原生菜单；
- 焦点在网页内部时 Ctrl+T / Ctrl+W / Ctrl+Tab 可能被 WebView2 吃掉（HwndHost 键盘路由限制），用标签条按钮一定可以；
- 下载走 WebView2 自带的下载提示（还没做下载列表 / 断点续传）；书签 / 历史记录 / 代理设置还没做。

## 调试 / 截图验收

- `YLT_BROWSER_URL=<地址>`：启动工具时**临时**把第一个标签导航到指定页面（不写配置），用来测本地页面：
  ```powershell
  $env:YLT_SHOT_TOOL="browser"; $env:YLT_SHOT="shots/browser.png"
  $env:YLT_BROWSER_URL="data:text/html,<h1>Hello Chromium</h1>"
  $env:YLT_SHOT_DELAY_MS=8000
  Start-Process .\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe -Wait
  ```
- `YLT_SHOT` 截图模式下除了正常的 `shots/browser.png`（WPF 部分：标签条 / 工具条 / 提示层），还会用 WebView2 的
  `CapturePreviewAsync` 另存一张 **`shots/browser.png.web.png`** —— 这张才是网页区域，用来确认 Chromium
  真的把页面渲染出来了。
- `YLT_BROWSER_TRACE=<日志路径>`：把内核启动（含每个标签的 `create-core` / `core-ready`）/ 导航完成 / 右键菜单
  这些关键节点追加写入日志，排查「内核没起来 / 右键没反应」时用（正常使用不产生文件）。
- `YLT_BROWSER_MENU_TEST=1`：首屏加载完后自动弹一次右键菜单，用来验证「WPF 菜单能盖在 WebView2 原生窗口上」。
- 想截「左侧标签栏」：把 `Config\browser\config.json` 的 `TabBarPosition` 改成 `"Left"`（或先在界面里点 ⇄）再截图。

## 实现位置

| 内容 | 文件 |
| --- | --- |
| 工具入口 / 主页卡片信息 | `src/YuLiangToolkit.Tools.Browser/BrowserTool.cs` |
| 地址栏输入解析（纯逻辑，自检 `browser` 组） | `src/YuLiangToolkit.Tools.Browser/BrowserUrl.cs` |
| 标签栏规则：位置归一化 / 会话清洗 / 关标签补位（纯逻辑） | `src/YuLiangToolkit.Tools.Browser/BrowserTabBar.cs` |
| 配置项（含会话快照 `SessionTabs`） | `src/YuLiangToolkit.Tools.Browser/BrowserConfig.cs` |
| 单个标签的状态（标题 / 地址 / 加载 / 前进后退） | `src/YuLiangToolkit.Tools.Browser/ViewModels/BrowserTab.cs` |
| ViewModel（标签列表 / 命令 / 工具条镜像 / 会话恢复） | `src/YuLiangToolkit.Tools.Browser/ViewModels/BrowserViewModel.cs` |
| 界面（上方 / 左侧标签条）/ 每标签一个 WebView2 / 右键菜单 | `src/YuLiangToolkit.Tools.Browser/Views/BrowserView.xaml(.cs)` |
