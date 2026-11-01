using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.Live2D.Interop;

/// <summary>
/// 一个已初始化的 Live2D 模型（Cubism Core 的 <c>csmModel</c>）：参数 / 部件 / 网格数据的 C# 包装。
///
/// <para><b>每帧流程</b>：先把要改的参数 <see cref="CubismParameter.Value"/> / 部件
/// <see cref="CubismPart.Opacity"/> 写进去 → <see cref="Update"/>（Core 求值 + 变形）→
/// 渲染器读 <see cref="CubismDrawable.VertexPositions"/> 画三角形。</para>
///
/// <para><b>坐标系</b>：顶点位置是「模型单位」，画布宽 = 1.0；转成画布像素用
/// <see cref="UnitsToCanvas"/>（y 轴向上为正，画布像素 y 向下）。</para>
///
/// <para><b>线程</b>：非线程安全，只在 UI 线程用。</para>
/// </summary>
public sealed unsafe class CubismModel : IDisposable
{
    private const int ModelAlignment = 16;

    private readonly CubismMoc _moc;
    private byte* _modelBuffer;
    private IntPtr _model;
    private bool _disposed;

    // 指向 Core 内部指针表的指针（表本身地址稳定，表项内容每次 Update 后要重读）
    private readonly IntPtr _vertexPositionsTable;
    private readonly IntPtr _vertexUvsTable;
    private readonly IntPtr _indicesTable;

    // 参数 / 部件的原始数组（地址稳定，值原地更新）
    private readonly float* _parameterValues;
    private readonly float* _partOpacities;

    // 复合画布信息缓冲区（csmReadCanvasInfo 一次填三个值）
    private readonly IntPtr _canvasBuffer;

    private readonly int[] _renderOrderKeys;
    private readonly int[] _renderOrderIndices;
    private readonly Dictionary<string, CubismParameter> _parameterById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CubismPart> _partById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CubismDrawable> _drawableById = new(StringComparer.Ordinal);

