using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace YuLiangToolkit.Core.Input;

/// <summary>快捷键被按下时回调（在 UI 线程触发）。</summary>
/// <param name="binding">哪一条绑定触发了。</param>
public delegate void ShortcutHandler(ShortcutBinding binding);

/// <summary>
/// 一条「已注册到引擎」的快捷键绑定。
///
/// <para>引擎按它决定用哪条通道：<see cref="Kind"/> = <see cref="ShortcutKind.Single"/> 且
/// <see cref="Primary"/> 恰好一个主键 → Win32 <c>RegisterHotKey</c>；
/// 否则（多主键同时按 / 分步）走低级键盘钩子。</para>
/// </summary>
public sealed class ShortcutBinding
{
    internal ShortcutBinding(int id, ShortcutKind kind, KeyCombo primary, KeyCombo? secondary, ShortcutHandler handler)
    {
        Id = id;
        Kind = kind;
        Primary = primary;
        Secondary = secondary;
        Handler = handler;
    }

    /// <summary>引擎内部的绑定号（同一绑定重新注册时保持不变）。</summary>
    public int Id { get; }

    /// <summary>单步 / 分步。</summary>
    public ShortcutKind Kind { get; }

    /// <summary>第一步（单步就是唯一那一步）。</summary>
    public KeyCombo Primary { get; }

    /// <summary>第二步（仅 <see cref="ShortcutKind.Sequence"/> 有值）。</summary>
    public KeyCombo? Secondary { get; }

    internal ShortcutHandler Handler { get; }

    /// <summary>这条绑定是否需要键盘钩子（多主键同时按 / 任一步含多主键）。</summary>
    internal bool NeedsHook => Kind == ShortcutKind.Sequence
                               || !Primary.IsRegisterHotKeyCompatible
                               || Primary.KeyCount > 4;
}

/// <summary>
/// 快捷键体系里「全程序统一」的那几个数字，集中一处定义（设置页、配置类、引擎都引它，
/// 避免三处各写一套默认值 / 范围）。
/// </summary>
public static class ShortcutTiming
{
    /// <summary>分步快捷键两步之间的默认等待（毫秒）。5 秒：够从容地按第二步，又不至于等太久。</summary>
    public const int DefaultSequenceTimeoutMs = 5000;

    /// <summary>可调范围下限（毫秒）。</summary>
    public const int MinSequenceTimeoutMs = 500;

    /// <summary>可调范围上限（毫秒）。</summary>
    public const int MaxSequenceTimeoutMs = 30000;

    /// <summary>0 是否表示「不限时」。</summary>
    public const bool UnlimitedIsZero = true;

    /// <summary>把「用户选择的值」规范成内部值：0（或负数）= 不限时，其余夹到范围内。</summary>
    public static int Normalize(int value)
        => value <= 0 ? 0 : Math.Clamp(value, MinSequenceTimeoutMs, MaxSequenceTimeoutMs);

    /// <summary>给人看的写法：「5.0 秒」/「不限时」。</summary>
    public static string Describe(int value)
        => value <= 0 ? "不限时" : $"{value / 1000.0:0.#} 秒";
}

/// <summary>快捷键形态。</summary>
public enum ShortcutKind
{
    /// <summary>同时按下这几组键（<c>Ctrl+U+K</c>）。</summary>
    Single,

    /// <summary>分两步按（先 <c>Ctrl+U</c>、再 <c>Ctrl+K</c>）。</summary>
    Sequence,
}

/// <summary>
/// 「分步快捷键等第二步时按错了键」的信息：实际按的组合 + 本来该按的组合。
///
/// <para>界面用它在屏幕角落提示「未被识别：…」—— 那一下已经被吞掉了，
/// 不给提示的话用户会以为键盘失灵。</para>
/// </summary>
/// <param name="actual">实际按下的组合（含修饰键）。</param>
/// <param name="expected">本来该按的第二步。</param>
public sealed class SequenceRejectedEventArgs(KeyCombo actual, KeyCombo? expected) : EventArgs
{
    /// <summary>实际按下的组合。</summary>
    public KeyCombo Actual { get; } = actual;

