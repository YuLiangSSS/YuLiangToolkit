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

## OsuParsers 1.7.2（NuGet 依赖）

- 用途：osu!mania 三件套（Key 转换 / LN 转换 / 等级计算）读写 `.osu` 谱面文件
- 项目主页：<https://github.com/mrflashstudio/OsuParsers>
- 许可：MIT License
- 版权：Copyright (c) 2021 mrflashstudio

## ppy osu!lazer 的数学工具（源码片段）

- 位置：`src/YuLiangToolkit.Tools.OsuMania/Core/Difficulty/Utils.cs`
- 来源：<https://github.com/ppy/osu>（ppy/osu!lazer）的数学工具函数
- 许可：MIT License
- 版权：Copyright (c) ppy Pty Ltd <contact@ppy.sh>

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