    private CubismModel(CubismMoc moc, IntPtr model, byte* modelBuffer)
    {
        _moc = moc;
        _model = model;
        _modelBuffer = modelBuffer;

        var parameterCount = CubismCore.csmGetParameterCount(model);
        var partCount = CubismCore.csmGetPartCount(model);
        var drawableCount = CubismCore.csmGetDrawableCount(model);

        // ---------- 画布 ----------
        _canvasBuffer = Marshal.AllocHGlobal(24);
        CubismCore.csmReadCanvasInfo(model, _canvasBuffer, _canvasBuffer + 8, _canvasBuffer + 16);
        CanvasWidthPixels = ((float*)_canvasBuffer)[0];
        CanvasHeightPixels = ((float*)_canvasBuffer)[1];
        CanvasOriginX = ((float*)_canvasBuffer)[2];
        CanvasOriginY = ((float*)_canvasBuffer)[3];
        PixelsPerUnit = *(float*)(_canvasBuffer + 16);
        if (PixelsPerUnit <= 0) PixelsPerUnit = 1;

        // ---------- 参数 ----------
        var parameterIds = CubismCore.csmGetParameterIds(model);
        var parameterMins = (float*)CubismCore.csmGetParameterMinimumValues(model);
        var parameterMaxs = (float*)CubismCore.csmGetParameterMaximumValues(model);
        var parameterDefaults = (float*)CubismCore.csmGetParameterDefaultValues(model);
        _parameterValues = (float*)CubismCore.csmGetParameterValues(model);
        Parameters = new CubismParameter[parameterCount];
        for (var i = 0; i < parameterCount; i++)
        {
            var parameter = new CubismParameter(this, i, ReadString(parameterIds, i))
            {
                Minimum = parameterMins[i],
                Maximum = parameterMaxs[i],
                DefaultValue = parameterDefaults[i],
            };
            Parameters[i] = parameter;
            _parameterById[parameter.Id] = parameter;
        }

        // ---------- 部件 ----------
        var partIds = CubismCore.csmGetPartIds(model);
        _partOpacities = (float*)CubismCore.csmGetPartOpacities(model);
        Parts = new CubismPart[partCount];
        for (var i = 0; i < partCount; i++)
        {
            var part = new CubismPart(this, i, ReadString(partIds, i));
            Parts[i] = part;
            _partById[part.Id] = part;
        }

        // ---------- 网格 ----------
        var drawableIds = CubismCore.csmGetDrawableIds(model);
        var constantFlags = (byte*)CubismCore.csmGetDrawableConstantFlags(model);
        var textureIndices = (int*)CubismCore.csmGetDrawableTextureIndices(model);
        var drawOrders = (int*)CubismCore.csmGetDrawableDrawOrders(model);
        var renderOrders = (int*)CubismCore.csmGetDrawableRenderOrders(model);
        var maskCounts = (int*)CubismCore.csmGetDrawableMaskCounts(model);
        var masksTable = CubismCore.csmGetDrawableMasks(model);
        var vertexCounts = (int*)CubismCore.csmGetDrawableVertexCounts(model);
        var indexCounts = (int*)CubismCore.csmGetDrawableIndexCounts(model);
        var multiplyColors = (float*)CubismCore.csmGetDrawableMultiplyColors(model);
        var screenColors = (float*)CubismCore.csmGetDrawableScreenColors(model);
        _vertexPositionsTable = CubismCore.csmGetDrawableVertexPositions(model);
        _vertexUvsTable = CubismCore.csmGetDrawableVertexUvs(model);
        _indicesTable = CubismCore.csmGetDrawableIndices(model);

        Drawables = new CubismDrawable[drawableCount];
        for (var i = 0; i < drawableCount; i++)
        {
            var maskCount = maskCounts[i];
            var maskIndices = maskCount > 0 ? new int[maskCount] : Array.Empty<int>();
            if (maskCount > 0)
            {
                var maskPtr = Marshal.ReadIntPtr(masksTable, i * IntPtr.Size);
                for (var m = 0; m < maskCount; m++) maskIndices[m] = ((int*)maskPtr)[m];
            }

            var drawable = new CubismDrawable(this, i, ReadString(drawableIds, i))
            {
                TextureIndex = textureIndices[i],
                DrawOrder = drawOrders[i],
                RenderOrder = renderOrders[i],
                ConstantFlags = constantFlags[i],
                VertexCount = vertexCounts[i],
                IndexCount = indexCounts[i],
                MaskIndices = maskIndices,
                MultiplyColor = new System.Numerics.Vector4(multiplyColors[i * 4], multiplyColors[i * 4 + 1], multiplyColors[i * 4 + 2], multiplyColors[i * 4 + 3]),
                ScreenColor = new System.Numerics.Vector4(screenColors[i * 4], screenColors[i * 4 + 1], screenColors[i * 4 + 2], screenColors[i * 4 + 3]),
            };
            Drawables[i] = drawable;
            _drawableById[drawable.Id] = drawable;
        }

        _renderOrderKeys = new int[drawableCount];
        _renderOrderIndices = new int[drawableCount];

        TotalVertexCount = 0;
        TotalTriangleCount = 0;
        TexturesUsed = 0;
        foreach (var drawable in Drawables)
        {
            TotalVertexCount += drawable.VertexCount;
            TotalTriangleCount += drawable.IndexCount / 3;
            TexturesUsed = Math.Max(TexturesUsed, drawable.TextureIndex + 1);
        }

        Update();
    }

    /// <summary>从 moc 创建一个模型（自动做一次 <see cref="Update"/> 让数据可用）。</summary>
    public static CubismModel Create(CubismMoc moc)
    {
        ArgumentNullException.ThrowIfNull(moc);
        var size = CubismCore.csmGetSizeofModel(moc.Handle);
        if (size == 0) throw new InvalidOperationException("csmGetSizeofModel 返回 0：moc 无效");

        var buffer = (byte*)NativeMemory.AlignedAlloc(size, ModelAlignment);
        var model = CubismCore.csmInitializeModelInPlace(moc.Handle, (IntPtr)buffer, size);
        if (model == IntPtr.Zero)
        {
            NativeMemory.AlignedFree(buffer);
            throw new InvalidOperationException("csmInitializeModelInPlace 失败：模型初始化不出来");
        }

        return new CubismModel(moc, model, buffer) { ModelSizeBytes = size };
    }

    /// <summary>Core 里的 model 指针。</summary>
    public IntPtr Handle => _model;

    /// <summary>model 工作内存大小（字节）。</summary>
    public uint ModelSizeBytes { get; private init; }

    /// <summary>moc3 格式版本（见 <see cref="CubismMoc.MocVersion"/>）。</summary>
    public uint MocVersion => _moc.MocVersion;

