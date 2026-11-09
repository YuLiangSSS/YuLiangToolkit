# Live2D 桌宠 —— 开发者文档

> 工具 Id `live2d` ｜ 工程 `src/YuLiangToolkit.Tools.Live2D/` ｜ 自检组 `live2d`
> ｜ 用户文档 [`docs/tools/live2d.md`](../tools/live2d.md)
>
> 本文写给要改这个工具的人：分层架构、关键文件格式、坐标换算、渲染语义、构建发布、许可与坑。
> 不读也能用工具；改渲染 / 动作 / 物理 / 打包之前建议先过一遍。

## 1. 目标与边界

- 在工具集里内置「Live2D 桌宠」：**模型测试台**（参数 / 部件 / 动作 / 表情 / 物理 / 眼睛跟随逐项调）
  + **桌面宠物**（透明置顶、拖动、滚轮缩放、右键菜单、点击穿透、眼睛跟随鼠标、待机动画）；
- **不新增任何 NuGet 包**（仓库硬性规则）：模型加载靠官方 Cubism Core 原生 DLL，渲染靠自己写的软件光栅器，
  摄像头动捕靠 Windows 自带 MediaCapture / FaceAnalysis（OS 组件，零第三方库）；
- 可选**摄像头面部动捕**（转头 / 眨眼 / 张嘴，默认关）与**身体晃动测试**（看物理链）；
- 本轮新增 **AI 聊天 / 好感度**（DeepSeek，用户自填 API Key，见 §19）：HTTP / JSON / 提示词 / 好感度全部手写，
  API Key 的 DPAPI 加密复用已有的 `System.Security.Cryptography.ProtectedData` 包（邮箱代收已引），**不新增依赖**；
- 联网只发生在两个可选功能：**在线语音**（只发送要说的文字）与 **AI 聊天**（只发送人格提示词 + 最近对话 +
  好感度状态行）；其余功能全部本地；
- 不做 VTube Studio / VSeeFace 之类的协议联动；
- 只支持 Cubism 3+ 的 `.moc3` 模型（不支持 Cubism 2 的 `model.json`）。

## 2. 分层架构

```
YuLiangTookit（主程序）
  └─ Live2DTool : ITool ────────────── 工具入口：CreateView() 返回 Live2DView
      └─ Live2DSession ─────────────── 进程级会话：模型运行时 + 桌宠窗口的存活（切页不丢）
          ├─ Live2DViewModel ───────── 工具页 ViewModel（预览 / 参数 / 部件 / 动作 / 表情 / 设置）
          │   └─ Views/Live2DView.xaml
          └─ Views/PetWindow.xaml ──── 透明置顶桌宠窗口（拖动 / 滚轮 / 右键 / 点击穿透 / 眼睛跟随）
                │
                ▼
  Model/ModelRuntime ───────────────── 运行时：加载资源 / Tick（动捕·待机·晃动·动作·表情·物理）/ RenderFrame
      ├─ Live2DModelAsset ──────────── 模型资源发现（model3.json → moc / 纹理 / 物理 / cdi3 / 动作 / 表情）
      ├─ Motion3 / MotionPlayer ──────── motion3.json 解析、播放与混合（含 fade / loop）
      ├─ Expression3 ────────────────── exp3.json 解析 + 表情管理器（每表情独立权重施加）
      ├─ Physics3 / CubismPhysicsEvaluator ── physics3.json 解析 + 钟摆物理求值
      ├─ IdleBehavior（待机 / 眨眼）/ EyeTrackingController（眼头身跟随）
      ├─ BodySwayController ────────── 身体晃动测试（正弦 / 多频叠加写身体 + 物理输入参数）
      ├─ GesturePlayer / PerformancePlayer / DanceController ── 动作系统（见 §14）
      │   ├─ GesturePlayer ────────── 8 手势：3 作者 enter/exit 动作 + hold 参数 / 5 参数姿势
      │   ├─ PerformancePlayer ────── 6 个数据驱动时间线表演（Pose/Look/Nod/Talk/Gesture）
      │   ├─ DanceController ──────── 4 程序化舞蹈模式（相位驱动，0.25s 权重淡入淡出）
      │   ├─ InteractionController ── 鼠标互动（见 §16）：12 热区 / 摸头 / 三态表情 / 捏脸 / 挠痒 / 护住 / 拖拽物理
      │   └─ PetIdleActions ───────── 互动待机小动作（6 套程序化动作 + 可选表情 + 间隔/冷却门控）
      │
      ├─ Mocap/ ────────────────────── 摄像头面部动捕（见 §10）
      │   ├─ MocapCameraSource ─────── MediaCapture + MediaFrameReader 采集 + FaceDetector 检测（WinRT）
      │   ├─ FaceExpressionAnalyzer ── 人脸框内像素启发式（眨眼 / 张嘴）+ 滚动分位数自动校准
      │   ├─ MocapMath ─────────────── 脸位置 → 头部角度 / 眼球方向（纯数学，自检可测）
      │   └─ MocapContracts ────────── IMocapProvider / MocapTrackingResult / MocapPreviewFrame / MocapSettings
      │
      ├─ Audio/ ────────────────────── 音乐跟唱 / 律动（见 §13）
      │   ├─ WasapiLoopbackCapture ─── WASAPI 回环采集（P/Invoke COM，48k 立体声 float）
      │   ├─ SmtcWatcher ───────────── SMTC 媒体信息（歌名 / 歌手 / 应用，只显示用）
      │   ├─ MusicReactiveSource ───── IMusicReactiveProvider：采集 → 分析 → 发布不可变快照
      │   ├─ MusicReactiveContracts ── MusicContentKind / MusicAnalysisSnapshot / IMusicReactiveProvider
      │   └─ Analysis/ ─────────────── 纯算法（自检可测）
      │       ├─ FastFft ───────────── radix-2 FFT + 幅度谱 + 朴素 DFT 对拍 + Hann 窗
      │       └─ MusicAnalyzer ─────── 48k→16k 重采样 / FFT 特征 / YIN / 分类 / 节拍 / 唇形
      │
      ├─ Speech/ ───────────────────── 语音说话 / 轻对话 / 气泡（见 §15）
      │   ├─ SpeechContracts ───────── SpeechVoiceInfo（在线 / 本地，Detail）/ SpeechUtterance
      │   ├─ WaveAudio ─────────────── WAV RIFF 块解析 + 16bit 样本读取 + 20ms 唇形包络（纯静态，自检可测）
      │   └─ TtsService ────────────── 在线 Edge 软妹音（失败回退）→ 本地 WinRT TTS → MediaPlayer 播放 + 口型
      │
      ├─ Ai/ ───────────────────────── DeepSeek AI 聊天 / 好感度（见 §19）
      │   ├─ AiContracts / AiChatService ── 契约 + 好感度引擎 / 聊天流程 / 历史裁剪 / token / 主动调度 / 落盘
      │   ├─ AiPromptBuilder / AiReplyParser ── 提示词与模型输出解析（纯函数，自检可测）
      │   ├─ DeepSeekClient ────────── 手写 HTTP 客户端（可注入 HttpMessageHandler 自检）
      │   ├─ AiSecretStore ─────────── API Key 的 DPAPI 加解密（dpapi: 前缀，兼容历史明文）
      │   ├─ Controls/AffectionVial ── 粉色好感度试管（自绘 + LiquidRatio 纯函数）
      │   └─ Views/AiChatWindow / AiChatPlacement ── 聊天窗 UI 与贴桌宠的纯定位算法
      │
      ├─ Rendering/CubismRenderer ──── 手写软件光栅器（三角形光栅化 / 蒙版 / 三种混合 / 调试叠加）
      │   └─ Rendering/TextureAtlas ── 纹理 PNG 加载 → BGRA 像素（可选降到 2048）
      │
      └─ Interop/CubismMoc + CubismModel + CubismCore ── Cubism Core 的 P/Invoke 薄封装
                  │
                  ▼
        Live2DCubismCore.dll（官方原生库，exe 旁边；csm* API）
```

**线程模型**：Cubism Core 的模型对象**不是线程安全的**。加载（解码 moc / 纹理 / 解析 json）可以在后台线程做，
但 `csmUpdateModel` / 渲染 / 参数写入都只在 **UI 线程**（`DispatcherTimer` 里 Tick + Render）；
工具页与桌宠共用同一个 `ModelRuntime`，不要各建一个（Core 模型会重复吃内存）。

**动捕线程模型**（细节见 §10）：摄像头采集回调在 WinRT 线程池 → 把帧丢进 `Channel`（最多缓存 2 帧、
满了丢旧帧）→ 单独的后台 task 串行做「降采样 + FaceDetector 检测 + 眨眼 / 张嘴像素分析 + 平滑」→
发布**不可变快照**（`MocapTrackingResult` / `MocapPreviewFrame`，整体引用替换）→ UI 线程 Tick 里只读
`IMocapProvider.Latest`。摄像头生命周期归 `Live2DSession` 管（页面或桌宠可见且开关打开才启动，
关掉立即释放设备，隐私 + 省 CPU）。

**音乐线程模型**（细节见 §13）：WASAPI 回环采集在独立后台线程（`AboveNormal`，`Sleep(5)` 轮询
`GetNextPacketSize`）→ 每包数据**同步**交给 `MusicAnalyzer.Analyze`（纯算法、无锁、0 分配，目标 < 1.5ms）→
发布**不可变快照** `MusicAnalysisSnapshot`（volatile 引用替换）→ UI 线程 Tick 里只读
`IMusicReactiveProvider.Latest`（超过 800ms 视为静音）；SMTC 媒体信息由另一个后台线程每 2 秒轮询（失败不影响
音频分析）。音频采集生命周期归 `Live2DSession` 管：`MusicReactiveEnabled && (页面可见 || 桌宠可见) && 非截图模式`
才启动；关掉开关 / 切走页面 / 隐藏桌宠立即停止并释放设备。

**语音线程模型**（细节见 §15）：`TtsService` 由会话懒创建；合成（在线 Edge WebSocket / 本地 WinRT）用
`SemaphoreSlim` 串行化、`SynthesizeAsync` 返回内存 WAV + 包络；`SpeakAsync` 把 WAV 写进
`InMemoryRandomAccessStream` → `MediaPlayer.Play()`，**播放真正开始时**回调 `onPlaybackStarted`
（会话在这里弹气泡 + 自动小动作）；会话帧循环（~60fps）把 `TtsService.IsSpeaking / MouthOpen` 同步到
`ModelRuntime.SpeechActive / SpeechMouthOpen`（Tick 里写嘴型，动捕有脸时让位）；播放完成靠按 WAV 时长轮询
（`MediaEnded` 不可靠）收尾并释放播放器 / 流；App 退出时 Dispose。气泡窗口由会话每帧按桌宠位置重算坐标，
不抢焦点、点击穿透。

**AI 聊天线程模型**（细节见 §19.3）：`AiChatService` 由会话懒创建于 UI 线程，`await` 回构造时的
`SynchronizationContext`（WPF 有 UI 上下文，事件也在 UI 线程触发）；请求用 `SemaphoreSlim` 串行化，
同一时间只有一条在途（`IsBusy` 期间 `TickSchedule` 跳过主动说话，不打断正在等回复的那一轮）；
主动说话由会话的 **2 秒 `DispatcherTimer`** 调 `TickSchedule(now, userPresent)`，内部 `FireAndForget`
不阻塞 Tick；落盘是同步小 JSON（≤200 条消息，`ToolStorage.WriteText` 原子写），失败只记日志、绝不打断聊天。

**为什么不引 WebView2 / SkiaSharp / 官方 C# SDK**：

- 引 WebView2 / SkiaSharp 都要多一份分发或兼容成本（「网页浏览器」工具已经用 WebView2 承载网页，但桌宠要求透明叠加 / 点击穿透 / 零额外运行时，继续自写更稳；这不是项目全局禁令，见 AGENTS.md 规则 2）；
- 官方 Cubism Web SDK 是 JS，得套 WebView2（还要另外分发 WebView2 Runtime），且和 WPF 的透明叠加 / 点击穿透难配合；
- 官方 Cubism Native SDK 是 C++，跨桥接引入的复杂度比自写一个「只需要画三角形」的光栅器还高；
- 自写软件光栅器只有几千行，完全可控、零分发依赖，桌宠尺寸下性能足够（见 §7）。

## 3. 关键文件格式（实现语义的 source of truth）

官方 Cubism SDK 的 Native / Web Framework 是这些格式的权威实现（`CubismMotion.cpp`、`CubismExpressionMotion.cpp`、
`CubismPhysics.cpp`、`CubismRenderer.cpp`、`CubismShader_D3D11.cpp`）。下面只记要点，细节看官方源码 / 文档。

### 3.1 moc3 与 Cubism Core

- `moc3` 是二进制模型数据（内置模型 16.5 MB，格式版本 **5** = Cubism 5；Core 的 `csmGetMocVersion` 报版本号）；
- 流程：读成 byte[] → **64 字节对齐**的原生内存（`NativeMemory.AlignedAlloc`）→ `csmReviveMocInPlace`
  → `csmGetSizeofModel` → **16 字节对齐**的 model 缓冲区 → `csmInitializeModelInPlace`；
- 之后每帧：写参数值 / 部件不透明度 → `csmUpdateModel`（Core 内部求值 + 变形）→ 从 Core 拿
  每个 drawable 的顶点指针 / UV / 三角形下标 / 不透明度 / 可见性 / 渲染顺序；
- **moc 缓冲区在 moc 生命周期内不能移动 / 释放**（Core 内部直接引用它）；同理 model 缓冲区；
- `csmHasMocConsistency` 可以做一次数据一致性校验（自检会跑，16.5 MB 大约几十 ms）。

`Interop/CubismCore.cs` 里是全部 P/Invoke 声明；**不要改成 `[DllImport]` 带路径**：`Live2DCubismCore.dll`
和 exe 放同一目录，用裸文件名让 .NET 默认搜索（`AppContext.BaseDirectory` 在探测路径里）。
x64 only（官方 Core 只提供 x86_64；主程序 / 自检都编译成 x64 语义运行）。

### 3.2 motion3.json（动作）

```jsonc
{
  "Version": 3,
  "Meta": {
    "Duration": 5.983,          // 时长（秒）
    "Fps": 60.0,
    "Loop": true,               // 建议循环
    // FadeInTime / FadeOutTime 可缺省；缺省时官方按 1.0 秒，本实现用 0.3 秒（见下面说明）
  },
  "Curves": [
    { "Target": "Parameter", "Id": "ParamAngleX",
      "Segments": [ 0, 0,               // 第 1 个点：t0=0, v0=0
                    0, 0.5, 30,         // 段类型 0 = 线性：t=0.5 时到 30
                    1, 0.8, 30, 0.6,30, 0.7,30,   // 段类型 1 = 三次贝塞尔：t, v, c1x,c1y, c2x,c2y
                    2, 1.0, 30,         // 段类型 2 = 阶跃（保持上一值到 t 再跳）
                    3, 1.2, 30 ] },     // 段类型 3 = 反向阶跃
  ]
}
```

- `Segments` 第一个数是起点时间、第二个是起点值，之后按段类型分组：类型 0/2/3 占 3 个数、类型 1 占 7 个数；
- 求值：找到 `t` 落在哪一段，线性插值 / 贝塞尔控制点求解 / 阶跃保持不变；`t` 超过 Duration 用最后值。
  注意贝塞尔的控制点 x 是**绝对时间**、且可以超出段端点（`AreBeziersRestricted = false`），
  官方用 Cardano 解 `x(t) = time` 再求 `y(t)`，不能简单按归一化 `u` 插值（自检里的求值器只是近似，
  只用来断言 t=0 / t=Duration / 中段值合理）；
- **fade**：开始播放时动作权重按 `FadeInTime` 正弦缓动 0 → 1，结束（非循环）时按 `FadeOutTime` 下降；
  权重用来和「底层值」混合。本实现为了短动作（1~2 秒的手势）能看清，**动作文件没写 FadeInTime 时
  默认用 0.3 秒**（官方默认 1.0 秒会把这些短动作糊掉）；循环动作只在第一次淡入，不每次回绕都淡入；
- 动作对参数是**覆盖式驱动**（motion 权重 1 时参数 = 动作值；权重在 fade 中段混合）；
  动作帧率 `Fps` 只影响采样密度，播放时按真实时间求值即可；
- 没被动作曲线覆盖的眨眼 / 口型参数，用 Model 曲线（`Target = Model`、`Id = EyeBlink/LipSync`）补写。

### 3.3 exp3.json（表情）

```jsonc
{
  "Type": "Live2D Expression",
  "FadeInTime": 1, "FadeOutTime": 1,      // 可缺省
  "Parameters": [
    { "Id": "key4", "Value": 1.0, "Blend": "Add" }   // Blend: Add / Multiply / Overwrite
  ]
}
```

- 表情在动作之后写参数：`Add` = 当前值 + Value×权重，`Multiply` = 当前值×Value，`Overwrite` = 直接写；
- 权重按 `FadeInTime` 淡入；清除时按 `FadeOutTime` 淡出；
- **UI 语义：同一时间只有一个表情生效**（官方 `CubismExpressionMotionManager` 的行为）：切换时旧表情淡出、
  新表情淡入（交叉淡化），旧表情没被新表情引用的参数回默认值。UI 的表情卡是单选，切换 = 先 Clear 再 Apply；
- **实现是「叠加超集」**：`ExpressionManager` 按每个表情自己的权重独立施加（加算相加 / 乘算相乘 / 覆盖插值），
  多个表情同时施加时都会生效、单个表情时结果与官方一致。UI 单选所以表现为官方单激活；保留叠加能力是给
  将来「多表情叠加」留口子。自检覆盖「单选切换」与「API 连续施加两个都生效」两种路径；
- 表达式的参数一般不在 moc 里（`key1`~`key13` 这类开关参数是模型作者加在 moc 里的，值变化会驱动对应网格显隐）。

### 3.4 physics3.json（物理）

- `Meta`：`PhysicsSettingCount / TotalInputCount / TotalOutputCount / VertexCount / Fps / EffectiveForces / PhysicsDictionary`；
- 每组 `PhysicsSettings[i]`：`Input`（输入参数 + 权重 + 轴类型 X/Y + 是否反向）、`Output`（输出参数 + 权重 + 是否反向）、
  `Vertices`（钟摆的摆球：Position / Radius / Weight）、`Normalization`（位置 / 角度 / 权重的 min-default-max）；
- 求值按官方 `CubismPhysics` 的**钟摆模型**：输入归一化 → 平滑 → 加重力 / 风 → 每摆球做角度积分 →
  输出归一化写回 Output 参数。`Fps` 是模拟频率（内置模型 60），Tick 的 delta 要按它累计（掉帧时拆成多次物理步进）；
- 输入参数（如 `ParamAngleX` / `ParamAngleZ`）变了，物理输出（头发 / 尾巴 / 飘带参数）会跟摆；
  关掉物理 / 强度 = 0 时**不要写 Output 参数**（保留动作 / 参数给的值），否则模型会「僵住」。

### 3.5 cdi3.json（显示信息）

- `Parameters`：`Id / GroupId / Name`（中文名）
- `ParameterGroups`：分组（`Id / GroupId / Name`）
- `Parts`：`Id / Name`（内置模型的 cdi3 **没有 PartGroups 段**，所以 PartBinding 一律归「其他」）
- 作用只是 UI 显示名 / 分组；文件缺失时 UI 退回显示英文 Id，不影响运行。

## 4. 坐标与画布换算（最容易错的地方）

- moc 里的顶点坐标是**模型单位**（Model unit）：画布宽 = **1.0** 模型单位；
- 内置模型：画布 **3749 × 7697 px**，原点 `(1874.5, 3848.5)`，`PixelsPerUnit = 3749`；
- **模型单位 y 轴向上为正**，而画布像素 / 位图像素 y 轴向下 —— 换算：
  `canvasX = originX + x * ppu`，`canvasY = originY - y * ppu`（`CubismModel.UnitsToCanvas` 已封装）；
- 渲染到输出位图：`RenderOptions.FitCanvas = true` 时取
  `scale = min(Width / 3749, Height / 7697)`（等比、不变形），
  `offsetX = (Width - 3749*scale) / 2`、`offsetY = (Height - 7697*scale) / 2` 居中；
  否则直接用 `Scale / OffsetX / OffsetY`；
- 输出缓冲区是 **Pbgra32（预乘 BGRA）**：WPF `WriteableBitmap.PixelFormats.Pbgra32` 要的就是预乘 alpha，
  写像素时 `b = b*a`（0~255），否则透明边缘会发黑 / 发白；
- 透明背景：整块先清 0（alpha = 0），只在有三角形覆盖的地方写像素。

## 5. 自写软件光栅器（渲染管线）

按 `model.RenderOrderedDrawableIndices`（Core 给的 render order，升序）逐个 drawable：

1. **跳过** `!IsVisible` 或 `Opacity <= 0`；
2. 把该网格的三角形从模型单位 → 画布像素 → 输出像素（缩放 + 偏移），对每个三角形做**扫描线光栅化**；
3. UV 线性插值 → 到对应纹理（`TextureAtlasSet[TextureIndex]`，图集已按纹理上限降采样到最长边 ≤ 2048 / 4096）采样（最近邻或双线性）；
4. **蒙版**：`MaskIndices` 里的网格先各自光栅化成覆盖缓冲（**只取纹理 alpha**，不乘蒙版网格自身的
   `Opacity` ——官方 `PixelSetupMask` 就是这样）；多个蒙版取**并集覆盖度** `1 - Π(1 - a_i)`；
   `IsInvertedMask` 取 `1 - 覆盖度`；被蒙版网格的 `color.a`（已乘自身 opacity）再乘覆盖度 ——
   **部分透明像素**是 Live2D 常见效果（眼白 / 光效），不能二值化；
5. **颜色公式**（以官方 shader 为准，见 `CubismEffect.fx` / `CubismRenderer_D3D11`）：
   `tex.rgb *= MultiplyColor.rgb` → `tex.rgb = tex.rgb + ScreenColor.rgb - tex.rgb * ScreenColor.rgb`；
   `color.rgb *= alpha`（预乘），最后有蒙版时整条 `color` 再乘覆盖度；
6. **混合**（预乘 alpha 下，D3D11 状态：Normal = `(ONE, INV_SRC_ALPHA)`、Additive = `(ONE, ONE)`、
   Multiplicative = `(DEST_COLOR, INV_SRC_ALPHA)`）：
   - Normal：`out = src + dst * (1 - src.a)`（alpha 也走 src-over）
   - Additive：`out.rgb = dst.rgb + src.rgb`（alpha 不动）
   - Multiplicative：`out.rgb = src.rgb * dst.rgb + dst.rgb * (1 - src.a)`（alpha 不动）
7. 一帧画完再画**调试叠加**（包围盒 / 蒙版范围 / 高亮 / 蒙版缓冲）；
8. **分块并行**：按 **128×128** tile 切分，`Parallel.For` 逐 tile 光栅化（每个 tile 一个局部像素缓冲，
   只画与它相交的三角形，最后拼回输出数组）；蒙版覆盖缓冲按「蒙版组合」在整帧范围维护（只清上一帧的
   脏矩形）；桌宠窗口小、tile 少时并行度按逻辑核数自动定（`RenderOptions.MaxDegreeOfParallelism` 可覆盖）。

**实测（分构建类型，重要）**：

| 场景 | 输出尺寸 | Release 渲染耗时 | Debug |
| --- | --- | --- | --- |
| 工具页预览 | 855×430（已改成只按模型实际显示区域出图，不再整块容器渲染） | ≈ **10 ms** | 慢 2.5~3× |
| 桌宠 | 420×862 | ≈ **14 ms** | 慢 2.5~3× |
| 大尺寸预览 | 700×1400 | ≈ **25~30 ms** | 慢 2.5~3× |

- 工具页预览改成按模型实际显示区域出图后，**发布版（Release）稳定 30fps**；
- **发布版（Release）才能代表真实性能；Debug 只用于开发**（Debug 慢 2.5~3×，工具页 41 ms 那类数字都是 Debug 下的）；
- 自检里的性能哨兵 `< 200 ms` 只用来防渲染退化到不可用，**别拿它当达标依据**。

