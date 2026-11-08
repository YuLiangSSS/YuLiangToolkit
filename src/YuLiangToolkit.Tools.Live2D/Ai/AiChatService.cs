using System.Text.Json;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Live2D.Ai;

/// <summary>
/// <see cref="AiChatService"/> 的实现部分（契约 / 公开成员签名在 AiContracts.cs 的 partial 类里）。
///
/// <para><b>数据流</b>：用户发言 → 加消息 →（有 Key）拼系统提示词 + 最近 N 轮历史 + 带状态行的当前句
/// → <see cref="DeepSeekClient"/> → 解析 JSON → 加桌宠消息 → 好感度结算 → 落盘 → <see cref="ReplyReady"/>；
/// 没 Key / 请求失败 → 本地兜底回复（<see cref="AiReply.IsFallback"/>），失败时额外加一条 notice 说明原因。</para>
///
/// <para><b>省 token</b>：默认关闭思考、max_tokens 160、历史只带最近 N 轮、系统提示词 ≤700 字；
/// 主动说话不带历史、max_tokens 80。</para>
///
/// <para><b>线程</b>：构造与公开方法都在 UI 线程调；await 后回到调用时的 SynchronizationContext，
/// 事件也在该上下文触发。请求用 <see cref="SemaphoreSlim"/> 串行化，同一时间只有一条在途。</para>
/// </summary>
public sealed partial class AiChatService
{
    /// <summary>聊天状态落盘位置（相对 <c>Data\live2d\</c>）。</summary>
    private const string StateRelativePath = "ai/chat-state.json";

    /// <summary>聊天记录最多保留条数（落盘 / 内存一致）。</summary>
    private const int MaxMessages = 200;

    /// <summary>两条主动消息之间的最小间隔（避免连续轰炸）。</summary>
    private static readonly TimeSpan MinProactiveGap = TimeSpan.FromSeconds(120);

    /// <summary>用户刚说完话的一段时间内不主动搭话（留出回复时间）。</summary>
    private static readonly TimeSpan QuietAfterUser = TimeSpan.FromMinutes(2);

    private readonly DeepSeekClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Random _random = new();

    private bool _disposed;

    // 今日 token 统计（跨天自动清零；落盘一起保存）
    private long _todayTokens;
    private long _todayPromptTokens;
    private long _todayCompletionTokens;
    private string _todayKey = "";

    // 主动说话调度状态
    private DateTimeOffset _lastUserAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProactiveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextIdleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextChatterAt = DateTimeOffset.MinValue;
    private int _idleStreak;

    // 落盘节流：拖好感度滑杆时会连续调 SetAffection，别每次都写一遍 chat-state.json
    private DateTimeOffset _lastSaveAt = DateTimeOffset.MinValue;

    /// <summary>创建服务（<paramref name="options"/> 的回调都允许为 null，取默认值）。</summary>
    public AiChatService(AiChatServiceOptions options)
    {
        Options = options ?? new AiChatServiceOptions();
        _client = new DeepSeekClient(Options.HttpHandler);

        // 恢复落盘状态（不存在 = 全新开始；读失败只记日志，不阻止聊天）
        var saved = LoadStateFile();
        Affection = new AffectionEngine(saved?.Affection);
        Affection.Attributes = AiPromptBuilder.ParseAttributes(ReadPersona().Attributes);

        if (saved?.Messages is { Count: > 0 } messages)
        {
            foreach (var message in messages.TakeLast(MaxMessages))
            {
                if (message is null) continue;
                if (string.IsNullOrWhiteSpace(message.Role) || string.IsNullOrWhiteSpace(message.Text)) continue;
                Messages.Add(message);
            }
        }

        if (saved is not null)
        {
            _todayKey = saved.TodayKey ?? "";
            _todayTokens = Math.Max(0, saved.TodayTokens);
            _todayPromptTokens = Math.Max(0, saved.TodayPromptTokens);
            _todayCompletionTokens = Math.Max(0, saved.TodayCompletionTokens);
        }
        ResetTokensIfNewDay(DateTimeOffset.Now);

        if (Affection.State.LastChatUnix > 0)
        {
            try { _lastUserAt = DateTimeOffset.FromUnixTimeSeconds(Affection.State.LastChatUnix); }
            catch (ArgumentOutOfRangeException) { _lastUserAt = DateTimeOffset.MinValue; }
        }

        try { Affection.ApplySettings(ReadAffectionSettings()); }
        catch (Exception ex) { Log("恢复好感度设置失败：" + ex.Message); }

        StatusText = BuildIdleStatus();
    }

