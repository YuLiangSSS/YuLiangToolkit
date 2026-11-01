using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.Live2D.Interop;

/// <summary>
/// Live2D Cubism Core 原生库（<c>Live2DCubismCore.dll</c>，官方 Cubism SDK for Native 5-r.1）的 P/Invoke 声明。
///
/// <para><b>这是整个 Live2D 工具的地基</b>：moc3 的加载 / 参数求值 / 网格变形全部由这个库完成，
/// C# 只负责「设参数 → Update → 读变形后的顶点」。所有函数都是 Cdecl（x64 上 cdecl/stdcall 相同）；
/// 返回指针的数组（<c>const T**</c>）要自己读指针表，别直接当二维数组用。</para>
///
/// <para><b>内存对齐</b>：moc 数据要求 64 字节对齐、model 工作内存要求 16 字节对齐
/// （见 <see cref="CubismMoc"/> / <see cref="CubismModel"/>，用 <see cref="NativeMemory.AlignedAlloc"/>）。</para>
///
/// <para>官方文档：<see href="https://docs.live2d.com/en/cubism-sdk-manual/"/>；头文件对照见
/// <c>ThirdParty\Live2DCubismCore\Live2DCubismCore.h</c>。</para>
/// </summary>
internal static unsafe class CubismCore
{
    private const string Dll = "Live2DCubismCore.dll";

    /// <summary>日志回调（C 侧传入 UTF-8 字符串指针）。</summary>
    public delegate void LogFunction(IntPtr message);

    // 保持委托实例存活：传给原生库的函数指针一旦被 GC 回收就是野指针。
    private static LogFunction? _logHandler;
    private static Action<string>? _logSink;

    /// <summary>注册 Core 的日志输出（null = 关闭）。返回的委托必须保持存活，本类已持有。</summary>
    public static void SetLogHandler(Action<string>? sink)
    {
        _logSink = sink;
        if (sink is null)
        {
            csmSetLogFunction(null);
            _logHandler = null;
            return;
        }

        _logHandler = message =>
        {
            try
            {
                var text = Marshal.PtrToStringUTF8(message) ?? "";
                if (text.Length > 0) _logSink?.Invoke(text);
            }
            catch
            {
                // 日志回调里绝不抛异常
            }
        };
        csmSetLogFunction(_logHandler);
    }

    // ---------- 版本 ----------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint csmGetVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint csmGetLatestMocVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint csmGetMocVersion(IntPtr address, uint size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmHasMocConsistency(IntPtr address, uint size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern LogFunction? csmGetLogFunction();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmSetLogFunction(LogFunction? handler);

    // ---------- moc / model ----------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmReviveMocInPlace(IntPtr address, uint size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint csmGetSizeofModel(IntPtr moc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmInitializeModelInPlace(IntPtr moc, IntPtr address, uint size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmUpdateModel(IntPtr model);

    /// <summary>画布信息：两个 csmVector2（各 8 字节）+ 一个 float。用已分配的缓冲区接，别用 out 结构体。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmReadCanvasInfo(IntPtr model, IntPtr sizeInPixels, IntPtr originInPixels, IntPtr pixelsPerUnit);

    // ---------- 参数 ----------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetParameterCount(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterIds(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterTypes(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterMinimumValues(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterMaximumValues(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterDefaultValues(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterValues(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterKeyCounts(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetParameterKeyValues(IntPtr model);

    // ---------- 部件 ----------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetPartCount(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetPartIds(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetPartOpacities(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetPartParentPartIndices(IntPtr model);

    // ---------- 可绘制对象（网格） ----------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetDrawableCount(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableIds(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableConstantFlags(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableDynamicFlags(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableTextureIndices(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableDrawOrders(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableRenderOrders(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableOpacities(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableMaskCounts(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableMasks(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableVertexCounts(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableVertexPositions(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableVertexUvs(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableIndexCounts(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableIndices(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableMultiplyColors(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableScreenColors(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr csmGetDrawableParentPartIndices(IntPtr model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmResetDrawableDynamicFlags(IntPtr model);
}
