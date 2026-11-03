# 🌐 网页浏览器（Chromium 内核）

> 主页卡片「🌐 网页浏览器」（分类：网络）。用**微软官方 WebView2** 承载 **Chromium 内核**（和 Edge 同源），
> 浏览器外壳（地址栏 / 前进后退 / 刷新 / 主页 / 页内右键刷新）由本工具自己实现。

## 一句话

在工具集里直接上网：地址栏输入**网址**或**搜索词**，前进 / 后退 / 刷新 / 主页按钮，网页里**右键也能刷新**；
记住上次看的页面（可关）。后面再按需要加标签页、书签、下载管理。

## 怎么用

1. 打开工具（主页「🌐 网页浏览器」卡片）→ 自动进入**主页**（默认 `https://www.bing.com/`）。
2. **地址栏输入**：
   - `https://example.com/a?b=1` → 直接用（带 `http://` / `https://` 的地址原样打开）；
   - `example.com` / `www.bing.com/search?q=1` → 自动补 `https://`；
   - `localhost:8080` / `127.0.0.1:5000` / `192.168.1.5` → 自动补 `http://`（本地服务通常没证书）；
   - `file:///C:/temp/a.html`、`about:blank` 这类协议地址原样交给内核；
   - **其它内容当搜索词**（`天气 上海`、`hello world`）→ 用配置里的搜索模板（默认 Bing）搜。
3. 回车或点「前往」导航；**Alt+← / Alt+→** 前进后退，**F5 / Ctrl+R** 刷新（网页内核自带快捷键）。
4. **网页里右键** → 自定义菜单：**刷新**、后退、前进、复制、全选、开发者工具（F12）。
   这是本工具自己画的菜单（WebView2 默认右键菜单已关掉），所以第一条就是你要的「刷新」。
5. `target=_blank` / `window.open` 的链接**在当前页打开**（本工具暂时是单页）。

## 设置（`Config\browser\config.json`）

改一下自动落盘，没有保存按钮；改坏了会自动备份并回退默认值。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `HomePage` | `https://www.bing.com/` | 主页：点工具条 ⌂ 回到这里；没记住页面时打开也来这里 |
| `SearchUrl` | `https://www.bing.com/search?q={0}` | 地址栏输入不是网址时的搜索模板；`{0}` = 关键词。模板为空或没写 `{0}` 会回退默认 Bing |
| `RememberLastPage` | `true` | 是否记住上次关闭时看的页面（下次打开接着看） |
| `LastPage` | 空 | 上次访问的页面（自动写；`RememberLastPage=false` 时不使用） |

## 数据 / 缓存放在哪

- 配置：`Config\browser\config.json`
- **网页用户数据**：`Cache\browser\WebView2\`（Cookie / 登录态 / 网页缓存 / IndexedDB…）
  - 属于「随时可以删」的缓存：**主页设置 → 清理缓存**会把它一起清掉（登录态也会没）；
  - 内核正在运行时部分文件被占用，清理可能清不干净，切走再清 / 下次清理即可。

## 依赖与要求

- 需要系统装有 **Microsoft Edge WebView2 Runtime**（Chromium 运行时）。Windows 10 2004+ 一般随 Edge 自带；
  没有的话工具会提示「没有找到 WebView2 运行时」，到微软官网装一次即可（不随本项目打包，避免 100+MB 体积）。
- 本项目新增了 NuGet 包 `Microsoft.Web.WebView2`（见 [THIRD-PARTY-NOTICES](../../THIRD-PARTY-NOTICES.md)）：
  从源码构建时**首次需要联网还原一次**，之后走本机 NuGet 缓存（`build.ps1` 离线也能命中）。
- 网页需要联网；打不开时看工具条右边的状态文字（域名解析失败 / 拒绝连接 / 证书问题等）。

## 已知限制（后面可以继续加）

- **单页**：还没有标签页 / 书签 / 历史记录 / 下载管理 / 代理设置（想好了再加）；
- WebView2 是**原生窗口**：主页的粒子 / 鼠标特效这类 WPF 覆盖层在网页区域看不见（这是 Windows 混合渲染的
  限制，不是 bug）；`YLT_SHOT` 的 `RenderTargetBitmap` 截图也抓不到网页内容（见下）；
- 网页里的右键菜单是我们自己画的，所以只有上面那几项，不是 Chromium 的全套原生菜单；
- 下载走 WebView2 自带的下载提示（还没做下载列表 / 断点续传）。

## 调试 / 截图验收

- `YLT_BROWSER_URL=<地址>`：启动工具时**临时**打开指定页面（不写配置），用来测本地页面：
  ```powershell
  $env:YLT_SHOT_TOOL="browser"; $env:YLT_SHOT="shots/browser.png"
  $env:YLT_BROWSER_URL="data:text/html,<h1>Hello Chromium</h1>"
  $env:YLT_SHOT_DELAY_MS=8000
  Start-Process .\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe -Wait
  ```
- `YLT_SHOT` 截图模式下除了正常的 `shots/browser.png`（WPF 部分：工具条 / 提示层），还会用 WebView2 的
  `CapturePreviewAsync` 另存一张 **`shots/browser.png.web.png`** —— 这张才是网页区域，用来确认 Chromium
  真的把页面渲染出来了。
- 工具页切走 / 切回会重建网页内核（为了不留隐藏的 Edge 进程）；Cookie 在 `Cache\browser\WebView2` 里，
  所以登录态还在。

## 实现位置

| 内容 | 文件 |
| --- | --- |
| 工具入口 / 主页卡片信息 | `src/YuLiangToolkit.Tools.Browser/BrowserTool.cs` |
| 地址栏输入解析（纯逻辑，自检 `browser` 组） | `src/YuLiangToolkit.Tools.Browser/BrowserUrl.cs` |
| 配置项 | `src/YuLiangToolkit.Tools.Browser/BrowserConfig.cs` |
| 界面 / WebView2 生命周期 / 右键菜单 | `src/YuLiangToolkit.Tools.Browser/Views/BrowserView.xaml(.cs)` |
| ViewModel（命令 / 状态） | `src/YuLiangToolkit.Tools.Browser/ViewModels/BrowserViewModel.cs` |