**性能目标（设计目标）**：桌宠 420×860 @30fps 渲染 < 8 ms；测试页 ~700×1400 @30fps < 16 ms；
Core `csmUpdateModel` 全量 715 网格约 **1.8 ms** / 帧。当前 Release 实测大尺寸预览 25~30 ms 尚未达到
16 ms 目标，属于后续优化空间（桌宠 / 测试台默认尺寸已够用）。

## 6. 动作 / 表情 / 物理 / 眼睛跟随 / 动捕 / 晃动 / 语音的求值顺序

每帧 `Tick(delta)` 的顺序（`Model/ModelRuntime.cs`，和官方 Cubism 更新管线一致）：

1. **只把「受管参数」恢复默认**：受管参数 = 动作曲线 / 表情 / 物理输出 / 待机 / 眼睛跟随 / 动捕 / 晃动
   会写到的参数（加载时收集成下标表）。其余参数（模型里大部分纯调试参数）**不会**被每帧重置，所以工具页
   手动拖滑块在它们上面是有效的；
2. **动捕状态**：读 `IMocapProvider.Latest`（启用 + 有脸 + 时间戳距今 ≤ 600ms 才算有效），刷新
   `MocapHasFace / MocapLastFaceAgeMs / MocapStatusText`；
3. 待机行为：呼吸（`ParamBreath`）/ 自动眨眼（`ParamEyeLOpen/ParamEyeROpen`）/ 待机摇摆写参数；
   **动捕接管眨眼时不自动眨眼**（避免抢同一批参数）；
4. 有脸 → **动捕写参数**（`ParamAngle*` + `Angle*`、`ParamBodyAngleX`/`PositionX` 温和跟随、`ParamEyeBall*`、
   `EyeLOpen/ROpen`、`ParamMouthOpenY`）；没脸 → 鼠标眼控 + 待机摇摆（两套互斥，见 §10 优先级）；
3.5 **语音口型**（`SpeechActive`）：`ApplySpeechMouth` 写 `ParamMouthOpenY / MouthForm / JawOpen`；
   **动捕有脸时让位动捕**；语音期间音乐跟唱闭嘴（优先级：**动捕脸 > 语音 > 音乐**）；
5. **身体晃动测试**（`SwayEnabled` 或「摇一摇」倒计时 > 0 时）：`BodySwayController.Apply` 写身体主轴 /
   物理输入 / 头部补偿 / 腰腿参数（见 §11）；
5.5 **音乐跟唱 / 律动**（`MusicEnabled` 且 `Music.Latest` 在 800ms 内）：`ApplyMusicResponse` 写跟唱嘴型
   （`ParamMouthOpenY/MouthForm/JawOpen`，语音 / 动捕有脸时让位）+ 轻微点头（`ParamAngleY`）；
   **身体律动改由 4.6 的整身轻倾表现**（不再写 `ParamBodyAngle*` 等扭身体参数）；在手动晃动之后、
   在动作之前；`EyeTrackingEnabled` 时点头也不写（见 §13）；
4.6 **整身轻倾**（`BodyLean`）：常态 4s/±0.6°、有音乐平滑加快到 2.6s/±1.0°×MusicSwayStrength；只算
   `BodyLeanDegrees` 给渲染层旋转（**不写模型参数**，见 §11.5）；
6. 待机动作：待机动画开着且当前没有动作在播时，循环播 `IdleMotion`；
7. 动作：当前动作按时间求值 + 淡入淡出权重混合，写参数；
7.5 **手势 / 表演 / 舞蹈**：`UpdateActionPlayers` 在动作更新之后写（覆盖 IDLE 手臂曲线），在表情 / 物理之前；
8. 表情：按权重施加当前表情（UI 单选；管理器本身是叠加超集，见 §3.3）；
8.5 **待机随机小动作**：条件满足时按随机间隔播手势 / 表演（见 §14.5）；
9. 物理：读输入参数（前面写好的值）→ 钟摆求值 → 写输出参数；
10. `model.Update()`（Core 求值 + 变形）→ 触发 `FrameUpdated` → UI 渲染。

**参数优先级速查**：动捕有脸 > 语音 > 音乐（嘴型）；动捕有脸 > 鼠标眼控 > 音乐点头（头部）；
手动晃动测试 > 音乐（身体参数，音乐已改为渲染层倾斜不写参数）；手势 / 表演 / 舞蹈 > 动作 IDLE（手臂）；
待机随机小动作只在没有动捕脸 / 语音 / 音乐 / 手势 / 表演 / 舞蹈、且当前动作是 IDLE 或没有时触发。

**眼睛跟随数学**（自检会验）：目标在画布中心 → 角度 ≈ 0；目标在右侧 → `ParamAngleX` / `ParamEyeBallX` 为正。
本轮新增 4 个手感参数（`EyeTrackingController`）：

```
归一化目标 nx = clamp((canvasX - centerX) / centerX, -1, 1)
           ny = clamp((canvasY - centerY) / centerY, -1, 1)
加增益 / 偏移（按实现顺序）：
  nx *= clamp(HorizontalGain, 0.3, 3)                         // 横向灵敏度
  ny  = ny + clamp(VerticalOffset, -0.5, 0.5)                  // 先加纵向中性偏移
  ny *= ny < 0 ? clamp(UpGain, 0.3, 3)                         // 负 = 目标在上方 = 抬头
               : clamp(DownGain, 0.3, 3)                       // 正 = 目标在下方 = 低头
  再分别 clamp 到 -1~1（平滑之前）
平滑后：
  angleX   = clamp(curX, -1, 1) * 30 * clamp(HeadStrength, 0, 1.5)
  angleY   = clamp(-curY, -1, 1) * 30 * clamp(HeadStrength, 0, 1.5)
  eyeBallX = clamp(curX,  -1, 1) * clamp(EyeStrength, 0, 1.5) → 再夹到参数范围 -1~1
```

- **顺序坑**：`VerticalOffset` 是在乘上下增益**之前**加到归一化目标上的，所以偏移量本身也会被增益放大；
  正偏移 = 中性点下移（更容易出现「低头」侧）。
- 强度 clamp 从 0~1 放宽到 **0~1.5**（和 UI 滑杆一致）；眼球参数本身范围 -1~1，强度 >1 时会先放大再被参数
  范围夹住（视觉上更快到满，但不会越界）；头部最大 30° 同理；
- 上下不对称修正就是 Up/DownGain 分开 + VerticalOffset：向下跟不到 → 大 DownGain / 正 VerticalOffset；
- 平滑是「每帧向目标插值」的指数平滑，多次迭代后收敛到目标方向；`EyeTrackingSmoothing` 越大越柔和；
- 这些值都来自 `Live2DConfig`，测试页里改（`ModelRuntime.EyeTracking*` 属性）桌宠共用。

**物理强度上限 = 1**（`PhysicsEnabled` 打开时把 `PhysicsStrength` 夹到 0~1 传给评估器），UI 滑杆也是 0~1。

### 6.1 受管参数动态化（本轮新增）

原来「受管参数」（每帧开头恢复默认的那批）是**加载时一次性收集的静态数组**，所以动捕 / 晃动 / 眼控 / 物理 /
待机**关掉以后**，它们写过的参数仍然每帧被复位 → 手动用「面部测试」卡的滑杆调嘴 / 眼 / 舌头会被打回默认。

现在改为**按「这一帧真的会写哪些参数」动态复位**（`ModelRuntime.Tick` 开头）：

| 子系统 | 每帧怎么复位 | 关掉后的行为 |
| --- | --- | --- |
| 动作 MotionPlayer | `ResetMotionTouchedParameters`：只复位**上一帧动作实际写过的**参数（touched 下标表） | 不再写 → 参数保留最后值，手动可改 |
| 表情 ExpressionManager | 有表情生效时才复位 `_expressionManaged` 组 | 同上 |
| 物理 CubismPhysicsEvaluator | `PhysicsEnabled` 才复位物理输出组 | 物理输出保留最后值 |
| 待机 IdleBehavior | `IdleEnabled` 才复位呼吸组；`blinkActive`（自动眨眼且没被动捕接管）才复位眨眼组 | 呼吸 / 眨眼参数不再复位 |
| 鼠标眼控 EyeTrackingController | `EyeTrackingEnabled` 才复位眼控组 | 头部 / 眼球 / 身体参数不再复位 → 手动可改 |
| 动捕 ApplyMocapFace | `MocapEnabled` 才复位动捕组 | 没脸 / 关掉 → 参数不再复位（回落到鼠标眼控时由眼控接管） |
| 身体晃动 BodySwayController | `SwayEnabled` 或「摇一摇」倒计时 > 0 才复位晃动组 | 身体 / 腰腿参数不再复位 |
| 音乐 Music | `ResetMusicTouchedParameters`：只复位**上一帧音乐实际写过的**参数（touched 下标表） | 不写 → 参数保持 / 手动值保留 |

设计约定：

- **「写」和「复位」必须成对**：写参数时记录下标（touched），下一帧开头只复位自己写过的；这样动捕 / 音乐 / 动作
  这类「时写时不写」的系统关掉后不会残留在旧姿势；
- 开关切换的那一帧要**先复位一次再交给新状态**（`HandleManagedGroupTransitions`：物理 / 待机 / 眨眼 / 眼控 /
  动捕 / 晃动从开→关时复位对应组一次，之后不再碰，让手动值保留）；
- 动作 / 音乐这类「时写时不写」的用 touched 列表而不是固定组，避免误复位用户手动调的参数；
- 自检断言：关掉全部驱动后手动设 `ParamMouthOpenY=0.8 / TongueOut=1 / ParamEyeBallY=-1`，Tick 30 帧后仍保持；
  打开动捕（假 provider 有脸）后 `ParamMouthOpenY` 被每帧驱动覆盖；关掉音乐后手动嘴值 0.6 保留 20 帧。

## 7. 性能要点

- **瓶颈一：Core Update**（每帧 `csmUpdateModel`）：与参数变化量有关，全量 715 网格约 1.8 ms（8 核机器），
  没有变化时省不了（Core 没有增量接口），所以帧率上限要可调（15/30/60）；
- **瓶颈二：软件光栅化**：耗时 ≈ 窗口像素数 × 三角形重叠层数；桌宠窗口别开太大（默认 420 宽），
  渲染倍率 0.5~1.5 是「按屏幕像素渲染的倍率」；
- **纹理内存**：内置模型 5 张纹理的实际尺寸是 4096×8192 / 4096×8192 / 2048×2048 / 8192×8192 / 4096×4096，
  按「最长边 ≤ maxSize」降采样：2048 档合计约 50~60 MB，4096 档约 200 MB（原尺寸约 500 MB）——默认 2048；
- **解码耗时**：首次加载 5 张 PNG 约 1~3 秒（先按 2 倍目标尺寸让 WIC 解码，再箱式滤波降采样；加载时给进度）；
- 参数滑杆 / 表情 / 动作拖动时只改值，**不要每改一次就 `Update + Render`**（让 DispatcherTimer 统一帧驱动）；
- **摄像头动捕**：只在启用时耗 CPU。内置摄像头实测「降采样 + FaceDetector + 眨眼/张嘴分析 + 预览」
  一帧约 **8~10ms**（15fps 时约占单核 13%，24fps 更高）；分析帧固定 320 宽、优先 640 宽采集格式，
  不要喂 720p/1080p 给 FaceDetector（慢 3~4 倍）；关掉「显示摄像头预览」省掉每帧 ~230KB 的 BGRA 拷贝；
- **身体晃动**：每帧只写 17 个参数、0 堆分配，实测 **< 0.1ms**（可忽略）；
- **音乐跟唱**：只在启用时耗 CPU。WASAPI 采集线程 `Sleep(5)` 轮询（几乎不占 CPU）+ 分析器每包
  目标 < 1.5ms（62.5 hop/s，实测 0.5~1.2ms；全预分配、0 分配）；静音时走快速路径跳过 FFT / YIN，
  没人放音乐时基本不耗 CPU；SMTC 每 2 秒一次轻量轮询。关掉开关 / 切走页面 / 隐藏桌宠立即停止采集释放设备。
- **语音说话**：只在用户触发时耗 CPU / 网络。在线合成延迟主要是网络往返（约 0.3~2 秒，逐句串行、不占渲染帧）；
  本地合成通常 < 0.3 秒；播放期间每帧只按播放进度取包络值（O(1)），气泡窗口透明合成开销很小；
  不保存音频、播完立即释放流 / 播放器。
- 调试视图（包围盒 / 蒙版缓冲）只在开启时画，别常开。

## 8. 构建 / 发布：模型与原生 DLL 怎么跟着 exe 走

工程 `YuLiangToolkit.Tools.Live2D.csproj` 里两条复制规则（**别改成手写路径 / 别用 post-build copy**）：

1. **原生 DLL**：`ThirdParty\Live2DCubismCore\Live2DCubismCore.dll` → 输出目录根（`Link="Live2DCubismCore.dll"`，
   `CopyToOutputDirectory=PreserveNewest` / `CopyToPublishDirectory=PreserveNewest`）；
2. **内置模型**：`DeepSeek\**\*` 作为 `Content` 拷到 `Live2D\Models\DeepSeek\**`（`Link` 里带
   `$(Live2DModelOutputDir)`），排除 `psd\**`、`*.vbridger`、`*.vtube.json`、`items_pinned_to_model.json`。

- **TFM 是 `net9.0-windows10.0.19041.0`**（不是 `net9.0-windows`）：摄像头动捕用 WinRT
  MediaCapture / FaceAnalysis，需要 Windows SDK 投影；升 TFM 不加 NuGet（SDK 自动带投影包），
  主程序 / 自检工程本来就是 19041 TFM；
- 自检工程通过 `ProjectReference` 引用本工程，MSBuild 会把上面这些 `CopyToOutputDirectory` 的项**传递**到
  自检输出目录（所以 `<selfcheck exe 旁边>\Live2D\Models\DeepSeek\` 也有一份，自检断言就查那里）；
- `publish.ps1` 打包后同样在发布目录里；**发布目录里的 `Live2D\` 和 `Live2DCubismCore.dll` 不能少**，
  少了就是「找不到 Core / 找不到模型」；
- **PSD 不入库**：`DeepSeek\psd\大肥鱼公皮分享拆分.psd` 131 MB，超过 GitHub 单文件 100 MB 限制，且运行时完全用不到
  （`.gitignore` 已忽略 `src/YuLiangToolkit.Tools.Live2D/DeepSeek/psd/`）；想留档就本地留着，别 `git add -f`；
- `DeepSeek\` 里的 `*.vtube.json` / `*.vbridger` / `items_pinned_to_model.json` 是模型平台（VTube Studio / VBRIDGER）
  的配置，本工具不读，也不随 exe 分发（仍留在仓库 / 本地模型文件夹里）；
- 内置模型是**用户自有模型**：仓库里只是本工具的内置演示资源，别单独抽出去再分发。

## 9. 许可注意事项

- `Live2DCubismCore.dll` / `.h` 是 **Live2D Inc. 的专有软件**（Live2D Proprietary Software License Agreement）：
  - 只允许**原样**随本程序分发，**不修改、不反编译、不拆出单独售卖**；
  - 保留 `ThirdParty/Live2DCubismCore/NOTICE.txt`（来源 / 版本 / EULA 链接）与 `THIRD-PARTY-NOTICES.md` 的说明；
  - 升级 Core 版本时同步改 NOTICE 与第三方位说明；
- 动作 / 表情 / 物理 / 渲染的**语义**参考了官方 Cubism SDK（Native / Web Framework）与其示例，
  但代码是**自行编写**的，没有复制官方源码；不要把官方源码粘进本项目（官方示例的许可与本项目 Unlicense 不同）；
- **摄像头动捕**用 Windows 自带的 `Windows.Media.Capture` / `Windows.Media.FaceAnalysis`（操作系统 API，
  不是第三方库、不需要额外许可文件）；仓库里没有引入 OpenCV / MediaPipe / ONNX 等任何视觉库；
- **语音说话**：本地语音用 Windows 自带 TTS（SAPI / OneCore，操作系统组件）；默认的**在线软妹音**调用
  **微软 Edge 语音合成服务**（非官方公开协议，需要联网，只发送文字）——不是第三方库，但属于第三方在线服务；
  UI / 文档要明确提示联网与隐私，并保留本地回退；仓库不引入任何第三方语音 / 网络库（WebSocket / HTTP / WAV
  解析均手写）；
- 内置模型 `大肥鱼` 版权归模型作者（用户自有），仓库内仅本工具使用；
- 本项目其余部分 Unlicense，不因上述第三方许可而改变。

## 10. 摄像头面部动捕（Mocap/）

> 目标：**不引任何第三方包**（OpenCV / MediaPipe / ONNX 都不行），用 **Windows 自带**的
> `MediaCapture`（采集）+ `MediaFrameReader`（取帧）+ `FaceAnalysis.FaceDetector`（人脸框），
> 配合自写的像素启发式做「转头 / 眨眼 / 张嘴」。摄像头只在用户打开开关时启动（**默认关**）。

### 10.1 数据流

```
MediaCapture（优先 640 左右宽的格式）
  → MediaFrameReader（CPU 内存 BGRA8，15/24fps）
  → 降采样到 320 宽分析帧
  → FaceDetector.DetectFacesAsync（只给人脸框）
  → 框位置：MocapMath.MapHead/MapPupil → Yaw/Pitch/Pupil
  → 框内像素：FaceExpressionAnalyzer.Analyze → 眨眼/张嘴开合度（滚动分位数自动校准）
  → MocapMath.Smooth 指数平滑 → MocapTrackingResult（不可变快照）
  → ModelRuntime.Tick 读 IMocapProvider.Latest → 写模型参数
