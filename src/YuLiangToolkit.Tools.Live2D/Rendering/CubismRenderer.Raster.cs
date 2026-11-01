using System.Buffers;
using System.Runtime.CompilerServices;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Rendering;

/// <summary>
/// 渲染器的光栅化部分：可见网格收集 / 蒙版覆盖度 / 分块并行三角形光栅化。
/// 语义对照官方 Cubism Native Framework（详见 <see cref="CubismRenderer"/> 注释）。
/// </summary>
public sealed unsafe partial class CubismRenderer
{
    /// <summary>一个可见网格的渲染数据（每帧重建；指针指向 Core 的数组，只在两帧之间有效）。</summary>
    private struct VisibleDrawable
    {
        /// <summary>在 <c>model.Drawables</c> 里的下标。</summary>
        public int DrawableIndex;

        /// <summary>UV（2 个 float / 顶点）。</summary>
        public float* Uv;

        /// <summary>三角形下标。</summary>
        public ushort* Indices;

        /// <summary>顶点在 <see cref="_vertexXY"/> 里的起始顶点号。</summary>
        public int VertexBase;

        /// <summary>三角形数。</summary>
        public int TriangleCount;

        /// <summary>蒙版组合下标（-1 = 无蒙版）。</summary>
        public int MaskContext;

        /// <summary>是否反转蒙版（画在蒙版外）。</summary>
        public bool InvertedMask;

        /// <summary>当前不透明度（0~1，已含部件影响）。</summary>
        public float Opacity;

        /// <summary>蒙版网格下标（引用 Core 数据；建蒙版组合时用）。</summary>
        public int[]? MaskIndices;

        /// <summary>混合模式：0 = Normal，1 = Additive，2 = Multiplicative（语义同官方 D3D11 混合状态）。</summary>
        public int Blend;

        // MultiplyColor / ScreenColor（官方 shader 用它们改颜色）
        public float MultiplyR;
        public float MultiplyG;
        public float MultiplyB;
        public float ScreenR;
        public float ScreenG;
        public float ScreenB;

        // 纹理图集
        public uint[] TexturePixels;
        public int TextureWidth;
        public int TextureHeight;
        public bool TexturePowerOfTwo;
        public int TextureWidthMask;
        public int TextureHeightMask;

        // 输出像素包围盒（用于整块剔除）
        public float MinX;
        public float MinY;
        public float MaxX;
        public float MaxY;
    }

    /// <summary>三角形光栅化初始化数据（边函数与 UV 梯度 + 每行 x 区间）。</summary>
    private struct TriSetup
    {
        public int X0;
        public int X1;
        public int Y0;
        public int Y1;
        public float Area2;
        public float W0;
        public float W1;
        public float W0Dx;
        public float W1Dx;
        public float W0Dy;
        public float W1Dy;
        public float UStart;
        public float VStart;
        public float UDx;
        public float VDx;
        public float UDy;
        public float VDy;

        // 行内 x 区间：三条边 w>=0 的交集，避免在三角形包围盒的空白区域空转
        // （Live2D 有大量细长斜三角，包围盒利用率很低）
        public float Bound0X;
        public float Bound0XStep;
        public int Bound0Dir; // +1 = 下界 / -1 = 上界 / 0 = 该边与 x 无关（整行判断）
        public float Edge0W;
        public float Edge0WStep;
        public float Bound1X;
        public float Bound1XStep;
        public int Bound1Dir;
        public float Edge1W;
        public float Edge1WStep;
        public float Bound2X;
        public float Bound2XStep;
        public int Bound2Dir;
        public float Edge2W;
        public float Edge2WStep;
    }

    // =====================================================================================
    //  可见网格收集
    // =====================================================================================

