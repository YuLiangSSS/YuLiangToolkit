namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 音频设备目录：用 WASAPI 枚举「当前可用的」扬声器 / 麦克风，并给采集 / 播放层打开设备。
///
/// <para><b>为什么要有这一层</b>：<c>WindowsCallMediaFactory</c> 只负责「对接口暴露枚举结果」，
/// 而「枚举端点 → 读友好名 → 打开设备 → 兜底默认设备」这些细节集中放这里，
/// 麦克风和扬声器两个实现就不用各抄一遍（也就不会出现一边报错、一边悄悄回退的不一致行为）。</para>
///
/// <para><b>线程</b>：每次调用都在调用线程上新建并释放 COM 对象（MMDeviceEnumerator 是 Both 套间，
/// 哪个线程建都行）；调用返回后不持有任何 COM 引用。<see cref="OpenDevice"/> 返回的
/// <see cref="IMMDevice"/> 由调用方持有并负责释放。</para>
///
/// <para><b>已知边界</b>：只枚举 <c>DEVICE_STATE_ACTIVE</c>（被禁用 / 拔掉的设备不出现）；
/// 友好名读不到时用端点 Id 尾段拼一个「音频设备 xxxxxxxx」，保证下拉列表里至少不是空白。</para>
/// </summary>
internal static class AudioDeviceCatalog
{
    /// <summary>友好名读不到时的兜底名字前缀。</summary>
    private const string FallbackNamePrefix = "音频设备 ";

    /// <summary>
    /// 枚举某个方向上的音频端点。
    /// </summary>
    /// <param name="dataFlow">
    /// <see cref="WasapiInterop.DataFlowRender"/>（0 = 播放 / 扬声器）或
    /// <see cref="WasapiInterop.DataFlowCapture"/>（1 = 采集 / 麦克风）。
    /// </param>
    /// <returns>设备列表（顺序即系统枚举顺序）；枚举失败时返回空列表，不抛异常。</returns>
    internal static IReadOnlyList<CallDevice> Enumerate(int dataFlow)
    {
        var result = new List<CallDevice>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = WasapiInterop.CreateDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(dataFlow, WasapiInterop.DeviceStateActive, out collection) != 0
                || collection is null)
            {
                return result;
            }

            if (collection.GetCount(out var count) != 0) return result;

            for (var index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(index, out device) != 0 || device is null) continue;

                    string? id = null;
                    try { device.GetId(out id); } catch { /* 失败就当作匿名设备 */ }

                    var name = WasapiInterop.ReadFriendlyName(device);
                    if (string.IsNullOrWhiteSpace(name)) name = FallbackName(id);
                    result.Add(new CallDevice(id ?? "", name!));
                }
                finally
                {
                    WasapiInterop.Release(device);
                }
            }
        }
        catch
        {
            // 设备枚举失败（声卡服务没起来 / 权限问题）就当没有设备：工厂层再兜一层，界面显示空列表。
        }
        finally
        {
            WasapiInterop.Release(collection);
            WasapiInterop.Release(enumerator);
        }

        return result;
    }

    /// <summary>
    /// 打开一个音频端点：优先用界面里选中的 Id，找不到（设备被拔掉 / Id 过期）或没选时
    /// 回退到系统默认端点。
    /// <para>回退到默认设备是刻意的：用户选了个已经拔掉的耳机时，能继续用默认设备说话，
    /// 比直接「通话启动失败」友好。</para>
    /// </summary>
    /// <param name="dataFlow">数据流方向（0 = 播放，1 = 采集）。</param>
    /// <param name="preferredId">界面里选中的端点 Id；空 = 直接用默认设备。</param>
    /// <returns>调用方持有的 IMMDevice；失败返回 null（调用方负责写中文原因）。</returns>
    internal static IMMDevice? OpenDevice(int dataFlow, string? preferredId)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = WasapiInterop.CreateDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(preferredId))
            {
                try
                {
                    if (enumerator.GetDevice(preferredId.Trim(), out var selected) == 0 && selected is not null)
                        return selected;
                }
                catch
                {
                    // Id 无效时 GetDevice 可能抛 COMException：继续走默认设备。
                }
            }

            if (enumerator.GetDefaultAudioEndpoint(dataFlow, WasapiInterop.RoleConsole, out var fallback) == 0
                && fallback is not null)
            {
                return fallback;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            // 设备对象自己带引用计数，枚举器释放后仍然有效（这是 WASAPI 的标准用法）。
            WasapiInterop.Release(enumerator);
        }
    }

    /// <summary>把端点 Id 变成可读的兜底名字：取 Id 里最后一个 GUID 的前 8 位。</summary>
    private static string FallbackName(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return FallbackNamePrefix + "（未知）";

        var id = deviceId.Trim();
        var lastBrace = id.LastIndexOf('{');
        var tail = lastBrace >= 0 && lastBrace < id.Length - 1 ? id[(lastBrace + 1)..] : id;
        tail = tail.TrimEnd('}');
        if (tail.Length > 8) tail = tail[..8];
        return string.IsNullOrWhiteSpace(tail) ? FallbackNamePrefix + "（未知）" : FallbackNamePrefix + tail;
    }
}