    /// <summary>本来该按的第二步。</summary>
    public KeyCombo? Expected { get; } = expected;

    /// <summary>给界面直接显示的一句话。</summary>
    public string Describe()
        => Expected is { IsEmpty: false } expected
            ? $"未被识别的快捷键：{Actual}（这里应该按 {expected}）"
            : $"未被识别的快捷键：{Actual}";
}

/// <summary>
/// 全局快捷键引擎：把「同时按 / 分步」两种形态的快捷键注册到系统，并按下的瞬间回调。
///
/// <para><b>为什么是混合引擎</b>：Win32 <c>RegisterHotKey</c> 只能表达「修饰键 + 1 个主键」，
/// 而本项目的「合并」会把 <c>Ctrl+U</c> + <c>Ctrl+K</c> 合成 <c>Ctrl+U+K</c>（1 修饰 + 2 主键），
/// 这是 <c>RegisterHotKey</c> 做不到的。所以：</para>
/// <list type="bullet">
///   <item><b>通道 A</b>：单主键组合走 <c>RegisterHotKey</c> —— 前台是管理员窗口时照样生效，且不会误吞键；</item>
///   <item><b>通道 B</b>：多主键组合 / 分步走低级键盘钩子 <c>WH_KEYBOARD_LL</c> —— 能识别任意集合，
///         但<b>前台是管理员权限窗口时收不到</b>（Windows 的 UIPI 限制，任何非驱动级钩子都一样）。</item>
/// </list>
///
/// <para><b>分步的吞键规则</b>：第一步被完整按下后<b>吞掉</b>（不然在编辑器里会既触发快捷键又打出一个字符）；
/// 等第二步时按对了 → 触发并吞掉；按了别的非修饰键 → <b>也吞掉</b>并抛
/// <c>SequenceRejected</c>（让界面提示「没认出来」，免得用户觉得键盘坏了）；超时 → 作废并抛
/// <c>SequenceCancelled</c>。等待时长见 <see cref="SequenceTimeout"/>（可设成不限时）。</para>
///
/// <para><b>线程</b>：所有公开方法都要在 UI 线程调用（<c>RegisterHotKey</c> 必须与消息窗同线程）；
/// 从后台线程调用会自动转回 UI 线程。回调也在 UI 线程。</para>
///
/// <para><b>失败不抛异常</b>：<see cref="Register"/> 返回 false 并用中文说明原因
/// （1409 = 组合已被其它程序占用）。</para>
/// </summary>
public sealed class GlobalShortcutEngine : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    // 分步快捷键两步之间的默认等待（毫秒）；真正生效的值由外壳写进 SequenceTimeout
    private const int DefaultSequenceTimeoutMs = ShortcutTiming.DefaultSequenceTimeoutMs;

    private readonly object _gate = new();

    /// <summary>通道 A 的绑定：热键 id → 回调。</summary>
    private readonly Dictionary<int, ShortcutBinding> _registerHotKeyBindings = new();

    /// <summary>通道 B 的绑定（含分步）。</summary>
    private readonly List<ShortcutBinding> _hookBindings = new();

    /// <summary>当前按住的键（钩子通道用；键码已归一）。</summary>
    private readonly HashSet<int> _pressed = new();

    /// <summary>当前按住的主键（不含修饰键），用于判定「主键集合完全一致」。</summary>
    private readonly HashSet<int> _pressedKeys = new();

    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private IntPtr _keyboardHook;
    private LowLevelKeyboardProc? _keyboardProc;   // 委托必须保活，否则会被 GC 回收导致钩子崩
    private DispatcherTimer? _sequenceTimer;

    private ShortcutBinding? _pendingSequence;
    private bool _disposed;

    /// <summary>最近一次注册失败的原因（成功时清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 分步快捷键第一步被识别、开始等第二步时触发（界面据此弹一个「已识别第一步」的轻提示）。
    /// 在 UI 线程触发。
    /// </summary>
    public event EventHandler<KeyCombo>? SequenceStarted;

    /// <summary>分步快捷键作废（超时）时触发，界面可以收起那条「等第二步」的提示。</summary>
    public event EventHandler? SequenceCancelled;

    /// <summary>
    /// 分步快捷键**等第二步时按错了键**：这次作废，界面据此提示「没认出来」。
    /// <para>那一下的按键已经被吞掉（不会漏给前台程序），所以一定要让用户知道发生了什么。</para>
    /// </summary>
    public event EventHandler<SequenceRejectedEventArgs>? SequenceRejected;

    /// <summary>
    /// 分步快捷键两步之间的等待时长（毫秒）。**这是全程序统一的一项设置**（主页设置 → 快捷键里调），
    /// 不是每个工具各自的：改了之后对所有分步快捷键立即生效。
    ///
    /// <para><b>0 = 不限时</b>：按下第一步后一直等，直到按对第二步、或按了不匹配的键才结束
    /// （代价：键盘钩子会一直挂着，且第一步的输入已经被吞掉，忘了自己在等第二步时会觉得键盘不对劲，
    /// 所以界面上会明确提示「正在等第二步」）。</para>
    ///
    /// <para>非 0 时夹到 500~30000ms。</para>
    /// </summary>
    public int SequenceTimeout
    {
        get => _sequenceTimeoutMs;
        set => _sequenceTimeoutMs = ShortcutTiming.Normalize(value);
    }

    /// <summary>现在是不是「不限时等待」模式。</summary>
    public bool IsSequenceTimeoutUnlimited => _sequenceTimeoutMs <= 0;

    private int _sequenceTimeoutMs = ShortcutTiming.DefaultSequenceTimeoutMs;

    // ================================================================== 注册 / 注销

    /// <summary>注册一条快捷键。<paramref name="id"/> 相同的旧绑定会先被移除（改键走这里）。</summary>
    /// <param name="id">调用方给的稳定编号（同一个调用方内唯一）。</param>
    /// <param name="kind">单步 / 分步。</param>
    /// <param name="primary">第一步（单步就是唯一那一步）。</param>
    /// <param name="secondary">第二步（单步传 null）。</param>
    /// <param name="handler">按下后的回调（UI 线程）。</param>
    /// <param name="error">失败原因（成功为 null）。</param>
    public bool Register(int id, ShortcutKind kind, KeyCombo primary, KeyCombo? secondary,
        ShortcutHandler handler, out string? error)
    {
        error = null;
        LastError = null;

        if (_disposed)
        {
            error = "快捷键引擎已释放，不能继续注册";
            LastError = error;
            return false;
        }

        if (handler is null)
        {
            error = "快捷键回调为空";
            LastError = error;
            return false;
        }

        // 合法性：不合法就别进入系统（否则会变成「按了没反应」的哑快捷键）
        var primaryError = primary.Validate();
        if (primaryError.Length > 0)
        {
            error = primaryError;
            LastError = error;
            return false;
        }

        if (kind == ShortcutKind.Sequence)
        {
            if (secondary is null || secondary.IsEmpty)
            {
                error = "分步快捷键缺少第二步";
                LastError = error;
                return false;
            }

            var secondaryError = secondary.Validate();
            if (secondaryError.Length > 0)
            {
                error = "第二步：" + secondaryError;
                LastError = error;
                return false;
            }
        }

        // 必须在 UI 线程（消息窗所属线程）操作
        var dispatcher = ResolveDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                error = "界面线程已退出，无法注册快捷键";
                LastError = error;
                return false;
            }

            try
            {
                var result = dispatcher.Invoke(() => RegisterCore(id, kind, primary, secondary, handler));
                error = result.Error;
                LastError = result.Error;
                return result.Ok;
            }
            catch (Exception ex)
            {
                error = "转到 UI 线程注册快捷键失败：" + ex.Message;
                LastError = error;
                return false;
            }
        }

        var local = RegisterCore(id, kind, primary, secondary, handler);
        error = local.Error;
        LastError = local.Error;
        return local.Ok;
    }

    /// <summary>取消注册一条快捷键（没注册过也安全）。</summary>
    public void Unregister(int id)
    {
        RunOnUi(() => UnregisterCore(id));
    }

    /// <summary>取消全部注册（换工具配置、整体禁用时用）。</summary>
    public void UnregisterAll()
    {
        RunOnUi(() =>
        {
            foreach (var id in _registerHotKeyBindings.Keys.ToList()) UnregisterCore(id);
            _hookBindings.Clear();
            ResetSequence();
            UpdateKeyboardHook();
        });
    }

    /// <summary>某个编号当前是否已注册（不含「注册失败」的）。</summary>
    public bool IsRegistered(int id)
    {
        lock (_gate)
        {
            return _registerHotKeyBindings.ContainsKey(id)
                   || _hookBindings.Any(b => b.Id == id);
        }
    }

    private (bool Ok, string? Error) RegisterCore(int id, ShortcutKind kind, KeyCombo primary,
        KeyCombo? secondary, ShortcutHandler handler)
    {
        try
        {
            // 同一个 id：先移除旧的，避免「换键后旧组合还生效」
            UnregisterCore(id);

            var binding = new ShortcutBinding(id, kind, primary, secondary, handler);

            if (!binding.NeedsHook)
            {
                var source = EnsureWindow();
                var modifiers = ToWin32Modifiers(primary.Modifiers);
                var key = primary.Keys[0];
                if (!RegisterHotKey(source.Handle, id, modifiers | ModNoRepeat, key))
                {
                    var code = Marshal.GetLastWin32Error();
                    return (false, code == ErrorHotkeyAlreadyRegistered
                        ? $"「{primary}」已被其它程序占用（Win32 错误 {code}）"
                        : $"注册「{primary}」失败（Win32 错误 {code}）");
                }

                lock (_gate) _registerHotKeyBindings[id] = binding;
                return (true, null);
            }

            // 钩子通道：先登记，再确保钩子已经装上
            lock (_gate) _hookBindings.Add(binding);
            UpdateKeyboardHook();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"注册快捷键失败：{ex.Message}");
        }
    }

    private void UnregisterCore(int id)
    {
        ShortcutBinding? hotkeyBinding = null;
        lock (_gate)
        {
            if (_registerHotKeyBindings.TryGetValue(id, out var found))
            {
                hotkeyBinding = found;
                _registerHotKeyBindings.Remove(id);
            }
            _hookBindings.RemoveAll(b => b.Id == id);
        }

        if (hotkeyBinding is not null)
        {
            try
            {
                var source = _source;
                if (source is not null && source.Handle != IntPtr.Zero) UnregisterHotKey(source.Handle, id);
            }
            catch { /* 退出流程忽略 */ }
        }

        UpdateKeyboardHook();
    }

    /// <summary>把修饰键集合翻成 Win32 <c>MOD_*</c> 位。</summary>
    private static uint ToWin32Modifiers(IEnumerable<int> modifiers)
    {
        uint result = 0;
        foreach (var modifier in modifiers)
        {
            uint bit = KeyComboNames.NormalizeModifier(modifier) switch
            {
                0x12 => 0x0001u,   // MOD_ALT
                0x11 => 0x0002u,   // MOD_CONTROL
                0x10 => 0x0004u,   // MOD_SHIFT
                0x5B => 0x0008u,   // MOD_WIN
                _ => 0u,
            };
            result |= bit;
        }
        return result;
    }

    // ================================================================== 键盘钩子（通道 B）

    /// <summary>只在真的存在「钩子通道」的绑定时才装钩子，用完立刻卸（不长期挂着拖慢系统键盘）。</summary>
    private void UpdateKeyboardHook()
    {
        var needed = _hookBindings.Count > 0;
        if (needed)
        {
            if (_keyboardHook != IntPtr.Zero) return;

            _keyboardProc ??= KeyboardHookProc;
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                LastError = $"键盘钩子安装失败（Win32 错误 {Marshal.GetLastWin32Error()}），多键组合与分步快捷键不可用";
                _keyboardProc = null;
                return;
            }

            _sequenceTimer ??= new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(SequenceTimeout),
            };
            _sequenceTimer.Tick += (_, _) => CancelSequence();
            return;
        }

        ResetSequence();
        if (_keyboardHook == IntPtr.Zero) return;

        try { UnhookWindowsHookEx(_keyboardHook); } catch { /* 退出流程忽略 */ }
        _keyboardHook = IntPtr.Zero;
        _keyboardProc = null;
        lock (_gate)
        {
            _pressed.Clear();
            _pressedKeys.Clear();
        }
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        try
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var key = NormalizeHookKey((int)data.vkCode);

            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (HandleKeyDown(key)) return (IntPtr)1;   // 吞掉：不传给前台程序
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                HandleKeyUp(key);
            }
        }
        catch
        {
            // 钩子回调里抛异常会直接带崩进程：一律咽掉
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>钩子给的是左右键码，这里归一成通用修饰键，和 <see cref="KeyCombo"/> 的口径一致。</summary>
    private static int NormalizeHookKey(int virtualKey)
    {
        // 小键盘键在低级钩子里带 KEY_EV_EXTENDED 区分，但这里统一按通用键处理即可
        return KeyComboNames.NormalizeModifier(virtualKey);
    }

    /// <summary>返回 true = 吞掉这次按键（不给前台程序）。</summary>
    private bool HandleKeyDown(int key)
    {
        lock (_gate)
        {
            // 按住集合是判定依据：修饰键与主键都要记（否则组合永远匹配不上）
            if (!_pressed.Add(key)) return false;   // 连发（按住不放）不重复触发

            if (!KeyComboNames.IsModifier(key)) _pressedKeys.Add(key);
        }

        // ① 正在等第二步？
        if (_pendingSequence is { Secondary: { } second } pending)
        {
            if (MatchesCurrentCombo(second))
            {
                ResetSequence();
                InvokeHandler(pending);
                return true;   // 第二步也吞掉（与第一步一致，避免前台多打一个字符）
            }

            // 按了不匹配的主键 → 本次分步作废。
            // ⚠ 这一下**也吞掉**（按需求）：既然用户已经在「等第二步」的状态里，
            // 漏给前台就会在编辑器里多打一个字符；宁可吞掉并弹一条「未被识别」的提示。
            if (!KeyComboNames.IsModifier(key))
            {
                var expected = pending.Secondary;
                var actual = KeyCombo.FromKeys(_pressed);   // _pressed 里已经包含刚按下的这个键

                ClearPendingSequence();   // 只清状态，不抛「超时作废」事件
                try { SequenceRejected?.Invoke(this, new SequenceRejectedEventArgs(actual, expected)); }
                catch { /* 提示失败不影响吞键 */ }
                return true;
            }
            return false;
        }

        // ② 先看分步的「第一步」——分步要先吃掉，否则前台会同时收到 Ctrl+K
        ShortcutBinding? sequenceMatch = null;
        lock (_gate)
        {
            sequenceMatch = _hookBindings.FirstOrDefault(b =>
                b.Kind == ShortcutKind.Sequence && MatchesCurrentCombo(b.Primary));
        }

        if (sequenceMatch is not null)
        {
            _pendingSequence = sequenceMatch;

            // 不限时模式下不启动计时器：一直等到用户按对第二步、或按了不匹配的键
            if (!IsSequenceTimeoutUnlimited)
            {
                _sequenceTimer!.Interval = TimeSpan.FromMilliseconds(SequenceTimeout);
                _sequenceTimer.Stop();
                _sequenceTimer.Start();
            }

            try { SequenceStarted?.Invoke(this, sequenceMatch.Primary); } catch { /* 通知失败不影响识别 */ }
            return true;
        }

        // ③ 再找「多主键同时按」的单步绑定
        ShortcutBinding? singleMatch = null;
        lock (_gate)
        {
            singleMatch = _hookBindings.FirstOrDefault(b =>
                b.Kind == ShortcutKind.Single && MatchesCurrentCombo(b.Primary));
        }

        if (singleMatch is not null)
        {
            InvokeHandler(singleMatch);
        }

        return false;
    }

    private void HandleKeyUp(int key)
    {
        lock (_gate)
        {
            _pressed.Remove(key);
            _pressedKeys.Remove(key);
        }
    }

    /// <summary>当前按住的键是不是刚好等于这条组合（修饰键 + 主键都必须全按下，且没有多按别的键）。</summary>
    private bool MatchesCurrentCombo(KeyCombo combo)
    {
        lock (_gate)
        {
            if (combo.IsEmpty) return false;

            var currentKeys = new HashSet<int>(_pressedKeys);

            // 主键必须完全一致（多按任何一个主键都不算命中）
            if (!currentKeys.SetEquals(combo.Keys)) return false;

            // 修饰键：按住里必须包含组合要求的全部修饰键
            foreach (var modifier in combo.Modifiers)
            {
                if (!_pressed.Contains(KeyComboNames.NormalizeModifier(modifier))) return false;
            }
            return true;
        }
    }

    /// <summary>分步超时 / 被打断：回到待命。</summary>
    private void CancelSequence()
    {
        if (_pendingSequence is not null)
        {
            _pendingSequence = null;
            try { SequenceCancelled?.Invoke(this, EventArgs.Empty); } catch { /* 忽略 */ }
        }

        try { _sequenceTimer?.Stop(); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 只清掉「正在等第二步」的状态，**不**抛 <see cref="SequenceCancelled"/>。
    /// <para>用于「按了不匹配的键」：那条路径要抛的是 <see cref="SequenceRejected"/>，
    /// 界面据此换一条「没认出来」的提示，而不是静默收起。</para>
    /// </summary>
    private void ClearPendingSequence()
    {
        _pendingSequence = null;
        try { _sequenceTimer?.Stop(); } catch { /* 忽略 */ }
    }

    private void ResetSequence()
    {
        _pendingSequence = null;
        try { _sequenceTimer?.Stop(); } catch { /* 忽略 */ }
        lock (_gate)
        {
            _pressed.Clear();
            _pressedKeys.Clear();
        }
    }

    private void InvokeHandler(ShortcutBinding binding)
    {
        try { binding.Handler(binding); }
        catch { /* 快捷键回调异常不能漏进消息循环 */ }
    }

    // ================================================================== 消息窗

    private Dispatcher? ResolveDispatcher()
    {
        var app = Application.Current;
        if (app is not null) return app.Dispatcher;
        lock (_gate) return _source?.Dispatcher;
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = ResolveDispatcher();
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try { dispatcher.Invoke(action); } catch { /* 退出流程忽略 */ }
    }

    private HwndSource EnsureWindow()
    {
        if (_source is not null) return _source;

        // 隐藏的顶层窗口：只收 WM_HOTKEY，不抢焦点、不进任务栏
        var source = new HwndSource(new HwndSourceParameters("YuLiangToolkitShortcutWindow")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        });

        _hook = WndProc;
        source.AddHook(_hook);
        _source = source;
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        handled = true;

        ShortcutBinding? binding;
        lock (_gate) _registerHotKeyBindings.TryGetValue(wParam.ToInt32(), out binding);
        if (binding is not null) InvokeHandler(binding);

        return IntPtr.Zero;
    }

    /// <summary>给注册表也算一下「这条组合会不会被别的绑定抢」（纯文本层面的辅助，不做系统探测）。</summary>
    public bool HasHookBinding(int id)
    {
        lock (_gate) return _hookBindings.Any(b => b.Id == id);
    }
    /// <summary>当前有几条绑定走的是键盘钩子（界面提示管理员窗口限制用）。</summary>
    public int HookBindingCount
    {
        get { lock (_gate) return _hookBindings.Count; }
    }

    // ================================================================== 释放

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { UnregisterAll(); } catch { /* 退出流程忽略 */ }

        try
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        }
        catch { /* 忽略 */ }
        _keyboardHook = IntPtr.Zero;
        _keyboardProc = null;

        HwndSource? source;
        HwndSourceHook? hook;
        lock (_gate)
        {
            source = _source;
            hook = _hook;
            _source = null;
            _hook = null;
            _registerHotKeyBindings.Clear();
            _hookBindings.Clear();
        }

        if (source is null) return;
        try { if (hook is not null) source.RemoveHook(hook); } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
    }

    // ================================================================== Win32 声明

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
