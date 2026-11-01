using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Rendering;

/// <summary>
/// 渲染器的调试叠加部分：网格包围盒线框 / 蒙版范围染色 / 高亮网格 / 蒙版缓冲可视化。
/// 都画在主渲染结果之上（单线程，只在调试开关打开时执行）。
/// </summary>
public sealed unsafe partial class CubismRenderer
{
    private void DrawDebugOverlays(CubismModel model, RenderOptions options, float axisScale, float baseX, float baseY)
    {
        if (_width <= 0 || _height <= 0 || _output.Length == 0) return;
        if (!options.ShowDrawableBounds && !options.ShowMaskRanges &&
            options.HighlightDrawableIndex < 0 && options.ShowMaskBufferForDrawableIndex < 0)
        {
            return;
        }

        fixed (uint* output = _output)
        fixed (float* vertexXY = _vertexXY)
        {
            if (options.ShowDrawableBounds) DrawDrawableBounds(output);
            if (options.ShowMaskRanges) DrawMaskRanges(output);
            if (options.HighlightDrawableIndex >= 0)
            {
                DrawHighlight(output, vertexXY, model, options.HighlightDrawableIndex, axisScale, baseX, baseY);
            }

            if (options.ShowMaskBufferForDrawableIndex >= 0)
            {
                DrawMaskBufferVisual(output, options.ShowMaskBufferForDrawableIndex);
            }
        }
    }

    /// <summary>所有可见网格的包围盒线框（绿色）。</summary>
    private void DrawDrawableBounds(uint* output)
    {
        for (var i = 0; i < _visibleCount; i++)
        {
            ref var item = ref _visible[i];
            DrawRect(output, item.MinX, item.MinY, item.MaxX, item.MaxY, 0f, 1f, 0.25f, 0.9f);
        }
    }

    /// <summary>把所有蒙版上下文的覆盖度区域染红（看蒙版范围 / 蒙版是否错位）。</summary>
    private void DrawMaskRanges(uint* output)
    {
        for (var context = 0; context < _contextCount; context++)
        {
            var rect = _contextRects[context];
            var buffer = _maskBuffers[context];
            if (rect.IsEmpty || buffer is null) continue;

            for (var y = rect.Y0; y < rect.Y1; y++)
            {
                var index = y * _width + rect.X0;
                for (var x = rect.X0; x < rect.X1; x++, index++)
                {
                    var coverage = 255 - buffer[index]; // 缓冲存的是 Π(1-a)，覆盖度 = 1 - 值
                    if (coverage <= 0) continue;
                    BlendPixel(output, index, 1f, 0f, 0f, coverage * (0.5f / 255f));
                }
            }
        }
    }

    /// <summary>高亮一个网格：三角形线框（青色）+ 包围盒（黄色）。</summary>
    private void DrawHighlight(uint* output, float* vertexXY, CubismModel model, int drawableIndex,
        float axisScale, float baseX, float baseY)
    {
        // 先在可见网格里找（顶点已经变换好）
        for (var i = 0; i < _visibleCount; i++)
        {
            ref var item = ref _visible[i];
            if (item.DrawableIndex != drawableIndex) continue;
            DrawTriangleWireframe(output, vertexXY + item.VertexBase * 2, item.Indices, item.TriangleCount, 0f, 1f, 1f, 1f);
            DrawRect(output, item.MinX, item.MinY, item.MaxX, item.MaxY, 1f, 1f, 0f, 1f);
            return;
        }

        // 不在可见列表里（比如透明度 0 的蒙版网格）：直接按模型顶点画
        if ((uint)drawableIndex >= (uint)model.DrawableCount) return;
        var drawable = model.Drawables[drawableIndex];
        if (drawable.Positions is null || drawable.Indices is null || drawable.VertexCount < 3) return;

        var positions = drawable.Positions;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i < drawable.VertexCount; i++)
        {
            var x = positions[i * 2] * axisScale + baseX;
            var y = -positions[i * 2 + 1] * axisScale + baseY;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        DrawRect(output, minX, minY, maxX, maxY, 1f, 1f, 0f, 1f);

        var indices = drawable.Indices;
        for (var t = 0; t < drawable.IndexCount / 3; t++)
        {
            var i0 = indices[t * 3];
            var i1 = indices[t * 3 + 1];
            var i2 = indices[t * 3 + 2];
            var ax = positions[i0 * 2] * axisScale + baseX;
            var ay = -positions[i0 * 2 + 1] * axisScale + baseY;
            var bx = positions[i1 * 2] * axisScale + baseX;
            var by = -positions[i1 * 2 + 1] * axisScale + baseY;
            var cx = positions[i2 * 2] * axisScale + baseX;
            var cy = -positions[i2 * 2 + 1] * axisScale + baseY;
            DrawLine(output, ax, ay, bx, by, 0f, 1f, 1f, 1f);
            DrawLine(output, bx, by, cx, cy, 0f, 1f, 1f, 1f);
            DrawLine(output, cx, cy, ax, ay, 0f, 1f, 1f, 1f);
        }
    }