    /// <summary>
    /// 按渲染顺序收集可见网格，把顶点从「模型单位（y 向上）」变换到「输出像素（y 向下）」
    /// （<c>outX = x * axisScale + baseX</c>，<c>outY = -y * axisScale + baseY</c>），
    /// 并做不透明度 / 屏幕外包围盒剔除。返回可见三角形总数。
    /// </summary>
    private int BuildVisibleDrawables(CubismModel model, float axisScale, float baseX, float baseY, int width, int height)
    {
        EnsureVertexCapacity(model.TotalVertexCount);
        _visibleCount = 0;
        var vertexBase = 0;
        var triangles = 0;

        var ordered = model.RenderOrderedDrawableIndices;
        fixed (float* vertexXY = _vertexXY)
        {
            for (var k = 0; k < ordered.Length; k++)
            {
                var drawable = model.Drawables[ordered[k]];

                // 官方 DrawDrawable：不可见直接跳过；DrawMeshDX11：透明度 <= 0 跳过
                if (!drawable.IsVisible) continue;
                if (drawable.VertexCount < 3 || drawable.IndexCount < 3) continue;
                if (drawable.Positions is null || drawable.Uvs is null || drawable.Indices is null) continue;
                if (drawable.Opacity <= MinDrawableOpacity) continue;

                var positions = drawable.Positions;
                var vertexCount = drawable.VertexCount;
                var writeBase = vertexBase * 2;

                var minX = float.MaxValue;
                var minY = float.MaxValue;
                var maxX = float.MinValue;
                var maxY = float.MinValue;
                for (int i = 0, s = 0, d = writeBase; i < vertexCount; i++, s += 2, d += 2)
                {
                    // 模型单位 y 向上 → 画布像素 y 向下 → 输出像素（缩放 + 平移）
                    var x = positions[s] * axisScale + baseX;
                    var y = -positions[s + 1] * axisScale + baseY;
                    vertexXY[d] = x;
                    vertexXY[d + 1] = y;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

                // 屏幕外剔除（半开区间：maxX <= 0 表示整块在左边）
                if (!(maxX > 0f && minX < width && maxY > 0f && minY < height)) continue;

                var textureIndex = drawable.TextureIndex;
                if (textureIndex < 0 || textureIndex >= _textures.Count) continue;
                var view = _atlasViews[textureIndex];

                if (_visibleCount == _visible.Length) Array.Resize(ref _visible, _visible.Length * 2);

                ref var item = ref _visible[_visibleCount];
                item.DrawableIndex = drawable.Index;
                item.Uv = drawable.Uvs;
                item.Indices = drawable.Indices;
                item.VertexBase = vertexBase;
                item.TriangleCount = drawable.IndexCount / 3;
                item.MaskContext = -1;
                item.InvertedMask = drawable.IsInvertedMask;
                item.Opacity = drawable.Opacity;
                item.MaskIndices = drawable.MaskIndices;
                item.Blend = drawable.IsAdditive ? 1 : drawable.IsMultiplicative ? 2 : 0;
                item.MultiplyR = drawable.MultiplyColor.X;
                item.MultiplyG = drawable.MultiplyColor.Y;
                item.MultiplyB = drawable.MultiplyColor.Z;
                item.ScreenR = drawable.ScreenColor.X;
                item.ScreenG = drawable.ScreenColor.Y;
                item.ScreenB = drawable.ScreenColor.Z;
                item.TexturePixels = view.Pixels;
                item.TextureWidth = view.Width;
                item.TextureHeight = view.Height;
                item.TexturePowerOfTwo = view.PowerOfTwo;
                item.TextureWidthMask = view.WidthMask;
                item.TextureHeightMask = view.HeightMask;
                item.MinX = minX;
                item.MinY = minY;
                item.MaxX = maxX;
                item.MaxY = maxY;

                _visibleCount++;
                vertexBase += vertexCount;
                triangles += item.TriangleCount;
            }
        }

        return triangles;
    }

    private void EnsureVertexCapacity(int vertexCount)
    {
        var needed = Math.Max(vertexCount, 3) * 2;
        if (_vertexXY.Length >= needed) return;
        _vertexXY = new float[Math.Max(needed, 1024)];
    }

    // =====================================================================================
    //  蒙版组合 + 覆盖度缓冲
    // =====================================================================================

    /// <summary>把可见网格的蒙版下标组合归类成「蒙版上下文」（相同的一组蒙版共用一份覆盖缓冲）。</summary>
    private void BuildMaskContexts()
    {
        _contextCount = 0;
        EnsureContextCapacity(Math.Min(Math.Max(_visibleCount, 8), 512));

        for (var v = 0; v < _visibleCount; v++)
        {
            ref var item = ref _visible[v];
            var masks = item.MaskIndices;
            if (masks is null || masks.Length == 0)
            {
                item.MaskContext = -1;
                continue;
            }

            var count = masks.Length;
            if (count > _scratchMaskIndices.Length)
            {
                // 极端情况（正常 ≤ 16 个蒙版）：扩容后重来
                _scratchMaskIndices = new int[Math.Max(count, _scratchMaskIndices.Length * 2)];
            }

            Array.Copy(masks, _scratchMaskIndices, count);
            InsertionSort(_scratchMaskIndices, count);
            item.MaskContext = FindOrAddContext(count);
        }
    }

    private static void InsertionSort(int[] values, int count)
    {
        for (var i = 1; i < count; i++)
        {
            var key = values[i];
            var j = i - 1;
            while (j >= 0 && values[j] > key)
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = key;
        }
    }

    private int FindOrAddContext(int count)
    {
        for (var c = 0; c < _contextCount; c++)
        {
            var existing = _contextMasks[c];
            if (existing is null || existing.Length != count) continue;
            var same = true;
            for (var k = 0; k < count; k++)
            {
                if (existing[k] != _scratchMaskIndices[k])
                {
                    same = false;
                    break;
                }
            }

            if (same) return c;
        }

        EnsureContextCapacity(_contextCount + 1);
        var stored = _contextMasks[_contextCount];
        if (stored is null || stored.Length != count) stored = new int[count];
        Array.Copy(_scratchMaskIndices, stored, count);
        _contextMasks[_contextCount] = stored;
        var index = _contextCount;
        _contextCount++;
        return index;
    }

    private void EnsureContextCapacity(int needed)
    {
        if (_contextMasks.Length >= needed) return;
        var capacity = Math.Max(needed, _contextMasks.Length * 2);
        Array.Resize(ref _contextMasks, capacity);
        Array.Resize(ref _contextRects, capacity);
        Array.Resize(ref _maskBuffers, capacity);
        Array.Resize(ref _maskDirty, capacity);
    }

    /// <summary>
    /// 每帧按当前顶点重新光栅化每个蒙版上下文的覆盖度（官方蒙版 pass 的等价物）。
    /// 缓冲里存 <c>Π(1 - 蒙版alpha)</c>，所以「覆盖度 = 255 - 值」；反转蒙版直接用值。
    /// 缓冲维护不变式：本帧写过的矩形之外恒为 255。
    /// </summary>
    private void RasterizeMaskContexts(CubismModel model, float axisScale, float baseX, float baseY,
        int width, int height, int dop)
    {
        var pixelCount = checked(width * height);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = dop };

        Parallel.For(0, _contextCount, parallelOptions, context =>
        {
            var buffer = _maskBuffers[context];
            if (buffer is null || buffer.Length != pixelCount)
            {
                buffer = new byte[pixelCount];
                Array.Fill(buffer, (byte)255);
                _maskBuffers[context] = buffer;
                _maskDirty[context] = default;
            }

            // 上一帧写过的区域 + 本帧包围盒取并集，一次清回 255（区域外维持不变式，不整屏清）
            var previousRect = _maskDirty[context];
            var rect = ComputeContextBounds(model, _contextMasks[context], axisScale, baseX, baseY, width, height);
            _contextRects[context] = rect;
            _maskDirty[context] = rect;

            var clearRect = Union(previousRect, rect);
            if (clearRect.IsEmpty) return;
            FillRect(buffer, width, clearRect, 255);
            if (rect.IsEmpty) return;

            RasterizeMaskGeometry(model, _contextMasks[context], buffer, width, axisScale, baseX, baseY, rect);
        });
    }

