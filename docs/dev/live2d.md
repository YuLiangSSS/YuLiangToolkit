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
      │
      ├─ Mocap/ ────────────────────── 摄像头面部动捕（见 §10）
      │   ├─ MocapCameraSource ─────── MediaCapture + MediaFrameReader 采集 + FaceDetector 检测（WinRT）
      │   ├─ FaceExpressionAnalyzer ── 人脸框内像素启发式（眨眼 / 张嘴）+ 滚动分位数自动校准
      │   ├─ MocapMath ─────────────── 脸位置 → 头部角度 / 眼球方向（纯数学，自检可测）
      │   └─ MocapContracts ────────── IMocapProvider / MocapTrackingResult / MocapPreviewFrame / MocapSettings
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

## 6. 动作 / 表情 / 物理 / 眼睛跟随 / 动捕 / 晃动的求值顺序

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
5. **身体晃动测试**（`SwayEnabled` 或「摇一摇」倒计时 > 0 时）：`BodySwayController.Apply` 写身体主轴 /
   物理输入 / 头部补偿 / 腰腿参数（见 §11）；
6. 待机动作：待机动画开着且当前没有动作在播时，循环播 `IdleMotion`；
7. 动作：当前动作按时间求值 + 淡入淡出权重混合，写参数；
8. 表情：按权重施加当前表情（UI 单选；管理器本身是叠加超集，见 §3.3）；
9. 物理：读输入参数（前面写好的值）→ 钟摆求值 → 写输出参数；
10. `model.Update()`（Core 求值 + 变形）→ 触发 `FrameUpdated` → UI 渲染。

**眼睛跟随数学**（自检会验）：目标在画布中心 → 角度 ≈ 0；目标在右侧 → `ParamAngleX` / `ParamEyeBallX` 为正
（`AngleX = 归一化 x × 30 × 头转幅度`，眼球同理 ×1）；平滑是「每帧向目标插值」的指数平滑，
多次迭代后收敛到目标方向；`EyeTrackingSmoothing` 越大越柔和（retention 大、跟得慢）。

**物理强度上限 = 1**（`PhysicsEnabled` 打开时把 `PhysicsStrength` 夹到 0~1 传给评估器），UI 滑杆也是 0~1。

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

- **采集格式**：优先 `VideoFormat.Width ∈ [320, 960]` 里最接近 640 的格式（默认可能是 720p，检测太慢）；
  分析帧固定降采样到 **320 宽**（640×360 → 320×180，FaceDetector 快 3~4 倍，框坐标正好是分析帧坐标）；
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

## 12. 坑（踩过的都记在这里）

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

## 13. 调试钩子（环境变量，开发 / 截图验收用）

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
| `YLT_SELFCHECK_TRACE=1` | 自检排错：打印「进入 / 完成哪个分组」（查「全 PASS 但进程不退出」卡在哪一步） |