```

### 10.2 类职责

| 文件 | 职责 |
| --- | --- |
| `MocapContracts.cs` | `IMocapProvider`（Latest / LatestPreview / IsRunning / LastError / MeasuredFps / FrameSize / StateChanged）、`MocapTrackingResult`、`MocapPreviewFrame`、`MocapCameraInfo`、`MocapSettings.FromConfig`（帧率 clamp 10~24、平滑 clamp 0~0.95） |
| `MocapMath.cs` | 纯数学（自检可测）：`MapHead`（左右 0.20 窗口→±30°、上下 0.16→±22°）、`MapPupil`（×0.9 / ×0.6）、`Smooth`（指数平滑） |
| `FaceExpressionAnalyzer.cs` | 框内像素启发式：眼睛横带**灰度标准差**（睁眼有亮眼白+暗瞳孔→方差大，闭眼→小）、嘴部横带**暗像素比例+对比度**；滚动 180 帧取 10%/90% 分位数自动校准；样本不足 24 帧给安全值（眼 1 / 嘴 0）；`ResetCalibration` 清零；框 <24px 直接安全值 |
| `MocapCameraSource.cs` | `MediaCapture` 采集 + `FaceDetector` 检测 + 平滑快照 + 预览帧；`EnumerateCamerasAsync`（**只枚举设备**）、`StartAsync` / `StopAsync` / `Dispose`；处理耗时统计 |
| `Live2DSession.cs` | **摄像头生命周期归会话**：页面或桌宠可见且 `MocapEnabled` 才启动；隐藏 / 关开关 / 程序退出立即 Stop 释放；失败记 `MocapStartError`（中文），提供「重新校准 / 校准中性位」入口 |
| `Live2DViewModel` / `Live2DView` | 动捕卡：设备下拉（优先内置摄像头）+ 刷新、镜像 / 预览 / 帧率 / 平滑、三个驱动开关、三个强度、两个校准按钮、288×162 预览小窗 + 人脸框 overlay、状态 / 错误文本 |
| `ModelRuntime` | 每帧读 `Latest`（600ms 过期保护）；有脸 → `ApplyMocapFace` 写参数；没脸 → 鼠标眼控 + 自动眨眼 |

### 10.3 关键常量与行为（改之前先看）

- **采集格式（颜色坑，别改回旧写法）**：**不要**对源调 `SetFormatAsync` 去选 640 宽格式——本机实测它会抛
  COMException，且某些摄像头在非默认格式下由 MediaCapture 做 YUY2→BGRA 转换会得到「绿油油」的错色画面；
  现在直接用**默认格式**（多为 NV12，系统转换成 BGRA8 颜色正确），分辨率大就自己在后面降采样。
  `MediaFrameReader` 优先请求 `Bgra8`，个别摄像头不支持时退回源格式，再在回调里转；
- **转换坑**：把 `SoftwareBitmap` 转 BGRA 时**只在 `BitmapPixelFormat != Bgra8` 时 Convert**；
  不要按「alpha 模式不是 Premultiplied」去 Convert —— 摄像头的 Bgra8 帧通常是 Straight alpha，
  对同格式再做一次 Convert 会二次转换导致错色；我们只关心 RGB，alpha 统一置 255；
- **分析帧**固定降采样到 **320 宽**（640×360 → 320×180，FaceDetector 快 3~4 倍，框坐标正好是分析帧坐标）；
- **镜像**：人脸中心的 x 先做 `1 - x`（`Mirror=true` 时），预览帧也按镜像处理；
  `Pitch` 正 = 抬头（画面 y 向下，公式是 `(neutralY - faceY) / PitchWindow`）；
- **平滑**：`Smooth(current, target, smoothing)`，`alpha = 1 - smoothing`；第一帧 / 脸重新出现时**直接 snap**
  （不从小角度慢慢爬）；眼睛 / 嘴的平滑用 `smoothing × 0.5 / 0.7`（比头部更跟手）；
- **没脸防抖**：连续 `NoFaceFramesBeforeLost = 4` 帧（≈0.25 秒 @15fps）才发布 `NoFace`，
  避免检测抖动让角色闪；`ModelRuntime` 只接受时间戳距今 ≤ **600ms** 的结果（相机卡住时角度不会僵住）；
- **预览帧**：只在 `MocapShowPreview` 打开时每帧新建 BGRA 数组（320×180×4 ≈ 230KB、15fps），UI 按需取最新；
- **设备枚举**：`DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)`，**不打开摄像头**；
  「刷新设备」只重新枚举；`PickPreferredCamera` 优先内置摄像头（名字含 HD User Facing / Integrated / 品牌内置），
  尽量避开 DroidCam / OBS 等虚拟摄像头；
- **中性位**：「校准中性位」把当前有脸位置写 `MocapNeutralFaceX/Y`（clamp 0.05~0.95），改配置会**重建数据源**
  （已在跑就重启一次摄像头）。

### 10.4 与鼠标眼控 / 自动眨眼的关系（优先级）

- 有脸（且未过期）→ **动捕接管**：写 `ParamAngleX/Y/Z` + `AngleX/Y/Z`、`ParamEyeBallX/Y`、
  `ParamEyeLOpen/ROpen`、`ParamMouthOpenY`，并写 `ParamBodyAngleX` + `PositionX`（Yaw×0.35，身体温和跟随）；
  **鼠标眼控整段跳过**，自动眨眼在动捕接管眨眼时关闭（避免两边抢同一批参数）；
- 没脸 / 帧过期 → 回落到原来的**鼠标眼控 + 自动眨眼**（角度平滑回正）。

### 10.5 隐私 / 权限（硬性）

- `MocapEnabled` 默认 **false**；**没有打开开关就永远不会请求摄像头**；
- `MocapCameraSource` 构造函数不碰摄像头；只有 `StartAsync` 才 `MediaCapture.InitializeAsync`；
- 画面只在内存里处理，**不录像、不存图、不上传**；预览只在测试页窗口显示（桌宠开动捕时不显示预览）；
- 页面切走 / 桌宠隐藏 / 关开关 / 程序退出 → 会话 Stop 并释放设备；
- 权限被拒 / 设备被占用时 `StartAsync` 返回 false，`LastError` 是中文原因（显示在卡片上，不静默）；
- **自检绝不启动摄像头**：只测 `MocapMath` / `FaceExpressionAnalyzer`（合成 BGRA 图）/ `MocapSettings` /
  `EnumerateCamerasAsync`（仅枚举）/ 假 `IMocapProvider` 驱动的 ModelRuntime 集成。

### 10.6 TFM / 发布注意

工具工程 TFM 从 `net9.0-windows` 升到 **`net9.0-windows10.0.19041.0`**（WinRT / MediaCapture / FaceAnalysis
需要 Windows SDK 投影；.NET 9 的 `net9.0-windows10.x` 会自动引 WindowsSDK 包，**不需要额外 NuGet**）。
主程序与自检工程本来就是 19041 TFM，引用没问题；发布产物里 `Microsoft.Windows.SDK.NET.dll` 会跟着出现（正常）。

## 11. 身体晃动测试（BodySwayController）

> 这是「看物理链」的测试功能：按正弦规律写身体主轴 / 物理输入 / 头部补偿 / 腰腿参数，让 physics3 的钟摆物理
> 把头发 / 裙子 / 尾巴带着甩。**不是动作捕捉**，也不改 moc / 物理配置。

### 11.1 参数分工（本模型实测，换模型要重新核对）

| 参数 | 作用 |
| --- | --- |
| `ParamBodyAngleX/Y/Z` | 身体主轴角度，直接驱动身体变形；同时是物理输入 |
| `PositionX/Y/Z` | 物理输入（Setting 4 / 14 / 9 等 → `ParamBodyAngle*`），带动下游肩 / 胸 / 胯 / 手臂 / 袖 / 飘带 / 裙 / 尾巴等 |
| `AngleX/Y/Z` | 头部角度控制器（Setting 1~3 → `ParamAngle*`） |
| `ParamAngleX/Y/Z` | 头部最终角度（直接驱动头部变形） |
| `HipAngleX/Y/Z`、`ParamHipAngleZ`、`LEGAngleZ2` | 腰 / 腿（勾「连腰部 / 腿部一起晃」才写） |

内置模型里 **28 组物理设置**的输入含 `ParamBodyAngleX/Z`，输出有 `ShoulderZ`、`HipAngle*` / `HipZ*`、
`ChestX*`、`SkirtX*`、`LArm/RArm`、`LSleeve*/RSleeve*`、`LRibbon*/RRibbon*`、`Lace*`、
`Param_Angle_Rotation84~89`（尾巴的 [0]~[5] 段）等；`ParamAngleX` 还会经 Setting 50 / 54 / 59 带动
`FrontHairX1~4` / `SideHairX1~4` / `LateHairX1~5`（头发）。这就是「身体一晃、头发和裙子慢半拍地甩」的来源。

### 11.2 模式公式（A = 幅度度、angle = 2π·time·SpeedHz）

| 模式 | 公式 | 备注 |
| --- | --- | --- |
| 左右摇 `ModeLeftRight` | X = A·sin(angle)；Z = A·0.15·sin(angle+90°) | 附带少量侧倾，最常用；默认模式 |
| 前后摇 `ModeForwardBack` | Y = A·sin(angle) | |
| 扭腰 `ModeTwist` | Z = A·sin(angle)；X = A·0.12·sin(angle+90°) | 扭腰 + 少量左右 |
| 椭圆 `ModeEllipse` | X = A·sin(angle)；Z = A·0.6·cos(angle) | **X/Z 相位差 90°**（自检验这个） |
| 自然晃 `ModeNatural` | X = A·0.65·sin(angle) + A·0.25·sin(0.37·angle+0.7)；Y = A·0.18·sin(0.23·angle+1.3)；Z = A·0.14·sin(0.61·angle+2.1) | 多频叠加，最像真人 |

- **头部反向补偿**：`headX/Y/Z = -bodyX/Y/Z × HeadFollow × 0.9`（`HeadCounterWeight`），
  `|补偿| < 0.05°` 时不写（`HeadCounterDeadZone`，避免把鼠标 / 动捕的头部跟随压成 0）；
- **腰腿**：`HipX/Y = bodyX/Y × 0.55`、`HipZ = -bodyZ × 0.55`、`LEGAngleZ2 = HipZ × 0.6`；
- `Phase = frac(time × SpeedHz)`（UI 曲线 / 进度用）；`LastApplied` 记录本帧实际写了哪些参数
  （UI 显示 + 自检断言，0 分配复用列表）；`ManagedParameterIds` 是 17 个可能写到的参数 Id。

### 11.3 ModelRuntime 集成

- 属性：`SwayEnabled / SwayMode / SwayAmplitudeDegrees / SwaySpeedHz / SwayHeadFollow / SwayIncludeHips`；
  `BodySway`（别名 `Sway`）暴露控制器；`StartSwayDemo(seconds)` / `StopSwayDemo()` /
  `SwayDemoRemainingSeconds` 是「摇一摇」限时演示（不影响 `SwayEnabled`，到点自动停）；
- Tick 里在**动捕之后、待机动作 / 动作之前**执行：`SwayEnabled || demoRemaining > 0` 才 `Apply`，
  否则 `BodySway.ClearApplied()`（UI 显示「没有在晃」）；运行时属性每帧同步给控制器；
- **和动捕同时开启的优先级**：`UpdateBodySway(deltaSeconds, mocapActive)` 在动捕有脸时把
  `sway.HeadFollow` 临时置 0 —— 晃动仍写 `ParamBodyAngle*` / `Position*` / 腰部 / 物理输入，
  但**不写** `ParamAngle*` / `Angle*`（头部反向补偿），头部保持动捕驱动；没脸时按配置正常做头部补偿。
  改这个优先级要同步改 UI 文案和自检；
- 受管参数：`BodySwayController.ManagedParameterIds` 在加载时并入受管参数表，所以关闭晃动后这些参数会被
  恢复默认、不残留（注意别在其他地方绕过受管参数表写这些参数）；
- `ResetParameters()` 会 Reset 控制器并清掉「摇一摇」倒计时。

### 11.4 UI / 曲线 / 调试钩子

- 晃动卡：模式下拉 / 幅度 0~30° / 速度 0.1~2Hz / 头部补偿 0~1 / 连腰腿 checkbox /
  「摇一摇（3 秒）」/「停」/ 倒计时文本；
- 右侧 Canvas 画最近 5 秒四条线（同一 ±30° 尺度）：`ParamBodyAngleX`（蓝）、`AngleX`（绿）、
  `ParamAngleX`（红，反向补偿后）、`FrontHairX1`（灰，物理输出，能看出慢半拍）；参数值每 100ms 刷新；
- `YLT_LIVE2D_SWAY_DEMO=1`：打开页面自动设「左右摇 15° / 0.5Hz」并演示 10 秒（截图验收中间帧用）。

### 11.5 整身轻倾（BodyLean，本轮新增；和上面的「扭身体测试」是两回事）

> 用户要的是「**整身以脚为支点左右轻倾**」，不是让身体网格扭来扭去。产品上拆成两层：
> **`ModelRuntime` 只算一个角度**（`BodyLeanDegrees`），**UI 在渲染层做整体旋转**（预览 + 桌宠，支点在脚底），
> 不写任何模型参数。

- `ModelRuntime`：
  - 只读 `float BodyLeanDegrees`：当前倾斜角（正 = 向右倾，单位度）；
  - 可写 `bool BodyLeanEnabled`（配置 `BodyLeanEnabled` 默认 true）：整身轻倾总开关；
  - **常态**：4 秒一个来回、幅度 **±0.6°**（几乎察觉不到的轻晃）；
  - **音乐在播且 `MusicSwayEnabled`**：周期平滑加快到 **~2.6 秒**、幅度 **1.0° × `MusicSwayStrength`**；
  - **关闭音乐 / 关 SwayEnabled**：平滑回到常态参数（相位连续，不跳变）；
  - **`BodyLeanEnabled=false`**：幅度**平滑趋近 0**（不瞬间归零），再打开从 0 平滑恢复；相位始终连续；
  - 角度 = `sin(2π·phase) × 幅度`；相位推进用「目标周期」的角速度，音乐切换只改角速度（保持相位）→ 不跳变；
- **UI 渲染层**：`Live2DView` 预览和 `PetWindow` 都把 `BodyLeanDegrees` 应用到整体旋转
  （WPF `RotateTransform`，`RenderTransformOrigin = (0.5, 1)` = 窗口底边中点 = 脚底支点）；
  预览/桌宠用同一个角度 → 两边看起来一致；卡片里显示「身体倾斜 +0.62°」实时值；
- **和旧实现（已删）的区别**：旧版音乐跟唱会写 `ParamBodyAngleX` / `PositionX` / `ParamBodyAngleZ` / `HipAngleX`
  （=`BodySwayController` 那套扭身体参数），用户反馈「像扭腰、不像整个人在晃」，而且会和物理链打架；
  现在**音乐响应不再写任何身体参数**（嘴型 / 点头保留），整身倾斜只在渲染层做；
- 自检断言：常态周期 ≈4s / ±0.6°（固定 dt 采样过零/峰值，±15% 容差）；有音乐周期 ≤3s、幅度 > 常态且随时间变化；
  `MusicSwayEnabled=false` 保持常态；`BodyLeanEnabled=false` 平滑归零、相邻帧变化小（不跳变）；
  关音乐回常态；相邻帧相位连续；Tick 预算不超。

## 12. 面部参数测试卡（直接写 Cubism 参数）

> 「😛 面部测试」卡（`Live2DView.xaml` / `Live2DViewModel`）是**绕过所有运行时系统、直接写 Cubism 参数**的
> 调试面板：眼睛位置 / 开闭、嘴巴、舌头三参数各有滑杆 + 一组预设按钮。它不是新机制，
> 只是把常用参数挑出来做成 UI（和「参数」列表里的滑杆本质一样）。

### 12.1 涉及参数

| 组 | 参数（Id 列表在 `Live2DViewModel.FaceEyeIds / FaceMouthIds / FaceTongueIds`） | 说明 |
| --- | --- | --- |
| 眼睛（5） | `ParamEyeBallX` / `ParamEyeBallY` / `ParamEyeLOpen` / `ParamEyeROpen` / `EyeSmile` | 眼球位置 / 开闭 / 眯眼笑 |
| 嘴（13） | `ParamMouthOpenY` / `ParamMouthForm` / `JawOpen` / `Mouthpucker` / `MouthFunnel` / `MouthPuckerWiden` / `MouthPressLipOpen` / `MouthRollUpper` / `MouthRollLower` / `MouthSmile_L` / `MouthSmile_R` / `PoutOffsetXL` / `PoutOffsetXR` | 张嘴 / 嘴形 / 撅嘴 / 嘴角等 |
| 舌头（3） | `TongueOut` / `ParamTongueUpDown` / `ParamTongueWidth` | 伸舌头 / 上下 / 宽窄（重点） |

- 滑杆范围直接取 `CubismParameter.Minimum/Maximum`（不是写死的 0~1）；写值走 `CubismParameter.Value`
  （自动夹到参数 min~max），UI 每帧 `RefreshFaceValues()` 同步模型当前值；
- **预设按钮 7 个**（`ApplyFacePreset`，截图调试钩子也复用）：`tongue`（TongueOut=1 + 张嘴 0.7 + JawOpen 0.4 +
  嘴形 0.5）/ `open` / `pucker` / `smile`（MouthSmile_L/R + EyeSmile + 嘴形 1）/ `wink`（左眼 0.05 + 右眼 1）/
  `rolleyes`（眼球 Y=1 + 双眼 0.5）/ `reset`（**只把这一卡的面部参数**恢复默认，不等同于「一键全部重置」）；
- 参数不在模型里时该行自动跳过（自定义模型可能没有舌头参数）——`BuildFaceRows` 按 `FindParameter` 结果建行。

### 12.2 与各系统的冲突（什么时候手动值会被覆盖）

| 系统 | 会覆盖哪些面部参数 |
| --- | --- |
| 动捕（有脸时） | 头部 / `ParamEyeBall*` / `ParamEyeLOpen/ROpen` / `ParamMouthOpenY` |
| 鼠标眼控（开启时） | 头部 / `ParamEyeBall*` / 身体角度 |
| 自动眨眼 / 待机 | `ParamEyeLOpen/ROpen`（眨眼）、`ParamBreath`（呼吸） |
| 表情 | 表情文件声明的参数（可能是嘴形 / 眼睛表情开关） |
| 动作 | 动作曲线涉及的参数（例如手部动作也会带动 `Movement*` 开关参数） |

**受管参数动态化**（§6.1）之后：把这些系统关掉，它们写的参数就不再每帧复位，手动值会保留；
但如果「面部测试」卡写了某个参数，而另一个仍开着的系统也会写它（例如动捕还开着），那还是会被覆盖。
UI 上「一键全部重置」会复位全部参数并停掉动作 / 表情 / 晃动演示。

### 12.3 自检覆盖

- 参数存在 / 范围 / 默认值：`TongueOut`、`ParamTongueUpDown`、`ParamTongueWidth` 必须在 moc 里；
  范围包含 0~1（舌头 / 嘴都能从 0 张到 1）；
- 写端点 / 超范围值 → 读回一致或被夹取；
- 受管参数动态化后：关掉全部驱动 → 手动设 `ParamMouthOpenY=0.8` / `TongueOut=1` / `ParamEyeBallY=-1`，
  Tick 30 帧后仍保持；打开动捕（假 provider 有脸）→ `ParamMouthOpenY` 被驱动覆盖。

## 13. 音频回环与音乐分析（Audio/）

> 「🎵 音乐跟唱 · 律动」：用 **Windows 自带 WASAPI 回环**采集系统正在播放的声音（扬声器 / 耳机混音，
> **不申请麦克风**），纯 C# 做 FFT / YIN / 共振峰 / 中心声道 / 音节调制 / 节拍分析，驱动模型跟唱嘴型 +
> 跟节拍摇身体。**不录音、不写音频文件、不联网**；SMTC 只用来显示歌名 / 歌手。

### 13.1 分层与文件

| 文件 | 职责 |
| --- | --- |
| `Audio/WasapiLoopbackCapture.cs` | P/Invoke COM 包装：`IMMDeviceEnumerator → IMMDevice → IAudioClient → IAudioCaptureClient`；共享模式 + `AUDCLNT_STREAMFLAGS_LOOPBACK` 抓默认播放设备混音；统一输出**立体声 interleaved float**（PCM16/24/32 也转换） |
| `Audio/SmtcWatcher.cs` | `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`：后台线程每 2 秒轮询当前会话的歌名 / 歌手 / 应用 / 播放状态；**只是显示**，识别不依赖它 |
| `Audio/MusicReactiveContracts.cs` | `MusicContentKind`（Unknown/Silent/Instrumental/VocalSong）、`MusicAnalysisSnapshot`（不可变快照）、`IMusicReactiveProvider`（Latest / IsRunning / LastError / MediaTitle / MediaArtist / MediaApp / StateChanged） |
| `Audio/MusicReactiveSource.cs` | provider 实现：启停采集、把数据同步喂给分析器、发布快照、处理设备切换重启、把 SMTC 事件转 StateChanged |
| `Audio/Analysis/FastFft.cs` | 纯算法：radix-2 迭代 FFT（位反转 + 蝶形，双精度）、`MagnitudeSpectrum`、`NaiveDft`（自检对拍）、`InverseTransform`（逆变换，调试 / 工具用）、`FillHannWindow` / `ApplyWindow`；不分配内存 |
| `Audio/Analysis/MusicAnalyzer.cs` | 纯算法：任意采样率→16k 重采样、mid/side、FFT 特征、YIN（含八度防抖）、`GoertzelAmplitude`（单频幅度，自检对拍用）、分类、节拍、唇形；`Analyze(ReadOnlySpan<float>, frameCount, ts)` → 快照 |

`MusicVocalMode`（`Model/MusicVocalMode.cs`）：`Auto / ForceVocal / ForceInstrumental`，配置里存中文
（`自动识别` / `强制跟唱` / `强制纯音乐`），由 ViewModel / Session 映射。

### 13.2 WASAPI 回环（`WasapiLoopbackCapture`）

- **必须共享模式**：`IAudioClient.Initialize(AUDCLNT_SHAREMODE_SHARED=0, AUDCLNT_STREAMFLAGS_LOOPBACK=0x20000, ...)`；
  回环只能共享模式；设备混音格式用 `GetMixFormat()`（一般 48k 立体声 float32，也可能 44.1k / PCM16/24/32）；
- 缓冲 100ms（`hnsBufferDuration = 1_000_000`，100ns 单位）；后台线程 `AboveNormal` 轮询
  `GetNextPacketSize` + `GetBuffer`/`ReleaseBuffer`，每 5ms 睡一次（不用事件回调，简单、少 COM 线程问题）；
- **静音包**：`AUDCLNT_BUFFERFLAGS_SILENT` 时缓冲区内容是未定义，必须填 0 再交给分析器（否则是垃圾）；
- **格式转换**：`WAVEFORMATEX` / `WAVEFORMATEXTENSIBLE` → `Float32/Pcm16/Pcm24/Pcm32`；
  多声道取前两声道、单声道复制；统一输出 interleaved float（-1~1）；
- **默认设备变化**：采集线程每 2 秒比对一次默认设备 id，变了触发 `DeviceChanged` →
  `MusicReactiveSource` 300ms 后重启采集（分析器重建、重置状态）；
- `DataAvailable` 给的数组**会复用**，订阅者必须当场消费（`MusicReactiveSource` 同步喂分析器）。

### 13.3 SMTC（`SmtcWatcher`）

- `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` 在后台线程同步等待（一次）；
- 每 2 秒：优先取 `GetPlaybackInfo().PlaybackStatus == Playing` 的会话，否则 `GetCurrentSession()`；
  `TryGetMediaPropertiesAsync()` 拿标题 / 歌手；`SourceAppUserModelId` 拿应用；
- SMTC 不可用 / 旧系统 / 播放器不支持 → 捕获异常后**静默忽略**，只是界面上没有媒体信息，**不影响**跟唱；
- 隐私：媒体标题 / 文件名会写进 debug selftest 日志（见 §18），排错用，不要公开贴出。

### 13.4 `MusicReactiveSource`（provider）

- `StartAsync()` 在 `Task.Run` 里：`WasapiLoopbackCapture.Start()` → `new MusicAnalyzer(capture.SampleRate)` →
  `SmtcWatcher.Start()`；失败返回 false 并给 `LastError` 中文原因（没有播放设备 / 被独占 / 不支持的格式）；
- 采集线程每包数据**同步**调 `analyzer.Analyze(buffer, frames, Environment.TickCount64)` → `_latest = snapshot`；
- `Sensitivity` 直接转给分析器（在线改，不重启采集）；
- 设备切换 / 分析异常 → `_restartRequested` → 重建 capture + analyzer；
- `StopAsync()` / `Dispose()`：停采集线程、释放 COM 设备、停 SMTC、清快照；用 `SemaphoreSlim` 串行化启停。

### 13.5 处理链（`MusicAnalyzer`，48k → 16k → 特征）

```
48k 立体声 float（WASAPI）
  → mid = (L+R)/2、side = (L−R)/2
  → 线性插值重采样到 16k（全局输入帧坐标 _nextOutputPosition，跨 Analyze 调用连续；step = sampleRate/16000）
  → 16k 环形缓冲（2048）
  → 每 hop 256（16ms，≈62.5 帧/秒）取最近 1024 点做 Hann 窗 FFT（mid / side 各一次）
  → 特征：RMS / 低频 60~200Hz / 人声带 300~3400Hz / 谱平坦度 / 谱质心 / 共振峰峰数 /
          **带限自相关周期显著度（mid 200~3500Hz）** / **HPS 谐波显著度** / **3~7Hz 包络调制** /
          **voice-band mid/side 比** / YIN 基频（只输出 pitch）/ 中心声道占比 / 音节调制 / 颤音深度
  → 节拍：**时域 hop 电平差 onset**（比单窗谱通量更抗「瞬态落在 Hann 窗边缘」，稀疏点击 / 鼓点也稳）
          → 6 秒 onset 自相关估 BPM（60~180）→ 相位 PLL
  → 分类：人声显著度多特征加权 + 自适应分位 + 灵敏度 + 平滑 + 时序滞后
  → 唇形：人声带包络（attack 15ms / release 90ms、最近 10 秒 p90 归一化）+ 谱质心映射嘴型
  → MusicAnalysisSnapshot（不可变）