    /// <summary>两个像素矩形的并集（空矩形按无处理）。</summary>
    private static RectI Union(RectI a, RectI b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var x0 = Math.Min(a.X0, b.X0);
        var y0 = Math.Min(a.Y0, b.Y0);
        var x1 = Math.Max(a.X1, b.X1);
        var y1 = Math.Max(a.Y1, b.Y1);
        return new RectI(x0, y0, x1, y1);
    }

    /// <summary>算一个蒙版上下文的输出像素包围盒（所有蒙版网格顶点变换后的并集，向外取整）。</summary>
    private static RectI ComputeContextBounds(CubismModel model, int[] maskDrawables, float axisScale,
        float baseX, float baseY, int width, int height)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        for (var m = 0; m < maskDrawables.Length; m++)
        {
            var drawable = model.Drawables[maskDrawables[m]];
            var positions = drawable.Positions;
            if (positions is null || drawable.VertexCount == 0) continue;
            for (var i = 0; i < drawable.VertexCount; i++)
            {
                var x = positions[i * 2] * axisScale + baseX;
                var y = -positions[i * 2 + 1] * axisScale + baseY;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (minX > maxX) return default;

        var x0 = (int)MathF.Floor(minX);
        var y0 = (int)MathF.Floor(minY);
        var x1 = (int)MathF.Ceiling(maxX) + 1;
        var y1 = (int)MathF.Ceiling(maxY) + 1;
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > width) x1 = width;
        if (y1 > height) y1 = height;
        return new RectI(x0, y0, x1, y1);
    }

