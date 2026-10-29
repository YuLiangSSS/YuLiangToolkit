using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Kps.Services;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「KPS 按键统计」输入层自检：热键字符串解析 / 键名映射 / 键音 / Raw Input 注册与真实 SendInput 注入 / 全局热键注册。
///
/// <para>Raw Input 与全局热键需要 WPF 消息循环，本文件自己起 STA 线程 + <see cref="Dispatcher"/> 跑；
/// 注入的是很冷门的 <c>VK_F24</c>，不抢焦点、不影响用户正在做的事。</para>
///
/// <para>环境不支持（SendInput 被安全软件拦、Raw Input 注册失败等）时如实 <c>FAIL</c> 并带上
/// <c>BackendName</c> / <c>LastError</c>，不静默通过。</para>
/// </summary>
public static class KpsInputChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        void C(string name, bool ok, string detail = "") => check("[KPS输入] " + name, ok, detail);

        CheckHotkeyGesture(C);
        CheckKeyNames(C);
        CheckKeySound(C);
        CheckRawInput(C);
        CheckGlobalHotkey(C);
    }

    // ================================================================== 1. 热键解析 / 格式化

    private static void CheckHotkeyGesture(Action<string, bool, string> c)
    {
        void C(string name, bool ok, string detail = "") => c(name, ok, detail);

        C("热键修饰键位与 Win32 MOD_* 一致",
            HotkeyGesture.ModAlt == 0x0001 && HotkeyGesture.ModControl == 0x0002
            && HotkeyGesture.ModShift == 0x0004 && HotkeyGesture.ModWin == 0x0008);

        var ctrlAltKOk = HotkeyGesture.TryParse("Ctrl+Alt+K", out var ctrlAltK);
        C("解析 Ctrl+Alt+K",
            ctrlAltKOk
            && ctrlAltK.Modifiers == (HotkeyGesture.ModControl | HotkeyGesture.ModAlt)
            && ctrlAltK.VirtualKey == 0x4B,
            ctrlAltK.ToString());
        C("格式化 Ctrl+Alt+K（顺序 Ctrl / Alt / Shift / Win）",
            ctrlAltK.ToString() == "Ctrl+Alt+K", ctrlAltK.ToString());

        var ctrlShiftOOk = HotkeyGesture.TryParse("Ctrl+Shift+O", out var ctrlShiftO);
        C("解析 Ctrl+Shift+O",
            ctrlShiftOOk
            && ctrlShiftO.Modifiers == (HotkeyGesture.ModControl | HotkeyGesture.ModShift)
            && ctrlShiftO.VirtualKey == 0x4F,
            ctrlShiftO.ToString());

        var altF4Ok = HotkeyGesture.TryParse("alt+f4", out var altF4);
        C("解析 alt+f4（大小写不敏感）",
            altF4Ok && altF4.Modifiers == HotkeyGesture.ModAlt && altF4.VirtualKey == 0x73
            && altF4.ToString() == "Alt+F4",
            altF4.ToString());

        var chineseOk = HotkeyGesture.TryParse("控制+上档+菜单+空格", out var chinese);
        C("解析中文修饰键 控制+上档+菜单+空格",
            chineseOk
            && chinese.Modifiers == (HotkeyGesture.ModControl | HotkeyGesture.ModShift | HotkeyGesture.ModAlt)
            && chinese.VirtualKey == 0x20,
            chinese.ToString());
        C("中文修饰键格式化往返",
            chineseOk && RoundTrip(chinese), chinese.ToString());

        var ctrlEnterOk = HotkeyGesture.TryParse("ctrl+回车", out var ctrlEnter);
        C("解析中文主键 Ctrl+回车", ctrlEnterOk && ctrlEnter.VirtualKey == 0x0D, ctrlEnter.ToString());

        var punctuationOk = HotkeyGesture.TryParse("Ctrl+;", out var punctuation)
                            && punctuation.VirtualKey == 0xBA;
        C("解析符号标点 Ctrl+;（显示为中文短名）",
            punctuationOk && punctuation.ToString() == "Ctrl+分号" && RoundTrip(punctuation),
            punctuation.ToString());

        // 主键名字里自带「+」：解析要能把它和修饰键分隔符区分开
        var numpadPlusOk = HotkeyGesture.TryParse("Ctrl+小键盘+", out var numpadPlus);
        C("解析主键 小键盘+（名字里带加号）",
            numpadPlusOk && numpadPlus.VirtualKey == 0x6B
            && numpadPlus.ToString() == "Ctrl+小键盘+",
            numpadPlus.ToString());
        C("主键 小键盘+ 格式化往返", numpadPlusOk && RoundTrip(numpadPlus), numpadPlus.ToString());

        var winSpaceOk = HotkeyGesture.TryParse("win+空格", out var winSpace);
        C("解析 Win+空格",
            winSpaceOk && winSpace.Modifiers == HotkeyGesture.ModWin && winSpace.VirtualKey == 0x20,
            winSpace.ToString());

        // 未知键（VK 不在键名表里）：Display 输出 VK_0xNN，也要能解析回来
        var unknownGesture = new HotkeyGesture(HotkeyGesture.ModControl, 0xFE);
        var unknownText = unknownGesture.ToString();
        C("未知键 VK_0xNN 显示与往返",
            unknownText == "Ctrl+VK_0xFE"
            && HotkeyGesture.TryParse(unknownText, out var unknownParsed)
            && unknownParsed == unknownGesture,
            unknownText);

        C("TryParseMainKey 支持 F24 与方向键",
            HotkeyGesture.TryParseMainKey("F24", out var f24) && f24 == 0x87
            && HotkeyGesture.TryParseMainKey("←", out var left) && left == 0x25);
        C("TryParseMainKey 拒绝修饰键（只按 Ctrl 时不算主键）",
            !HotkeyGesture.TryParseMainKey("Shift", out _)
            && !HotkeyGesture.TryParseMainKey("左Ctrl", out _));

        C("非法热键 Ctrl+ 返回 false", !HotkeyGesture.TryParse("Ctrl+", out _));
        C("非法热键 Foo+K 返回 false", !HotkeyGesture.TryParse("Foo+K", out _));
        C("非法热键 Ctrl+Shift（没有主键）返回 false", !HotkeyGesture.TryParse("Ctrl+Shift", out _));
        C("禁用写法：空字符串 / 无 / none / - 都返回 false",
            !HotkeyGesture.TryParse("", out _)
            && !HotkeyGesture.TryParse("   ", out _)
            && !HotkeyGesture.TryParse("无", out _)
            && !HotkeyGesture.TryParse("NoNe", out _)
            && !HotkeyGesture.TryParse("-", out _));
        C("空手势 IsEmpty", default(HotkeyGesture).IsEmpty && new HotkeyGesture(0, 0).IsEmpty);
        C("FromParts 只保留 MOD_* 低四位",
            HotkeyGesture.FromParts(0xFFFFFFFF, 0x41).Modifiers == 0x000F,
            HotkeyGesture.FromParts(0xFFFFFFFF, 0x41).Modifiers.ToString("X"));
        C("空手势 ToString 为空", default(HotkeyGesture).ToString() == "");
    }

    private static bool RoundTrip(HotkeyGesture gesture)
        => HotkeyGesture.TryParse(gesture.ToString(), out var reparsed) && reparsed == gesture;

    // ================================================================== 2. 键名映射

    private static void CheckKeyNames(Action<string, bool, string> c)
    {
        void C(string name, bool ok, string detail = "") => c(name, ok, detail);

        C("键名 字母 / 数字", KeyNames.Display(0x41) == "A" && KeyNames.Display(0x30) == "0");
        C("键名 F1 / F24", KeyNames.Display(0x70) == "F1" && KeyNames.Display(0x87) == "F24");
        C("键名 空格 / 回车 / 退格 / Tab / Esc",
            KeyNames.Display(0x20) == "空格" && KeyNames.Display(0x0D) == "回车"
            && KeyNames.Display(0x08) == "退格" && KeyNames.Display(0x09) == "Tab"
            && KeyNames.Display(0x1B) == "Esc");
        C("键名 方向键",
            KeyNames.Display(0x25) == "←" && KeyNames.Display(0x26) == "↑"
            && KeyNames.Display(0x27) == "→" && KeyNames.Display(0x28) == "↓");
        C("键名 左右 Shift / Ctrl / Alt / Win",
            KeyNames.Display(0xA0) == "左Shift" && KeyNames.Display(0xA1) == "右Shift"
            && KeyNames.Display(0xA2) == "左Ctrl" && KeyNames.Display(0xA3) == "右Ctrl"
            && KeyNames.Display(0xA4) == "左Alt" && KeyNames.Display(0xA5) == "右Alt"
            && KeyNames.Display(0x5B) == "左Win" && KeyNames.Display(0x5C) == "右Win");
        C("键名 小键盘 0~9 与运算符",
            KeyNames.Display(0x60) == "小键盘0" && KeyNames.Display(0x69) == "小键盘9"
            && KeyNames.Display(0x6A) == "小键盘*" && KeyNames.Display(0x6B) == "小键盘+"
            && KeyNames.Display(0x6D) == "小键盘-" && KeyNames.Display(0x6E) == "小键盘."
            && KeyNames.Display(0x6F) == "小键盘/");
        C("键名 常用标点中文短名",
            KeyNames.Display(0xBA) == "分号" && KeyNames.Display(0xBB) == "等号"
            && KeyNames.Display(0xBC) == "逗号" && KeyNames.Display(0xBD) == "减号"
            && KeyNames.Display(0xBE) == "句号" && KeyNames.Display(0xBF) == "斜杠"
            && KeyNames.Display(0xC0) == "反引号" && KeyNames.Display(0xDB) == "左方括号"
            && KeyNames.Display(0xDC) == "反斜杠" && KeyNames.Display(0xDD) == "右方括号"
            && KeyNames.Display(0xDE) == "单引号");
        C("键名 未知键 VK_0xNN", KeyNames.Display(0xFE) == "VK_0xFE", KeyNames.Display(0xFE));
        C("键名 0 显示破折号", KeyNames.Display(0) == "—", KeyNames.Display(0));

        C("修饰键判断",
            KeyNames.IsModifier(0x10) && KeyNames.IsModifier(0xA0) && KeyNames.IsModifier(0x11)
            && KeyNames.IsModifier(0x5B) && !KeyNames.IsModifier(0x41) && !KeyNames.IsModifier(0x20));
        C("Join 用空格连接键名",
            KeyNames.Join(new[] { 0x44, 0x46, 0x4A }) == "D F J", KeyNames.Join(new[] { 0x44, 0x46, 0x4A }));

        var parsedList = KeyNames.ParseList("D F J K");
        C("ParseList 解析 D F J K",
            parsedList.SequenceEqual(new[] { 0x44, 0x46, 0x4A, 0x4B }),
            string.Join(",", parsedList));
        var chineseList = KeyNames.ParseList("左Shift 小键盘+");
        C("ParseList 解析中文名与小键盘+",
            chineseList.SequenceEqual(new[] { 0xA0, 0x6B }), string.Join(",", chineseList));
        C("ParseList 空字符串返回空集合", KeyNames.ParseList(null).Count == 0 && KeyNames.ParseList("  ").Count == 0);
        C("DebugDump 有内容且含字母 A", KeyNames.DebugDump().Contains("0x41=A"), KeyNames.DebugDump()[..Math.Min(60, KeyNames.DebugDump().Length)]);
    }

    // ================================================================== 3. 键音

    private static void CheckKeySound(Action<string, bool, string> c)
    {
        void C(string name, bool ok, string detail = "") => c(name, ok, detail);

        var player = new KeySoundPlayer();
        try
        {
            player.Enabled = false;
            C("键音 关闭时 Play 不抛异常", DoesNotThrow(player.Play));

            player.Enabled = true;
            player.Volume = 50;
            C("键音 内置点击声 Reload + Play 不抛异常", DoesNotThrow(() =>
            {
                player.Reload();
                player.Play();
            }), player.LastError ?? "");
            C("键音 内置点击声无错误", player.LastError is null, player.LastError ?? "");

            player.Volume = 100;
            C("键音 音量 100 重新合成不抛异常", DoesNotThrow(() =>
            {
                player.Reload();
                player.Play();
            }), player.LastError ?? "");

            player.SoundFilePath = Path.Combine(
                Path.GetTempPath(), $"ylt-kps-不存在-{Guid.NewGuid():N}.wav");
            C("键音 文件不存在时 Reload + Play 不抛异常", DoesNotThrow(() =>
            {
                player.Reload();
                player.Play();
            }));
            C("键音 文件不存在时 LastError 有说明",
                !string.IsNullOrWhiteSpace(player.LastError), player.LastError ?? "");

            // 真实的自定义 WAV：临时目录写一个合法小文件，验证播放路径不抛异常
            var wavPath = Path.Combine(Path.GetTempPath(), $"ylt-kps-test-{Guid.NewGuid():N}.wav");
            try
            {
                File.WriteAllBytes(wavPath, BuildSilentWav());
                player.SoundFilePath = wavPath;
                player.Volume = 60;
                C("键音 自定义 WAV Reload + Play 不抛异常", DoesNotThrow(() =>
                {
                    player.Reload();
                    player.Play();
                }), player.LastError ?? "");
                C("键音 自定义 WAV 无错误", player.LastError is null, player.LastError ?? "");
            }
            finally
            {
                try { File.Delete(wavPath); } catch { /* 临时文件删不掉无所谓 */ }
            }
        }
        finally
        {
            player.Dispose();
        }

        C("键音 Dispose 可重复调用且之后 Play 不抛异常", DoesNotThrow(() =>
        {
            player.Dispose();
            player.Play();
        }));
    }

    private static bool DoesNotThrow(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>自检用：50ms 静音 16-bit PCM 单声道 WAV（合法文件，用来走自定义 WAV 播放分支）。</summary>
    private static byte[] BuildSilentWav()
    {
        const int sampleRate = 8000;
        const int samples = 400;
        var pcm = new byte[samples * 2]; // 全 0 = 静音
        var wav = new byte[44 + pcm.Length];
        using var stream = new MemoryStream(wav);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return wav;
    }

    // ================================================================== 4. Raw Input（STA + SendInput 注入）

    private const int VkF24 = 0x87;

    private static void CheckRawInput(Action<string, bool, string> c)
    {
        void C(string name, bool ok, string detail = "") => c(name, ok, detail);

        RunStaThread(dispatcher =>
        {
            using var keyboard = new RawInputKeyboard();
            var downs = new List<int>();
            var ups = new List<int>();
            keyboard.KeyDown += (_, e) => downs.Add(e.VirtualKey);
            keyboard.KeyUp += (_, e) => ups.Add(e.VirtualKey);

            if (!keyboard.Start())
            {
                C("Raw Input 启动", false,
                    $"Start() 返回 false（BackendName={keyboard.BackendName}，LastError={keyboard.LastError ?? "(空)"}）");
                return;
            }

            try
            {
                C("Raw Input 启动", true,
                    $"后端={keyboard.BackendName}，IsRunning={keyboard.IsRunning}");
                C("Raw Input 后端名有效",
                    keyboard.BackendName is "Raw Input" or "低级键盘钩子", keyboard.BackendName);

                // ---- 按下 ----
                if (!InjectKey(VkF24, keyUp: false))
                {
                    C("Raw Input SendInput 注入按下", false,
                        $"SendInput 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                    return;
                }

                var gotDown = PumpUntil(dispatcher, () => downs.Count > 0, 2000);
                C("Raw Input 收到 KeyDown（VK_F24=0x87）",
                    gotDown && downs[0] == VkF24,
                    gotDown
                        ? $"VirtualKey=0x{downs[0]:X2}"
                        : $"2 秒内没收到 KeyDown（后端={keyboard.BackendName}，LastError={keyboard.LastError ?? "(空)"}）");
                C("Raw Input 按下集合包含 F24", keyboard.PressedKeys.Contains(VkF24),
                    string.Join(",", keyboard.PressedKeys.Select(k => $"0x{k:X2}")));

                // ---- 抬起 ----
                if (!InjectKey(VkF24, keyUp: true))
                {
                    C("Raw Input SendInput 注入抬起", false,
                        $"SendInput 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                    return;
                }

                var gotUp = PumpUntil(dispatcher, () => ups.Count > 0, 2000);
                C("Raw Input 收到 KeyUp（VK_F24=0x87）",
                    gotUp && ups[0] == VkF24,
                    gotUp ? $"VirtualKey=0x{ups[0]:X2}" : "2 秒内没收到 KeyUp");
                C("Raw Input 抬起后按下集合移除 F24", !keyboard.PressedKeys.Contains(VkF24),
                    string.Join(",", keyboard.PressedKeys.Select(k => $"0x{k:X2}")));

                // ---- 长按去重：连续两次 KeyDown + 一次 KeyUp，只应各算一次 ----
                downs.Clear();
                ups.Clear();
                InjectKey(VkF24, keyUp: false);
                InjectKey(VkF24, keyUp: false);
                InjectKey(VkF24, keyUp: true);
                PumpFor(dispatcher, 700);

                C("Raw Input 长按重复按下只算一次", downs.Count == 1, $"KeyDown 次数={downs.Count}");
                C("Raw Input 长按抬起算一次", ups.Count == 1, $"KeyUp 次数={ups.Count}");

                // ---- 停止 ----
                keyboard.Stop();
                C("Raw Input Stop 后 IsRunning=false", !keyboard.IsRunning);
                C("Raw Input Stop 后后端名复位", keyboard.BackendName == "未启动", keyboard.BackendName);
                C("Raw Input Stop 可重复调用", DoesNotThrow(keyboard.Stop));

                downs.Clear();
                ups.Clear();
                InjectKey(VkF24, keyUp: false);
                InjectKey(VkF24, keyUp: true);
                PumpFor(dispatcher, 500);
                C("Raw Input Stop 后不再上报事件", downs.Count == 0 && ups.Count == 0,
                    $"KeyDown={downs.Count}，KeyUp={ups.Count}");
            }
            finally
            {
                keyboard.Stop();
            }
        }, c);
    }

    private static bool InjectKey(int virtualKey, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
    }

    // ================================================================== 5. 全局热键

    private static void CheckGlobalHotkey(Action<string, bool, string> c)
    {
        void C(string name, bool ok, string detail = "") => c(name, ok, detail);

        RunStaThread(dispatcher =>
        {
            _ = dispatcher;
            var manager = new GlobalHotkeyManager();
            try
            {
                var parsed = HotkeyGesture.TryParse("Ctrl+Alt+Shift+F13", out var gesture);
                C("全局热键 组合解析", parsed, gesture.ToString());
                if (!parsed)
                {
                    C("全局热键 注册 Ctrl+Alt+Shift+F13", false, "热键组合解析失败，无法注册");
                    return;
                }

                var registered = manager.Register(1, gesture, () => { });
                C("全局热键 注册 Ctrl+Alt+Shift+F13", registered,
                    registered ? "" : manager.LastError ?? "(LastError 为空)");
                if (!registered) return;

                C("全局热键 注册后 IsRegistered=true", manager.IsRegistered(1));

                // 不做真实按键触发（避免影响用户 / 其它程序），只验证注册 / 注销；
                // 回调不可达不影响注册本身正确性（WM_HOTKEY 分发有单独 try/catch 保护）。

                // 同一 Id 重复注册：先注销旧的，换成另一把冷门键
                var second = HotkeyGesture.TryParse("Ctrl+Alt+Shift+F14", out var f14) && f14.VirtualKey == 0x7D;
                C("全局热键 解析 Ctrl+Alt+Shift+F14", second, f14.ToString());
                if (second)
                {
                    var reRegistered = manager.Register(1, f14, () => { });
                    C("全局热键 同一 Id 重复注册自动替换", reRegistered,
                        reRegistered ? "" : manager.LastError ?? "(LastError 为空)");
                    C("全局热键 替换后 IsRegistered=true", manager.IsRegistered(1));
                }

                manager.Unregister(1);
                C("全局热键 注销后 IsRegistered=false", !manager.IsRegistered(1));
                C("全局热键 注销未注册的 Id 安全", DoesNotThrow(() => manager.Unregister(999)));

                // 空手势 / 非法热键
                C("全局热键 空手势返回 false", !manager.Register(2, default, () => { }));
                C("全局热键 空手势 LastError 有说明", !string.IsNullOrWhiteSpace(manager.LastError),
                    manager.LastError ?? "");

                // UnregisterAll：注册一把再整体注销
                var third = HotkeyGesture.TryParse("Ctrl+Alt+Shift+F15", out var f15);
                if (third && manager.Register(3, f15, () => { }))
                {
                    manager.UnregisterAll();
                    C("全局热键 UnregisterAll 后 IsRegistered=false", !manager.IsRegistered(3));
                }
                else
                {
                    C("全局热键 注册 Ctrl+Alt+Shift+F15（UnregisterAll 前置）", false,
                        manager.LastError ?? "(LastError 为空)");
                }
            }
            finally
            {
                manager.Dispose();
                C("全局热键 Dispose 后 IsRegistered=false", !manager.IsRegistered(1));
                C("全局热键 Dispose 可重复调用", DoesNotThrow(manager.Dispose));
            }
        }, c);
    }

    // ================================================================== 工具方法

    /// <summary>起一个 STA 线程 + Dispatcher 跑需要 WPF 消息循环的测试；超时 / 异常都会记 FAIL。</summary>
    private static void RunStaThread(Action<Dispatcher> action, Action<string, bool, string> c)
    {
        void C(string name, bool ok, string detail = "") => c(name, ok, detail);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                action(dispatcher);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try { dispatcher.InvokeShutdown(); }
                catch { /* 收尾忽略 */ }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(20000))
        {
            C("STA 输入测试线程超时", false,
                "20 秒内没跑完（SendInput / 消息循环可能被系统或安全软件拦截）");
            return;
        }

        if (failure is not null)
        {
            C("STA 输入测试线程异常", false,
                $"{failure.GetType().Name}：{failure.Message}");
        }
    }

    /// <summary>在 Dispatcher 上泵消息，直到 <paramref name="condition"/> 为 true 或超时。</summary>
    private static bool PumpUntil(Dispatcher dispatcher, Func<bool> condition, int timeoutMs)
    {
        if (condition()) return true;

        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(timeoutMs),
        };
        var poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };

        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            poll.Stop();
            frame.Continue = false;
        };
        poll.Tick += (_, _) =>
        {
            if (!condition()) return;
            timeout.Stop();
            poll.Stop();
            frame.Continue = false;
        };

        timeout.Start();
        poll.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
            poll.Stop();
        }

        return condition();
    }

    /// <summary>在 Dispatcher 上泵固定时长（等可能的重复事件都到齐）。</summary>
    private static void PumpFor(Dispatcher dispatcher, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };

        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    // ================================================================== SendInput 声明

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;

        // INPUT 的联合体：这里只需要键盘分支，大小必须给足，否则 SendInput 的 cbSize 对不上（x64 = 40）
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
}
