# 第三方组件与许可

本项目整体使用 **Unlicense**（见 [LICENSE](LICENSE)）：随便用、随便改、随便 fork、商用、二次发布、
打包分发都可以，不需要署名，也不需要保留版权声明。

仓库里还包含 / 依赖以下第三方作品，它们**遵循各自的许可**（不因本项目的 Unlicense 而改变）：

## DmNote（仅功能设计参考，不含源码）

- 位置：`src/YuLiangToolkit.Tools.Kps/`（KPS 按键统计）
- 来源：<https://github.com/DmNote-App/DmNote>
- 许可：GPL-3.0，Copyright (C) 2024 DM NOTE
- 说明：本项目里的 KPS 工具只参考了 DmNote 公开的功能行为与统计口径（KPS 1 秒滑动窗口、
  平均 KPS 只算有按键的采样、计数器持久化等），**没有复制、移植或包含其任何源码**，实现为独立重写；
  因此本项目不继承 GPL-3.0，该工具仍按 Unlicense 发布。详见 [docs/tools/kps.md](docs/tools/kps.md#来源与致谢)。

## FULASKA/-live2d-（仅玩法思路参考，不含源码 / 资产）

- 位置：`src/YuLiangToolkit.Tools.Live2D/Model/InteractionController.cs` 等（鼠标互动玩法）
- 来源：<https://github.com/FULASKA/-live2d->（参考项目，本地 clone 仅用于理解玩法）
- 说明：「Live2D 桌宠」的**鼠标互动玩法**（摸头 / 额头三态表情 / 捏脸 / 挠痒 / 护住 / 点击手势 /
  待机随机小动作等）借鉴了该参考项目的**玩法思路与参数区域划分思路**；本项目**没有复制、移植或包含
  其任何源码、模型、图片、音频或其它资产**，实现（`Model/InteractionController.cs`、桌宠鼠标事件、
  本地语音台词）为本项目独立编写；因此不涉及参考项目的许可传染。玩法说明见
  [docs/tools/live2d.md](docs/tools/live2d.md#-鼠标互动玩法)。

## OsuParsers 1.7.2（NuGet 依赖）

- 用途：osu!mania 三件套（Key 转换 / LN 转换 / 等级计算）读写 `.osu` 谱面文件
- 项目主页：<https://github.com/mrflashstudio/OsuParsers>
- 许可：MIT License
- 版权：Copyright (c) 2021 mrflashstudio

## SharpCompress 0.50.4（NuGet 依赖）

- 用途：「压缩包工具」（`src/YuLiangToolkit.Tools.Archive/`）的压缩 / 解压内核：
  读 RAR（RAR4 / RAR5）、ZIP、7Z、TAR 及 GZ / BZ2 / XZ / ZST 等，写 ZIP / 7Z
- 项目主页：<https://github.com/adamhathcock/sharpcompress>
- 许可：MIT License
- 版权：Copyright (c) 2014 Adam Hathcock（MIT）；包内含有的加密 / 压缩算法代码另附其原始版权声明（如 Bouncy Castle），详见 NuGet 包内 `LICENSE` 与源码注释
- 说明：本项目只引用 NuGet 包（纯托管程序集），**没有修改**其源码；不依赖、也不调用系统的
  `rar.exe` / `7z.exe`，用户机器不用预装任何压缩软件。RAR 的**压缩**算法是专有格式，
  该库只提供读取、没有写入器，因此本工具「RAR 只能解、不能压」。

## Microsoft.Web.WebView2 1.0.4314.35（NuGet 依赖）

- 用途：「网页浏览器」（`src/YuLiangToolkit.Tools.Browser/`）承载 Chromium 内核（和 Edge 同源）的 WPF 控件
- 项目主页：<https://aka.ms/webview>
- 许可：BSD-3-Clause（随包 `LICENSE.txt`，Copyright (C) Microsoft Corporation. All rights reserved.）
- 说明：本项目只引用微软官方发布的 SDK 包（托管程序集 + `WebView2Loader.dll`），**不修改**它；
  **WebView2 运行时**（Microsoft Edge WebView2 Runtime，Chromium 引擎本体）是**系统组件**，
  由用户机器上的 Edge 安装提供，**不随本项目分发**，所以发布目录不会多出 100+MB 浏览器内核。
  运行「网页浏览器」要求机器已安装该运行时（Win10 2004+ 一般随 Edge 自带，缺失时工具会提示去安装）。

## ppy osu!lazer 的数学工具（源码片段）

- 位置：`src/YuLiangToolkit.Tools.OsuMania/Core/Difficulty/Utils.cs`
- 来源：<https://github.com/ppy/osu>（ppy/osu!lazer）的数学工具函数
- 许可：MIT License
- 版权：Copyright (c) ppy Pty Ltd <contact@ppy.sh>

## Live2D Cubism Core（Proprietary Software License，随程序分发）

- 位置：`src/YuLiangToolkit.Tools.Live2D/ThirdParty/Live2DCubismCore/`
  （`Live2DCubismCore.dll` / `Live2DCubismCore.h` / `NOTICE.txt`，运行时拷到 exe 旁边）
- 用途：「Live2D 桌宠」工具加载 `.moc3` 模型、参数求值与网格变形的运行时（P/Invoke）
- 来源：Live2D Inc. 官方发布物 **Live2D Cubism SDK for Native 5-r.1**（Core 版本 5.0.0，
  `csmGetVersion()` = `0x05000000`，支持 moc3 文件格式 v5）
  - 官方下载页：<https://www.live2d.com/download/cubism-sdk/download-native/>
  - 本工程取自公开发布镜像：<https://github.com/andforce/CubismSdkForNative-5-r.1>
- 许可：**Live2D Proprietary Software License Agreement**（专有软件许可，不是 MIT / Unlicense）
  - EULA：<https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html>
  - 版权：Copyright(c) Live2D Inc. All rights reserved.
- 说明：本项目**不修改**该库、只把它作为运行时**原样随程序分发**；不得反编译、不得把该库单独抽取
  再分发或售卖，重新分发本项目时同样受 Live2D 官方条款约束。详见
  [docs/dev/live2d.md](docs/dev/live2d.md#9-许可注意事项)。

## 内置 Live2D 模型「大肥鱼」（用户自有模型）

- 位置：`src/YuLiangToolkit.Tools.Live2D/DeepSeek/`
  （`大肥鱼.model3.json` / `大肥鱼.moc3` / `大肥鱼.physics3.json` / `大肥鱼.cdi3.json` / 纹理 / `Animation/` / `Exp3/`）
- 版权：**模型作者（本项目用户）自有**，不是第三方开源素材
- 说明：该模型随本工具作为**内置演示 / 测试模型**一起分发，仓库内仅本工具使用；
  请勿把模型单独抽出再分发或另作他用。模型文件夹里的 PSD 源文件（131 MB）**不入库**
  （见 `.gitignore`，运行时也用不到）。

## Windows 系统组件（摄像头面部动捕，非第三方库）

- 位置：`src/YuLiangToolkit.Tools.Live2D/Mocap/`（调用处）
- 用途：「Live2D 桌宠」的**摄像头面部动捕**（转头 / 眨眼 / 张嘴）使用 **Windows 自带**的
  `Windows.Media.Capture`（MediaCapture / MediaFrameReader 采集）与
  `Windows.Media.FaceAnalysis`（FaceDetector 人脸检测）——属于**操作系统组件**，随 Windows 提供，
  **不是第三方库**，本仓库没有引入 OpenCV / MediaPipe / ONNX 等任何视觉库；
- 说明：摄像头默认关闭，只有用户显式打开动捕开关才会请求系统摄像头权限；画面只在内存中做检测，
  **不录像、不存图、不上传**；动捕的眨眼 / 张嘴像素启发式与数学映射为本项目自行编写（见
  [docs/dev/live2d.md](docs/dev/live2d.md#10-摄像头面部动捕mocap)）。

## Windows 系统组件（音乐跟唱 / 律动，非第三方库）

- 位置：`src/YuLiangToolkit.Tools.Live2D/Audio/`（调用处）
- 用途：「Live2D 桌宠」的**音乐跟唱 / 律动**用 **Windows 自带**的
  **WASAPI 回环采集**（`IMMDeviceEnumerator` / `IAudioClient` / `IAudioCaptureClient`，
  `AUDCLNT_STREAMFLAGS_LOOPBACK` 抓系统扬声器混音）和 **SMTC**（`Windows.Media.Control`，
  只用来显示歌名 / 歌手 / 播放状态）——属于**操作系统组件**，随 Windows 提供，**不是第三方库**，
  本仓库没有引入任何音频 / 音乐识别库；音频分析的 FFT / YIN / 节拍估算是本项目自行编写的纯 C# 实现；
- 说明：音乐功能默认关闭，只有用户打开开关才会采集**系统正在播放的声音**（不申请麦克风）；
  只做实时特征提取，**不录音、不写任何音频文件、不上传**。

## Windows 系统组件与在线服务（语音说话，无第三方库）

- 位置：`src/YuLiangToolkit.Tools.Live2D/Speech/`（调用处）
- **本地语音**：用 **Windows 自带** TTS（`Windows.Media.SpeechSynthesis.SpeechSynthesizer`，
  SAPI / OneCore 语音）合成 WAV，`Windows.Media.Playback.MediaPlayer` 播放——属于**操作系统组件**，
  **不是第三方库**；
- **在线软妹音（默认）**：调用**微软 Edge 语音合成服务**（Edge TTS，WebSocket）合成更自然的神经网络语音；
  需要联网，**只把要说的文字**发给微软语音服务（不发送音频 / 文件 / 其它信息）；
  本项目**没有引入任何第三方语音 / 音频 / 网络库**，HTTP / WebSocket / WAV 解析 / 唇形包络都是手写 C# 实现；
- 说明：语音功能由用户主动触发（点「说出来 / 发送对话 / 试听」或打开空闲自言自语）；合成音频只在内存里播放，
  **不保存任何音频文件**、不申请麦克风；在线合成失败会自动回退本地语音。

## AvalonEdit 6.3.1.120（NuGet 依赖）

- 用途：「文本编辑器」（`src/YuLiangToolkit.Tools.TextEditor/`）的编辑内核：行号列 / 语法高亮 / 大文件虚拟化 / 撤销重做
- 项目主页：<https://github.com/icsharpcode/AvalonEdit>
- 许可：MIT License
- 版权：Copyright (c) 2014-2024 AlphaSierraPapa / ICSharpCode
- 说明：本项目只引用 NuGet 包（托管程序集与主题资源），**没有修改**其源码；从源码构建时首次需要联网还原一次，
  之后走本机 NuGet 缓存，运行时不依赖额外的系统组件。

## SkiaSharp 2.88.9 / SkiaSharp.NativeAssets.Win32（NuGet 依赖）

- 用途：「屏幕贴纸」（`src/YuLiangToolkit.Tools.ScreenSticker/`）的 **GIF / 动图 WebP 逐帧解码**：
  Windows 自带的 WIC / GDI+ 不解 WebP，而且很多聊天软件导出的 GIF 是**帧尺寸不一致的优化 GIF**
  （WIC 只给子矩形帧、拼不回整幅画面）——这些情况改由 Skia 的 `SKCodec` 按帧合成后交给贴纸播放器。
- 项目主页：<https://github.com/mono/SkiaSharp>
- 许可：MIT License（Copyright (c) Microsoft Corporation / Mono Project，详见包内 `LICENSE`）
- 说明：只引用 NuGet 包，**没有修改**其源码；`SkiaSharp.NativeAssets.Win32` 会随程序分发
  `runtimes/win-*/native/libSkiaSharp.dll`（x64 / x86 / arm64，约 9 MB/个），因此发布目录会比以前大一些。
  APNG（动图 PNG）由本项目手写的解析器解码，不依赖 Skia。
- 首次从源码构建需要联网还原一次，之后走本机 NuGet 缓存。

## MIT License 全文

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