    /// <summary>画布宽度（像素）。</summary>
    public float CanvasWidthPixels { get; }

    /// <summary>画布高度（像素）。</summary>
    public float CanvasHeightPixels { get; }

    /// <summary>模型原点在画布上的 x（像素，通常是画布宽的一半）。</summary>
    public float CanvasOriginX { get; }

    /// <summary>模型原点在画布上的 y（像素）。</summary>
    public float CanvasOriginY { get; }

    /// <summary>1 个模型单位 = 多少画布像素（通常等于画布宽）。</summary>
    public float PixelsPerUnit { get; }

    /// <summary>参数数量。</summary>
    public int ParameterCount => Parameters.Length;

    /// <summary>部件数量。</summary>
    public int PartCount => Parts.Length;

    /// <summary>网格（可绘制对象）数量。</summary>
    public int DrawableCount => Drawables.Length;

    /// <summary>所有网格的顶点总数。</summary>
    public int TotalVertexCount { get; }

    /// <summary>所有网格的三角形总数。</summary>
    public int TotalTriangleCount { get; }

    /// <summary>模型用到的纹理张数。</summary>
    public int TexturesUsed { get; }

    /// <summary>全部参数（顺序与 moc 内一致）。</summary>
    public CubismParameter[] Parameters { get; }

    /// <summary>全部部件（顺序与 moc 内一致）。</summary>
    public CubismPart[] Parts { get; }

    /// <summary>全部网格（顺序 = drawable 下标）。</summary>
    public CubismDrawable[] Drawables { get; }

    /// <summary>当前这一帧应该按什么顺序画：值是 <see cref="Drawables"/> 的下标（每次 <see cref="Update"/> 后刷新）。</summary>
    public ReadOnlySpan<int> RenderOrderedDrawableIndices => _renderOrderIndices;

    /// <summary>最近一次 <see cref="Update"/> 用掉的毫秒数（性能面板用）。</summary>
    public double LastUpdateMilliseconds { get; private set; }

    /// <summary>按 Id 找参数（没有返回 null）。</summary>
    public CubismParameter? FindParameter(string id) => _parameterById.GetValueOrDefault(id);

    /// <summary>按 Id 找部件（没有返回 null）。</summary>
    public CubismPart? FindPart(string id) => _partById.GetValueOrDefault(id);

    /// <summary>按 Id 找网格（没有返回 null）。</summary>
    public CubismDrawable? FindDrawable(string id) => _drawableById.GetValueOrDefault(id);

    /// <summary>把参数恢复成 moc 里的默认值。</summary>
    public void ResetParametersToDefault()
    {
        foreach (var parameter in Parameters) parameter.Value = parameter.DefaultValue;
    }

    /// <summary>把所有部件不透明度恢复成 1（全部显示）。</summary>
    public void ResetPartOpacities()
    {
        foreach (var part in Parts) part.Opacity = 1.0f;
    }

    /// <summary>把模型单位坐标转成画布像素坐标（x 向右、y 向下）。</summary>
    public (float X, float Y) UnitsToCanvas(float x, float y)
        => (CanvasOriginX + x * PixelsPerUnit, CanvasOriginY - y * PixelsPerUnit);

    /// <summary>更新模型：Core 求值 + 网格变形，然后刷新所有动态数据（顶点指针 / 透明度 / 可见性）。</summary>
    public void Update()
    {
        if (_disposed) return;
        var started = Environment.TickCount64;
        CubismCore.csmUpdateModel(_model);

        var dynamicFlags = (byte*)CubismCore.csmGetDrawableDynamicFlags(_model);
        var opacities = (float*)CubismCore.csmGetDrawableOpacities(_model);
        var renderOrders = (int*)CubismCore.csmGetDrawableRenderOrders(_model);

        for (var i = 0; i < Drawables.Length; i++)
        {
            var drawable = Drawables[i];
            drawable.Positions = (float*)Marshal.ReadIntPtr(_vertexPositionsTable, i * IntPtr.Size);
            drawable.Uvs = (float*)Marshal.ReadIntPtr(_vertexUvsTable, i * IntPtr.Size);
            drawable.Indices = (ushort*)Marshal.ReadIntPtr(_indicesTable, i * IntPtr.Size);
            drawable.Opacity = opacities[i];
            drawable.IsVisible = (dynamicFlags[i] & DrawableFlagsIsVisible) != 0;
            drawable.RenderOrder = renderOrders[i];
            _renderOrderKeys[i] = renderOrders[i];
            _renderOrderIndices[i] = i;
        }

        // 渲染顺序：render order 小的先画（Cubism 的最终绘制顺序）
        Array.Sort(_renderOrderKeys, _renderOrderIndices);

        for (var i = 0; i < Parts.Length; i++) Parts[i].Opacity = _partOpacities[i];

        LastUpdateMilliseconds = (Environment.TickCount64 - started);
    }