    /// <summary>把一组蒙版网格的纹理 alpha 乘进覆盖度缓冲（<c>buf = buf * (1 - alpha)</c>，对应官方 Blend_Mask）。</summary>
    private void RasterizeMaskGeometry(CubismModel model, int[] maskDrawables, byte[] buffer, int width,
        float axisScale, float baseX, float baseY, RectI rect)
    {
        for (var m = 0; m < maskDrawables.Length; m++)
        {
            var drawable = model.Drawables[maskDrawables[m]];
            if (drawable.VertexCount < 3 || drawable.IndexCount < 3) continue;
            if (drawable.Positions is null || drawable.Uvs is null || drawable.Indices is null) continue;
            var textureIndex = drawable.TextureIndex;
            if (textureIndex < 0 || textureIndex >= _textures.Count) continue;
            var view = _atlasViews[textureIndex];

            var positions = drawable.Positions;
            var uvs = drawable.Uvs;
            var indices = drawable.Indices;
            var pixels = view.Pixels;
            var textureWidth = view.Width;
            var textureHeight = view.Height;
            var texturePowerOfTwo = view.PowerOfTwo;
            var textureWidthMask = view.WidthMask;
            var textureHeightMask = view.HeightMask;
            var triangleCount = drawable.IndexCount / 3;

            for (var t = 0; t < triangleCount; t++)
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

                if (!SetupTriangle(ax, ay, bx, by, cx, cy,
                        uvs[i0 * 2], uvs[i0 * 2 + 1],
                        uvs[i1 * 2], uvs[i1 * 2 + 1],
                        uvs[i2 * 2], uvs[i2 * 2 + 1],
                        rect.X0, rect.Y0, rect.X1, rect.Y1, out var tri))
                {
                    continue;
                }

                var w0Row = tri.W0;
                var w1Row = tri.W1;
                var uRow = tri.UStart;
                var vRow = tri.VStart;
                var area2 = tri.Area2;

                for (var y = tri.Y0; y < tri.Y1; y++)
                {
                    // 用三条边求本行 x 区间：只遍历真正在三角形内的像素（细长斜三角形省很多）
                    if (ComputeRowSpan(ref tri, out var lo, out var hi))
                    {
                        var w0 = w0Row;
                        var w1 = w1Row;
                        var u = uRow;
                        var v = vRow;
                        var skip = lo - tri.X0;
                        if (skip > 0)
                        {
                            w0 += tri.W0Dx * skip;
                            w1 += tri.W1Dx * skip;
                            u += tri.UDx * skip;
                            v += tri.VDx * skip;
                        }

                        var bufferIndex = y * width + lo;
                        for (var x = lo; x < hi; x++)
                        {
                            if (w0 >= 0f && w1 >= 0f && (area2 - w0 - w1) >= 0f)
                            {
                                var alpha = SampleBilinearAlpha(pixels, textureWidth, textureHeight,
                                    texturePowerOfTwo, textureWidthMask, textureHeightMask, u, v);
                                if (alpha > 0f)
                                {
                                    var alpha255 = (int)(alpha * 255f + 0.5f);
                                    if (alpha255 > 0)
                                    {
                                        if (alpha255 > 255) alpha255 = 255;
                                        var inverse = 255 - alpha255;
                                        buffer[bufferIndex] = (byte)((buffer[bufferIndex] * inverse + 127) / 255);
                                    }
                                }
                            }

                            w0 += tri.W0Dx;
                            w1 += tri.W1Dx;
                            u += tri.UDx;
                            v += tri.VDx;
                            bufferIndex++;
                        }
                    }

                    w0Row += tri.W0Dy;
                    w1Row += tri.W1Dy;
                    uRow += tri.UDy;
                    vRow += tri.VDy;
                }
            }
        }
    }

    /// <summary>把矩形填成某个值（用于蒙版缓冲的清理 / 初始化）。</summary>
    private static void FillRect(byte[] buffer, int stride, RectI rect, byte value)
    {
        for (var y = rect.Y0; y < rect.Y1; y++)
        {
            Array.Fill(buffer, value, y * stride + rect.X0, rect.X1 - rect.X0);
        }
    }

    // =====================================================================================
    //  分块并行主渲染
    // =====================================================================================

    /// <summary>把可见网格分块并行画到输出缓冲。返回实际着色的像素数（含 overdraw）。</summary>
    private long RenderTiles(RenderOptions options, int width, int height, bool transparent,
        float bgR, float bgG, float bgB, float bgA)
    {
        var tileSize = options.TileSize;
        if (tileSize <= 0)
        {
            // 大画布用大 tile（任务少、每块缓存命中更好），小画布用小 tile（并行粒度更细）
            tileSize = Math.Max(width, height) >= 1024 ? 128 : 64;
        }
        else if (tileSize < 32) tileSize = 32;
        else if (tileSize > 512) tileSize = 512;

        var columns = (width + tileSize - 1) / tileSize;
        var rows = (height + tileSize - 1) / tileSize;
        var tileCount = columns * rows;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = EffectiveDop(options.MaxDegreeOfParallelism) };

        long pixelsFilled = 0;
        long triangleSetups = 0;
        long pixelTests = 0;

        // 用 GCHandle 固定数组，让并行体里能直接用指针（fixed 局部变量不能被 lambda 捕获）
        var vertexHandle = System.Runtime.InteropServices.GCHandle.Alloc(_vertexXY, System.Runtime.InteropServices.GCHandleType.Pinned);
        var outputHandle = System.Runtime.InteropServices.GCHandle.Alloc(_output, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var vertexXY = (float*)vertexHandle.AddrOfPinnedObject();
            var output = (uint*)outputHandle.AddrOfPinnedObject();

            Parallel.For(0, tileCount, parallelOptions, tileIndex =>
            {
                var tileX = tileIndex % columns;
                var tileY = tileIndex / columns;
                var originX = tileX * tileSize;
                var originY = tileY * tileSize;
                var tileWidth = Math.Min(tileSize, width - originX);
                var tileHeight = Math.Min(tileSize, height - originY);
                var pixelCount = tileWidth * tileHeight;

                var buffer = ArrayPool<float>.Shared.Rent(pixelCount * 4);
                try
                {
                    fixed (float* bufferPtr = buffer)
                    {
                        if (transparent)
                        {
                            new Span<float>(buffer, 0, pixelCount * 4).Clear();
                        }
                        else
                        {
                            for (var i = 0; i < pixelCount * 4; i += 4)
                            {
                                buffer[i] = bgR;
                                buffer[i + 1] = bgG;
                                buffer[i + 2] = bgB;
                                buffer[i + 3] = bgA;
                            }
                        }

                        long filled = 0;
                        long setups = 0;
                        long tests = 0;
                        for (var v = 0; v < _visibleCount; v++)
                        {
                            ref var item = ref _visible[v];

                            // 整块剔除（半开区间）
                            if (item.MaxX <= originX || item.MinX >= originX + tileWidth ||
                                item.MaxY <= originY || item.MinY >= originY + tileHeight)
                            {
                                continue;
                            }

                            var maskBuffer = item.MaskContext >= 0 ? _maskBuffers[item.MaskContext] : null;
                            RasterizeDrawable(ref item, vertexXY, bufferPtr, originX, originY, tileWidth, tileHeight,
                                width, height, maskBuffer, ref filled, ref setups, ref tests);
                        }

                        WriteTile(output, bufferPtr, originX, originY, tileWidth, tileHeight, width);
                        Interlocked.Add(ref pixelsFilled, filled);
                        Interlocked.Add(ref triangleSetups, setups);
                        Interlocked.Add(ref pixelTests, tests);
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer);
                }
            });
        }
        finally
        {
            vertexHandle.Free();
            outputHandle.Free();
        }

        LastTriangleSetups = triangleSetups;
        LastPixelTests = pixelTests;
        return pixelsFilled;
    }

    /// <summary>把一个网格的所有三角形光栅化进 tile 缓冲（只处理与 tile 相交的部分）。</summary>
    private void RasterizeDrawable(ref VisibleDrawable item, float* vertexXY, float* buffer,
        int tileX, int tileY, int tileWidth, int tileHeight, int width, int height, byte[]? maskBuffer,
        ref long filled, ref long setups, ref long tests)
    {
        var positions = vertexXY + item.VertexBase * 2;
        var uvs = item.Uv;
        var indices = item.Indices;
        var pixels = item.TexturePixels;
        var textureWidth = item.TextureWidth;
        var textureHeight = item.TextureHeight;
        var texturePowerOfTwo = item.TexturePowerOfTwo;
        var textureWidthMask = item.TextureWidthMask;
        var textureHeightMask = item.TextureHeightMask;
        var opacity = item.Opacity;
        var blend = item.Blend;
        var maskContext = item.MaskContext;
        var inverted = item.InvertedMask;
        var mulR = item.MultiplyR;
        var mulG = item.MultiplyG;
        var mulB = item.MultiplyB;
        var scrR = item.ScreenR;
        var scrG = item.ScreenG;
        var scrB = item.ScreenB;

        var clipX1 = tileX + tileWidth;
        var clipY1 = tileY + tileHeight;

        for (var t = 0; t < item.TriangleCount; t++)
        {
            var i0 = indices[t * 3];
            var i1 = indices[t * 3 + 1];
            var i2 = indices[t * 3 + 2];

            if (!SetupTriangle(
                    positions[i0 * 2], positions[i0 * 2 + 1],
                    positions[i1 * 2], positions[i1 * 2 + 1],
                    positions[i2 * 2], positions[i2 * 2 + 1],
                    uvs[i0 * 2], uvs[i0 * 2 + 1],
                    uvs[i1 * 2], uvs[i1 * 2 + 1],
                    uvs[i2 * 2], uvs[i2 * 2 + 1],
                    tileX, tileY, clipX1, clipY1, out var tri))
            {
                continue;
            }

            setups++;
            var w0Row = tri.W0;
            var w1Row = tri.W1;
            var uRow = tri.UStart;
            var vRow = tri.VStart;
            var area2 = tri.Area2;

            for (var y = tri.Y0; y < tri.Y1; y++)
            {
                // 用三条边求本行 x 区间：只遍历真正在三角形内的像素（细长斜三角形省很多）
                if (ComputeRowSpan(ref tri, out var lo, out var hi))
                {
                tests += hi - lo;
                var w0 = w0Row;
                var w1 = w1Row;
                var u = uRow;
                var v = vRow;
                var skip = lo - tri.X0;
                if (skip > 0)
                {
                    w0 += tri.W0Dx * skip;
                    w1 += tri.W1Dx * skip;
                    u += tri.UDx * skip;
                    v += tri.VDx * skip;
                }

                var localFloat = ((y - tileY) * tileWidth + (lo - tileX)) * 4;
                var maskIndex = y * width + lo;

                for (var x = lo; x < hi; x++)
                {
                    if (w0 >= 0f && w1 >= 0f && (area2 - w0 - w1) >= 0f)
                    {
                        // 蒙版覆盖度（缓冲存的是 Π(1-a)，反转蒙版直接用）
                        var maskValue = 1f;
                        if (maskContext >= 0 && !_debugSkipMask)
                        {
                            var stored = maskBuffer![maskIndex];
                            maskValue = inverted ? stored * (1f / 255f) : (255 - stored) * (1f / 255f);
                        }

                        if (maskValue > 0f)
                        {
                            var alpha = opacity * maskValue;
                            var texR = 0.5f;
                            var texG = 0.5f;
                            var texB = 0.5f;

                            if (!_debugSkipTextureSampling)
                            {
                                // 官方 shader：Out.uv.y = 1 - In.uv.y；texel 中心在 +0.5，采样坐标再 -0.5
                                var texX = u * textureWidth - 0.5f;
                                var texY = (1f - v) * textureHeight - 0.5f;
                                var itx = (int)texX;
                                var fx = texX - itx;
                                if (fx < 0f) { itx--; fx += 1f; }
                                var ity = (int)texY;
                                var fy = texY - ity;
                                if (fy < 0f) { ity--; fy += 1f; }

                                int x0, x1, y0, y1;
                                if (texturePowerOfTwo)
                                {
                                    // 2 的幂尺寸：位与直接实现 WRAP 采样（和历史纹理一致的环绕语义）
                                    x0 = itx & textureWidthMask;
                                    x1 = (itx + 1) & textureWidthMask;
                                    y0 = ity & textureHeightMask;
                                    y1 = (ity + 1) & textureHeightMask;
                                }
                                else
                                {
                                    if (itx < 0) { x0 = textureWidth - 1; x1 = 0; }
                                    else if (itx >= textureWidth) { x0 = 0; x1 = textureWidth > 1 ? 1 : 0; }
                                    else { x0 = itx; x1 = itx + 1 == textureWidth ? 0 : itx + 1; }
                                    if (ity < 0) { y0 = textureHeight - 1; y1 = 0; }
                                    else if (ity >= textureHeight) { y0 = 0; y1 = textureHeight > 1 ? 1 : 0; }
                                    else { y0 = ity; y1 = ity + 1 == textureHeight ? 0 : ity + 1; }
                                }

                                var row0 = y0 * textureWidth;
                                var row1 = y1 * textureWidth;
                                var p00 = pixels[row0 + x0];
                                var p01 = pixels[row0 + x1];
                                var p10 = pixels[row1 + x0];
                                var p11 = pixels[row1 + x1];

                                // 先只算 alpha：全透明像素直接跳过（省掉 RGB 插值和混合）
                                var a00 = (int)(p00 >> 24);
                                var a01 = (int)(p01 >> 24);
                                var a10 = (int)(p10 >> 24);
                                var a11 = (int)(p11 >> 24);
                                var alphaTop = a00 + (a01 - a00) * fx;
                                var alphaBottom = a10 + (a11 - a10) * fx;
                                alpha *= (alphaTop + (alphaBottom - alphaTop) * fy) * (1f / 255f);

                                if (alpha > 0.0001f)
                                {
                                    texR = Interpolate((int)((p00 >> 16) & 0xFF), (int)((p01 >> 16) & 0xFF),
                                        (int)((p10 >> 16) & 0xFF), (int)((p11 >> 16) & 0xFF), fx, fy) * (1f / 255f);
                                    texG = Interpolate((int)((p00 >> 8) & 0xFF), (int)((p01 >> 8) & 0xFF),
                                        (int)((p10 >> 8) & 0xFF), (int)((p11 >> 8) & 0xFF), fx, fy) * (1f / 255f);
                                    texB = Interpolate((int)(p00 & 0xFF), (int)(p01 & 0xFF),
                                        (int)(p10 & 0xFF), (int)(p11 & 0xFF), fx, fy) * (1f / 255f);
                                }
                                else
                                {
                                    alpha = 0f;
                                }
                            }

                            // 官方 PixelNormal：tex.rgb *= MultiplyColor.rgb；再和 ScreenColor.rgb 做 screen；
                            // color.rgb *= color.a（预乘）→ 本例 = screen 后颜色 × alpha
                            if (alpha > 0.0001f)
                            {
                                var r = texR * mulR;
                                var g = texG * mulG;
                                var b = texB * mulB;
                                r = r + scrR - r * scrR;
                                g = g + scrG - g * scrG;
                                b = b + scrB - b * scrB;

                                var srcR = r * alpha;
                                var srcG = g * alpha;
                                var srcB = b * alpha;

                                if (blend == 0)
                                {
                                    if (alpha >= 0.999f)
                                    {
                                        // 不透明快速路径：Normal + 不透明度 1 时结果直接覆盖（省掉读-乘-加）
                                        buffer[localFloat] = srcR;
                                        buffer[localFloat + 1] = srcG;
                                        buffer[localFloat + 2] = srcB;
                                        buffer[localFloat + 3] = 1f;
                                    }
                                    else
                                    {
                                        var inverseAlpha = 1f - alpha;
                                        buffer[localFloat] = srcR + buffer[localFloat] * inverseAlpha;
                                        buffer[localFloat + 1] = srcG + buffer[localFloat + 1] * inverseAlpha;
                                        buffer[localFloat + 2] = srcB + buffer[localFloat + 2] * inverseAlpha;
                                        buffer[localFloat + 3] = alpha + buffer[localFloat + 3] * inverseAlpha;
                                    }
                                }
                                else if (blend == 1)
                                {
                                    // 加算：RGB 相加，alpha 不变（官方 SrcAlpha=ZERO / DestAlpha=ONE）
                                    buffer[localFloat] += srcR;
                                    buffer[localFloat + 1] += srcG;
                                    buffer[localFloat + 2] += srcB;
                                }
                                else
                                {
                                    // 乘算：dst = src*dst + dst*(1-srcA)，alpha 不变
                                    var inverseAlpha = 1f - alpha;
                                    buffer[localFloat] = srcR * buffer[localFloat] + buffer[localFloat] * inverseAlpha;
                                    buffer[localFloat + 1] = srcG * buffer[localFloat + 1] + buffer[localFloat + 1] * inverseAlpha;
                                    buffer[localFloat + 2] = srcB * buffer[localFloat + 2] + buffer[localFloat + 2] * inverseAlpha;
                                }

                                filled++;
                            }
                        }
                    }

                    w0 += tri.W0Dx;
                    w1 += tri.W1Dx;
                    u += tri.UDx;
                    v += tri.VDx;
                    localFloat += 4;
                    maskIndex++;
                }
                }

                w0Row += tri.W0Dy;
                w1Row += tri.W1Dy;
                uRow += tri.UDy;
                vRow += tri.VDy;
            }
        }
    }

    /// <summary>把 tile 的 float 缓冲（预乘 RGBA，0~1）转成 Pbgra32 写进输出缓冲。</summary>
    private static void WriteTile(uint* output, float* buffer, int originX, int originY, int tileWidth, int tileHeight, int width)
    {
        for (var y = 0; y < tileHeight; y++)
        {
            var destination = (originY + y) * width + originX;
            var source = y * tileWidth * 4;
            for (var x = 0; x < tileWidth; x++)
            {
                var a = ToByte(buffer[source + 3]);
                var r = ToByte(buffer[source]);
                var g = ToByte(buffer[source + 1]);
                var b = ToByte(buffer[source + 2]);
                output[destination + x] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                source += 4;
            }
        }
    }

    /// <summary>float 0~1 → byte 0~255（四舍五入并夹取）。</summary>
    private static byte ToByte(float value)
    {
        var scaled = (int)(value * 255f + 0.5f);
        if (scaled <= 0) return 0;
        if (scaled >= 255) return 255;
        return (byte)scaled;
    }

    // =====================================================================================
    //  三角形初始化
    // =====================================================================================

    /// <summary>
    /// 准备一个三角形：统一成「面积为正」的顶点顺序（模型全部网格双面，不剔除背面），
    /// 算裁剪后的像素包围盒、边函数增量、UV 梯度。
    /// </summary>
    private static bool SetupTriangle(
        float ax, float ay, float bx, float by, float cx, float cy,
        float u0, float v0, float u1, float v1, float u2, float v2,
        int clipX0, int clipY0, int clipX1, int clipY1, out TriSetup tri)
    {
        tri = default;
        var area2 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (!(MathF.Abs(area2) > 1e-6f)) return false;

        if (area2 < 0f)
        {
            // 交换 b / c 把面积转正（双面渲染：不剔背面，直接统一方向）
            (bx, cx) = (cx, bx);
            (by, cy) = (cy, by);
            (u1, u2) = (u2, u1);
            (v1, v2) = (v2, v1);
            area2 = -area2;
        }

        // 像素包围盒（像素中心 = 整数 + 0.5）
        var minX = MathF.Min(ax, MathF.Min(bx, cx));
        var minY = MathF.Min(ay, MathF.Min(by, cy));
        var maxX = MathF.Max(ax, MathF.Max(bx, cx));
        var maxY = MathF.Max(ay, MathF.Max(by, cy));

        var x0 = (int)MathF.Ceiling(minX - 0.5f);
        var y0 = (int)MathF.Ceiling(minY - 0.5f);
        var x1 = (int)MathF.Floor(maxX - 0.5f) + 1;
        var y1 = (int)MathF.Floor(maxY - 0.5f) + 1;
        if (x0 < clipX0) x0 = clipX0;
        if (y0 < clipY0) y0 = clipY0;
        if (x1 > clipX1) x1 = clipX1;
        if (y1 > clipY1) y1 = clipY1;
        if (x0 >= x1 || y0 >= y1) return false;

        // 边函数：w0 = edge(b,c,p)，w1 = edge(c,a,p)，w2 = area2 - w0 - w1
        var px = x0 + 0.5f;
        var py = y0 + 0.5f;
        var w0 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
        var w1 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);

        var invArea = 1f / area2;
        var w0Dx = by - cy;
        var w1Dx = cy - ay;
        var w0Dy = cx - bx;
        var w1Dy = ax - cx;

        var du0 = u0 - u2;
        var du1 = u1 - u2;
        var dv0 = v0 - v2;
        var dv1 = v1 - v2;
        var w2 = area2 - w0 - w1;

        tri.X0 = x0;
        tri.X1 = x1;
        tri.Y0 = y0;
        tri.Y1 = y1;
        tri.Area2 = area2;
        tri.W0 = w0;
        tri.W1 = w1;
        tri.W0Dx = w0Dx;
        tri.W1Dx = w1Dx;
        tri.W0Dy = w0Dy;
        tri.W1Dy = w1Dy;
        tri.UStart = (w0 * u0 + w1 * u1 + w2 * u2) * invArea;
        tri.VStart = (w0 * v0 + w1 * v1 + w2 * v2) * invArea;
        tri.UDx = (w0Dx * du0 + w1Dx * du1) * invArea;
        tri.VDx = (w0Dx * dv0 + w1Dx * dv1) * invArea;
        tri.UDy = (w0Dy * du0 + w1Dy * du1) * invArea;
        tri.VDy = (w0Dy * dv0 + w1Dy * dv1) * invArea;

        // 三条边的行内 x 边界（w2 = area2 - w0 - w1）
        SetupEdge(w0, w0Dx, w0Dy, x0, out tri.Bound0Dir, out tri.Bound0X, out tri.Bound0XStep, out tri.Edge0W, out tri.Edge0WStep);
        SetupEdge(w1, w1Dx, w1Dy, x0, out tri.Bound1Dir, out tri.Bound1X, out tri.Bound1XStep, out tri.Edge1W, out tri.Edge1WStep);
        SetupEdge(w2, -w0Dx - w1Dx, -w0Dy - w1Dy, x0, out tri.Bound2Dir, out tri.Bound2X, out tri.Bound2XStep, out tri.Edge2W, out tri.Edge2WStep);
        return true;
    }

    /// <summary>
    /// 算一条边（<c>w(p) = w + dwDx*(px-x0) + dwDy*(py-y0)</c>）的行内 x 边界：
    /// 下界（dwDx &gt; 0）/ 上界（dwDx &lt; 0）/ 与 x 无关（整行判断 w &gt;= 0）。
    /// </summary>
    private static void SetupEdge(float w, float dwDx, float dwDy, int x0,
        out int dir, out float boundX, out float boundXStep, out float edgeW, out float edgeWStep)
    {
        edgeW = w;
        edgeWStep = dwDy;
        if (dwDx > 1e-12f)
        {
            dir = 1;
            boundX = x0 - w / dwDx;
            boundXStep = -dwDy / dwDx;
        }
        else if (dwDx < -1e-12f)
        {
            dir = -1;
            boundX = x0 - w / dwDx;
            boundXStep = -dwDy / dwDx;
        }
        else
        {
            dir = 0;
            boundX = 0f;
            boundXStep = 0f;
        }
    }

    /// <summary>用三条边的行内边界求本行像素 x 区间 [lo, hi)（本行完全在三角形外时返回 false）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ComputeRowSpan(ref TriSetup tri, out int lo, out int hi)
    {
        lo = tri.X0;
        hi = tri.X1;
        var empty = false;

        if (tri.Bound0Dir > 0)
        {
            var b = tri.Bound0X;
            if (b > lo) { var c = b < hi ? (int)MathF.Ceiling(b - 0.001f) : hi; if (c > lo) lo = c; }
        }
        else if (tri.Bound0Dir < 0)
        {
            var b = tri.Bound0X;
            if (b < hi) { var f = b > lo ? (int)MathF.Floor(b + 0.001f) + 1 : lo; if (f < hi) hi = f; }
        }
        else if (tri.Edge0W < 0f) empty = true;

        if (tri.Bound1Dir > 0)
        {
            var b = tri.Bound1X;
            if (b > lo) { var c = b < hi ? (int)MathF.Ceiling(b - 0.001f) : hi; if (c > lo) lo = c; }
        }
        else if (tri.Bound1Dir < 0)
        {
            var b = tri.Bound1X;
            if (b < hi) { var f = b > lo ? (int)MathF.Floor(b + 0.001f) + 1 : lo; if (f < hi) hi = f; }
        }
        else if (tri.Edge1W < 0f) empty = true;

        if (tri.Bound2Dir > 0)
        {
            var b = tri.Bound2X;
            if (b > lo) { var c = b < hi ? (int)MathF.Ceiling(b - 0.001f) : hi; if (c > lo) lo = c; }
        }
        else if (tri.Bound2Dir < 0)
        {
            var b = tri.Bound2X;
            if (b < hi) { var f = b > lo ? (int)MathF.Floor(b + 0.001f) + 1 : lo; if (f < hi) hi = f; }
        }
        else if (tri.Edge2W < 0f) empty = true;

        tri.Bound0X += tri.Bound0XStep;
        tri.Bound1X += tri.Bound1XStep;
        tri.Bound2X += tri.Bound2XStep;
        tri.Edge0W += tri.Edge0WStep;
        tri.Edge1W += tri.Edge1WStep;
        tri.Edge2W += tri.Edge2WStep;

        return !empty && lo < hi;
    }

    // =====================================================================================
    //  纹理采样（双线性，非预乘；uv 的 v 轴按官方 shader 翻转成图片行方向）
    // =====================================================================================

    /// <summary>双线性采样 alpha（0~1）。蒙版 pass 只关心 alpha，省掉 RGB 插值。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinearAlpha(uint[] pixels, int width, int height,
        bool powerOfTwo, int widthMask, int heightMask, float u, float v)
    {
        var texX = u * width - 0.5f;
        var texY = (1f - v) * height - 0.5f;

        var itx = (int)texX;
        var fx = texX - itx;
        if (fx < 0f) { itx--; fx += 1f; }
        var ity = (int)texY;
        var fy = texY - ity;
        if (fy < 0f) { ity--; fy += 1f; }

        int x0, x1, y0, y1;
        if (powerOfTwo)
        {
            x0 = itx & widthMask;
            x1 = (itx + 1) & widthMask;
            y0 = ity & heightMask;
            y1 = (ity + 1) & heightMask;
        }
        else
        {
            if (itx < 0) { x0 = width - 1; x1 = 0; }
            else if (itx >= width) { x0 = 0; x1 = width > 1 ? 1 : 0; }
            else { x0 = itx; x1 = itx + 1 == width ? 0 : itx + 1; }
            if (ity < 0) { y0 = height - 1; y1 = 0; }
            else if (ity >= height) { y0 = 0; y1 = height > 1 ? 1 : 0; }
            else { y0 = ity; y1 = ity + 1 == height ? 0 : ity + 1; }
        }

        var row0 = y0 * width;
        var row1 = y1 * width;
        var a00 = (int)(pixels[row0 + x0] >> 24);
        var a01 = (int)(pixels[row0 + x1] >> 24);
        var a10 = (int)(pixels[row1 + x0] >> 24);
        var a11 = (int)(pixels[row1 + x1] >> 24);
        return Interpolate(a00, a01, a10, a11, fx, fy) * (1f / 255f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Interpolate(int c00, int c01, int c10, int c11, float fx, float fy)
    {
        var top = c00 + (c01 - c00) * fx;
        var bottom = c10 + (c11 - c10) * fx;
        return top + (bottom - top) * fy;
    }
}