```

关键常量（改之前先看源码常量区）：

| 项 | 值 |
| --- | --- |
| 分析采样率 / 窗 / hop | 16000 Hz / 1024 点（64ms）/ 256（16ms，62.5 帧/秒） |
| 频点 Hz | 15.625 Hz/bin |
| 静音门限 | 块 RMS < 0.0015（≈-56 dBFS）走快速路径（跳过 FFT / YIN，只推进节拍相位） |
| 播放挂起 | 500ms（句间空隙不闪「静音」） |
| 频带 | 低频 bin 4~13（62~203Hz）、人声 bin 19~218（297~3406Hz）、onset bin 13~256 |
| YIN | lag 16~178（≈90~1000Hz），带 ±1 八度防抖；**只用于输出 pitch，不再作为人声硬门槛** |
| 分类特征 | **中心占比门控 × 共振峰 × 中心包络动态 × 频带互补 + 噪声门控**，再加 YIN 清晰度 × 稳定度兜底（`ComputeVocalRawScoreEx`；权重见源码常量） |
| 判定阈值 | 高 **0.58** / 低 **0.50**（按真实混音校准，以源码常量为准）；数据不足（有效分析 < **1.0s** **且**播放墙钟 < 1.0s）→ Unknown；带歌词持续 **1.2s** / 纯音乐持续 **2.0s** 才切换；每个 hop（含静音 hop）都推进计时 → 稀疏点击也能按墙钟下结论 |
| 无节拍回退 | 估不出可靠 BPM 时相位按 **0.5Hz** 舒缓摆动、`BeatStrength` 保底 **0.35**（再按响度略放大）；BPM 恢复时相位速率平滑换挡（0.05/hop），避免回退↔真实节拍切换跳变 |
| 历史窗口 | 分数 30s（1875）、包络 10s（625）、onset 6s（375）、音高 4s（128） |

**YIN（`MusicAnalyzer.EstimatePitchYin`）**：标准 CMNDF，但注意两个坑（本轮踩过）：
①差分函数要覆盖 lag=1..maxLag（`difference[1..minLag-1]` 不能是 0，否则累计均值分母偏小、整条曲线被推高）；
②低于阈值的点只是「谷的肩部」，要继续走到**局部最小**才是周期，否则 220Hz 会估成 242Hz。
**八度防抖**：谐波丰富的信号 YIN 会在基频 / 高八度之间跳（110 ↔ 220）；实现里用
`_pitchSmoothed` 上一帧值把明显 ±1 八度的结果拉回同一八度（比值 1.7~2.3 → ×0.5；0.43~0.59 → ×2），
再配 0.8/0.2 平滑。
**本轮角色变化（按真实歌曲混音重调后）**：真实混音里伴奏低音 / 鼓常盖过主唱基频，单帧 YIN clarity
很容易「看着低、其实是伴奏」；所以人声判定**不再用 YIN clarity 当硬门槛**，改用更适合混音的多特征
相对显著度（见下）；YIN 只保留输出 `PitchHz` 给界面显示 / 调试，另以「清晰度 × 稳定度」作为
**清晰人声的兜底证据**（不是必须条件）。
注意：**107Hz 不一定是八度错**——离线合成 WAV（voice-like.wav）频谱主峰本来就是 110Hz（220/660/1100 也有），
YIN 报 107~113 是按内容正确。

**分类（人声显著度，`ComputeVocalRawScoreEx`）**：`_vocalRawScore` 的核心是**适合真实混音的相对证据**：
1. **中心占比门控**：voice-band mid/side 比 + 立体声中心声道占比（主唱居中、乐器 / 和声偏两侧）；
2. **共振峰**：频谱平滑后数宽峰（人声包络，不是单一基频）；
3. **中心包络动态**：3~7Hz 包络调制（念字咬字，`GoertzelAmplitude` 带能量）+ 颤音深度；
4. **频带互补**：mid 声道 200~3500Hz 带限自相关周期显著度 + HPS 谐波梳显著度 + 谱平坦度 / 音调度；
5. **噪声门控**：谱平坦度 / 高频带平坦度等，压掉鼓点 / 噪声被误判；
6. **YIN 兜底**：清晰度 × 稳定度高时给「清晰人声」加分（不满足也不否决）。

再乘 **自适应分位**（最近 30 秒运行分位数归一化，抵消不同歌曲 / 音量的整体偏移）和 **Sensitivity**；
`_vocalSmoothedScore` 每帧 0.1 低速平滑；`≥0.58` 累计 1.2s → VocalSong，`≤0.50` 累计 2.0s → Instrumental，
中间地带保持上一结论（防抖）。`Confidence = |smoothed − 0.5| × 2`。
实测（自检合成信号）：类人声 VocalScore 0.63 / 纯乐器 0.225；另用真实歌曲混音离线校准（素材属于用户私有媒体，
**只在本机调参，不随仓库分发**）。**调试诊断**：`MusicAnalyzer` 暴露 `PeriodicitySalience / HarmonicSalience /
VoiceBandEnergyRatio / ClarityDiag / PitchStabilityDiag / FormantScoreDiag / FlatnessDiag / VibratoDepthDiag /
MidHighBandRatioDiag / MidHighBandFlatnessDiag / PeriodicityLagDiag` 等只读属性，排错时看这些值比只看 Kind 直观。

**节拍**：`onset = clamp(log(RMS_hop / RMS_prev) × 0.35)`（时域电平差，快起慢落平滑 0.6/0.25）→
6 秒历史自相关，峰值相关度 ≥ 0.25 才接受 BPM（60~180），`_bpm` 平滑 0.7/0.3；每个 hop（含静音 hop）都推进
BPM 估计（原来只在有声 hop 估算，稀疏点击永远估不出——已修）；`BeatPhase += beatRate × hop`，强 onset（≥0.35）
再按 0.25 × 强度 PLL 拉向最近拍。**无节拍回退**：BPM 不可靠（`_beatReliable=false`）时 `beatRate` 回退到
**0.5Hz** 舒缓摆动、`BeatStrength` 用 0.35 保底 + 响度略放大，保证钢琴曲 / 纯人声 / 环境音乐也能轻轻晃；
回退↔真实节拍的切换对 `beatRate` 做 0.05/hop 平滑，避免相位跳变。实测：120BPM 纯点击 → 120.5；无节拍静音/稳态
→ BPM=0 但 Phase 仍缓慢推进（用 Publish 的 BeatStrength 门槛判断是否「在晃」）。

**唇形**：人声带包络按最近 10 秒 p90 归一化 → `MouthOpen`（attack 15ms / release 90ms 快起慢落）；
`MouthForm = clamp((质心 − 1850) / 1550, −1, 1)`（谱质心越高嘴形越「亮」）。

### 13.6 ModelRuntime 集成

- `ModelRuntime.Music`（`IMusicReactiveProvider`）+ `MusicEnabled / MusicMode / MusicLipSyncStrength /
  MusicSwayStrength / MusicSwayEnabled / MusicHeadBobEnabled`；`MusicKind / MusicStatusText` 只读给 UI；
- Tick 里在**身体晃动之后**调 `ApplyMusicResponse(mocapFace)`（§6 第 5.5 步）：
  - 数据源 `Latest` 为 null / `IsPlaying=false` / 时间戳距今 > **800ms** → 按静音处理（清零 touched）；
  - 有效类型：`MusicMode` 强制优先，否则分析器 `Kind`；**只有 VocalSong（或 ForceVocal）才写嘴**；
    纯音乐照常写身体摇晃；
  - 嘴：`ParamMouthOpenY = MouthOpen × lipSync`、`ParamMouthForm = MouthForm × 0.6 × lipSync`、
    `JawOpen = mouthOpen × 0.5`；**动捕有脸时整段嘴让位**（优先级：动捕 > 音乐）；
  - 身体：`ParamBodyAngleX = PositionX = sin(2π·phase) × 6 × swayStrength × (0.4+0.6·beatStrength)`，
    `ParamBodyAngleZ = sin(π·phase + 1.2) × 2 × swayStrength`，`HipAngleX = bodyX × 0.4`；
  - 点头：`ParamAngleY = −sin(2π·phase) × 2 × swayStrength`，**动捕有脸 / EyeTrackingEnabled 时不写**；
  - 所有写入记录到 `_musicTouchedIndices`，**下一帧开头只复位自己写过的参数**（不写时不动 → 手动值保留）；
- 状态文案：`静音` / `识别中`（Unknown）/ `纯音乐` / `带歌词`（按分析器 Kind，**不随强制模式改变**——
  强制跟唱时可能显示「纯音乐」但嘴在动，这是有意的：显示的是识别结果，模式是用户覆盖）；
- 配置 7 项见 `docs/tools/live2d.md` 配置表；截图模式（`YLT_SHOT`）**刻意禁用音乐采集**（截图流程不碰系统声音）。

### 13.7 性能预算

- 采集线程 + 分析器：目标 < 1.5ms / 包（48k、480 帧/10ms 包），62.5 hop/s；FFT 1024×2 + YIN（每 2 hop）
  + 共振峰平滑 + 分类 ≈ 0.5~1.2ms（8 核机器）；
- 全部缓冲预分配（ring / FFT / 频谱 / 历史 / 分位数排序缓冲），**每帧 0 分配**；快照整体替换引用；
- 静音快速路径：跳过 FFT / YIN，只推进节拍相位 + 衰减能量 → 没人放音乐时几乎不耗 CPU；
- UI 读数 100ms 刷新一次（`DispatcherTimer`），不要把快照属性直接绑到每帧 Hook。

### 13.8 坑（本轮踩过的，改代码前必看）

1. **重采样相位必须跨块连续**：旧实现用「块内坐标 i」比较 `_resamplePhase <= i`，第一块之后相位永远大于 i →
   0 push → 分析器停摆（但 RMS / IsPlaying 仍在动，界面看起来很正常，极易漏测）。现改为全局输入帧坐标
   `_nextOutputPosition` + `_inputFrameIndex`；自检要连续喂多块验证 hop 继续推进。
2. **YIN 两个坑**：diff 从 lag=1 开始算；阈值穿越点要走到局部最小（见 §13.5）。还有八度错误风险。
3. **分类权重容易被「稳定乐器」骗**：纯乐器有谐波 / 共振峰 / 中心声道也可能高分；需要真实音频校准，
   别用单一合成信号调参。
4. **节拍估 BPM 曾被稀疏点击坑过**：旧实现只在「有声 hop」推进 BPM 估计、onset 用单窗谱通量 →
   纯点击（每 500ms 只响 60ms）永远估不出；现在每个 hop（含静音 hop）都推进 BPM，onset 改成时域 hop 电平差
   （瞬态落在 Hann 窗边缘也抓得到），实测 120BPM 纯点击 → 120.5。改回旧写法会复现 BPM=0。
5. **`ADCLNT_STREAMFLAGS_LOOPBACK` 只能共享模式**；独占模式播放器绕过系统混音，回环抓不到。
6. **静音包必须填 0**（`AUDCLNT_BUFFERFLAGS_SILENT` 时缓冲内容未定义）。
7. **设备切换**（拔耳机 / 切蓝牙）默认设备 id 会变：轮询检测 + 延迟 300ms 重启采集，否则一直静音。
8. **SMTC 可能不可用 / 权限受限**：只当显示用，失败不能影响音频分析（否则整个功能卡死）。
9. **不要每帧分配**：音频线程 62.5 帧/秒，`new` 数组 / LINQ 会造成 GC 抖动；全部复用缓冲。
10. **隐私**：不录音 / 不存音频；debug selftest 日志含 SMTC 媒体标题（本地文件名），别贴到公开场合。
11. **无节拍回退的相位换挡要平滑**：真实节拍 ↔ 0.5Hz 回退切换时如果直接改 `beatRate`，身体会「跳一下」；
    实现里对速率做 0.05/hop 指数平滑，并让 `BeatStrength` 用 0.35 保底（再按响度放大），
    保证钢琴曲 / 纯人声也轻晃；改这部分注意别破坏「PLL 强拍拉相位」。
12. **判定时间不能只算「有声音的 hop」**：稀疏点击 / 安静段落里，只按 active 分析时长会永远判 Unknown；
    现在用「active ≥ 1.5s 或播放墙钟 ≥ 1.5s」双条件（`_observedPlayingSeconds`），每个 hop（含静音 hop）都推进。

## 14. 动作系统（手势 / 表演 / 舞蹈 / 待机灵动）

> 本轮给「更生动」加的三层动作：**手势**（点一下就摆姿势）、**表演**（多步小剧情）、**舞蹈**（程序化循环），
> 外加**待机随机小动作**。全部走 `Model/` 下的三个新播放器，由 `ModelRuntime` 统一调度；
> 写参数沿用「touched 下标表」：每帧只复位自己上一帧写过的参数，停掉后不残留、手动值可保留。

### 14.1 文件与职责

| 文件 | 职责 |
| --- | --- |
| `Model/GesturePlayer.cs` | 8 个手势：3 个**作者原生手势**（enter/exit 动作 + hold 参数）+ 5 个**参数姿势**（直接给手臂目标值）；`Ids / ActiveId / HasActive / Play / Stop / StopImmediate / Update / ResetTouched` |
| `Model/PerformancePlayer.cs` | 6 个**数据驱动时间线表演**（`BuildDefinitions()` 里定义 Id / Duration / Action 列表：Pose / Look / Nod / Talk / Gesture），按时间推进、触发嵌套手势；`Definitions / Ids / ActiveId / ActiveDuration / ElapsedSeconds / Play / Stop / Update / ResetTouched` |
| `Model/DanceController.cs` | 4 个**程序化舞蹈模式**（`轻快摇摆 / 挥手舞 / 摇摆转圈 / 酷炫抖肩`）：相位驱动写手臂 / 肩 / 头 / 身体参数，权重 0.25s 淡入淡出；`Modes / ActiveMode / HasActive / Speed / Phase / Start / Stop / StopImmediate / Update / ResetTouched` |
| `Model/ModelRuntime.cs` | 对外 API：`GestureIds / ActiveGesture / PlayGesture(id) / StopGesture()`、`PerformanceIds / ActivePerformance / PlayPerformance / StopPerformance`、`DanceModes / ActiveDance / DanceSpeed(0.5~2) / StartDance(mode, speed) / StopDance`、`StopAllDemos()`、`IdleRandomGestureEnabled / IdleRandomGestureIntervalSeconds`、内部 `TryTriggerIdleRandomGesture()`（自检 / 调试）；`UpdateActionPlayers` / `UpdateIdleRandomGesture` 在 Tick 里调用 |

### 14.2 手势：作者原生动作 + hold 参数

- **原生手势**（抱胸致意 `IdHandOnChest`、竖起食指 `IdPoint`、拿起狗尾草 `IdGrass`）：
  1. `PlayGesture` 先停其它演示 / 当前动作（避免 `StopMotion` 把 enter 顶掉），
  2. 播作者 **enter 动作**（`MotionPlayer`，淡入 0.12s），翻到 `GesturePhase.Enter`；
  3. enter 播完（`_phaseTimer >= data.Duration`）→ `GesturePhase.Hold`：每帧把 `BuildHoldFromMotion` 提取的
     **enter 曲线末值**直接写参数（`WriteHold`）——这样动作淡出前就「接住」手臂，不会掉回 IDLE；
  4. `StopGesture`：播作者 **exit 动作**（淡入 0.12s），播完释放（`GesturePhase.None`）。
- **hold 参数提取语义**（和 `Motion3Data.CollectFinalParameterValues` 一致）：遍历 `Target=Parameter` 的曲线，
  取**最后一段的终点值**（`EndValue`；没有段取首值），同 Id 多条曲线时**后写覆盖前写**（字典）。
  注意 motion3 的贝塞尔段布局是 `[1, c1x, c1y, c2x, c2y, endX, endY]`——**末值是第 7 个数**，不是第 3 个；
  自检里解析曲线末值也要按这个布局（本轮自检就踩了这个坑，导致 hold 断言误报 0.198 / 0.066）。
- **参数姿势**（举手打招呼 / 比心 / 叉腰 / 抱头 / 放下手）：作者没留对应动作，直接给语义手臂参数目标值
  （`LArm / LForearm / LWrist / LHand / LHand2`、`RArm / …`、`ShoulderX / Y（含 Yy）`），
  `WritePose(weight)` 用 `DemoMath.SmoothStep` 0.35s 淡入、`BlendFromDefault` 从默认值混合；停止 0.25s 淡出。
- `NormalizeId` 同时支持中文名与英文别名（`hand_on_chest / point / grass / wave / heart / akimbo / head_hold / hands_down`）。

### 14.3 表演：数据驱动时间线

- `PerformancePlayer.BuildDefinitions()` 里 6 个定义（Id 即中文名，`Duration` 9~15s）：
  `好奇→思考→恍然大悟`（15s）/ `俏皮比心`（10s）/ `开心挥手`（9s）/ `骄傲展示`（12s）/ `害羞低头`（10.5s）/
  `晚安道别`（11s）；
- 每个定义是 `PerformanceAction[]`：`Pose`（写参数）、`Look`（头 / 眼方向）、`Nod`（点头）、`Talk`（嘴型开合）、
  `Gesture`（触发嵌套手势，用 `_startGesture/_stopGesture` 回调），带 `At / Duration / Strength / Direction / Count`；
- 每帧 `Update(delta, allowTalk)`：`_elapsed += delta`，超过 Duration 自动结束（清 touched）；
  `allowTalk = !mocapFace && !_speechActive`——**动捕 / 语音说话时表演不抢嘴型**；
- `Stop` 立即结束（不走完时间线），嵌套手势会停下。

### 14.4 舞蹈：程序化相位

- 4 个模式常量：`ModeSway=轻快摇摆 / ModeWave=挥手舞 / ModeSpin=摇摆转圈 / ModeShimmy=酷炫抖肩`；
- `Start` 只设模式 + 速度；`Update`：`_weight` 按 0.25s 淡入/淡出，`_phase += speed × delta × 2π × BaseFrequency(mode)`，
  各模式用不同相位的 sin/cos 组合写手臂 / 肩 / `Angle*` / `Position*` / `LeanInput` / `ParamAngle*`；
- `Speed` 夹在 0.5~2.0；`Phase` 公开（自检直接读；速度 2x 时同时间内相位推进约 2 倍）；
  `Stop` 淡出、`StopImmediate` 立即清；`ResetTouched` 每帧开头复位上一帧写过的参数。

### 14.5 ModelRuntime 集成与优先级

- Tick 顺序（§6）：**3.5 语音口型 → 4 身体晃动 → 4.5 音乐 → 4.6 整身轻倾 → 5 待机动作 → 动作更新 →
  5.5 手势 / 表演 / 舞蹈（写在动作之后，覆盖 IDLE 手臂曲线）→ 表情 → 5.6 待机随机小动作 → 物理 → Update**；
- 互斥规则：
  - `PlayGesture`：停表演 / 舞蹈 / 当前动作（`StopMotion(0.12)`）；
  - `PlayPerformance`：停手势（立即）/ 舞蹈（立即）/ 当前动作；
  - `StartDance`：停手势（立即）/ 表演 / 当前动作；
  - `StopAllDemos`：全部停（手势立即、表演 stop、舞蹈立即、动作 `StopMotion(0.2)`）；
- **待机随机小动作**（`UpdateIdleRandomGesture`）：`IdleRandomGestureEnabled` 开时倒计时，
  间隔 = `clamp(IntervalSeconds, 4, 600) × (0.55 + rand×1.05)`；到点调 `TryPlayRandomIdleDemo`：
  - 条件 `CanPlayIdleRandomDemo`：没有动捕脸 / 没在语音 / 没在放音乐 / 没有手势 / 表演 / 舞蹈在播 /
    当前动作要么没有、要么是 IDLE 候选（`IsIdleCandidate`）；条件不满足 → 3 秒后重试；
  - 满足 → 随机手势或表演（`_idleRandomRng` 固定种子便于复现）；`IdleRandomTriggerCount` 内部计数（自检用）。
- 配置：`IdleRandomGestureEnabled`（默认 true）/ `IdleRandomGestureIntervalSeconds`（默认 22）。
- 自检（`Live2DChecks` L 段）：8/6/4 数量与中文名、未知 id 返回 false、3 原生手势 hold 参数 = enter 末值
  （±2e-2）且保持 1 秒、StopGesture 后回默认、5 参数姿势到位与保持、表演中段变化 + 自然结束回默认 + Stop 立即停、
  4 舞蹈各 2 秒移动且有限 + Stop 回默认 + 速度 2x 相位约 2 倍、DanceSpeed 夹取、StopAllDemos、待机随机
  「关闭不触发 / 忙碌不抢 / 空闲触发」。

### 14.6 坑

1. **motion3 贝塞尔段布局**：`[type=1, c1x, c1y, c2x, c2y, endX, endY]`——解析末值 / 采样时别把 c1y 当终点。
2. **原生手势的 enter 动作不能再被 `StopMotion` 顶掉**：`PlayGesture` 内部先 `_stopMotion(0.12)` 再播 enter；
   若外部在 enter 期间又 StopMotion，手势会卡在 Enter（定时器继续走但没有动作可看）。
3. **hold 参数要在动作淡出前接住**：`BuildHoldFromMotion` 在 Play 时就提取好，Hold 阶段每帧直接写；
   不要改成「等动作播完再读当前参数值」（那时已被淡出改回默认）。
4. **参数姿势目标值要按参数范围夹取**（`CubismParameter.Value` 自动 clamp）；不同模型手臂参数范围/语义不同，
   换模型时要核对（本模型 -1~1、默认 0）。
5. **舞蹈相位不要重置**：`Start` 不重置 `_phase`，所以连续切换模式相位连续；速度变化只改推进速率。
6. **待机随机不能抢正在播的内容**：条件漏了任意一项（如没判断音乐 / 语音），就会出现「跟唱时突然抱胸」。
7. **所有演示的写参数都要走 touched 表**：停止后下一帧开头复位，否则会残留姿势；手动值在不再写时保留。

## 15. 语音 TTS 管线（本地 + 在线软妹音 / 聊天气泡）

> 用户要「软软萌萌的软妹音」，Windows 本地「慧慧」太像老式播报；所以默认改为**微软 Edge 在线神经网络语音**，
> 本地 TTS 仅作**离线回退**。要求**声音 / 口型 / 气泡同一时刻出现**：先合成 → 播放真正开始时才弹气泡 +
> 开始口型（+ 可选小动作）。

### 15.1 文件与职责

| 文件 | 职责 |
| --- | --- |
| `Speech/SpeechContracts.cs` | `SpeechVoiceInfo(Id, DisplayName, Language, Gender, Detail = null)`（Detail 非空时显示 `名字（Detail）`，用于「在线 · 软妹音」；本地显示 `名字（语言，女声）`）；`SpeechUtterance(Text, WaveBytes, SampleRate, Channels, BitsPerSample, DurationSeconds, Envelope)` |
| `Speech/WaveAudio.cs` | 纯静态：`TryParse` 按 **RIFF 块表遍历**（不假设 44 字节头）找 `fmt ` / `data`；`ReadMonoSamples`（8/16/24/32bit，取第一声道）；`BuildEnvelope`（20ms RMS、p95 归一化、快起慢落 0.55/0.30） |
| `Speech/EdgeTtsClient.cs` | 在线 Edge 神经网络语音（零第三方包）：内置中文女声清单 `Voices` / `DefaultShortName` / `GetDisplayName`、`GenerateSecMsGec` 时间签名、`SynthesizeMp3Async`（WebSocket 流式合成，15s 超时，返回 MP3）；失败返回 null（原因写 `LastError`） |
| `Speech/TtsService.cs` | 语音合成 / 播放：`GetVoices`（在线 Edge 女声 `edge:` + 本地系统语音 `win:`）、`SynthesizeAsync`（在线 MP3→WAV 转码 / 本地 WinRT WAV）、`SpeakAsync(text, onPlaybackStarted, ct)`、`IsSpeaking / MouthOpen / PositionSeconds / LastError / LastSpokenText / LastEngine / VoiceId / Rate / PitchPercent / Volume / Stop / Dispose` |
| `Views/SpeechBubbleWindow.xaml(.cs)` | 聊天气泡窗口：透明置顶、点击穿透、不抢焦点；纯静态定位 `SpeechBubblePlacement.Calculate(...)`；跟随桌宠、说话结束约 1.5s 淡出 |
| `Views/Live2DView` / `PetWindow` / `Live2DSession` | 说话卡 UI、会话帧循环把 TTS 的 `MouthOpen / IsSpeaking` 同步到 `ModelRuntime.SpeechActive / SpeechMouthOpen`；`SpeakText` 先合成、`onPlaybackStarted` 时才 ShowBubble + 自动小动作 |

### 15.2 语音选择：在线优先、本地回退

- `GetVoices()`：
  - 在线：内置 Edge 神经网络女声清单（Id 前缀 **`edge:`**，Detail=「在线 · 软妹音」；默认
    `zh-CN-XiaoyiNeural`「晓伊 · 少女音」；还有 晓晓 / 晓北 / 晓妮 / 晓臻 / 晓雨 / 晓曼 等，见
    `EdgeTtsClient.Voices`）；
  - 本地：`Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices`（Id 前缀 **`win:`**，Detail=null）；
  - 中文女声排前；结果缓存（`_voiceCache`），失败返回空列表（不抛）。
- `VoiceId`：空 = 自动在线晓伊；`edge:` = 指定在线；`win:` = 指定本地；
- 在线合成失败（断网 / 服务变更 / 协议失败）→ **自动回退本地**（选第一个可用本地语音），
  `LastError` 注明原因（如「在线语音失败，已回退本地：…」）；`LastEngine` 记录最近一次实际引擎
  （「在线晓伊 · 少女音」/「本地慧慧」），卡片状态显示；
- 本地合成走 WinRT `SpeechSynthesizer.SynthesizeSsmlToStreamAsync`（系统 WAV）；在线走手写
  `EdgeTtsClient`（WebSocket，零第三方包）：
  - 连接 `wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1`，
    带 `Sec-MS-GEC` 时间签名参数（`GenerateSecMsGec`），15 秒连接超时；
  - 请求格式 `audio-24khz-48kbitrate-mono-mp3`（Edge 返回 MP3）→ `Mp3ToWavAsync` 用系统
    `Windows.Media.Transcoding` 转成 WAV（实测 48k / 2ch / 16bit）→ 再走 `WaveAudio` 解析 / 包络；
  - 内置语音清单是写死的 ShortName（不联网列举；服务端可接受），失败一律回退本地。
- **隐私**：在线只发送**文字**（不发送音频 / 文件 / 其它信息）；合成 WAV 只在内存里播放、播完释放，不写磁盘。

### 15.3 合成 → 解析 → 口型 → 播放（同步链路）

```
文本 →（在线 EdgeTtsClient / 本地 SpeechSynthesizer）→ WAV 字节（内存）
  → WaveAudio.TryParse（RIFF 块遍历：fmt / data，支持 fmt 18 / LIST 在前或在后）
  → ReadMonoSamples → BuildEnvelope（20ms 一帧、0~1）
  → SpeechUtterance
  → SpeakAsync：InMemoryRandomAccessStream + MediaPlayer.Play()
  → onPlaybackStarted 回调（会话在这里 ShowBubble + 随机小动作）
  → 帧循环：MouthOpen = Envelope[(播放进度 ms)/20] → ModelRuntime.SpeechMouthOpen
  → 播放完成监测：按 WAV 时长 + 0.35s 轮询收尾（MediaEnded 某些机器不触发）→ 释放流 / 播放器 → 气泡淡出