    // ---------- 对外契约的实现 ----------

    /// <inheritdoc/>
    public partial Task<AiReply> SendUserMessageAsync(string text, CancellationToken cancellationToken)
        => SendUserMessageCoreAsync(text, cancellationToken);

    /// <inheritdoc/>
    public partial Task<AiReply> SendProactiveAsync(AiProactiveKind kind, CancellationToken cancellationToken)
        => SendProactiveCoreAsync(kind, cancellationToken);

    /// <inheritdoc/>
    public partial void TickSchedule(DateTimeOffset now, bool userPresent)
    {
        if (_disposed) return;

        // 页面 / 桌宠 / 聊天窗都没人看着：不结算掉值、也不主动说话
        if (!userPresent) return;

        ResetTokensIfNewDay(now);

        // 1) 结算「长时间不聊」的掉值（只掉完整周期；重复调用不会重复扣）
        try
        {
            var lost = Affection.ApplyDecay(now);
            if (lost > 0)
            {
                Log($"好感度因长时间没聊天掉了 {lost} 点（现在 {Affection.Value:0.0}，{Affection.MoodText}）");
                RaiseAffectionChanged();
                SaveStateInternal(force: false);
                RaiseStateChanged();
            }
        }
        catch (Exception ex)
        {
            Log("结算好感度掉值失败：" + ex.Message);
        }

        // 2) 正在等回复 / 正在主动说话：跳过，不打断
        if (IsBusy) return;

        var proactive = ReadProactiveSettings();

        // 3) 两条主动消息至少隔 120 秒；用户刚说完 2 分钟内不主动
        if (_lastProactiveAt != DateTimeOffset.MinValue && now - _lastProactiveAt < MinProactiveGap) return;
        if (_lastUserAt != DateTimeOffset.MinValue && now - _lastUserAt < QuietAfterUser) return;

        // 4) 久未回复撒娇：连续最多 3 次，之后等待时间 ×4
        if (proactive.IdleReminderEnabled && _lastUserAt != DateTimeOffset.MinValue)
        {
            var thresholdMinutes = Math.Clamp(proactive.IdleSilenceMinutes, 1, 720);
            var silenceMinutes = (now - _lastUserAt).TotalMinutes;
            if (silenceMinutes >= thresholdMinutes && now >= _nextIdleAt)
            {
                _lastProactiveAt = now;
                _idleStreak++;
                var waitMinutes = _idleStreak >= 3 ? thresholdMinutes * 4 : thresholdMinutes;
                _nextIdleAt = now.AddMinutes(waitMinutes);
                Log($"用户已 {silenceMinutes:0} 分钟没说话：触发久未回复撒娇（第 {_idleStreak} 次）");
                FireAndForget(() => SendProactiveCoreAsync(AiProactiveKind.IdleReminder, CancellationToken.None));
                return;
            }
        }

        // 5) 定时主动搭话：间隔 × (0.6~1.4) 随机
        if (proactive.Enabled)
        {
            var intervalMinutes = Math.Clamp(proactive.IntervalMinutes, 3, 360);
            if (_nextChatterAt == DateTimeOffset.MinValue)
                _nextChatterAt = now.AddMinutes(intervalMinutes * NextChatterRandomFactor());
            if (now >= _nextChatterAt)
            {
                _nextChatterAt = now.AddMinutes(intervalMinutes * NextChatterRandomFactor());
                _lastProactiveAt = now;
                FireAndForget(() => SendProactiveCoreAsync(AiProactiveKind.Chatter, CancellationToken.None));
            }
        }
        else
        {
            // 关掉主动搭话后重新打开：从下一次 Tick 起重新排期，不会一打开就立刻说话
            _nextChatterAt = DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc/>
    public partial void ApplySettings()
    {
        try
        {
            Affection.ApplySettings(ReadAffectionSettings());
            Affection.Attributes = AiPromptBuilder.ParseAttributes(ReadPersona().Attributes);
        }
        catch (Exception ex)
        {
            Log("应用 AI 设置失败：" + ex.Message);
        }

        // 主动说话开关 / 间隔可能变了：重新排期
        _nextChatterAt = DateTimeOffset.MinValue;

        // 契约要求：这些属性变化都要通知界面
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AffectionValue));
        OnPropertyChanged(nameof(MoodText));
        OnPropertyChanged(nameof(TodayTokens));
        OnPropertyChanged(nameof(TodayPromptTokens));
        OnPropertyChanged(nameof(TodayCompletionTokens));
        OnPropertyChanged(nameof(HasUsableApiKey));
        if (!IsBusy) SetStatus(BuildIdleStatus());
        RaiseStateChanged();
    }

