namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>一次「真正按下 / 抬起」事件（长按自动重复已在输入层过滤）。</summary>
public sealed class KpsKeyEventArgs : EventArgs
{
    public KpsKeyEventArgs(int virtualKey)
    {
        VirtualKey = virtualKey;
    }

    /// <summary>Windows 虚拟键码。</summary>
    public int VirtualKey { get; }
}

/// <summary>
/// 全局键盘输入监听（Windows Raw Input，<c>RIDEV_INPUTSINK</c> 后台也收得到；失败时退回低级键盘钩子）。
///
/// <para><b>为什么用 Raw Input</b>：很多音游直接用 Raw Input 读键盘，低级钩子会被它们绕过 / 被反作弊拦；
/// DmNote 也是用 Raw Input API（见 docs/tools/kps.md 的来源说明）。</para>
///
/// <para><b>线程</b>：必须<b>在 UI 线程</b> <see cref="Start"/>，事件也在 UI 线程回调（内部隐藏窗口挂在调用线程的消息循环上）。
/// 任意线程可调 <see cref="Stop"/> / <see cref="Dispose"/>（内部不碰 UI 对象）。</para>
///
/// <para><b>自动重复</b>：Windows 长按会重复发 WM_KEYDOWN，但 Raw Input 只报「状态变化」；
/// 这里再用按下集合兜底去重，保证按住不放只算一次按下、抬起时才算一次抬起。</para>
///
/// <para>⚠ 这是给队友实现的契约骨架：<see cref="Start"/> 里要真正 P/Invoke
/// <c>RegisterRawInputDevices</c> + 消息窗口；失败时写 <see cref="LastError"/> 并返回 false。</para>
/// </summary>
public sealed class RawInputKeyboard : IDisposable
{
    private readonly HashSet<int> _pressed = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>真正按下（从「没按」到「按下」的那一次）。</summary>
    public event EventHandler<KpsKeyEventArgs>? KeyDown;

    /// <summary>抬起。</summary>
    public event EventHandler<KpsKeyEventArgs>? KeyUp;

    /// <summary>是否正在监听。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>实际使用的后端名（"Raw Input" / "低级键盘钩子"），给界面显示 / 排错用。</summary>
    public string BackendName { get; private set; } = "未启动";

    /// <summary>最近一次启动失败的原因（成功时清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>开始监听；返回是否成功（失败原因见 <see cref="LastError"/>）。</summary>
    public bool Start()
    {
        // TODO(队友 kps-input)：实现 Raw Input。骨架阶段先返回失败，让界面能显示"监听未启动"。
        LastError = "输入监听尚未实现";
        BackendName = "未启动";
        return false;
    }

    /// <summary>停止监听并释放隐藏窗口 / 钩子。</summary>
    public void Stop()
    {
        IsRunning = false;
        BackendName = "未启动";
        lock (_gate) _pressed.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { /* 退出流程忽略 */ }
    }

    /// <summary>当前按住的键（诊断 / 切回界面时同步按下状态用）。</summary>
    public IReadOnlyCollection<int> PressedKeys
    {
        get { lock (_gate) return _pressed.ToArray(); }
    }

    /// <summary>输入层内部：处理一次按下（去重后发事件）。</summary>
    internal void RaiseKeyDown(int virtualKey)
    {
        if (virtualKey == 0) return;
        lock (_gate)
        {
            if (!_pressed.Add(virtualKey)) return;
        }
        KeyDown?.Invoke(this, new KpsKeyEventArgs(virtualKey));
    }

    /// <summary>输入层内部：处理一次抬起。</summary>
    internal void RaiseKeyUp(int virtualKey)
    {
        if (virtualKey == 0) return;
        lock (_gate)
        {
            if (!_pressed.Remove(virtualKey)) return;
        }
        KeyUp?.Invoke(this, new KpsKeyEventArgs(virtualKey));
    }
}