```

- `SpeechUtterance` 的 `DurationSeconds` 来自 `WaveAudioInfo.DurationSeconds`（`data` 长度 / BlockAlign / rate）；
  `MouthOpen` 读取时把索引夹在 `[0, Envelope.Length-1]`，播放结束后 `IsSpeaking=false` → 口型 0；
- **同步**：`onPlaybackStarted` 是 `MediaPlayer.Play()` 之后触发的，所以气泡 / 小动作 / 口型与声音同一时刻出现；
- `SpeakAsync` 内部先 `StopCore()` 并递增 generation，避免上一句结束后回调误伤新一句；
- 会话帧循环（~60fps）把 `tts.IsSpeaking / MouthOpen` 写到 `runtime.SpeechActive / SpeechMouthOpen`，
  Tick 里 `ApplySpeechMouth` 写 `ParamMouthOpenY / ParamMouthForm / JawOpen`，**动捕有脸时让位动捕**，
  **语音时音乐跟唱闭嘴**（优先级：动捕脸 > 语音 > 音乐）。

### 15.4 气泡窗口与定位

- `SpeechBubbleWindow`：`ShowActivated=False` + `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`，
  透明置顶、点击穿透、不抢焦点 / 不进 Alt+Tab；窗口坐标每帧由会话按桌宠位置重算（`PlaceAbove`）；
- 纯静态定位 `SpeechBubblePlacement.Calculate(petLeft, petTop, petWidth, petHeight, bubbleW, bubbleH,
  workX, workY, workW, workH)`（返回 `(Left, Top)`）：
  1. 默认桌宠**头顶居中**（`petLeft + (petWidth-bubbleW)/2`、`petTop - bubbleH - Gap`，`Gap=8`）；
  2. 左右夹取到屏幕工作区（各留 **4px** 边距；工作区比气泡还窄时贴左边）；
  3. 上方放不下 → **翻到桌宠脚下**（`petTop + petHeight + Gap`）；脚下也放不下 → 贴工作区顶部 4px；
  4. `bubbleW/H <= 1` 用默认 320×90，`workW/H <= 1` 用 1920×1080（防御）。
- 说话结束约 1.5s 后淡出；桌宠拖动 / 缩放时跟随。
- **DPI**：`Calculate` 的入参必须同单位（调用方把工作区换算成 DIP）；多屏负坐标也要按**桌宠所在屏**的工作区传。

### 15.5 生命周期 / 线程

- `TtsService` 懒创建（会话持有；App 退出 Dispose）；`SynthesizeAsync` 用 `SemaphoreSlim` 串行化，
  `SpeakAsync` 可在任意线程调（MediaPlayer 建议 UI 线程创建）；`StateChanged` 可能后台线程触发，UI 订阅要 `Ui(...)`；
- `Stop()`：递增 generation + 释放 `MediaPlayer` / `IMemoryStream`，停得干净；`Dispose` 停掉所有；
- 空闲自言自语：会话定时器（60~120s，`SpeechIdleChatterEnabled`），条件与待机随机类似（没在说话 / 没放音乐 / 页面或桌宠可见）；
- `SpeechAutoGesture`：`onPlaybackStarted` 时随机配一个手势（会先停别的演示）。

### 15.6 坑

1. **WAV 头不能假设 44 字节**：TTS 输出可能带 `LIST` / `fact` 等块、`fmt ` 可能是 18 字节
   （带 `cbSize`）；必须按 RIFF 块表遍历（`chunkSize` 要校验越界、奇数块有 1 字节 padding）。
   本轮自检就踩过：合成 WAV 的 LIST 块 chunk size 少写 4，导致解析器找不到 `data`。
2. **SSML 要转义**：文本里的 `& < > " '` 必须转义，否则合成直接失败；`prosody rate/pitch` 用
   `rate="0.95"`（倍率）与 `pitch="+0st"`/百分比要按服务支持的写法，乱写会被忽略或报错。
3. **在线协议是「非官方」的**：微软没承诺 Edge TTS 接口稳定，必须保留**本地回退**并把错误写 `LastError`；
   断网 / 代理拦截 / 服务改版时功能不能挂。

### 15.7 语音语气（`SpeechTone` / 分段韵律）

> 目标：让「摸头 / 捏脸 / 喂食 / 饿 / AI 情绪」听起来不一样，而不是所有台词一个朗读味。
> 入口：`Speech/SpeechTone.cs`（参数包 + 目录 + 分段）、`TtsService`（合成时覆盖全局设置）、
> 会话的 `SpeakText(text, tone)`（互动 / 投喂 / 饥饿 / AI 回复各带自己的语气）。

**参数包（`SpeechTone`）**：语速倍率 `Rate`、音调偏移 `Pitch`（百分比）、音量倍率 `Volume`、
情感风格 `Style` + `StyleDegree`（留给 Azure / 商用 TTS）、`PunchFirstExclaim`（第一声重音 + 停顿 + 后半句放轻）、
`BreakAtEllipsisMs`（省略号后停顿）。`TtsService` 把「全局设置 × 语气」算成实际语速 / 音调 / 音量，缓存键里带上语气
（同一句话不同情绪不能命中同一段音频）。

**Edge 实测的两个硬限制（本轮踩过，别再试）**：
1. `mstts:express-as` 情感风格：24 个样本（4 音色 × 6 情绪）**全部**触发「服务端返回空音频」→ 自动回退无风格。
   结论：Edge 朗读接口不支持情感风格，风格字段只对 Azure / 商用 TTS 有意义；
2. `<break time="..."/>` 停顿：同样返回空音频。**不能再往 SSML 里塞 break**。

**替代方案：分段合成（不换引擎也能有起伏）**
- `SpeechToneCatalog.BuildSegments(text, tone)` 按**句末标点**（！？。…；;、换行）切 1~8 段；逗号 / 顿号不切；
- 每段自带语速倍率 / 音调偏移 / 段后静音：
  - `PunchFirstExclaim` 的第一段：+16% 语速、+9 音调、段后 **150ms**；
  - 以「！」结尾：+6% / +4 / 110ms；「？」：+2% / +3 / 95ms；「…」：-5% / -1 / `BreakAtEllipsisMs`（默认 120ms）；
    「。」80ms；「；」70ms；最后一段静音 0；从第二段起隔段再 -2% / -1 音调（减少整句一个味）；
- `TtsService.SynthesizeEdgeAsync` 对每段分别请求 Edge（只改 prosody，不碰 style / break）→ 每段 MP3 转 WAV →
  读出 16bit 单声道样本 → 段间补静音 → `WaveAudio.BuildPcm16Wav` 重新打包 → 重新算口型包络；
- 本地 Windows TTS 回退仍整句合成（不支持分段韵律，声音本身就是机械的）；
- 试听钩子：`YLT_LIVE2D_TTS_AUDITION=<folder|1>`（+ `YLT_LIVE2D_TTS_AUDITION_EXIT=1`）会把
  「4 音色 × 6 条台词」用**真实合成链路**导出 WAV + `index.md` + `audition.m3u`，用来 A/B 对比；
- 调语气只改 `SpeechToneCatalog`（映射）和会话里 `SpeakText(line, tone)` 的调用；改完同步自检
  `Live2DTtsToneChecks`（分段 / SSML / WAV 拼接 / 缓存键断言）。
4. **MediaPlayer 的 `MediaEnded` 不可靠**：某些机器 / 格式不触发；实现按 WAV 时长轮询收尾。
   释放顺序：先 `Stop`/`Pause` → `Source=null` → `Dispose`，并把 `InMemoryRandomAccessStream` 保留到播放结束
   （提前释放会让播放中断）。
5. **声音 / 口型 / 气泡要同一时刻**：不要在合成完成就弹气泡 / 开始口型（合成可能几百 ms 到几秒）；
   必须用 `onPlaybackStarted` 回调。
6. **在线语音需要联网且只发文字**：文档 / UI 要说清楚；用户可在列表选本地语音彻底离线。
7. **Windows 没装中文语音包时本地列表可能没有中文**：在线仍可用；本地回退时会选任意可用语音（可能英文）并提示。
8. **气泡定位用错屏工作区**：多屏负坐标 / DPI 缩放要按桌宠所在屏幕的工作区与 DIP 换算，否则会跑到别的屏。

## 16. 鼠标互动玩法（InteractionController / PetIdleActions）

> 参考项目 **FULASKA/-live2d-** 的鼠标玩法（摸头 / 额头表情 / 捏脸 / 挠痒 / 护住 / 待机小动作）在本项目
> **独立重写**（没有复制其源码 / 资产）：`Model/InteractionController.cs`（热区 + 反应 + 拖拽物理）+
> `Model/PetIdleActions.cs`（待机小动作）+ `ModelRuntime` 集成 + `PetWindow` 的鼠标 / 快捷键。
> 只在**桌宠窗口**生效（测试页预览不接鼠标互动）。

### 16.1 文件与职责

| 文件 | 职责 |
| --- | --- |
| `Model/InteractionController.cs` | 热区几何（部件名 → drawable 缓存、每帧包围盒）、`HitTest` / `GetRegionCenter`、指针状态机（`PointerDown/Move/Up`、`Press`、`TriggerInteraction`）、反应状态机（PatHead / Pinch / Tickle / Protect / Shy / 手势切换）、额头三态表情、拖拽物理、touched 复位；公开 `InteractionRegion`（12 区）/ `InteractionGesture`（Tap/Pet） |
| `Model/PetIdleActions.cs` | 6 个待机小动作（换脚 / 东张西望 / 伸懒腰 / 挥挥手 / 戴铁盆 / 爱心眼脸红），程序化关键帧 + 表情挂载；间隔随机、互动冷却、被打断即撤；`ActionNames / ActiveName / TriggerCount / TryTrigger / NotifyInteraction` |
| `ModelRuntime.cs` | 对外属性（`InteractionEnabled / InteractionVoiceEnabled / PetDragPhysicsEnabled / PetIdleActionsEnabled / PetIdleActionIntervalSeconds / PetIdleExpressionEnabled`）、包装方法、`InteractionHappened` 事件、`LastInteractionVoiceEvent / LastInteractionName`；Tick 里更新互动 / 待机并做门控 |
| `Views/PetWindow.xaml(.cs)` | 桌宠窗口鼠标事件（命中才捕获、否则拖窗口）、`Ctrl+Alt+L` 穿透 / `Ctrl+Alt+0` 退出全局热键、右键菜单（含 ⚙ 设置 / ❌ 退出）；拖窗位移喂 `ApplyDragImpulse` |
| `Live2DSession` / UI | 订阅 `InteractionHappened` → 台词（1.5s 节流）→ `SpeakText`（语音 + 气泡 + 口型同步）；互动语音开关 / 台词池 |

### 16.2 热区几何

- **部件名 → drawable 索引缓存**（加载时一次）：用 CubismCore `csmGetDrawableParentPartIndices` + cdi3 中文部件名，
  按关键字归组：`HeadKeywords`（头/脸/五官/脖子/耳朵/发饰/前发/眉毛/眼眶/眼球/嘴/鼻子…）、
  `ChestKeywords`（胸衣/胸/领子/肩膀花边…）、裙摆（短裙/长裙/…）、尾巴（尾巴/狗尾草）、
  `PartGroup.Face`（脸/脸底）、`Hand`（手/手指）、`Legs`（腿/左腿/右腿）、腰（腰部衣服）；
- **每帧几何**：`model.Update()` 后 `InvalidateGeometry()`，下一次 `HitTest` 用当前蒙皮顶点重算各部件包围盒
  （只遍历缓存的 drawable 索引，实测单次 ~0.0022ms）；
- **区域构造**：
  - **额头**：脸包围盒上半段矩形（top 3% ~ 44% 高，左右各留 10%）；
  - **左 / 右脸**：脸包围盒 46% 以下的椭圆，按中线 x 一分为二（按住哪边捏哪边）；
  - **头**：头部件包围盒（代表点取顶部 6% 处，避开额头 / 脸）；
  - **胸 / 腰 / 裙摆 / 腿 / 脚**：按部件包围盒叠成竖直分段（颈线以下 → 胸 → 腰 → 裙 → 腿 → 脚）；
  - **左 / 右手**：手部件包围盒按中线 x 切成左 / 右，只剩一只手时用中间线兜底；
  - **尾巴**：尾巴包围盒（代表点取最右缘 2% 处，避免被身体各段抢走）；
- **优先级**（`PriorityOrder`，z 从高到低）：
  **额头 9 > 左脸/右脸 8 > 左手/右手 7 > 头 6 > 胸 5 > 腰 4 > 裙摆 3 > 腿 2 > 脚 1 > 尾巴 0**——
  `HitTest` 按此顺序第一个包含点的区域胜出（所以额头中心不会命中头、脸不会命中胸）；
- **坐标**：画布像素（y 向下，原点左上）；`RegionIds` / `GetRegionDisplayName` 给 UI / 自检用；
  `GetRegionCenter` 返回每区代表点（自检「代表点命中同区」就靠它）；
- **换模型注意**：关键字按内置模型（大肥鱼）的中文部件名调过；自定义模型部件名不同时热区可能退化
  （某些区域拿不到 bbox → 命不中），这是启发式玩法的固有限制。

### 16.3 指针状态机（点击 / 抚摸 / 拖动 / 捏脸）

- **UI 层判定**（`PetWindow`，屏幕像素）：左键按下后 `<200ms` 且位移 `<6px` = **点击互动**，
  `>6px` = **搬窗口**（移动窗口 + 按 dx/dy 喂 `ApplyDragImpulse` 甩头发 / 裙子）；右键 = 菜单；
- **控制器层判定**（画布像素，`InteractionController`）：
  - `PointerDown(x,y)`：`HitTest` 命中热区才返回 true（UI 此时捕获指针、**不**拖窗口）；
  - `PointerMove`：累计位移；脸区域 → 更新捏脸 anchor（`PinchMaxPullRatio = 0.07` 画布宽，`pow 1.15` 前段更钝）；
    头 / 额头 → 抚摸摆头目标；每滑过画布宽 **3%** 记一步，**第一步**触发抚摸反应（`HandlePet`）；
  - `PointerUp`：若在捏脸 → `ReleasePinch`；否则若「没拖动 + 没抚摸步 + 位移 ≤ 画布宽 2% + 时长 ≤ 420ms」
    → 判定 **tap**（`HandleTap`）；否则是拖动（拖窗 / 拖拽物理）；
- **blocked**：用户动作（非 IDLE）/ 表演 / 舞蹈进行中 → 拒绝新互动；互动进行中用户突然播演示 →
  `StopImmediate` 让位（不卡在半截）；待机随机演示会被互动顶掉；
- 关闭互动（`InteractionEnabled=false`）：`HitTest` 返回 None、所有 Press/Move 为 no-op。

### 16.4 反应状态机

| 反应 | 触发（tap / pet） | 时长 | 表现 |
| --- | --- | --- | --- |
| **摸头 PatHead** | 头 / 脸（tap 或 pet） | **0.8s** | 眯眼笑 / 歪头包络，语音事件 `pat_head` |
| **额头表情循环** | 额头 tap | 持续 | 常态 → 爱心眼 → 单片眼镜 / 黑框眼镜（交替）→ 常态；事件 `face_cycle` |
| **捏脸 Pinch** | 左 / 右脸按下并拖动 | 按住期间 | 对应半边鼓起（`CheeckPuff` / `CheeckPuff2` / `CheeckPuffL/R`，左右映射与热区相反：捏左脸鼓画面右半边）+ 撅嘴 / 嘴形 + 头跟随；松手 0.3s 内回落；事件 `pinch` |
| **挠痒 Tickle** | 腰 / 腿 / 脚（tap 或 pet） | **1.2s** | 身体 / 耳朵快抖（AngleX/Z、LEar/REar 等）+ ＞＜ 表情；事件 `tickle` |
| **护住 Protect** | 胸 / 裙摆（tap 或 pet） | **1.5s** | 播「抱胸致意」手势 + 黑脸 / 屑屑眼交替 + 前倾；事件 `protect` |
| **害羞 Shy** | 手（pet）/ 尾巴 | 短 | ＞＜ + 脸红；事件 `hand` / `tickle` |
| **手部手势切换** | 左手 / 右手 tap | 持续 | 播放「竖起食指」↔ 停止（放下手）；事件 `hand` |
| **拖拽物理** | 拖窗位移 | 衰减 | `ApplyDragImpulse(dx,dy)` 写 `PositionX/Y`、`AngleX`、`AngleZ`，约 1s 衰减到 0 |
| **待机小动作** | 空闲自动 | 3.5~8.2s | 见 16.5 |

- 反应写出的参数全部记 touched，`ResetTouched` 在下一帧开头复位 → **松手 / 超时即回常态，不卡住**；
- 反应结束（非立即取消）进入 **3 秒冷却**（`ReactionCooldownSeconds`），冷却期间待机小动作不恢复。

### 16.5 待机小动作（PetIdleActions）

- 6 个程序化动作（关键帧 + 可选表情）：`换脚`（5.2s）/ `东张西望`（5.6s）/ `伸懒腰`（4.2s）/
  `挥挥手`（8.2s）/ `戴铁盆`（5.4s，表情「钢盆」）/ `爱心眼脸红`（3.5s，表情「爱心眼」+「脸红」）；
- 间隔：`NextInterval = clamp(IntervalSeconds, 6, 60) × (0.66 + rand×0.84)` → 默认 12s 时约 **8~18s**
  （配置 UI 夹取 8~30）；
- **门控**（`IsPetIdleBlocked`）：动捕有脸 / 语音说话 / 音乐在播 / 手势 / 表演 / 舞蹈 / 用户动作
  （非 IDLE）/ **互动进行中 / 互动冷却** → 都不抢；被打断时立即 `StopImmediate` 并给 3 秒冷却；
- **表情**：只有 `PetIdleExpressionEnabled` 且当前没有用户表情（`CanPetIdleUseExpression`：
  额头三态没开 + 表情管理器没激活）才挂载，避免撤掉用户自己摆的表情；
- 被 block 时 `_timer = max(_timer, 1.0)`，恢复后至少再等 1 秒，避免刚结束就蹦一个。

### 16.6 语音事件 → 说话 / 气泡链路

```
InteractionController._raiseInteraction(voiceEvent, displayName)
  → ModelRuntime.OnInteractionEvent：记录 LastInteractionVoiceEvent / LastInteractionName
                                     （InteractionVoiceEnabled 时）InteractionHappened?.Invoke(voiceEvent)
  → Live2DSession 订阅：按事件选台词（pat_head / face_cycle / pinch / tickle / protect / hand，1.5 秒节流）
  → TtsService.SpeakText → onPlaybackStarted 时弹气泡 + 自动小动作（与声音 / 口型同步）
```

- 语音事件名：`pat_head` / `face_cycle` / `pinch` / `tickle` / `protect` / `hand`；
- **`InteractionVoiceEnabled=false` 时 `InteractionHappened` 不触发，但 `LastInteractionVoiceEvent` 仍记录**
  （自检 / 调试据此看「发生了什么」；只有实际语音由开关决定）；
- 台词池在会话层（不走参考项目资产），随机且尽量避免连续重复；关掉「互动语音」只保留动作 / 表情。
- 兼容别名（老代码 / 外部脚本用）：`HitTestInteraction` / `GetInteractionRegionCenter` / `PressInteraction` /
  `DragInteraction` / `ReleaseInteraction` / `InteractionRegions` / `PetDragPhysicsEnabled` 都等价于新名。
- 性能（自检实测，Debug）：`HitTest` 平均 **~0.0022ms**、互动控制器 `Update` **~0.0003ms/帧**、
  捏脸连续 120 帧 **0 字节分配**——热区几何只在 `HitTest` 时按需重算、写参数复用缓冲，不要改成每帧全量遍历。

### 16.7 快捷键（桌宠窗口）

- `Ctrl+Alt+L`：切换鼠标穿透（穿透后点不到桌宠；再按恢复）；`Ctrl+Alt+0`：退出程序；
- 用 Win32 `RegisterHotKey(windowHandle, id, MOD_CONTROL|MOD_ALT, key)` 注册，`WM_HOTKEY` 在窗口 `WndProc` 处理，
  窗口关闭 / 桌宠隐藏时 `UnregisterHotKey`（否则热键泄漏、重开注册失败）；
- 受 `PetHotkeysEnabled` 控制；不用 / 冲突时关掉。

### 16.8 配置（6 项）

`petInteractionEnabled`（true）/ `petInteractionVoiceEnabled`（true）/ `petDragPhysicsEnabled`（true）/
`petIdleActionIntervalSeconds`（12，UI 夹 8~30）/ `petIdleExpressionEnabled`（true）/ `petHotkeysEnabled`（true）。

### 16.9 坑

1. **drawable → parent part 表只有新版 Core 有**：`csmGetDrawableParentPartIndices` 拿不到时要退化为「无热区」，
   不能让互动功能把加载搞挂；索引缓存只在加载时扫一次，别每帧遍历全部 715 个 drawable 调部件名查找。
2. **热区用画布像素、y 向下**：预览 / 桌宠缩放 / 高 DPI 都要先换算成画布坐标再 `HitTest`；
   直接用屏幕像素会随缩放漂移（UI 的 6px / 200ms 是屏幕像素，控制器的 2% / 420ms 是画布像素）。
3. **部件名匹配是启发式**：换模型 / 改部件名后热区可能失效（关键词见 `HeadKeywords` 等常量），
   不要假设「任何模型都能摸」。
4. **blocked 会取消互动**：动作淡出中（`StopAllDemos` 后 0.2s 内）`IsInteractionBlocked` 仍为 true，
   此时新互动会被 `Update` 里的 `StopImmediate` 清掉；自检 / UI 都要等动作完全结束再互动。
5. **捏脸必须在松手 / 窗口失焦 / 关闭互动时复位**（`ReleasePinch` + touched 复位），否则脸会一直鼓着。
6. **待机小动作被打断要停表情并复位参数**（`StopImmediate` 里撤表情 + touched 复位），否则会残留「钢盆」。
7. **互动 / 待机 / 用户演示三者互斥**：在 `IsInteractionBlocked` / `IsPetIdleBlocked` 里维护，
   漏一个就会「摸着摸着突然开始表演」。
8. **快捷键注册要配对注销**：`RegisterHotKey` 失败（被占用）要静默降级并提示；程序退出 / 桌宠关闭时注销。

## 17. 坑（踩过的都记在这里）

1. **moc 缓冲区必须 64 字节对齐且终身不动**：`csmReviveMocInPlace` 之后 Core 内部一直引用它；
   用 `File.ReadAllBytes` 后直接 `GCHandle` 钉住是错的（可能被 GC 移动 + 对齐不保证）。
2. **model 缓冲区必须 16 字节对齐**，`csmGetSizeofModel` 给的大小不能少；`csmInitializeModelInPlace` 失败要清指针。
3. **参数写入要夹到 min~max**：Core 对越界行为未定义（不同版本表现不同），`CubismParameter.Value` setter 已夹取。
4. **部件不透明度是 0~1，不是 0~100**；写进去后要 `Update()` 才会体现在 drawable 的 `Opacity` 上。
5. **动态顶点指针每次 Update 后都会变**（Core 内部指针表重写），不要缓存 `float*`；`CubismModel.Update` 里会重读。
6. **y 轴方向**：模型单位 y 向上、位图 y 向下，少翻一次就是「上下颠倒」。
7. **预乘 alpha**：WPF `Pbgra32` 要求预乘；纹理采样出来的颜色是非预乘的，写像素前要乘 alpha；
   透明背景角落必须是 (0,0,0,0)，否则桌宠会有白边。
8. **蒙版缓冲要用「覆盖度」不是二值**：Live2D 的面部 / 光效常用软蒙版，二值化会出硬边。
9. **Multiplicative 模式不是 `dst * src`**：还要 `+ dst * (1 - src.a)`，否则乘算网格背后会透出黑块。
10. **单网格最多 16 个蒙版**（Core 限制，内置模型最大用到 16）；自己写蒙版缓冲别溢出。
11. **`RenderOrderedDrawableIndices` 是「当前帧排序结果」**：每帧 Update 后重新排序，不要持久化这个数组。
12. **不要缓存纹理的 `BitmapSource` 到静态 / 跨工具实例**：切走工具页后模型和纹理都要能释放（200~300 MB 级别），
    否则内存翻倍；`Live2DSession` 保留 ViewModel 但模型由会话管生命周期。