    /// <inheritdoc/>
    public partial void SetAffection(double value)
    {
        try
        {
            Affection.SetValue(value);
        }
        catch (Exception ex)
        {
            Log("设置好感度失败：" + ex.Message);
        }
        RaiseAffectionChanged();
        SaveStateInternal(force: false);   // 滑杆拖动会连续调用：节流写盘
        RaiseStateChanged();
    }

    /// <inheritdoc/>
    public void AddAffection(double delta)
    {
        var before = Affection.Value;
        double changed;
        try
        {
            changed = Affection.ApplyDirectGain(delta);
        }
        catch (Exception ex)
        {
            Log("投喂加好感度失败：" + ex.Message);
            return;
        }

        if (changed == 0) return;
        Log($"投喂让好感度 {(changed > 0 ? "+" : "")}{changed:0.0}（{before:0.0} → {Affection.Value:0.0}）");
        RaiseAffectionChanged();
        SaveStateInternal(force: false);
        RaiseStateChanged();
    }

    /// <inheritdoc/>
    public void AppendNotice(string text)
    {
        var content = (text ?? "").Trim();
        if (content.Length == 0) return;
        AddMessage(new AiChatMessage
        {
            TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Role = "notice",
            Text = content,
        });
        SaveStateInternal(force: false);
        RaiseStateChanged();
    }

    /// <inheritdoc/>
    public void AppendLocalPetMessage(string text, string emotion = "")
    {
        var content = (text ?? "").Trim();
        if (content.Length == 0) return;
        AddMessage(new AiChatMessage
        {
            TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Role = "assistant",
            Text = content,
            Emotion = emotion ?? "",
        });
        SaveStateInternal(force: false);
        RaiseStateChanged();
    }

    /// <inheritdoc/>
    public partial void ClearConversation()
    {
        Messages.Clear();
        _idleStreak = 0;
        SaveStateInternal();
        SetStatus(BuildIdleStatus());
        RaiseStateChanged();
        Log("AI 聊天记录已清空（好感度不变）");
    }

    /// <inheritdoc/>
    public partial Task<AiConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
        => TestConnectionCoreAsync(cancellationToken);

    /// <inheritdoc/>
    public partial void SaveState() => SaveStateInternal();

