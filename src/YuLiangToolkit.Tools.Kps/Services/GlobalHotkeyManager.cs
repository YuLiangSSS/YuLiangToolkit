namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// 全局热键注册（Win32 <c>RegisterHotKey</c> + 一个隐藏消息窗口）。热键回调在 UI 线程。
///
/// <para>实现约定：<see cref="Register"/> 必须在 UI 线程调用；同一 Id 重复注册先自动注销旧的。</para>
///
/// <para>⚠ 这是给队友实现的契约骨架：骨架阶段 <see cref="Register"/> 返回 false，
/// 界面仍能正常显示（热键不生效而已）。</para>
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _callbacks = new();
    private bool _disposed;

    /// <summary>注册失败原因（最近一次）。</summary>
    public string? LastError { get; private set; }

    /// <summary>注册一个热键；成功返回 true。<paramref name="id"/> 由调用方保证唯一（同一把热键不要重复注册）。</summary>
    public bool Register(int id, HotkeyGesture gesture, Action callback)
    {
        // TODO(队友 kps-input)：实现 RegisterHotKey + 隐藏窗口消息循环。
        _callbacks[id] = callback;
        LastError = "全局热键尚未实现";
        return false;
    }

    /// <summary>注销一个热键（没注册过也安全）。</summary>
    public void Unregister(int id)
    {
        _callbacks.Remove(id);
    }

    /// <summary>注销全部热键。</summary>
    public void UnregisterAll()
    {
        _callbacks.Clear();
    }

    /// <summary>某个 Id 是否已登记（注意：登记不代表系统注册成功，注册成功才返回 true —— 以 Register 返回值为准）。</summary>
    public bool IsRegistered(int id) => _callbacks.ContainsKey(id);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { UnregisterAll(); } catch { /* 退出流程忽略 */ }
    }

    /// <summary>消息窗口收到 <c>WM_HOTKEY</c> 时调用（由 Win32 层实现调用）。</summary>
    internal void InvokeCallback(int id)
    {
        if (_callbacks.TryGetValue(id, out var callback))
        {
            try { callback(); } catch { /* 热键回调异常不能影响消息循环 */ }
        }
    }
}