13. **桌宠透明窗 + 点击穿透**：静态加 `WS_EX_TRANSPARENT` 会让角色本体也收不到鼠标（无法拖动）；
    实现是**动态**的：每 ~40ms 用 `GetCursorPos` 换算成窗口 / 画布坐标，按「当前可见网格包围盒」判断
    鼠标下有没有角色，没有就临时给 HWND 加 `WS_EX_TRANSPARENT` 让鼠标穿透，回到角色上再摘掉。
    窗口本身还要 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`（不抢焦点、不进 Alt+Tab），
    `AllowsTransparency=True` + `WindowStyle=None` + `ShowActivated=False`。
14. **DPI 缩放**：`PetLeft/PetTop` 是屏幕坐标，100% / 150% 缩放混用多显示器时坐标会「跳」，
    显示前用 `SystemParameters.WorkArea` / 显示器 API 夹回可见区域（至少留 80×40 可见，防止拖丢）。
15. **自检不碰真实 Data/Config**：`Program.cs` 会把 `YLT_HOME` 指到临时目录；自己手动跑自检时同理。
16. **`dotnet clean` / 删 `bin` 会连模型和运行时数据一起删**（模型在 `bin\...\Live2D\` 里是拷贝，删了就重建，
    但 `Data\Config` 也在 exe 旁边 —— 见 AGENTS.md 硬性规则）。
17. **每帧只重置「受管参数」，别 `ResetParametersToDefault()` 全量重置**：全量重置会让工具页手动拖的
    纯调试参数每帧弹回默认。受管参数在加载时按「动作曲线 / 表情参数 / 物理输出 / 待机 / 眼睛跟随」收集；
    新加一个会写参数的系统时，记得把它的参数 id 加进 `BuildManagedParameterIndices()`。
18. **动作 / 表情的 fade 语义别照抄官方数值**：官方动作缺省 FadeIn 1.0 秒（模型自带的 1 秒手势动作会糊掉），
    本实现默认 0.3 秒；表情管理器是「按每个表情独立权重叠加」的超集实现，**UI 必须保持单选切换**
    （切换 = 先 Clear 再 Apply），否则多选时多个表情会同时生效、和「同一时间一个表情」的产品语义不符；
    UI 文案也别写成「多选叠加」。
19. **`Data\live2d\screenshots\` 是用户数据**（测试页「截图」按钮的产物），别把它当缓存自动清理；
    缓存清理只处理 `Cache\<工具Id>\`。
20. **配置类别用 `double.NaN` 当「没设置」哨兵**：`JsonFileStore` 用 System.Text.Json 默认配置，
    NaN 序列化会直接抛异常（`Serialize` 捕获后返回 `/* 序列化失败 */`），结果就是
    **配置永远存不下来、下次启动文件被判损坏并备份成 `config.bad-*.json`**。
    要表达「未设置」用可空类型（`double?` / null）或普通数值哨兵；Live2D 桌宠的 `petLeft/petTop`
    就是因为这个坑从 `double.NaN` 改成了 `double?`。
21. **`Math.Abs(NaN - x) > 0.5` 恒为 false**：WPF 的 `Image.Width/Height` 初始是 `NaN`，
    用「差值超过阈值才赋值」的写法会永远不赋值 → Image 被布局撑满、位图被 `Stretch=Fill` 拉宽
    （表现为模型「又胖又糊」，1600 窗口下比例 1.49 而正确是 0.487）。写这类判重逻辑要显式
    `double.IsNaN(old)`；`Stretch` 用 `Uniform` 兜底。
22. **动捕不引第三方包 → FaceDetector 只给「人脸框」**：眨眼 / 张嘴只能靠框内像素启发式
    （眼带灰度标准差、嘴部暗像素比例），换肤色 / 口红 / 胡子 / 光线都要靠自动校准 + 强度滑杆兜；
    别把它当通用人脸关键点方案。
23. **摄像头默认关 + 自检绝不 `StartAsync`**：启动摄像头会弹系统权限、占用用户设备；
    自检只允许 `EnumerateCamerasAsync`（只枚举）、合成图喂 `FaceExpressionAnalyzer`、假 `IMocapProvider`。
    新增断言别为了「测全」去真开相机。
24. **WinRT 需要 TFM `net9.0-windows10.0.19041.0`**：工具工程原来 `net9.0-windows` 编译不了
    MediaCapture / FaceAnalysis；升 TFM 会自动带 WindowsSDK 投影（不是 NuGet 第三方包），
    发布产物里会多 `Microsoft.Windows.SDK.NET.dll`，属正常。
25. **MediaCapture 初始化失败很常见**：被微信 / QQ / 钉钉 / 直播 / 浏览器视频会议占用，或 Windows 隐私设置
    拒绝桌面应用访问摄像头 → `StartAsync` 返回 false。UI 必须把 `LastError` 展示出来（不要静默），
    并提示「关掉占用程序 / 检查隐私设置」；DroidCam / OBS 虚拟摄像头要引导用户手选内置设备。
26. **动捕和鼠标眼控别同时写同批参数**：有脸时只用动捕、并把鼠标眼控整段跳过；没脸 / 600ms 过期才回落。
    两边都写会互相覆盖、角度抖动。
27. **自动校准依赖用户动作**：眨眼 / 张嘴要用户正常动几次（滚动 180 帧、<24 帧给安全默认），
    `ResetCalibration` 后短暂回中性值；UI 要提示「重新校准后请正常眨眼 / 张嘴」。
28. **BodySway 头部补偿有死区**：`|补偿| < 0.05°` 时不写头，避免把鼠标 / 动捕 / 待机的头部跟随压成 0；
    改 `HeadCounterWeight` / 死区时要同时跑自检的「HeadFollow=0 不写头」断言。
29. **`YLT_LIVE2D_SWAY_DEMO` / `YLT_LIVE2D_MOCAP_SELFTEST` 是开发验收钩子**：前者自动晃动 10 秒用于截图
    中间帧；后者会**真开摄像头**跑 6 秒并写 `Data\live2d\mocap-selftest.log`（只有数字和设备名，无画面），
    日常自检 / 发布验证都不许用它替代人工授权。
30. **摄像头预览错色（偏绿）两个根因**：①对源调 `SetFormatAsync` 选非默认格式 → 某些摄像头
    MediaCapture 的 YUY2→BGRA 转换出绿画面（还会抛 COMException）；②对已经是 `Bgra8` + Straight alpha 的帧
    按「alpha 不是 Premultiplied」再 Convert 一次 → 二次转换错色。现在用默认格式 + 只在像素格式不是 Bgra8
    时 Convert；改 `MocapCameraSource` 的取帧 / 转格式逻辑时别把这两个坑改回去。
31. **受管参数别再写回「加载时一次性收集的静态数组」**：动捕 / 晃动 / 眼控 / 物理 / 待机关闭后必须把对应参数
    从受管表移除，否则「面部测试」手动调的值会被每帧复位（用户实测反馈）。开关切换那一帧要先复位一次再交接；
    新增会写参数的系统时，记得同时给出「什么时候该纳入受管参数」的条件。
32. **鼠标跟随增益默认必须是 1/1/1/0**：1 = 原来的手感，改动默认值会让所有用户的手感突变；
    增益只乘在归一化目标上，最终角度 / 眼球值仍受参数范围与强度 clamp（0~1.5）约束，不要在别处再重复夹一次。
33. **`ParamBodyAngle*` / `Position*` / `HipAngle*` 是模型参数（物理输入 / 输出），不是「整身倾斜」**：
    写它们出来的是**扭身体**（身体网格变形 + 带动物理链），用户这次明确要的是「整身以脚为支点轻倾」；
    所以音乐 / 常态轻倾只在**渲染层旋转**（`RotateTransform`，支点脚底），**不要**再把倾斜写回这些参数，
    否则会和物理链打架、看起来像扭腰。旧的音乐写身体参数逻辑已删（见 §11.5）。
34. **BodyLean 相位要连续**：有音乐时周期 4s→2.6s 只是改**角速度**（保持 phase 不变），不能直接改相位；
    `BodyLeanEnabled=false` 时幅度要**平滑趋近 0** 再停，重新打开从 0 平滑恢复，否则用户会看到「突然跳正 / 跳歪」；
    自检用固定 dt 采样过零 / 峰值验证周期与幅度，并断言相邻帧变化足够小。
35. **真实歌曲素材不进仓库**：人声识别按真实歌曲混音校准，素材属于用户私有媒体，只在本机调参，
    **不进仓库 / 自检 / 截图 / 文档**；自检只能用合成信号回归（voice-like / instrumental / click / 静音）。

## 18. 调试钩子（环境变量，开发 / 截图验收用）

| 变量 | 作用 |
| --- | --- |
| `YLT_SHOT_TOOL=live2d` + `YLT_SHOT=<png>` | 外壳截图：直接打开本工具页并截图（本工具页在 `YLT_SHOT` 下会**同步加载模型**，保证截图是最终画面） |
| `YLT_WINDOW_SIZE=1600x1000` | 配合截图强制窗口尺寸 |
| `YLT_PAGE_SCROLL=bottom` | 截图前把整页滚到底（核对下排动作 / 表情 / 物理 / 桌宠 / 日志区块） |
| `YLT_LIVE2D_SHOT_PET=<png>` | 桌宠截图：显示桌宠窗口、渲染出画面后存 PNG 并退出（透明底会垫深色便于看轮廓） |
| `YLT_LIVE2D_PET_SELFTEST=1`（+ `_EXIT=1`） | 桌宠交互自测：拖动写回配置 / 点击穿透样式位 / 全局眼控参数变化写 `Data\live2d\pet-selftest.log` |
| `YLT_LIVE2D_PREVIEW_DEBUG=1` | 预览布局排查：前 5 帧的 host / image / bitmap 尺寸写 `%TEMP%\live2d-preview-debug.log`（查拉伸 / 模糊） |
| `YLT_LIVE2D_SWAY_DEMO=1` | 身体晃动演示：打开页面自动设「左右摇 15° / 0.5Hz」并演示 10 秒（截图抓中间帧用，不写入配置给用户） |
| `YLT_LIVE2D_MOCAP_SELFTEST=1`（+ `_EXIT=1`） | 动捕链路自测（**会真开摄像头** 6 秒）：枚举 / 启动 / FPS / 帧尺寸 / 处理耗时 / 有脸次数写 `Data\live2d\mocap-selftest.log`（只有数字与设备名，无画面）；加 `_EXIT=1` 写完退出，给自动验收用 |
| `YLT_LIVE2D_MUSIC_SELFTEST=1`（+ `_EXIT=1`） | **音乐跟唱链路自测（会真采集系统声音 8 秒）**：启用回环采集后统计 采样次数 / Latest 非空 / IsPlaying 占比 / Kind 分布 / 平均 RMS·VocalScore·Confidence·BPM / 音高有效率 / SMTC 歌名歌手应用 / LastError，写 `Data\live2d\music-selftest.log`（**只有数字与媒体信息，不保存音频**）；加 `_EXIT=1` 写完退出。**不能和 `YLT_SHOT` 同用**：截图模式会刻意禁用音乐采集（截图流程不碰系统声音） |
| `YLT_LIVE2D_GESTURE_DEMO=<手势 id>` | 打开页面后自动播指定手势（中文名或 hand_on_chest 等别名），截图验收用 |
| `YLT_LIVE2D_PERFORMANCE_DEMO=<表演 id>` | 自动播指定表演（中文名），截图验收用 |
| `YLT_LIVE2D_DANCE_DEMO=<模式名>` | 自动开始指定舞蹈模式（轻快摇摆 / 挥手舞 / 摇摆转圈 / 酷炫抖肩），截图验收用 |
| `YLT_LIVE2D_SPEECH_SELFTEST=1`（+ `_EXIT=1`） | **语音链路自测**：说一句预置台词（会真的合成并播放几秒），统计 采样次数 / IsSpeaking 占比 / 嘴型平均·最大 / LastError，写 `Data\live2d\speech-selftest.log`（**只写数字，不保存音频**）；加 `_EXIT=1` 写完退出 |
| `YLT_LIVE2D_BUBBLE_SHOT=<png>` | 桌宠 + 聊天气泡**离屏合成**截图（不抓桌面），验收气泡定位 / 透明度用 |
| `YLT_LIVE2D_INTERACTION_DEMO=<区域/事件>` | 模拟桌宠互动「按下 → 抚摸移动 / 点按 → 抬起」，写 `Data\live2d\interaction-demo.log`（区域名 / 画布坐标 / 是否命中 / LastInteractionName / VoiceEvent / 会话台词 / 是否在说话），并打印到工具页日志；只写状态文本，不保存音频 |
| `YLT_LIVE2D_INTERACTION_SHOT=<png>` | 配合上一条：互动后出**桌宠离屏**截图（不抓桌面）再自动退出 |
| `YLT_LIVE2D_SHOT_CHAT=<png>` | **AI 聊天窗**离屏截图：打开聊天窗 → 塞示例消息 → 渲染成 PNG 后退出（不碰真实聊天数据，可和桌宠截图拼在一起验收）；`Loaded` 后再等布局完成 |
| `YLT_LIVE2D_SHOT_FEED=<png>` | **养成投喂**离屏拼图截图：显示桌宠 + 聊天窗 + 两个大白饭团，拼成一张 PNG 后退出（验收投喂层 / 养成 HUD / 饭团外观用，不碰桌面） |
| `YLT_LIVE2D_FEED_TRACE=1` | 投喂定位排错：每秒把「嘴部画布坐标 → 屏幕坐标 / 桌宠窗口矩形 / 嘴半径」和投喂层异常写 `Data\live2d\feed-trace.log`（只写坐标数字，不抓画面） |
| `YLT_LIVE2D_TTS_AUDITION=<folder\|1>`（+ `_EXIT=1`） | **语音试听对比**：用真实合成链路导出「4 音色 × 6 条台词」的 WAV + `index.md` + `audition.m3u`（指定目录或默认 `Data\live2d\voice-auditions`），给调语气用；不写聊天记录 |
| `YLT_LIVE2D_CHAT_POS=<left,top>` | 打开聊天窗后把它摆成「用户手动位置」（等价于拖走标题条：关闭跟随、保存坐标），验收「拖走后不会被每帧拽回桌宠旁边」 |
| `YLT_LIVE2D_CHAT_DEMO_AFFECTION=<0~100>` | 配合 `YLT_LIVE2D_SHOT_CHAT`：截图里把好感度改成指定值（默认 78），用来看满格爱心试管 / 心动档配色 |
| `YLT_LIVE2D_OBSERVE_DEMO=<observe\|tease>`（+ `_AFFECTION` / `_FACE` / `_SHOT`） | **观察用户 / 逗你** 动作截图验收：按好感度触发观察或逗你，可强制 `_FACE=单眼瞪大/双眼微眯/微眯偏睁/单眼闭眨/白眼/不满/好奇`，`_SHOT=<png>` 把桌宠窗口离屏渲染后退出；`_AFFECTION` 默认 80 |
| `YLT_LIVE2D_TAIL_WAG_DEMO=1`（+ `_SHOT` / `_TRACE`） | **摇尾巴**验收：模型加载后 `StartTailWag(5,12)`；`_SHOT=<png>` 1 秒后离屏截图退出，`_TRACE=<path>` 写 `tailVisible/wagging/tailAngles` 文本 |
| `YLT_LIVE2D_POKE_DEMO=1`（+ `YLT_LIVE2D_POKE_SHOT=<png>`） | **戳脸彩蛋**验收：强制「＞＜ + 左右晃脸 + 微微笑」并在 0.8 秒后离屏截图退出 |
| `YLT_LIVE2D_PETCARD_SCROLL=<bottom/数字>` | 打开页面后把「桌面宠物」卡内部滚到指定位置（截图设置项用） |
| `YLT_SELFCHECK_TRACE=1` | 自检排错：打印「进入 / 完成哪个分组」（查「全 PASS 但进程不退出」卡在哪一步） |

## 19. AI 聊天 · 好感度（DeepSeek，`Ai/`）

> 本轮新增（v1.9.0）。用户文档见 [docs/tools/live2d.md §AI 聊天 · 好感度](../tools/live2d.md#-ai-聊天--好感度deepseek)。
> 三个设计口径：**能省就省**（默认思考关闭 / 160 token / 历史 6 轮 / 系统提示词 ≤700 字）、
> **失败绝不空屏**（没 Key / 断网 / 解析失败都走离线兜底 + 一条 notice）、**不新增任何 NuGet 包**
> （HTTP / JSON / 提示词 / 好感度手写；DPAPI 复用邮箱代收已引的 `ProtectedData`）。

### 19.1 文件与职责

| 文件 | 职责 |
| --- | --- |
| `Ai/AiContracts.cs` | **对外契约**：`AiThinkingLevel(s)` / `AiPersonaCatalog` / `AiPersonaSettings` / `AiRequestOptions` / `AiReply` / `AiChatMessage` / `AffectionEngine` + `AffectionState`/`AffectionSettings` / `AiChatServiceOptions` / `AiProactiveSettings` / `AiReplyEventArgs` / `AiChatService`（partial 契约，只有签名与规则注释） |
| `Ai/AiChatService.cs` | partial 实现：聊天流程 / 历史裁剪 / token 统计 / `TickSchedule` 主动调度 / 落盘 `ai/chat-state.json` |
| `Ai/AiPromptBuilder.cs` | 纯函数：系统提示词 / 状态行 / 用户消息 / 主动指令 / 属性解析 / 文本压缩 |
| `Ai/AiReplyParser.cs` | 纯函数：模型 content → `AiReply`（代码围栏 / 字段别名 / 未知标签置空 / d 夹取 / Markdown 清洗 / 截断 200） |
| `Ai/DeepSeekClient.cs` | 手写 `HttpClient`：`BuildRequestBody` / `ParseResponse` / `ChatAsync` / `TestConnectionAsync` / `MapHttpError` / `NormalizeBaseUrl`；构造函数可注入 `HttpMessageHandler`（自检用假 handler，不联网） |
| `Ai/AiSecretStore.cs` | API Key 的 DPAPI（`DataProtectionScope.CurrentUser`）加解密；`dpapi:` 前缀、空值 / 历史明文兼容 |
| `Controls/AffectionVial.cs` | 粉色好感度试管（WPF 自绘；`LiquidRatio` / `ClampValue` 是公开纯函数，自检直接断言） |
| `Views/AiChatWindow.xaml(.cs)` | 聊天窗：消息列表 / 快捷短语 / 忙碌与 token 状态 / 三个即时开关（主动聊天 / 自然好感度 / 语音）；有示例数据模式（截图冒烟用，不碰真实聊天数据） |
| `Views/AiChatPlacement.cs` | 聊天窗贴桌宠的**纯定位算法**（下方 → 左侧 → 右侧 → 夹回工作区；脏参数兜底） |
| `Live2DSession.cs`「AI 聊天」段 | 懒创建服务、2 秒 `DispatcherTimer`、`ReplyReady` → 表情 / 鼓嘴 / 手势 / TTS / 气泡、桌宠右键菜单、截图钩子、退出落盘 |
| `ViewModels/Live2DViewModel.cs`「AI 聊天」段 | 设置卡绑定 + 命令（打开聊天窗 / 清空 / 重置好感度 / 测试连接 / 清除 Key）；API Key 只读 DPAPI 解密结果 |
| `Live2DConfig.cs`「AI 聊天」段 | 全部 `Ai*` 配置项（自动落盘；`AiApiKeyProtected` 存密文） |

### 19.2 数据流

```text
聊天窗输入 / 快捷短语
  → AiChatService.SendUserMessageAsync(text)
  → 取历史（最近 N 轮，跳过 notice）→ 拼系统提示词（人格 + JSON 契约）+ 历史 + [状态行 + 用户原话]
  → DeepSeekClient.ChatAsync（POST {baseUrl}/chat/completions）
       ├─ 成功：ParseResponse → AiReplyParser → 文本 / 情绪 / 动作 / d / usage
       └─ 失败：中文错误文案 → 离线兜底回复 + 一条 notice（notice 不进请求、不进历史）
  → 加桌宠消息 → AffectionEngine.ApplyChat(模型 d) → 落盘 → ReplyReady(AiReplyEventArgs)
  → Live2DSession.OnAiReplyReady：PickAiGesture(action) + ApplyAiMoodEffects(emotion/mood)
       + TTS（声音 / 口型 / 气泡同步；可单独关）
