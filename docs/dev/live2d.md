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
- 不联网、不带音频 / 口型同步、不做 VTube Studio 之类的协议联动；
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
      │   └─ DanceController ──────── 4 程序化舞蹈模式（相位驱动，0.25s 权重淡入淡出）
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

**为什么不引 WebView2 / SkiaSharp / 官方 C# SDK**：

- 仓库规则「唯一第三方 NuGet 是 OsuParsers」，WebView2 / SkiaSharp 都会破例；
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
- 隐私：媒体标题 / 文件名会写进 debug selftest 日志（见 §17），排错用，不要公开贴出。

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
4. **MediaPlayer 的 `MediaEnded` 不可靠**：某些机器 / 格式不触发；实现按 WAV 时长轮询收尾。
   释放顺序：先 `Stop`/`Pause` → `Source=null` → `Dispose`，并把 `InMemoryRandomAccessStream` 保留到播放结束
   （提前释放会让播放中断）。
5. **声音 / 口型 / 气泡要同一时刻**：不要在合成完成就弹气泡 / 开始口型（合成可能几百 ms 到几秒）；
   必须用 `onPlaybackStarted` 回调。
6. **在线语音需要联网且只发文字**：文档 / UI 要说清楚；用户可在列表选本地语音彻底离线。
7. **Windows 没装中文语音包时本地列表可能没有中文**：在线仍可用；本地回退时会选任意可用语音（可能英文）并提示。
8. **气泡定位用错屏工作区**：多屏负坐标 / DPI 缩放要按桌宠所在屏幕的工作区与 DIP 换算，否则会跑到别的屏。

## 16. 坑（踩过的都记在这里）

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

## 17. 调试钩子（环境变量，开发 / 截图验收用）

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
| `YLT_SELFCHECK_TRACE=1` | 自检排错：打印「进入 / 完成哪个分组」（查「全 PASS 但进程不退出」卡在哪一步） |