    private const byte DrawableFlagsIsVisible = 1 << 0;

    internal float GetParameterValue(int index) => _parameterValues[index];

    internal void SetParameterValue(int index, float value)
    {
        // Core 要求参数值在 min ~ max 内（超界行为未定义），这里夹一下
        var parameter = Parameters[index];
        if (value < parameter.Minimum) value = parameter.Minimum;
        else if (value > parameter.Maximum) value = parameter.Maximum;
        _parameterValues[index] = value;
    }

    internal float GetPartOpacity(int index) => _partOpacities[index];

    internal void SetPartOpacity(int index, float value)
        => _partOpacities[index] = value < 0 ? 0 : value > 1 ? 1 : value;

    private static string ReadString(IntPtr stringTable, int index)
        => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(stringTable, index * IntPtr.Size)) ?? "";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_modelBuffer is not null)
        {
            NativeMemory.AlignedFree(_modelBuffer);
            _modelBuffer = null;
        }

        _model = IntPtr.Zero;
        if (_canvasBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_canvasBuffer);
    }
}

/// <summary>一个模型参数（例如 <c>ParamAngleX</c>）。改 <see cref="Value"/> 后下一次 <see cref="CubismModel.Update"/> 生效。</summary>
public sealed class CubismParameter
{
    private readonly CubismModel _model;

    internal CubismParameter(CubismModel model, int index, string id)
    {
        _model = model;
        Index = index;
        Id = id;
    }

    /// <summary>参数下标（moc 内顺序）。</summary>
    public int Index { get; }

    /// <summary>参数 Id（Cubism 参数名，例如 <c>ParamEyeBallX</c>）。</summary>
    public string Id { get; }

    /// <summary>最小值。</summary>
    public float Minimum { get; internal set; }

    /// <summary>最大值。</summary>
    public float Maximum { get; internal set; }

    /// <summary>默认值。</summary>
    public float DefaultValue { get; internal set; }

    /// <summary>当前值（写入会自动夹到 min ~ max）。</summary>
    public float Value
    {
        get => _model.GetParameterValue(Index);
        set => _model.SetParameterValue(Index, value);
    }

    public override string ToString() => $"{Id} = {Value:0.###}";
}

/// <summary>一个模型部件（例如 <c>PartHead</c>）。改 <see cref="Opacity"/> 可以整块淡出 / 隐藏。</summary>
public sealed class CubismPart
{
    private readonly CubismModel _model;

    internal CubismPart(CubismModel model, int index, string id)
    {
        _model = model;
        Index = index;
        Id = id;
    }

    /// <summary>部件下标。</summary>
    public int Index { get; }

    /// <summary>部件 Id。</summary>
    public string Id { get; }

    /// <summary>部件不透明度（0 ~ 1，写入自动夹取）。</summary>
    public float Opacity
    {
        get => _model.GetPartOpacity(Index);
        set => _model.SetPartOpacity(Index, value);
    }

    public override string ToString() => $"{Id} opacity={Opacity:0.###}";
}

/// <summary>
/// 一个可绘制网格（Cubism 的 drawable）：顶点 / UV / 三角形下标 / 蒙版 / 混合模式。
/// 动态数据（顶点位置、透明度、可见性）每次 <see cref="CubismModel.Update"/> 后刷新。
/// </summary>
public sealed unsafe class CubismDrawable
{
    private readonly CubismModel _model;

    internal CubismDrawable(CubismModel model, int index, string id)
    {
        _model = model;
        Index = index;
        Id = id;
    }

    /// <summary>网格下标。</summary>
    public int Index { get; }

    /// <summary>网格 Id（Cubism 的 art mesh / drawable 名）。</summary>
    public string Id { get; }

    /// <summary>使用第几张贴图（model3.json 里的 Textures 下标）。</summary>
    public int TextureIndex { get; internal set; }