```

### 19.3 线程模型

- 服务在 **UI 线程**创建；`await` 回构造时的 `SynchronizationContext`（WPF 有 UI 上下文，事件也在 UI 线程触发）；
  自检控制台没有上下文，就回线程池（所以自检的事件收集要加锁）。
- 请求用 `SemaphoreSlim` **串行化**：同一时间只有一条在途；`IsBusy` 期间 `TickSchedule` 跳过主动说话，
  不会打断正在等回复的那一轮。
- 主动说话由会话的 **2 秒 `DispatcherTimer`** 调 `TickSchedule(now, userPresent)`；内部 `FireAndForget`
  （不阻塞 Tick、异常不外抛）。页面 / 桌宠 / 聊天窗**都不可见**时不结算掉值、也不主动说话。
- 落盘是**同步小 JSON**（≤200 条消息，`ToolStorage.WriteText` 原子写）；失败只记日志，绝不打断聊天。

### 19.4 提示词结构与 JSON 输出契约

- **系统提示词** `AiPromptBuilder.BuildSystemPrompt`，总长硬上限 `MaxSystemPromptLength = 700` 字：
  身份（桌宠名）/ 人格预设名 + 描述 / 实际称呼 + 自称 / 属性 / 说话要求（中文口语、1~2 句、不 Markdown）/
  「只输出一个 json」/ `{"r","e","a","d"}` 示例 / e、a 取值 / d 说明 / 自定义人格（≤200 字）/ 自定义设定（≤300 字）；
  自定义文本按**剩余预算**截断（`AppendBudgeted`），保证总长不超。
- **JSON Output 模式**：DeepSeek 的 `response_format={"type":"json_object"}` 要求提示词里出现 `json` 字样，
  所以提示词两处都有；关掉自然模式 / 模型 d 参与时提示词不带 `"d"` 契约。
- **状态行** `BuildStateLine`：`[状态] 好感度 72.5/100，心情：开心；属性：…`；happy/love 追加「可以撒个娇」，
  angry/sulky 追加「可以带点小脾气」；`includeAffectionDelta` 时提示可用 `d` 字段。
- **用户消息** = `BuildUserContent(状态行, 原话)` → `"状态行\n用户说：原话"`。
- **主动说话**不带历史（省 token），只带 system + 一条 `BuildProactiveInstruction`：
  IdleReminder 带「已经 N 分钟没理你」；Chatter 是普通主动搭话；两者都可能带上一句桌宠台词做衔接。
- **模型输出**意图：`{"r":"回复","e":"情绪","a":"动作","d":-3~3}`；解析器认别名：
  `r/reply/text/content/message`、`e/emotion/mood/feeling`、`a/action/act/motion`、`d/delta/affection*`。
- **容错**：代码围栏 / 前后解释 / 未知标签置空 / d 夹 -3~+3 / 纯文本回退 / JSON 正文为空交给离线兜底 /
  Markdown 与多空白清洗 / 正文截断 200 字。
- **请求参数**：关闭思考 = `{"thinking":{"type":"disabled"}}` + `temperature: 0.9`、不发 `reasoning_effort`；
  打开思考 = `{"type":"enabled"}` + `reasoning_effort`（低 low / 中 high / 高 max），**不发 temperature**；
  `response_format` 只在 JSON 输出模式发；`max_tokens` 客户端安全夹 1~4096（用户预算 40~1000 由
  会话 / 设置卡夹），主动说话压到 ≤80、测试连接 16。

### 19.5 情绪 → 表情 / 动作映射

模型返回的 `e`（情绪）与 `a`（动作）在 `Live2DSession` 里转成表情 / 手势 / 鼓嘴：

| `e`（情绪） | 表情（按名字包含匹配，模型没有就跳过） |
| --- | --- |
| `love` | 爱心眼 → 脸红 |
| `shy` | 脸红 → ＞＜ |
| `happy` | 随机脸红（`AffectionEngine.IsBlushLikely`） |
| `jealous` / `cold` | 黑脸 → 屑屑眼 |
| `angry` / `sulky` | 黑脸 → ＞＜ |
| `sad` | 流泪 |
| `surprised` | 圈圈眼 |
| 空 / `neutral` | 按当前好感度档位推断（Love→love / Happy→happy / Angry→angry / 其它 neutral） |

- **鼓嘴**：`angry / jealous / cold` 或当前好感度 Angry → `MoodPuffAmount = 0.65`；sad → 0.3；Sulky → 0.35；
  到期由帧循环收回（爱心眼约 7 秒、其它 5 秒，然后 `ClearExpressions` / 鼓嘴归零）。
- **主动搭话收敛**：主动说话时好感度 <80 只允许爱心眼类表情，不会每次都红脸。
- **动作标签 → 手势**：`wave` 挥手 / `heart` 比心 / `nod` 点头 / `akimbo` 叉腰 / `head_hold` 抱头 /
  `point` 竖食指 / `grass` 狗尾草；`none` 或空 = 不摆手势，交给自动小动作。
- 表情 / 动作联动整体可关（`AiMoodEffects=false`），语音也可单独关（`AiSpeakReply=false`）。

### 19.6 好感度规则与持久化

- 常量：`DailyGainCap = 15`（每天聊天涨幅上限）、`MaxDeltaPerChat = 4`（单轮夹取）、0~100、
  1 位小数、`MidpointRounding.AwayFromZero`。
- 档位：`<30` 生气 / `30~49` 闹别扭 / `50~69` 平常 / `70~84` 开心 / `≥85` 心动（`AffectionEngine.ResolveMood`）。
- **涨**：只在**用户发言**侧结算一次（桌宠回复不重复加）；`GainPerChat + 模型 d`（启用时）夹 ±4；
  每日累计不超过 15；自然模式关（手动模式）时 `ApplyChat` / `ApplyDecay` 都返回 0。
- **掉**：基线 = `max(LastChatUnix, LastDecayUnix)`，每满 `DecayAfterHours` 掉 `DecayPerDay`，只掉完整周期、
  一次可补多个周期；这样每 2 秒 `TickSchedule` 调一次也不会重复扣。
- **兜底**：`FloorEnabled` 时自动变化不跌破 `Floor`；手动 `SetValue` 不受兜底限制（用户想设 0 就 0）。
- **脸红 / 爱心眼概率**：`Value ≥ 65` 才有机会，概率随好感度升高 + 「容易害羞」属性 +0.2；
  `IsBlushLikely(random01)` 注入随机数便于自检。
- **落盘**：`Data\live2d\ai\chat-state.json`（`ToolStorage` 的 `ai/chat-state.json`）：
  `messages`（≤200 条）/ `affection`（Value / LastChatUnix / LastDecayUnix / TodayGain / TodayKey）/
  今日 token 三项；构造时读回、每次发言 / 主动说话后保存、`Dispose` 再存一次；token 统计跨天自动清零。

### 19.7 主动聊天节流规则

- 前置条件：`userPresent`（页面 / 桌宠 / 聊天窗可见）；`IsBusy` 时跳过；两条主动消息 **≥120 秒**；
  用户刚说完话 **2 分钟内**不打扰。
- **久未回复**（`IdleReminderEnabled`）：距 `_lastUserAt` ≥ `IdleSilenceMinutes`（夹 1~720，默认 10）且
  `now ≥ _nextIdleAt` → 触发 `IdleReminder`；连续 3 次后等待时间 ×4；每条主动消息成功后重排 `_nextIdleAt`。
- **定时搭话**（`Enabled`）：`now ≥ _nextChatterAt`（首次 = now + `IntervalMinutes` × 随机 0.6~1.4）→ 触发
  `Chatter`；每次成功 / 触发后重排。关掉再打开从下一次 Tick 重新排期，不会一打开就说话。
- 主动请求限制：**不带历史**、`max_tokens = min(配置, 80)` 夹 16~200；**主动说话不改好感度**
  （只有用户发言才算聊了一轮）。

### 19.8 DPAPI（API Key）

- `AiSecretStore.Protect` = `dpapi:` + Base64(`ProtectedData.Protect(UTF8, null, CurrentUser)`)；
  `Unprotect` 兼容历史明文（不以 `dpapi:` 开头原样返回）；解不开（换用户 / 换机器）返回空串，界面显示未填。
- 配置里只存 `AiApiKeyProtected`；请求时 `GetAiApiKey` 解密后**只放 Authorization Header**
  （自检断言「请求体不含明文 Key」）；不写日志、不做跨用户漫游。
- 换用户 / 换机器需要重新填 Key（这是 DPAPI 的设计，不是 bug）。

### 19.9 坑（本轮踩过的，改之前必看）

1. **JSON Output 可能返回空 / 被内容过滤替换成全空格**：`ParseResponse` 判空 → 兜底错误 + 离线回复；
   绝不把空字符串上屏。
2. **思考模式不吃 temperature**：打开思考时不发 `temperature`（发了也无效）；关闭时才发 0.9。
3. **`max_tokens` 截断**：按 max_tokens 硬截断，配置过小回复会断；默认 160 对 1~2 句够用。
   客户端安全夹 1~4096；面向用户的 40~1000 在 `Live2DSession.BuildAiRequestOptions` / ViewModel 夹。
4. **代码围栏 / 字段别名**：DeepSeek 偶尔包 ```json 或字段名不同，解析器必须容忍（自检已覆盖）。
5. **聊天窗跟随桌宠 / 多屏**：`AiChatPlacement` 用桌宠所在屏幕工作区；优先级下方 → 左 → 右 → 夹回；
   脏参数（NaN / 无穷 / 0）退回默认窗口尺寸，别把窗口算到屏幕外。
6. **离线兜底**：没 Key / 401 / 402 / 429 / 网络失败都走本地台词；失败额外加一条 notice
   （notice 只在界面显示，不进请求、不进历史）。
7. **自检不联网**：所有 HTTP 断言走 `FakeChatHandler`；落盘走临时 `YLT_HOME`；好感度用固定
   `DateTimeOffset`；主动调度用 `TickSchedule(now)` 注入时间，但服务内部 `_lastUserAt` 用真实时钟，
   所以测试要以「真实 now + 偏移」推进。
8. **2 秒 DispatcherTimer 只是机会点**：主动说话不保证准点，调试时别用精确时间断言。
9. **多 agent 并行改同一工具**（本轮协作经验）：`AiContracts.cs` 先定签名与规则注释，再并行写
   `AiChatService` / UI / 配置卡；否则改签名会把别人正在写的代码全弄红。

### 19.10 自检覆盖

`tests/YuLiangToolkit.FrameworkSelfCheck/Live2DAiChecks.cs`（由 `Live2DChecks.Run` 末尾调用，同属 `live2d` 组）：
思考强度映射 / 人格目录 / 称呼解析 / 提示词 / 状态行 / 主动指令 / 回复解析 / 请求体与 401·429 文案 /
网络重试 / DPAPI / 好感度引擎 / `AiChatService` 全链路（假 handler + 临时 `ToolStorage` + 现读配置回调）/
聊天窗定位 / 试管液面 / 配置默认值与 JSON 往返；全部纯逻辑、不联网、不弹窗。

## 20. 饥饿 · 金币 · 每日任务 · 投喂（`Play/`）