    private void DrawTriangleWireframe(uint* output, float* positions, ushort* indices, int triangleCount,
        float r, float g, float b, float a)
    {
        for (var t = 0; t < triangleCount; t++)
        {
            var i0 = indices[t * 3];
            var i1 = indices[t * 3 + 1];
            var i2 = indices[t * 3 + 2];
            DrawLine(output, positions[i0 * 2], positions[i0 * 2 + 1], positions[i1 * 2], positions[i1 * 2 + 1], r, g, b, a);
            DrawLine(output, positions[i1 * 2], positions[i1 * 2 + 1], positions[i2 * 2], positions[i2 * 2 + 1], r, g, b, a);
            DrawLine(output, positions[i2 * 2], positions[i2 * 2 + 1], positions[i0 * 2], positions[i0 * 2 + 1], r, g, b, a);
        }
    }

    /// <summary>把某个网格的蒙版覆盖缓冲可视化（灰度 = 覆盖度）。</summary>
    private void DrawMaskBufferVisual(uint* output, int drawableIndex)
    {
        // 找这个网格用的蒙版上下文：它自己带蒙版 → 直接用；它是别人的蒙版 → 用别人的
        var context = -1;
        for (var i = 0; i < _visibleCount; i++)
        {
            ref var item = ref _visible[i];
            if (item.DrawableIndex == drawableIndex)
            {
                context = item.MaskContext;
                break;
            }

            var masks = item.MaskIndices;
            if (masks is null) continue;
            for (var m = 0; m < masks.Length; m++)
            {
                if (masks[m] == drawableIndex)
                {
                    context = item.MaskContext;
                    break;
                }
            }

            if (context >= 0) break;
        }

        if (context < 0 || context >= _contextCount) return;
        var rect = _contextRects[context];
        var buffer = _maskBuffers[context];
        if (rect.IsEmpty || buffer is null) return;

        for (var y = rect.Y0; y < rect.Y1; y++)
        {
            var index = y * _width + rect.X0;
            for (var x = rect.X0; x < rect.X1; x++, index++)
            {
                var coverage = (255 - buffer[index]) * (1f / 255f);
                BlendPixel(output, index, coverage, coverage, coverage, 0.95f);
            }
        }
    }

    // =====================================================================================
    //  画线 / 点叠加工具（直接改 Pbgra32 输出缓冲）
    // =====================================================================================

    private void DrawRect(uint* output, float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        DrawLine(output, x0, y0, x1, y0, r, g, b, a);
        DrawLine(output, x1, y0, x1, y1, r, g, b, a);
        DrawLine(output, x1, y1, x0, y1, r, g, b, a);
        DrawLine(output, x0, y1, x0, y0, r, g, b, a);
    }

    private void DrawLine(uint* output, float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var steps = (int)MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        if (steps <= 0)
        {
            PlotPixel(output, x0, y0, r, g, b, a);
            return;
        }

        if (steps > 8192) steps = 8192;
        var stepX = dx / steps;
        var stepY = dy / steps;
        var x = x0;
        var y = y0;
        for (var i = 0; i <= steps; i++)
        {
            PlotPixel(output, x, y, r, g, b, a);
            x += stepX;
            y += stepY;
        }
    }

    private void PlotPixel(uint* output, float x, float y, float r, float g, float b, float a)
    {
        var ix = (int)x;
        var iy = (int)y;
        if ((uint)ix >= (uint)_width || (uint)iy >= (uint)_height) return;
        BlendPixel(output, iy * _width + ix, r, g, b, a);
    }

    /// <summary>把「直通色 + alpha」src-over 到一个预乘 Pbgra32 像素上。</summary>
    private static void BlendPixel(uint* output, int index, float r, float g, float b, float a)
    {
        var destination = output[index];
        var destinationAlpha = (destination >> 24) * (1f / 255f);
        var inverse = 1f - a;
        var destinationR = ((destination >> 16) & 0xFF) * (1f / 255f);
        var destinationG = ((destination >> 8) & 0xFF) * (1f / 255f);
        var destinationB = (destination & 0xFF) * (1f / 255f);

        var outA = a + destinationAlpha * inverse;
        var outR = r * a + destinationR * inverse;
        var outG = g * a + destinationG * inverse;
        var outB = b * a + destinationB * inverse;

        var a8 = ToByte(outA);
        var r8 = ToByte(outR);
        var g8 = ToByte(outG);
        var b8 = ToByte(outB);
        output[index] = ((uint)a8 << 24) | ((uint)r8 << 16) | ((uint)g8 << 8) | b8;
    }
}