    /// <summary>绘制顺序（moc 里的 draw order，仅参考）。</summary>
    public int DrawOrder { get; internal set; }

    /// <summary>当前帧最终渲染顺序（小的先画；每次 Update 刷新）。</summary>
    public int RenderOrder { get; internal set; }

    /// <summary>不随参数变化的标志位（混合模式 / 双面 / 蒙版反转）。</summary>
    public int ConstantFlags { get; internal set; }

    /// <summary>顶点数量。</summary>
    public int VertexCount { get; internal set; }

    /// <summary>三角形下标数量（三角形数 = IndexCount / 3）。</summary>
    public int IndexCount { get; internal set; }

    /// <summary>蒙版网格下标（裁剪它的那些 drawable；空 = 不裁剪）。</summary>
    public int[] MaskIndices { get; internal set; } = Array.Empty<int>();

    /// <summary>当前不透明度（含部件 / 父级影响；每次 Update 刷新）。</summary>
    public float Opacity { get; internal set; }

    /// <summary>当前是否可见（每次 Update 刷新）。</summary>
    public bool IsVisible { get; internal set; }

    /// <summary>颜色乘算（Cubism 4.2+ 的 MultiplyColor；默认白色）。</summary>
    public System.Numerics.Vector4 MultiplyColor { get; internal set; }

    /// <summary>颜色叠加（Cubism 5 的 ScreenColor；默认透明）。</summary>
    public System.Numerics.Vector4 ScreenColor { get; internal set; }

    /// <summary>是否为加算混合。</summary>
    public bool IsAdditive => (ConstantFlags & 0x1) != 0;

    /// <summary>是否为乘算混合。</summary>
    public bool IsMultiplicative => (ConstantFlags & 0x2) != 0;

    /// <summary>是否双面（不剔除背面）。</summary>
    public bool IsDoubleSided => (ConstantFlags & 0x4) != 0;

    /// <summary>蒙版是否取反（画在蒙版外）。</summary>
    public bool IsInvertedMask => (ConstantFlags & 0x8) != 0;

    /// <summary>变形后的顶点位置（模型单位，x/y 交替；每次 Update 后有效）。</summary>
    public float* Positions { get; internal set; }

    /// <summary>顶点 UV（归一化到整张图集的 0~1；每次 Update 后有效）。</summary>
    public float* Uvs { get; internal set; }

    /// <summary>三角形下标（unsigned short；每次 Update 后有效）。</summary>
    public ushort* Indices { get; internal set; }

    /// <summary>顶点位置只读视图（模型单位）。</summary>
    public ReadOnlySpan<float> VertexPositions => Positions is null ? ReadOnlySpan<float>.Empty : new ReadOnlySpan<float>(Positions, VertexCount * 2);

    /// <summary>UV 只读视图。</summary>
    public ReadOnlySpan<float> VertexUvs => Uvs is null ? ReadOnlySpan<float>.Empty : new ReadOnlySpan<float>(Uvs, VertexCount * 2);

    /// <summary>三角形下标只读视图。</summary>
    public ReadOnlySpan<ushort> TriangleIndices => Indices is null ? ReadOnlySpan<ushort>.Empty : new ReadOnlySpan<ushort>(Indices, IndexCount);

    /// <summary>在画布像素坐标系里的包围盒（x 向右、y 向下）。没有顶点时返回 false。</summary>
    public bool TryGetCanvasBounds(out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        if (Positions is null || VertexCount == 0) return false;

        // 先算模型单位包围盒，再换算（换算对 x/y 是独立线性变换，可以先算 bbox）
        float uMinX = float.MaxValue, uMinY = float.MaxValue, uMaxX = float.MinValue, uMaxY = float.MinValue;
        for (var i = 0; i < VertexCount; i++)
        {
            var x = Positions[i * 2];
            var y = Positions[i * 2 + 1];
            if (x < uMinX) uMinX = x;
            if (y < uMinY) uMinY = y;
            if (x > uMaxX) uMaxX = x;
            if (y > uMaxY) uMaxY = y;
        }

        var (x0, y0) = _model.UnitsToCanvas(uMinX, uMaxY);
        var (x1, y1) = _model.UnitsToCanvas(uMaxX, uMinY);
        minX = x0;
        minY = y0;
        maxX = x1;
        maxY = y1;
        return true;
    }

    public override string ToString() => $"{Id} (tex {TextureIndex}, {VertexCount} 顶点)";
}