    /// <inheritdoc/>
    public partial void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _disposeCts.Cancel(); } catch { /* 退出流程忽略 */ }
        try { SaveStateInternal(); } catch { /* 退出流程忽略 */ }
        try { _client.Dispose(); } catch { /* 退出流程忽略 */ }
        // _gate / _disposeCts 不主动 Dispose：可能有在途请求正在用，交给 GC 即可
    }

    // ---------- 聊天流程 ----------

    private async Task<AiReply> SendUserMessageCoreAsync(string text, CancellationToken cancellationToken)
    {
        var userText = (text ?? "").Trim();
        if (userText.Length == 0)
            return new AiReply { Text = "", Error = "空消息，已忽略" };
        if (_disposed)
            return new AiReply { Text = "", IsFallback = true, Error = "聊天服务已关闭" };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var token = linked.Token;

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new AiReply { Text = "", IsFallback = true, Error = "请求已取消" };
        }

        try
        {
            var now = DateTimeOffset.Now;
            ResetTokensIfNewDay(now);

            // 历史必须在「加入当前这句」之前取（当前句单独拼状态行）
            var history = BuildHistoryTurns();
            var persona = ReadPersona();
            var requestOptions = ReadRequestOptions();
            var affectionSettings = ReadAffectionSettings();
            var includeDelta = requestOptions.JsonOutput && affectionSettings.NaturalMode && affectionSettings.UseModelDelta;

            // 用户消息先上屏（界面立刻有反馈）
            AddMessage(new AiChatMessage { TimestampUnix = now.ToUnixTimeSeconds(), Role = "user", Text = userText });
            _lastUserAt = now;
            _idleStreak = 0;
            _nextIdleAt = DateTimeOffset.MinValue;
            _nextChatterAt = DateTimeOffset.MinValue;
            RaiseStateChanged();

            SetBusy(true, requestOptions.IsUsable
                ? AiThinkingLevels.IsThinkingEnabled(requestOptions.Thinking) ? "正在思考（思考模式会慢一点）…" : "正在思考…"
                : "离线模式：正在用本地兜底回复…");

            AiReply reply;
            if (!requestOptions.IsUsable)
            {
                // 没填 Key：直接离线兜底（也算聊了一轮，好感度照常涨）
                reply = BuildFallbackReply(userText, "还没填 DeepSeek API Key");
                SetStatus("离线模式：还没填 API Key，先用本地兜底回复。");
            }
            else
            {
                var turns = BuildUserRequestTurns(history, persona, includeDelta, userText);
                reply = await _client.ChatAsync(requestOptions, turns, null, token).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(reply.Error))
                {
                    // 失败：加一条 notice 说明原因（notice 不发给模型），正文用离线兜底
                    var error = reply.Error!;
                    Log("AI 请求失败：" + error);
                    AddMessage(new AiChatMessage
                    {
                        TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                        Role = "notice",
                        Text = "AI 请求失败：" + error + "（已切换离线兜底回复）",
                    });
                    reply = BuildFallbackReply(userText, error);
                    SetStatus("连接失败：" + error + "（已用离线兜底）");
                }
                else if (string.IsNullOrWhiteSpace(reply.Text))
                {
                    reply = BuildFallbackReply(userText, "模型没有返回内容");
                    SetStatus("模型没有返回内容（已用离线兜底）");
                }
                else
                {
                    AddTokens(reply.PromptTokens, reply.CompletionTokens);
                }
            }

            if (string.IsNullOrWhiteSpace(reply.Text))
                reply = BuildFallbackReply(userText, reply.Error ?? "回复为空");

            var finishedAt = DateTimeOffset.Now;
            AddMessage(new AiChatMessage
            {
                TimestampUnix = finishedAt.ToUnixTimeSeconds(),
                Role = "assistant",
                Text = reply.Text,
                Emotion = reply.Emotion,
            });

            // 好感度：一轮「用户发言 → 桌宠回复」只结算一次（模型 d 值仅在自然模式 + 启用模型建议时参与）
            Affection.Attributes = AiPromptBuilder.ParseAttributes(persona.Attributes);
            Affection.ApplySettings(affectionSettings);
            var changed = Affection.ApplyChat(reply.IsFallback ? 0 : reply.AffectionDelta, finishedAt);
            RaiseAffectionChanged();
            if (changed != 0)
                Log($"好感度 {(changed > 0 ? "+" : "")}{changed}（现在 {Affection.Value:0.0}，{Affection.MoodText}）");

            SaveStateInternal();
            if (!reply.IsFallback) SetStatus(BuildIdleStatus());
            SetBusy(false, null);
            RaiseStateChanged();
            RaiseReplyReady(new AiReplyEventArgs { Reply = reply, Speak = true });
            return reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 调用方主动取消（界面关掉 / 用户取消）：原样抛出
        }
        catch (Exception ex)
        {
            return HandleUnexpectedError(userText, ex);
        }
        finally
        {
            try { _gate.Release(); } catch (SemaphoreFullException) { /* 已释放过，忽略 */ }
            SetBusy(false, null);
        }
    }

    private async Task<AiReply> SendProactiveCoreAsync(AiProactiveKind kind, CancellationToken cancellationToken)
    {
        if (_disposed)
            return new AiReply { Text = "", IsFallback = true, Error = "聊天服务已关闭" };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var token = linked.Token;

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new AiReply { Text = "", IsFallback = true, Error = "请求已取消" };
        }

        try
        {
            var now = DateTimeOffset.Now;
            ResetTokensIfNewDay(now);

            var persona = ReadPersona();
            var requestOptions = ReadRequestOptions();
            var affectionSettings = ReadAffectionSettings();
            var includeDelta = requestOptions.JsonOutput && affectionSettings.NaturalMode && affectionSettings.UseModelDelta;

            SetBusy(true, kind == AiProactiveKind.IdleReminder ? "在想怎么撒娇…" : "在想说什么…");

            AiReply reply;
            if (!requestOptions.IsUsable)
            {
                reply = new AiReply
                {
                    Text = PickOfflineProactiveLine(kind),
                    Emotion = kind == AiProactiveKind.IdleReminder ? "shy" : "happy",
                    IsFallback = true,
                };
                SetStatus("离线模式：还没填 API Key（用本地台词主动搭话）");
            }
            else
            {
                // 主动说话不带历史：省 token
                var silentMinutes = _lastUserAt == DateTimeOffset.MinValue ? 0 : (now - _lastUserAt).TotalMinutes;
                var turns = new List<AiChatTurn>
                {
                    new() { Role = "system", Text = AiPromptBuilder.BuildSystemPrompt(persona, includeDelta) },
                    new() { Role = "user", Text = AiPromptBuilder.BuildProactiveInstruction(kind, silentMinutes, LastAssistantText()) },
                };
                // 主动说话更短：默认 80 token
                var maxTokens = Math.Clamp(requestOptions.MaxTokens > 0 ? Math.Min(requestOptions.MaxTokens, 80) : 80, 16, 200);
                reply = await _client.ChatAsync(requestOptions, turns, maxTokens, token).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(reply.Error) || string.IsNullOrWhiteSpace(reply.Text))
                {
                    var error = string.IsNullOrWhiteSpace(reply.Error) ? "模型没有返回内容" : reply.Error!;
                    Log("主动说话失败：" + error);
                    reply = new AiReply
                    {
                        Text = PickOfflineProactiveLine(kind),
                        Emotion = kind == AiProactiveKind.IdleReminder ? "shy" : "happy",
                        IsFallback = true,
                        Error = error,
                    };
                    SetStatus("主动说话失败：" + error + "（已用离线台词）");
                }
                else
                {
                    AddTokens(reply.PromptTokens, reply.CompletionTokens);
                }
            }

            var finishedAt = DateTimeOffset.Now;
            AddMessage(new AiChatMessage
            {
                TimestampUnix = finishedAt.ToUnixTimeSeconds(),
                Role = "assistant",
                Text = reply.Text,
                Emotion = reply.Emotion,
            });

            // 主动说话不改好感度（只有用户发言才算聊了一轮），但属性要刷新，供脸红概率用
            Affection.Attributes = AiPromptBuilder.ParseAttributes(persona.Attributes);
            Affection.ApplySettings(affectionSettings);
            RaiseAffectionChanged();

            _lastProactiveAt = finishedAt;
            SaveStateInternal();
            if (!reply.IsFallback) SetStatus(BuildIdleStatus());
            SetBusy(false, null);
            RaiseStateChanged();
            RaiseReplyReady(new AiReplyEventArgs { Reply = reply, Speak = true, Proactive = kind });
            return reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("主动说话出错：" + ex.Message);
            if (_disposed)
                return new AiReply { Text = "", IsFallback = true, Error = "聊天服务已关闭" };

            var fallback = new AiReply
            {
                Text = PickOfflineProactiveLine(kind),
                Emotion = kind == AiProactiveKind.IdleReminder ? "shy" : "happy",
                IsFallback = true,
                Error = ex.Message,
            };
            AddMessage(new AiChatMessage
            {
                TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                Role = "assistant",
                Text = fallback.Text,
                Emotion = fallback.Emotion,
            });
            SetStatus("主动说话出错：" + ex.Message + "（已用离线台词）");
            SaveStateInternal();
            RaiseStateChanged();
            RaiseReplyReady(new AiReplyEventArgs { Reply = fallback, Speak = true, Proactive = kind });
            return fallback;
        }
        finally
        {
            try { _gate.Release(); } catch (SemaphoreFullException) { /* 已释放过，忽略 */ }
            SetBusy(false, null);
        }
    }

    private async Task<AiConnectionTestResult> TestConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var requestOptions = ReadRequestOptions();
        if (!requestOptions.IsUsable)
        {
            var message = "还没填 DeepSeek API Key：请先在设置里填写后再测试";
            SetStatus("连接测试失败：" + message);
            return new AiConnectionTestResult { Ok = false, Message = message };
        }
        if (_disposed)
            return new AiConnectionTestResult { Ok = false, Message = "聊天服务已关闭" };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var token = linked.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new AiConnectionTestResult { Ok = false, Message = "测试已取消" };
        }

        try
        {
            SetBusy(true, "正在测试 DeepSeek 连接…");
            var result = await _client.TestConnectionAsync(requestOptions, token).ConfigureAwait(true);
            Log("AI 连接测试：" + result.Message);
            SetStatus(result.Ok ? "就绪（连接测试通过）" : "连接测试失败：" + result.Message);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("AI 连接测试异常：" + ex.Message);
            var result = new AiConnectionTestResult { Ok = false, Message = "测试连接失败：" + ex.Message };
            SetStatus(result.Message);
            return result;
        }
        finally
        {
            try { _gate.Release(); } catch (SemaphoreFullException) { /* 已释放过，忽略 */ }
            SetBusy(false, null);
        }
    }

    /// <summary>拼这次请求的消息列表：system + 最近 N 轮历史 + 当前用户消息（带状态行）。</summary>
    private List<AiChatTurn> BuildUserRequestTurns(
        IReadOnlyList<AiChatTurn> history,
        AiPersonaSettings persona,
        bool includeAffectionDelta,
        string userText)
    {
        var turns = new List<AiChatTurn>(history.Count + 2)
        {
            new() { Role = "system", Text = AiPromptBuilder.BuildSystemPrompt(persona, includeAffectionDelta) },
        };
        turns.AddRange(history);

        var stateLine = AiPromptBuilder.BuildStateLine(Affection, includeAffectionDelta);
        turns.Add(new AiChatTurn { Role = "user", Text = AiPromptBuilder.BuildUserContent(stateLine, userText) });
        return turns;
    }

    /// <summary>取最近 N 轮（= 2 × N 条）user / assistant 消息；跳过 notice，保留离线兜底回复。</summary>
    private List<AiChatTurn> BuildHistoryTurns()
    {
        var result = new List<AiChatTurn>();
        var rounds = Math.Clamp(ReadHistoryTurns(), 0, 20);
        if (rounds <= 0 || Messages.Count == 0) return result;

        var picked = new List<AiChatMessage>(rounds * 2);
        for (var i = Messages.Count - 1; i >= 0 && picked.Count < rounds * 2; i--)
        {
            var message = Messages[i];
            if (message is null || message.IsNotice) continue;
            if (!message.IsUser && !message.IsBot) continue;
            if (string.IsNullOrWhiteSpace(message.Text)) continue;
            picked.Add(message);
        }

        picked.Reverse();
        foreach (var message in picked)
        {
            result.Add(new AiChatTurn
            {
                Role = message.IsUser ? "user" : "assistant",
                Text = message.Text.Trim(),
            });
        }
        return result;
    }

    // ---------- 事件 / 属性通知 ----------

    private void AddMessage(AiChatMessage message)
    {
        if (message is null) return;
        if (string.IsNullOrWhiteSpace(message.Text)) return;

        Messages.Add(message);
        while (Messages.Count > MaxMessages) Messages.RemoveAt(0);

        try
        {
            MessageAdded?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            Log("聊天消息事件订阅方异常：" + ex.Message);
        }
    }

    private void RaiseReplyReady(AiReplyEventArgs args)
    {
        try
        {
            ReplyReady?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            Log("AI 回复事件订阅方异常：" + ex.Message);
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log("AI 状态事件订阅方异常：" + ex.Message);
        }
    }

    private void RaiseAffectionChanged()
    {
        OnPropertyChanged(nameof(AffectionValue));
        OnPropertyChanged(nameof(MoodText));
    }

    private void SetBusy(bool busy, string? status)
    {
        if (IsBusy != busy)
        {
            IsBusy = busy;
            OnPropertyChanged(nameof(IsBusy));
        }
        if (status is not null) SetStatus(status);
    }

    private void SetStatus(string status)
    {
        status ??= "";
        if (string.Equals(StatusText, status, StringComparison.Ordinal)) return;
        StatusText = status;
        OnPropertyChanged(nameof(StatusText));
    }

    private string BuildIdleStatus()
    {
        try
        {
            if (!HasUsableApiKey) return "离线模式：还没填 API Key（用本地兜底回复）";
        }
        catch
        {
            return "离线模式：还没填 API Key（用本地兜底回复）";
        }

        return _todayTokens > 0 ? $"就绪（今天已用 {_todayTokens} token）" : "就绪";
    }

    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try { Options.Log?.Invoke(message); } catch { /* 日志失败不影响聊天 */ }
    }

    // ---------- 离线兜底 ----------

    private AiReply BuildFallbackReply(string userText, string? error)
    {
        var text = "";
        try
        {
            text = (Options.OfflineFallback?.Invoke(userText) ?? "").Trim();
        }
        catch (Exception ex)
        {
            Log("离线兜底回调异常：" + ex.Message);
        }

        if (text.Length == 0) text = "唔…本鱼现在有点连不上 DeepSeek，先陪主人说说话吧～";
        return new AiReply
        {
            Text = AiReplyParser.CleanText(text, AiReplyParser.MaxTextLength),
            Emotion = "",
            Action = "",
            IsFallback = true,
            Error = error,
        };
    }

    private string PickOfflineProactiveLine(AiProactiveKind kind)
    {
        try
        {
            var fromCallback = (Options.OfflineFallback?.Invoke("") ?? "").Trim();
            if (fromCallback.Length > 0) return AiReplyParser.CleanText(fromCallback, AiReplyParser.MaxTextLength);
        }
        catch
        {
            // 回调异常就用内置台词
        }

        return kind == AiProactiveKind.IdleReminder
            ? "主人好久没理我了…陪我说说话嘛～"
            : "主人在忙什么呀？我有点无聊了～";
    }

    private AiReply HandleUnexpectedError(string userText, Exception ex)
    {
        Log("AI 聊天出错：" + ex.Message);
        if (_disposed)
            return new AiReply { Text = "", IsFallback = true, Error = "聊天服务已关闭" };

        try
        {
            AddMessage(new AiChatMessage
            {
                TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                Role = "notice",
                Text = "AI 聊天出错：" + ex.Message + "（已切换离线兜底回复）",
            });

            var fallback = BuildFallbackReply(userText, ex.Message);
            AddMessage(new AiChatMessage
            {
                TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                Role = "assistant",
                Text = fallback.Text,
                Emotion = fallback.Emotion,
            });
            SetStatus("出错了：" + ex.Message + "（已用离线兜底）");
            SaveStateInternal();
            RaiseStateChanged();
            RaiseReplyReady(new AiReplyEventArgs { Reply = fallback, Speak = true });
            return fallback;
        }
        catch (Exception inner)
        {
            Log("AI 聊天兜底也失败了：" + inner.Message);
            return new AiReply { Text = "", IsFallback = true, Error = ex.Message };
        }
    }

    // ---------- 配置现读 ----------

    private AiRequestOptions ReadRequestOptions()
    {
        try
        {
            return Options.RequestOptions?.Invoke() ?? new AiRequestOptions();
        }
        catch (Exception ex)
        {
            Log("读取 AI 请求设置失败：" + ex.Message);
            return new AiRequestOptions();
        }
    }

    private AiPersonaSettings ReadPersona()
    {
        try
        {
            return Options.Persona?.Invoke() ?? new AiPersonaSettings();
        }
        catch (Exception ex)
        {
            Log("读取人格设置失败：" + ex.Message);
            return new AiPersonaSettings();
        }
    }

    private AffectionSettings ReadAffectionSettings()
    {
        try
        {
            return Options.Affection?.Invoke() ?? new AffectionSettings();
        }
        catch (Exception ex)
        {
            Log("读取好感度设置失败：" + ex.Message);
            return new AffectionSettings();
        }
    }

    private AiProactiveSettings ReadProactiveSettings()
    {
        try
        {
            return Options.Proactive?.Invoke() ?? new AiProactiveSettings();
        }
        catch (Exception ex)
        {
            Log("读取主动聊天设置失败：" + ex.Message);
            return new AiProactiveSettings();
        }
    }

    private int ReadHistoryTurns()
    {
        try
        {
            return Math.Clamp(Options.HistoryTurns?.Invoke() ?? 6, 0, 20);
        }
        catch
        {
            return 6;
        }
    }

    private string LastAssistantText()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var message = Messages[i];
            if (message is { IsBot: true } && !string.IsNullOrWhiteSpace(message.Text))
                return message.Text.Trim();
        }
        return "";
    }

    private double NextChatterRandomFactor() => 0.6 + _random.NextDouble() * 0.8;

    /// <summary>调度里触发主动说话：不阻塞 Tick，也不让异常冒到 DispatcherTimer。</summary>
    private void FireAndForget(Func<Task> action)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("主动说话任务失败：" + ex.Message);
            }
        }
    }

    // ---------- token 统计 ----------

    private void ResetTokensIfNewDay(DateTimeOffset now)
    {
        var today = now.ToLocalTime().ToString("yyyy-MM-dd");
        if (string.Equals(_todayKey, today, StringComparison.Ordinal)) return;

        _todayKey = today;
        _todayTokens = 0;
        _todayPromptTokens = 0;
        _todayCompletionTokens = 0;
        OnPropertyChanged(nameof(TodayTokens));
        OnPropertyChanged(nameof(TodayPromptTokens));
        OnPropertyChanged(nameof(TodayCompletionTokens));
    }

    private void AddTokens(int promptTokens, int completionTokens)
    {
        ResetTokensIfNewDay(DateTimeOffset.Now);
        _todayPromptTokens += Math.Max(0, promptTokens);
        _todayCompletionTokens += Math.Max(0, completionTokens);
        _todayTokens = _todayPromptTokens + _todayCompletionTokens;
        OnPropertyChanged(nameof(TodayTokens));
        OnPropertyChanged(nameof(TodayPromptTokens));
        OnPropertyChanged(nameof(TodayCompletionTokens));
    }

    // ---------- 落盘 ----------

    private AiChatStateFile? LoadStateFile()
    {
        var storage = Options.Storage;
        if (storage is null) return null;
        try
        {
            var json = storage.ReadText(StateRelativePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<AiChatStateFile>(json, JsonFileStore.Options) ?? new AiChatStateFile();
        }
        catch (Exception ex)
        {
            Log("读取 AI 聊天状态失败（按新会话开始）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 落盘聊天记录 + 好感度 + token 统计。<paramref name="force"/> = false 时 400ms 内最多写一次
    /// （好感度滑杆拖动会连续触发；真正重要的收尾 / 聊天完成 / 退出都用 force）。
    /// </summary>
    private void SaveStateInternal(bool force = true)
    {
        var storage = Options.Storage;
        if (storage is null) return;

        var now = DateTimeOffset.Now;
        if (!force && _lastSaveAt != DateTimeOffset.MinValue &&
            now - _lastSaveAt < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        try
        {
            var file = new AiChatStateFile
            {
                Messages = Messages.TakeLast(MaxMessages).ToList(),
                Affection = Affection.State.Clone(),
                TodayKey = _todayKey,
                TodayTokens = _todayTokens,
                TodayPromptTokens = _todayPromptTokens,
                TodayCompletionTokens = _todayCompletionTokens,
            };
            var json = JsonSerializer.Serialize(file, JsonFileStore.Options);
            var path = storage.WriteText(StateRelativePath, json);
            if (path is null) Log("保存 AI 聊天状态失败：写入被拒绝（磁盘只读 / 被占用？）");
            else _lastSaveAt = now;
        }
        catch (Exception ex)
        {
            // 落盘失败只记日志，绝不打断聊天
            Log("保存 AI 聊天状态失败：" + ex.Message);
        }
    }

    /// <summary>落盘结构（聊天记录 + 好感度 + 今日 token；camelCase，System.Text.Json）。</summary>
    internal sealed class AiChatStateFile
    {
        /// <summary>创建落盘结构（序列化需要无参构造）。</summary>
        public AiChatStateFile()
        {
        }

        /// <summary>聊天记录（最多 200 条）。</summary>
        public List<AiChatMessage> Messages { get; set; } = new();

        /// <summary>好感度状态。</summary>
        public AffectionState Affection { get; set; } = new();

        /// <summary>token 统计对应的本地日期（yyyy-MM-dd）。</summary>
        public string TodayKey { get; set; } = "";

        /// <summary>今天总 token。</summary>
        public long TodayTokens { get; set; }

        /// <summary>今天 prompt token。</summary>
        public long TodayPromptTokens { get; set; }

        /// <summary>今天 completion token。</summary>
        public long TodayCompletionTokens { get; set; }
    }
}