> 本轮新增（v1.10.0）。用户文档见 [docs/tools/live2d.md §🍙 饥饿 · 金币 · 每日任务（投喂）](../tools/live2d.md#-饥饿--金币--每日任务投喂)。
> 四个设计口径：**状态与规则分离**（开关 / 速度在 `Config` 的 `Pet*`，数值在 `Data\live2d\care-state.json`）、
> **纯逻辑可无头自检**（物理 / 台词 / 状态机 / 服务全是纯 C#，不碰 WPF）、**投喂层独立透明窗口**
> （不抢焦点、动态点击穿透、空闲隐藏）、**不新增任何 NuGet 包**（物理 / 台词 / 任务 / 落盘全部手写）。

### 20.1 文件与职责

| 文件 | 职责 |
| --- | --- |
| `Play/PetCareContracts.cs` | **契约 + 实现**：`RiceBallKind` / `RiceBallFood` / `RiceBallCatalog`（4 种）/ `DailyTaskKind` / `DailyTaskCatalog`（3 个）/ `FullnessTier` / `PetCareSettings` / `PetCareState` / `FeedResult` / `CareNoticeKind` / `CareNoticeEventArgs` / `PetCareServiceOptions` / `PetCareService`（`Tick` / `ApplyEat` / `TryBuy` / `TryConsumeInventory` / `ReportDailyEvent` / `SetFullness` / `SetCoins` / `SetInventory` / `ApplySettings` / `SaveState` / `Dispose` / `StateChanged` / `NoticeRaised` / 落盘 + 归一化） |
| `Play/RiceBallPhysics.cs` | `RiceBallBody` / `RiceCrumb` / `RiceBallDragSample` / `RiceBallPhysics`（常量 + `Step` / `ComputeThrowVelocity` / `TryHitMouth` / `SpawnCrumbs` / `StepCrumbs`），**全部纯静态**，自检直接喂固定 dt |
| `Play/FeedingLines.cs` | 台词池（吃完 3 档 + 饿的提醒）+ `ResolveTier` / `TierText` / `PickAfterEat` / `PickHungryReminder` / `GetExpressionKeyword`；称呼 `{0}` 占位，空白回落「主人」 |
| `Model/FeedingController.cs` | 凑近 / 咀嚼 / 吞咽**状态机**（`Phase` / `PhaseElapsed` / `IsEating` / `IsFocusActive` / `FocusX` / `FocusY` / `Strength` / `MouthOpen` / `HeadNodDegrees` / `BodyLeanDegrees` / `HasOutput` / `SetFocus` / `ClearFocus` / `StartEating` / `Reset` / `Update`） |
| `Model/ModelRuntime.cs`「投喂」段 | `ApplyFeeding`：每帧 `Update`，把凑近 / 嘴型 / 点头 / 身体倾写进 `ParamAngleX/Y`、`ParamBodyAngleX`、`ParamEyeBallX/Y`、`ParamMouthOpenY/Form`、`JawOpen`；动捕有脸让位、说话只让嘴 |
| `Model/InteractionController.cs` | `TryGetMouthCanvasCenter` / `MouthCanvasRadius`：嘴部锚点（脸椭圆中心下移 45%、半径按脸宽夹 18~80 画布像素） |
| `Views/PetFoodWindow.xaml(.cs)` | 透明全屏**投喂层**：16ms 物理循环 / 40ms 动态点击穿透 / 拖拽 / 吃掉判定 / 米粒 / 靠近回调；空闲 `Hide()` |
| `Views/Live2DView.xaml`「🍙 饥饿 · 金币 · 每日任务」卡 + `ViewModels/Live2DViewModel.cs`「养成」段 | 设置卡 + 绑定 / 命令（买饭团 / 扔出去 / 打开聊天窗 / 手动拖饱食度 / 金币输入） |
| `ViewModels/PetCareViewModels.cs` | 饭团商店行 `RiceBallRowViewModel` / 每日任务行 `DailyTaskRowViewModel`（行对象复用，只刷库存 / 进度） |
| `Views/AiChatWindow.xaml(.cs)`「养成 HUD / 投喂抽屉」段 | 聊天窗底部「🍙 饱食 · 🪙 金币」+「🍙 投喂 / 📋 任务」抽屉（4 行饭团商店 / 3 行任务） |
| `Views/PetWindow.xaml(.cs)` | 右键菜单「🍙 投喂饭团」（4 种）+ 实时养成状态行 |
| `Live2DSession.cs`「养成」段 | 懒创建 `PetCareService`（配置现读）、2 秒 `Tick` 调度、投喂回调结算（`ApplyEat` → `AddAffection` → `StartEating` → 2.2s 后说台词 / 表情）、饿了主动说话、右键投喂、退出落盘 |
| `Live2DConfig.cs`「养成」段 | 全部 `Pet*` 配置项 + `AiAffectionLocked`（自动落盘） |

### 20.2 数据流

```text
桌宠右键「🍙 投喂饭团」/ 聊天窗 HUD / 工具页饭团商店
  → Live2DSession.SpawnRiceBall(kind)（有库存 → 出库；没库存 → 先买再出库）
  → PetFoodWindow.SpawnRiceBall(kind, 屏幕 DIP 投放点)（铺满主屏工作区，Show + 开物理循环）
       投放点 = 桌宠左侧 150 DIP、贴工作区底部（静止好抓）；**初速度 = 0**，放下后不弹，
       拖起来甩出去才受重力影响（Spawn 时不再给随机向上 / 横向初速度）
  → 16ms 物理循环：RiceBallPhysics.Step → RiceBallVisual 同步（Canvas.Left = X - Radius、Top = Y - Radius、旋转）
       ├─ 拖动：IsDragging=true 物理暂停，松手 ComputeThrowVelocity(最近 5 个采样点) → 初速度（超 2600 夹住）
       ├─ 每帧 GetMouthScreenPoint()（InteractionController 嘴部锚点 → PetWindow.TryCanvasToScreen）
       ├─ 离嘴 < 300 DIP：OnFeedingFocus(nx, ny, strength, mouthOpen) → ModelRuntime.SetFeedFocus
       │                        → FeedingController.SetFocus（凑过来 / 头眼朝饭团 / 张嘴）
       └─ 离嘴 ≤ 34 DIP 或这一帧线段穿过嘴圆（TryHitMouth）：
            OnEaten(kind, 屏幕点) → Live2DSession.OnRiceBallEaten
              → PetCareService.ApplyEat(kind, now)（饱食度 + 喂食任务 + 可能发奖）
              → AiChatService.AddAffection(result.AffectionGain)（好感度锁定在这里生效）
              → ModelRuntime.StartEating(1.5, 0.65)（咀嚼 → 吞咽）
              → 2.2s 后 FeedingLines.PickAfterEat(Tier, 称呼) + GetExpressionKeyword
                 → 表情 / 鼓嘴 / TTS / 气泡 / 聊天记录一条本地桌宠消息
```

- **2 秒调度**（会话）：`Care.Tick(now)` 掉饱食度 + 跨天重置任务；`UpdateHungryReminder()` 按阈值 / 间隔主动说饿；
  聊天完成 `ReportDailyEvent(Chat)`；摸头互动 `ReportDailyEvent(PetHead)`。
- **配置现读**：`BuildCareSettings()` 每次 `Settings` 回调都从 `Cfg` 读并夹取（掉值 0~20、阈值 0~100、间隔 1~240），
  改设置立刻生效（`ApplySettings()` 会把 `LastUpdateUnix` 拨到当前，避免解锁后暴扣）。

### 20.3 物理参数（`RiceBallPhysics`，想调手感改这里 + 自检同步）

| 常量 / 参数 | 值 | 说明 |
| --- | --- | --- |
| `Gravity` | 2200 DIP/s² | 重力（屏幕坐标 y 向下） |
| `Restitution` | 0.45 | 地面 / 左右墙反弹系数 |
| `FloorFriction` | 0.86 | 落地水平摩擦（**每秒**保留比例，实现里 `pow(0.86, dt)` 归一化） |
| `AirDrag` | 0.9997 | 空气阻力（每帧保留比例，`pow(0.9997, dt*60)` 和帧率无关） |
| `MaxThrowSpeed` | 2600 DIP/s | 松手速度上限（按**矢量长度**夹，方向不变） |
| 天花板反弹 | 0.3 | 轻弹一下，不按地面全弹 |
| 贴地归零阈值 | vy < 40 / vx < 10 | 落地后小速度直接归零，避免贴地无限抖动 |
| 角速度 | vx × 0.4 | 纯观感（滚动感） |
| 饭团半径 | 27 / 24 / 27 / 30 DIP | 三角 / 球形 / 太鼓 / 俵形 |
| 物理循环 | 16ms | 只在「有球 / 有米粒 / 正在拖」时跑，空闲停 |
| 拖拽采样 | 最多 8 个，只用最近 5 个 | 松手差分算速度；采样时间缺失用松手时刻兜底 |
| 米粒 | 16 颗 / 120~420 DIP/s / 寿命 0.5~0.9s | 生成角度 -160°~-20°（朝上散开），重力 ×0.6 |

### 20.4 喂食判定

- **嘴部锚点**（`InteractionController`）：脸椭圆中心下移 `faceRy × 0.45`（画布像素）；半径 `clamp(faceRx × 0.55, 18, 80)`
  （没有脸椭圆时 fallback 30）；会话用 `PetWindow.TryCanvasToScreen` 换成屏幕 DIP。
  `TryGetMouthCanvasCenter` 第一次访问会先 `EnsureGeometry()`（按当前蒙皮算一遍部件包围盒），
  所以不用先点模型也能拿到嘴部锚点（真机联调时修的：原来没建几何会一直返回 false）。
- **命中半径**：饭团中心离嘴 ≤ **34 DIP**（`PetFoodWindow.EatDistance`）或这一帧线段穿过嘴圆；
  `TryHitMouth` 用「水平直线 + 竖直二次近似 `vy·dt + ½g·dt²`」求线段与圆交点，高速下坠也不会从圆里漏过去；
  起点已在圆内 → t = 0。
- **靠近反馈**：`FocusDistance = 300`（< 300 开始凑过来，`strength = 1 - d/300`）、
  `MouthOpenDistance = 120`（`mouthOpen = 1 - d/120`），都夹 0~1；与上次值相同不重复回调。
- **只有「吃掉」才结算**：蹭到身体 / 桌宠窗口都不算；没吃完的饭团留在屏幕上（可再拖）。

### 20.5 吃饭状态机（`FeedingController`）

- 常量：`DefaultChewSeconds = 1.4` / `DefaultSwallowSeconds = 0.65` / `ChewFrequencyHz = 1.7`；
  `StartEating` 里咀嚼夹 0.3~6s、吞咽夹 0.2~3s（会话固定传 `1.5 / 0.65`，多 0.1s 留余量）。
- **Chewing**：`PhaseElapsed ≥ ChewSeconds` → Swallowing；嘴目标 = `0.28 + |sin(2π·1.7·t)| × 0.42`（0.28~0.70），
  头 = `sin(...) × 2.2°`（一开一合 + 轻微点头）。
- **Swallowing**：`PhaseElapsed ≥ SwallowSeconds` → Idle；嘴目标 = `0.06 + (1-t) × 0.12`，头 = `-sin(πt) × 7°`（低头再抬）。
- **平滑**：凑近系数 `dt × 7`、嘴 `dt × 10`（指数逼近，`dt` 夹 0~0.25）；`BodyLeanDegrees = focusX × strength × 4.5°`（Idle / Chewing）。
- **HasOutput**：凑近 / 吃 / 回落过程中为 true；彻底归零后 false。`ModelRuntime` 只在「有输出」时写参数，
  落到 false 的第一帧把写过的参数恢复默认值一次（手动调的脸不会被残留值卡住）。
- **Reset** 立即回 Idle 并清空全部输出（关投喂 / 重载模型用）。

### 20.6 优先级（动捕 > 投喂；说话口型 > 投喂嘴型）

- `ModelRuntime` 每帧顺序：… 语音口型（3.5）→ 身体晃动 / 音乐 / 整身轻倾 → 动作 / 手势表演 / 互动 → **表情（8）** → **`ApplyFeeding`（8.2，嘴型压过表情的嘴形）** → 待机随机小动作 → 物理 → Update。
- **动捕有脸**（`mocapFace`）时 `ApplyFeeding` 整段 `return`：头 / 眼 / 嘴都交给真人脸；但 `Feeding` 状态机仍
  每帧 `Update`（避免动捕消失瞬间跳变）。
- **正在说话**（`_speechActive`）时头 / 身体 / 眼睛照常跟饭团，但**嘴部让给语音口型**（不写 `ParamMouthOpenY/Form/JawOpen`）。
- 没有输出时参数写回默认值（只恢复自己写过的参数，不碰别人）。

### 20.7 饱食度 / 金币 / 好感度三态语义

- **饱食度**：自然模式 = `Enabled && FullnessNatural && !FullnessLocked` → `Tick` 按**完整小时**扣（一次补多段，最低 0）；
  手动 / 锁定 / 总开关关 → 冻结时间，并把 `LastUpdateUnix` 拨到当前（恢复后不会一次性暴扣）；
  锁定 / 总开关关时 `ApplyEat` 不改饱食度；`SetFullness` 永远可改（用户手动）。
- **金币**：自然模式 = 任务达成时发奖（`CoinsNatural && !CoinsLocked`）；锁定 = 程序性加减全冻结
  （`AddCoins` 提示「金币已锁定」、`TryBuy` 拒绝并给中文原因），但 `SetCoins` 手动可改；
  任务达成即使不发奖也标记 `TaskRewarded`（`TaskNotice` 说明「未发奖励」），一天只提示一次。
- **好感度**：`AffectionSettings.Locked` → `ApplyChat` / `ApplyDecay` / `ApplyDirectGain` 都返回 0，`SetValue` 不受限。
  投喂的 `AffectionGain` 只在 `PetCareService.Enabled=true` 时给（和饱食度锁定无关），由会话交给
  `AiChatService.AddAffection` → `AffectionEngine.ApplyDirectGain`，所以**好感度锁定会吃掉投喂加成**。

### 20.8 每日任务跨天重置

- 日期键 = **本地** `yyyy-MM-dd`；`ReportDailyEvent` / `Tick` / 构造 `LoadState` 都会 `ResetTasksIfNewDay`
  （`TaskProgress` / `TaskRewarded` 全清零）。
- 进度只加不减、封顶 `TargetCount`；达成后即使不发奖也标记 rewarded（一天只提示一次）。
- 三个任务：**摸摸头**（3 次 +6）/ **聊聊天**（1 次 +4）/ **投喂饭团**（1 次 +8），全部完成 **+18**。
- 事件来源：摸头互动 → `ReportDailyEvent(PetHead)`；AI 聊天 / 轻对话完成 → `ReportDailyEvent(Chat)`；
  `ApplyEat` 内部 `ReportDailyEvent(Feed)`（跨天 / 已领奖 / 金币模式都在服务里判断）。

### 20.9 持久化（`care-state.json`）

- 位置：`Data\live2d\care-state.json`（`ToolStorage` 的 `care-state.json`），camelCase + `JsonFileStore.Options`；
- 结构：`fullness` / `lastUpdateUnix` / `coins` / `inventory[4]` / `taskDate` / `taskProgress[3]` / `taskRewarded[3]`；
- **构造 LoadState**：无文件 → 用 `PetCareSettings.FullnessInitial`（会话固定 70）/ `CoinsInitial`（30）建新档；
  有文件 → clamp + 数组归一化（长度补 / 裁、库存 0~99、进度 0~TargetCount、金币 0~100000）+ 跨天清理；
- **坏 JSON / 读失败**：只记日志按新存档开始，**绝不打断桌宠启动**；
- **写**：每次状态变化同步写（小 JSON，操作频率低）；`Dispose` 再写一次；失败只记日志。

### 20.10 坑（本轮踩过的，改之前必看）

1. **投喂层是独立透明全屏窗口**：必须 `AllowsTransparency` + 动态 `WS_EX_TRANSPARENT`（40ms 轮询 `GetCursorPos`），
   否则会挡住整个桌面；没有饭团 / 米粒时 `Hide()`，绝不能一直显示。
2. **屏幕坐标全是 DIP**：`GetCursorPos` 是物理像素，要按当前 DPI 换算；饭团位置 / 嘴部锚点 / 桌宠窗口矩形
   都在**同一 DIP 坐标系**比较，别混用屏幕像素。
3. **WPF 全屏透明窗口开销**：只在有球时 `Show`，物理 Timer 只在「有球 / 有米粒 / 正在拖」时跑；
   拖拽时用 `IsDragging` 暂停物理，松手按轨迹给初速度。
4. **物理自检容差**：`Step` 先加重力再位移，地面反弹的「入射速度」是**加重力之后**的值（≈ `vy + g·dt`），
   自检断言反弹速度要按这个算（±60 容差），别拿松手时的 `vy` 直接乘 0.45。
5. **物理不补帧**：`Step` 用真实 delta（clamp 0~0.25），掉帧时位移会跳；60 帧归一化只作用于空气阻力 / 摩擦。
6. **吃掉判定别用「碰到桌宠窗口」**：必须用嘴部锚点圆（34 DIP），否则身体 / 尾巴都会误吃。
7. **时间基准统一**：`Tick` / `ApplyEat` / `ReportDailyEvent` 传同一个 `now`（会话用 `DateTimeOffset.Now`），
   自检用固定 `DateTimeOffset`；系统时间往回调时 `Tick` 会重置基准（不补扣），别指望能「倒扣」。
8. **金币锁定 vs 买饭团**：`TryBuy` 在锁定 / 总开关关时**直接拒绝并给中文 error**（不要静默扣 0 元）；
   库存封顶 99 也要拒绝，避免白扣钱。
9. **多 agent 并行改同一工具**（本轮协作经验）：契约文件（`PetCareContracts` / `RiceBallPhysics` / `FeedingController` /
   `AiContracts.Locked`）先落签名与规则注释，再并行写 UI / 配置 / 自检；否则改签名会把别人正在写的代码全弄红。
10. **投喂定位先看 trace**：`YLT_LIVE2D_FEED_TRACE=1` 会每秒把「嘴部画布 → 屏幕坐标 / 桌宠窗口矩形 / 嘴半径（画布）」
    和「嘴部不可用」的具体原因写 `Data\live2d\feed-trace.log`；「拖不进嘴里」先看日志里嘴部坐标是否落在桌宠窗口内、
    `TryGetMouthCanvasCenter` / `TryCanvasToScreen` 有没有失败。截图验收用 `YLT_LIVE2D_SHOT_FEED=<png>`（拼图不抓桌面）。

### 20.11 自检覆盖

`tests/YuLiangToolkit.FrameworkSelfCheck/Live2DCareChecks.cs`（由 `Live2DChecks.Run` 末尾调用，同属 `live2d` 组，约 100 条断言）：
投喂台词（档位阈值 / 中文名 / 称呼替换 / 表情关键词）/ 饭团目录与半径 / 饭团物理（重力 / 反弹 / 拖拽速度 /
嘴部求交 / 米粒）/ 饱食度（整小时掉值 / 封底 / 手动·锁定·总开关 / 吃饭封顶）/ 金币与商店（买·出库·各种拒绝原因 /
夹取）/ 每日任务（进度 / 只发一次 / 金币锁定与自然模式关 / 跨天清零）/ 持久化（读回 / 坏 JSON / 手改坏值归一化）/
吃饭状态机（凑近 / 咀嚼波动 / 吞咽 / Reset）/ 好感度锁定与直接加减 / 聊天服务投喂接口（`AddAffection` /
`AppendNotice` / `AppendLocalPetMessage` / 落盘读回）/ 养成配置默认值与 JSON 往返；全部纯逻辑、不联网、不弹窗、不碰真实 `Data`。

## 21. 互动动作 / 台词预设 · 聊天窗自由摆放（v1.12.0）

> 用户文档见 [docs/tools/live2d.md §🐭 鼠标互动玩法](../tools/live2d.md#-鼠标互动玩法) 与
> [§聊天窗怎么用](../tools/live2d.md#聊天窗怎么用)。目标：**同一部位按好感度给不同动作 + 台词**、
> **表情可以多选叠加 / 装饰单独开关**、**聊天窗能自由拖**、**动作不再闪手**。

### 21.1 数据驱动：`Play/InteractionScenarios.cs`

- `InteractionScenario(Id, VoiceEvent, MinAffection, Expressions[], WiggleSeconds, Lines[])` +
  可选 `GestureId` / `LeanForward`；再加一条数据就能加新互动（用户以后要加对话 / 动作，改这个文件即可，不用改会话逻辑）。
- 档位：`ResolveTier(affection)` = **<30 冷淡 / 30~59 普通 / 60~84 亲近 / ≥85 心动**。
- 匹配：同一事件取「`MinAffection ≤ 当前好感` 里门槛最高」的一条；没到门槛 = `null`，走老台词池
  （`InteractionVoiceLines`）。**低好感不会出现爱心眼 / 前倾 / 扭动**——这是硬口径。
- 当前目录覆盖：摸头（60 脸红+圈圈眼；85 爱心眼+脸红+前倾）、摸裙子（30 脸红；60 脸红+＞＜+扭动+「不要这样啦」）、
  捏脸、挠痒、护住、腰 / 腿 / 脚 / 尾巴 / 胸口、点额头 / 点手。台词写得短、带语气词，每条 3~6 句。
- 语音与视觉**分开触发**：`ModelRuntime.OnInteractionEvent` 里
  - `InteractionTriggered`（新增，**不受互动语音开关影响**）→ 会话套表情 / 前倾 / 扭动；
  - `InteractionHappened`（老事件，仅互动语音开时触发）→ 会话挑台词说话；
  - 台词优先用场景里的，其次老台词池；高好感台词会 `AppendLocalPetMessage` 写进聊天记录。

### 21.2 害羞扭动 `ModelRuntime.StartShyWiggle(seconds)`

- 程序扭动，不新增模型动画：写 `ParamBodyAngleZ`（身体，1.1Hz、±7°）与 `ParamAngleZ`
  （头，±4.5°、相位滞后 0.45 弧度 → **头比身体慢半拍**），两端各 20% 时间淡入 / 淡出，默认 2.2 秒后自动回正；
- 写在 **表情之后、投喂 / 物理之前**（表情先算，扭动覆盖头部角度；投喂靠近时仍能抢头）；
- 参数取 `ParamBodyAngleZ` / `ParamAngleZ`，模型没有就安静跳过；`StopShyWiggle()` 立刻回正；
- 高好感摸裙子 / 摸腰 / 尾巴 / 挠痒用 1.2~2.4 秒。

### 21.3 动作卡顿 / 闪手（用户反馈）修复

- **根因**：互动台词走 `SpeakText` → `SpeakAsyncCore` 的 `onPlaybackStarted` 里，如果 AI 回复没带 gestureId
  且「说话自动配小动作」开着，会再 `PlayRandomGesture()`；它和互动自己摆的手势（如「竖起食指」）抢手臂，
  出现「手抬起来 → 闪回下面 → 又抬起来」；
- 修复：
  1. `SpeakText(text, tone, allowAutoGesture)` / `SpeakAsyncCore(..., allowAutoGesture)`：**互动 / 投喂 / 饥饿台词传 false**；
  2. `PlayRandomGesture()` 发现 `runtime.ActiveGesture != null` 直接返回（不硬切正在播的手势）；
  3. AI 回复自带手势的：台词播完在 `finally` 里 `runtime.StopGesture()`，走作者写的**退出过渡动作**回正；
  4. 手势切新手势时本来就会 `ReleaseHold` + 播 enter，不会被动作层顶掉；互动开始会停待机演示。
- 加新动作时注意：原生手势必须有 `ExitMotionName`（如 举手切默认手 / 竖手指切回默认 / 狗尾草切回默认手），
  否则 `StopGesture` 只能走参数淡出。

### 21.4 表情多选 + 装饰分组（工具页）

- 底层 `ExpressionManager` 本来就能叠加（每个表情独立淡入淡出、逐条写参数）；之前 UI 是单选语义；
- 现在「表情」卡 **点一下开、再点只关这一个**（`Session.ApplyExpression` / `Session.RemoveExpression`，
  新增 `ModelRuntime.RemoveExpression` + `ActiveExpressionCount`），摘要「N 个表情 · 已激活 M 个」；
- 装饰 = 兔牙 / 黑框眼镜 / 单片眼镜 / 裙子切换 / 胸口衣服 / 脱鞋 / 尾巴开关 / 钢盆 / 加载 / 体型1 / 体型2 / 默认体型；
  其余为表情 / 姿势（名单在 `Live2DViewModel.IsDecorationExpression`）；
- 组合预设：害羞心动（爱心眼+脸红+前倾）/ 委屈（流泪+＞＜）/ 生气（黑脸+流冷汗）/ 屑屑（屑屑眼）/
  撒娇（爱心眼+脸红）/ 小恶魔（黑脸+爱心眼）；先 `ClearExpressions` 再逐个 `ApplyExpression`；
- 刷新按 `runtime.IsExpressionActive(row.Expression)`（淡出期间仍算激活，点了马上变暗要另做待移除集合）。

### 21.5 聊天窗自由拖动 / 跟随开关

- 配置：`aiChatWindowFollow`（默认 true）、`aiChatWindowLeft/Top`（手动坐标，哨兵 -99999）；
- `AiChatWindow.PlaceBelow` 在 `_followPet == false` 或正在拖标题条（`_draggingTitle`）时**直接返回**，
  所以拖动期间不会和会话层的「每帧贴回桌宠」打架（原 bug：每帧被拽回，1 帧瞬移）；
- 拖动结束检测位置变化 → 关闭跟随、`RememberManualPosition()` 写配置、`FollowToggled` 让会话保存；
- 标题条 **📌 / 📍** 按钮切换跟随：开启时清掉手动坐标并立刻 `PlaceBelow`；自由摆放时保存当前位置；
- 重新打开窗口时 `ApplySavedPositionIfManual()` 用回手动坐标（仅跟随关闭时）；
- 调试：`YLT_LIVE2D_CHAT_POS=left,top` 走同一个 `PlaceManually(...)` 路径，`check` 用真实进程验「3 秒后不回弹」，
  `YLT_LIVE2D_CHAT_DEMO_AFFECTION=100` 看试管满格（截图验收）。

### 21.6 好感度试管满格（`AffectionVial`）

- 原问题：液体裁剪区顶部封在 `LiquidTopY=30` 的平线，100% 时爱心上半部（y 0~30）永远填不满；
- 修复：`LiquidTopY = InnerInset`（≈3），`BuildInnerClip` 顶部改成**沿心形内壁**（两个圆弧 + 中缝）而不是平线；
  100% 时液体灌满整个爱心内腔；液面高光只在直筒段（liquidTop > 32）画，避免横穿爱心耳朵；
- 坑：内腔路径是「右腰 → 右底 → 左底 → 左腰」方向，圆底弧必须用 `Clockwise`；用 `Counterclockwise`
  会在瓶底挖出一个深色圆洞（本轮踩过，截图 `shots/vial-zoom.png` 对拍）。

### 21.7 自检覆盖

`tests/YuLiangToolkit.FrameworkSelfCheck/Live2DInteractionScenarioChecks.cs`（`live2d` 组，约 18 条）：
好感档位边界 / NaN、场景按门槛匹配（低好感不触发亲密预设、高好感爱心眼 MinAffection ≥85）、
目录完整性（Id 唯一 / 门槛唯一 / 台词非空 / 表情名合法）、PickLine 去重、配置默认值、
试管比例与夹取；全部纯逻辑、不联网、不弹窗。

## 22. 观察用户 / 逗你（程序脸 + 盯人 + 动作时暂停鼠标跟随，v1.14.0）

> 用户场景 1：前倾 + 歪头 + 「单眼瞪大 / 双眼微眯」随机，说「{user}真是个神奇的生物呢，让我仔细看看」；
> 高好感改「用户真是可爱呢…」+ 微眯偏睁 + 脸红 + 爱心眼；低好感「这是什么脏东西黏我鞋底了」+ 黑脸 + 不满。
> 场景 2：「喜欢我这样看着你吗？」戳脸 / 自然时触发；歪头 + 身体微扭 + 50% 黑脸 + 白眼 / 屑；
> 高好感前倾 + 脸红 + 爱心眼；低好感一直黑脸。**视线要看着用户**（按桌宠屏幕位置算方向）。

### 22.1 数据：`Play/ObservationScenarios.cs`

- `ObservationScenario(Id, Kind, MinAffection, Expressions[], FacePoses[], LeanForward, RandomBlackFace,
  TiltDegrees, BodyTwistDegrees, LookAtUser, Seconds, Lines[])`，Kind = `observe` / `tease`；
  档位复用 `InteractionScenarios.ResolveTier`（<30 / 30~84 / ≥85，按 MinAffection 匹配最高档）。
- 台词里的 `{user}` 由会话替换成用户称呼：`AiAddressCustom`（非空优先）→ `AiAddressPreset` → 默认主人。
- 触发：
  - 自然待机 `TryIdleChatter`：35% 观察 / 25% 逗你 / 40% 普通自言自语（60~120 秒一次）；
  - 点额头 / 戳脸 `face_cycle` → **观察 / 逗你各 50% 随机**；捏脸 `pinch` → 50% 观察（其余走原有捏脸反应）；
  - 这些路径**不再走旧的 `InteractionScenarios` 爱心眼规则**（观察场景自己带表情）；
  - `speak` 与视觉分开：`InteractionTriggered` 只做视觉；`InteractionHappened` 只说话（互动语音关掉也会做动作）。

### 22.2 程序脸（`ModelRuntime.StartObservation`）

- 参数级表情，不新增 exp3：`ParamEyeLOpen/ROpen`、`LEyeSquint/REyeSquint`、`ParamBrowLY/RY`、
  `ParamBrowLAngle/RAngle`、`ParamMouthForm`、`ParamEyeBallX/Y`、`ParamAngleZ`、`ParamBodyAngleZ`；
- 预设目标（`ObservationFaceTargets.For`）：

  | 预设 | 眼睛开合 L/R | 眯眼 | 眉毛 Y / 角度 | 嘴形 | 用途 |
  | --- | --- | --- | --- | --- | --- |
  | 单眼瞪大 | 1.15 / 0.42（随机哪边大） | 0 | +0.15 / -0.10 | +0.15 | 拿放大镜看你的感觉（小眼留一条缝，不闭死） |
  | 双眼微眯 | 0.55 / 0.55 | 0.35 | +0.15 / +0.05 | +0.15 | 普通观察 |
  | 微眯偏睁 | 0.72 / 0.72 | 0.18 | +0.28 / +0.05 | +0.20 | 高好感「好奇地观察」 |
  | 单眼闭眨 | 闭 0.05 / 睁 1.0（左右随机） | 0 | +0.15 / +0.05 | +0.15 | 一闭一睁 + 睁眼连眨 1~3 下（长动作两组） |
  | 白眼 | 0.38 / 0.38 | 0.25 | +0.25 / +0.10 | -0.10 | 欠揍 / 逗你；眼球 Y=0.85 |
  | 不满 | 0.62 / 0.62 | 0.15 | -0.30 / -0.55 | -0.70 | 低好感嫌弃 |
  | 好奇 | 1.0 / 1.0 | 0 | +0.35 / +0.05 | +0.20 | 默认 |

- 权重：0.18 时间淡入 / 淡出，写 `Default + (target-Default)*weight`；到点回写默认值再交给鼠标跟随；
- **歪头 = 正面 Z 轴滚转**（`ParamAngleZ`，绕脸中心顺时针 / 逆时针转一点，随机方向），**不是左右转头**；
  身体 `ParamBodyAngleZ` 同向微扭（约头的 1/4）；两种观察动作（单眼瞪大 / 微眯 vs 单眼闭眨）共用这套滚转；
- 「单眼闭眨」的睁眼眨眼：`ObservationBlinkPlan.Build(duration, random)` 生成计划——短动作 1 组、长动作（≥3.5s）2 组，
  每组随机连眨 1~3 下（单次 0.34s + 间隔 0.34s，组间 0.85s，第一组 0.55s 后开始）；次数太多会压缩到动作结束前眨完；
  `BlinkPulse(elapsed, bursts)` 把每次眨眼展开成 0→1→0 的三角脉冲，睁眼开合 = `1 - pulse*0.95`；
- 眼球：`ParamEyeBallX = gazeX*0.9`、`ParamEyeBallY = gazeY*0.8`（白眼预设用固定 0.85）；
- 与 `ShyWiggle` 的顺序：表情 → 扭动 → **观察** → 投喂；观察会覆盖头 / 眼，投喂靠近时仍抢头。

### 22.3 盯人方向（`Play/ObservationScenarios.UserGaze.Compute`）

- 脸锚点 = 桌宠窗口 `(Left+W/2, Top+H*0.32)`；目标点 = 工作区中央偏上 `(workCenterX, workCenterY*0.45)`；
- `X = (targetX - faceX) / (workW/2)`（桌宠在右 → 负 → 眼球往左）；`Y = (faceY - targetY)/(workH/2)`（**取反**：
  Live2D 的 `ParamEyeBallY` 正方向朝上，屏幕 Y 朝下）；结果夹 -1~1；
- 工作区 / 窗口尺寸无效返回 (0,0)（纯函数，自检覆盖右 / 左 / 正中 / 下方 / 夹取）。

### 22.4 动作时暂停鼠标跟随（用户建议）

- 用户反馈：做特定动作时鼠标跟随会把视线带偏（截图 / 测试时也受鼠标影响）；
- 配置 `eyeTrackingPauseOnAction`（默认 true）+ 工具页「动作时暂停鼠标跟随」复选；
- `ModelRuntime.ShouldPauseEyeTracking(pauseOnAction, observing, gesture, performance, dance, interacting)`
  → 暂停条件 = 开总开关 + 任一动作模块激活；暂停期间跳过 `EyeController.Update`（眼睛回到默认中位，
  动作自己的头 / 眼参数不被抢），结束自动恢复；
- `EyeTrackingPaused` 暴露最近一帧状态（调试 / 自检）；暂停只影响**鼠标跟随**，不关自动眨眼 / 呼吸。

### 22.5 截图验收

- `YLT_LIVE2D_OBSERVE_DEMO=observe|tease` + `YLT_LIVE2D_OBSERVE_AFFECTION=<0~100>`（默认 80）+
  `YLT_LIVE2D_OBSERVE_FACE=<预设名>`（可选，强制脸）+ `YLT_LIVE2D_OBSERVE_SHOT=<png>`：
  显示桌宠 → 设置好感 → 触发观察 → 离屏渲染桌宠窗口 PNG 后退出（不抓桌面）；
- 本轮截图：`shots/obs-faces-grid.png`（单眼瞪大 / 双眼微眯 / 白眼 / 不满 四宫格）、
  `shots/observe-high.png`（高好感脸红+爱心眼+歪头），用来对拍表情幅度；
- 注意：截图模式会**真的写**临时 `YLT_HOME` 下的聊天状态（好感度演示值），不会碰用户真实数据。

### 22.6 自检覆盖

`Live2DInteractionScenarioChecks.cs` 追加约 13 条：观察 / 逗你三档匹配（低好感黑脸不满、高好感爱心眼）、
目录完整性（脸预设名合法 / 阈值唯一 / 台词非空 / `{user}` 占位）、PickLine 去重、
`UserGaze` 右 / 左 / 中 / 下 / 夹取、`ShouldPauseEyeTracking` 真值表、配置默认值、
`ObservationBlinkPlan` 短 / 长动作组数、次数 1~3、组间间隔、眨眼脉冲窗口。

## 23. 摇尾巴（高好感触摸随机触发，v1.15.0）

> 尾巴本身是物理驱动的：PhysicsSetting130 以 `ParamBodyAngleX/Z`、`LeanInput`、`ParamBreath`、`Clothes2`
> 为输入，输出 6 段 `Param_Angle_Rotation84~89`（Scale 15/20/30/30/30/30）和 `Param52`。
> 所以不能「直接给尾巴写死角度」，否则会被物理每帧覆盖；正确做法是**在物理求值之后叠加偏移**。

### 23.1 纯函数：`Play/TailWag.cs`

- `ShouldWag(affection, tailVisible, rollPercent)`：好感 ≥85 + 尾巴露着 + 随机 <35%；
- `Envelope(elapsed, duration)`：首尾各 0.3s 淡入淡出，中间 1，动作外 0；
- `AmplitudeScale(segment)`：根部 0.55 → 尾尖约 1.10（越靠尾尖摆得越大）；
- `Offset(elapsed, duration, amplitude, segment)` = `sin(2π·1.35·t − segment×0.55) × amplitude × scale × envelope`
  —— 逐节相位滞后 0.55 弧度，看起来一节带一节甩；
- `TailWagMatrix` 不新增文件，全部参数（频率 / 淡入 / 段数 / 相位 / 概率 / 门槛）都是常量，改手感只改这里。

### 23.2 运行时：`ModelRuntime`

- 新增字段：`_tailRotationParams[6]`（`Param_Angle_Rotation84~89`）、`_tailSwitch`（`Clothes5`）、
  `StartTailWag(seconds, amplitude)` / `StopTailWag()` / `IsTailWagging` / `IsTailVisible` / `TailWagDebugText`；
- `IsTailVisible`：`Clothes5` 当前值 ≈ 默认值 → 正常露尾；被「尾巴开关」装饰改掉（默认值 -1）→ 藏起来，不摇；
- Tick 顺序：**物理 `Evaluate` → `ApplyTailWag` → `model.Update()`**；
  - 物理开着：`parameter.Value += offset`（物理每帧重算基础值，不会累积）；
  - 物理关着：`parameter.Value = DefaultValue + offset`，结束后写回默认值（避免每帧累加）；
- 尾巴被藏起来时 `ApplyTailWag` 直接停掉计时。

### 23.3 触发：`Live2DSession.MaybeStartTailWag()`

- 在 `ApplyInteractionScenarioVisuals` 最前面调用（`InteractionTriggered` 每次互动都会走这里，
  所以**所有部位 / 所有互动台词**都有概率；脸、捏脸、观察等提前 return 的分支也会先经过它）；
- 条件：好感 ≥85 + 尾巴可见 + 1.2 秒节流 + 35% 随机；
- 随机时长 2.2~3.2 秒、根部幅度 6~10°；不额外说话 / 不额外表情，只摇尾巴；
- AI 主动聊天 / 说话不算「触摸」，不会触发（只挂互动事件）。

### 23.4 验收 / 调试

- `YLT_LIVE2D_TAIL_WAG_DEMO=1`：等待模型加载 → 显示桌宠 → `StartTailWag(5,12)`；
- `YLT_LIVE2D_TAIL_WAG_SHOT=<png>`：1 秒后把桌宠窗口离屏渲染成 PNG 再退出（不抓桌面）；
- `YLT_LIVE2D_TAIL_WAG_TRACE=<path>`：写 `tailVisible=… wagging=… tailAngles=84..89` 文本，验收尾巴参数是否被写入
  （本轮实测：`tailVisible=True wagging=True tailAngles=6.8,3.6,-1.4,-7.1,-11.7,-13.3`）；
- 自检：`TailWag.ShouldWag` 门槛 / 概率边界、`Envelope` 两端、`AmplitudeScale` 尾尖更大、
  `Offset` 动作外 / 幅度 0 为 0。截图 `shots/tail-wag.png` 只拍到一帧，实际摆动要看运行时。

## 24. 戳脸彩蛋：＞＜ + 「有点痒痒的」（v1.22.0）

- 策略在 `Play/PokeReaction.cs`：中 / 高好感门槛 **≥30**、概率 **35%**、表情 `＞＜`、台词「有点痒痒的」、
  表情保持 2.5 秒（常量集中，改数值只改这里）；
- `Live2DSession.TryPokeReaction(voiceEvent, speak)`：
  - 只对 `face_cycle`（戳脸 / 点额头）生效；视觉回调（`InteractionTriggered`）先做一次「是否触发」决定，
    语音回调（`InteractionHappened`）在 900ms 内复用同一次决定 → 不会出现「表情触发了但台词没对上」；
  - 触发时视觉：施加 `＞＜`（0.25s 淡入）并记录到 `_temporaryInteractionExpression`，2.5 秒后
    **只 `RemoveExpression` 这一个表情**（不影响用户自己开的表情 / 装饰）；
  - 同时 `ModelRuntime.StartPokeReaction(2.5)`：`ParamAngleX` 以 0.9Hz、±7° 左右晃（首尾各 0.25s 淡入淡出），
    嘴形 `ParamMouthForm` → +0.55、`MouthSmile_L/R` → +0.45（微微笑），到点和表情一起回正；
    期间 `IsPokeReacting` 会触发「动作时暂停鼠标跟随」，避免鼠标把头带偏；动捕有脸时让位；
  - 触发时语音：`SpeakText("有点痒痒的", SpeechToneCatalog.Shy, allowAutoGesture:false)`，并写进聊天记录；
  - 触发时**抢先于**观察 / 逗你动作：`ApplyInteractionScenarioVisuals` 里在 `face_cycle` 分支之前 return，
    没触发才按原来的 50% 观察 / 逗你走；
  - 视觉与语音分离：互动语音关了也照样做表情（只是不说话）；
- 帧循环里 `UpdateInteractionExpressionExpiry()` 到期撤掉临时表情（只撤这一个，不 `ClearExpressions`）；
- 自检：`PokeReaction.ShouldTrigger` 门槛 / 概率边界、表情名 / 台词常量；
- 调试钩子：`YLT_LIVE2D_POKE_DEMO=1`（+ `YLT_LIVE2D_POKE_SHOT=<png>`）强制做一次「＞＜ + 晃脸微微笑」并截图退出。




