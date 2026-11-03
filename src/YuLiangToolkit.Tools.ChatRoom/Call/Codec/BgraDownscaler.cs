namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// BGRA32 帧的「按面积平均」降采样（保持长宽比）：摄像头按源默认格式采集（常见 1280×720 /
/// 1920×1080），发送前缩到画质档位要求的宽度（240 / 320 / 480）。
///
/// <para><b>为什么按面积平均，不是最近邻抽点</b>：直接抽点会把细密的纹理（头发、衣服格子）
/// 变成闪烁的摩尔纹，而且人脸边缘会锯齿；面积平均等于一个盒式低通滤波，缩出来的画面
/// 干净、JPEG 也更好压。实现的思路是项目里 <c>MocapCameraSource.DownscaleBgra</c> 已经验证过的：
/// 每个目标像素对应源图的一块矩形，把矩形里的 B/G/R 分别求平均（alpha 统一写 255）。</para>
///
/// <para><b>尺寸规则</b>：<paramref name="width"/> = min(targetWidth, sourceWidth)，
/// <paramref name="height"/> 按源长宽比四舍五入（至少 1）。只限宽不限高：
/// 4:3 摄像头得到 320×240，16:9 得到 320×180，竖屏手机得到 320×569 —— 交给界面按比例显示。
/// 目标宽度不小于源宽时直接复制并补 alpha，不做无意义的放大。</para>
///
/// <para><b>线程模型</b>：纯静态、无状态、不分配共享资源，视频 worker 线程单线程调用；
/// 内部临时变量全在栈上 / 局部数组，任何线程调用都安全。</para>
///
/// <para><b>边界</b>：参数非法（空数组 / 尺寸 ≤0 / 长度不足）抛异常；alpha 一律写 255，
/// 因为 WinRT 转出来的 BGRA 有时 alpha 是 0，直接给 WPF 会变全透明黑图。</para>
/// </summary>
public static class BgraDownscaler
{
    /// <summary>
    /// 按面积平均把 BGRA32 帧降采样到指定宽度（高度按比例算，结果通过 out 返回）。
    /// </summary>
    /// <param name="source">源 BGRA32 像素（行优先、自上而下，stride = sourceWidth * 4）。</param>
    /// <param name="sourceWidth">源宽（像素）。</param>
    /// <param name="sourceHeight">源高（像素）。</param>
    /// <param name="targetWidth">目标宽（像素；不小于源宽时原样复制）。</param>
    /// <param name="width">实际输出宽。</param>
    /// <param name="height">实际输出高。</param>
    /// <returns>新的 BGRA32 字节数组（长度 = width * height * 4）。</returns>
    public static byte[] Downscale(byte[] source, int sourceWidth, int sourceHeight, int targetWidth,
        out int width, out int height)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源宽必须为正数");
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight), "源高必须为正数");
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth), "目标宽必须为正数");
        if (source.Length < (long)sourceWidth * sourceHeight * 4)
            throw new ArgumentException($"源 BGRA 数据长度 {source.Length} 不够 {sourceWidth}×{sourceHeight} 一帧", nameof(source));

        width = Math.Min(targetWidth, sourceWidth);
        height = Math.Max(1, (int)Math.Round(sourceHeight * (double)width / sourceWidth));

        if (width == sourceWidth && height == sourceHeight)
        {
            // 不需要缩：复制一份（调用方要独立缓冲），顺手把 alpha 修正为不透明
            var copy = new byte[sourceWidth * sourceHeight * 4];
            Array.Copy(source, copy, copy.Length);
            for (var i = 3; i < copy.Length; i += 4) copy[i] = 255;
            return copy;
        }

        var target = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            // 目标像素 y 对应源图的纵向区间 [sy0, sy1)
            var sy0 = (int)((long)y * sourceHeight / height);
            var sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                // 目标像素 x 对应源图的横向区间 [sx0, sx1)
                var sx0 = (int)((long)x * sourceWidth / width);
                var sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * sourceWidth / width));

                long b = 0, g = 0, r = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * sourceWidth;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var index = (row + sx) * 4;
                        b += source[index];
                        g += source[index + 1];
                        r += source[index + 2];
                        count++;
                    }
                }

                if (count == 0) count = 1;   // 理论上不会发生（区间至少 1 像素），防御除零
                var targetIndex = (y * width + x) * 4;
                target[targetIndex] = (byte)(b / count);
                target[targetIndex + 1] = (byte)(g / count);
                target[targetIndex + 2] = (byte)(r / count);
                target[targetIndex + 3] = 255;
            }
        }

        return target;
    }
}
