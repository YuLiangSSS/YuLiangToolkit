using YuLiangToolkit.Tools.Games.Snake;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.Games.Snake.ViewModels;

/// <summary>
/// 「贪吃蛇」界面 VM：单机（含 AI）与联机（房主权威）共用。
///
/// <para><b>循环</b>：用 <see cref="DispatcherTimer"/> 按 <see cref="SnakeSettings.Speed"/> 推进；
/// 按住加速键时把间隔缩短（<see cref="SnakeSettings.SpeedBoostMultiplier"/> 倍）。</para>
/// </summary>
public sealed class SnakeViewModel : ToolViewModelBase
{
    private readonly Random _random = new();
    private SnakeGame _game;
    private SnakeSettings _settings;
    private DispatcherTimer? _timer;
    private bool _isBoostKeyDown;
    private bool _running;

    private string _statusText = "点「开始游戏」开局（单机带 AI，或开房间联机）";
    private string _resultText = "";
    private int _countdownLeft;

    // 联机
    private SnakeNetSession? _session;
    private string _myId = "";
    private bool _isHost;
    private bool _netBusy;
    private string _noticeText = "";
    private bool _demoBoard;

    /// <summary>创建 VM。</summary>
    public SnakeViewModel() : base(SnakeConfig.ToolId)
    {
        Cfg = LoadConfig<SnakeConfig>();
        _settings = Cfg.Settings;
        _settings.Normalize();
        _game = new SnakeGame(_settings, _random);

        ModeOptions = OptionItem.From(("battle", "大混战（多蛇多果）"), ("shared", "共享果（分区域抢果）"));
        EdgeOptions = OptionItem.From(("die", "撞墙即死"), ("stop", "停住不动"), ("wrap", "穿墙"));
        AiLevelOptions = OptionItem.From(Enumerable.Range(1, 9)
            .Select(i => ((double)i, SnakeAi.LevelName(i))).ToArray());

        StartCommand = new RelayCommand(_ => StartLocal(), _ => !_running);
        StopCommand = new RelayCommand(_ => StopLoop(), _ => _running);
        BoostStartCommand = new RelayCommand(_ => SetBoost(true), _ => _settings.SpeedBoostEnabled);
        BoostEndCommand = new RelayCommand(_ => SetBoost(false), _ => _settings.SpeedBoostEnabled);

        RefreshUi();
        LoadDemoIfRequested();
    }

    /// <summary>配置。</summary>
    public SnakeConfig Cfg { get; }

    /// <summary>当前设置。</summary>
    public SnakeSettings Settings => _settings;

    /// <summary>模式选项。</summary>
    public IReadOnlyList<OptionItem<string>> ModeOptions { get; }

    /// <summary>边缘规则选项。</summary>
    public IReadOnlyList<OptionItem<string>> EdgeOptions { get; }

    /// <summary>AI 等级选项。</summary>
    public IReadOnlyList<OptionItem<double>> AiLevelOptions { get; }

    /// <summary>开始 / 停止 / 加速命令。</summary>
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand BoostStartCommand { get; }
    public RelayCommand BoostEndCommand { get; }

    /// <summary>玩家行（排行榜）。</summary>
    public ObservableCollection<SnakePlayerRow> Players { get; } = new();

    /// <summary>状态文字。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>结果文字。</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (!Set(ref _resultText, value)) return;
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>有没有结果。</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>倒计时剩余秒数。</summary>
    public int CountdownLeft
    {
        get => _countdownLeft;
        private set
        {
            if (!Set(ref _countdownLeft, value)) return;
            OnPropertyChanged(nameof(ShowCountdown));
            OnPropertyChanged(nameof(CountdownText));
        }
    }

    /// <summary>要不要显示倒计时遮罩。</summary>
    public bool ShowCountdown => CountdownLeft > 0;

    /// <summary>倒计时文字。</summary>
    public string CountdownText => CountdownLeft > 0 ? CountdownLeft.ToString() : "";

    /// <summary>对局进行中。</summary>
    public bool IsRunning => _running;

    /// <summary>现在是不是按着加速键。</summary>
    public bool IsBoosting => _isBoostKeyDown;

    /// <summary>棋盘（渲染用）。</summary>
    public SnakeRenderFrame Frame { get; private set; } = SnakeRenderFrame.Empty;

    /// <summary>模式选中项。</summary>
    public OptionItem<string> SelectedMode
    {
        get => OptionItem<string>.Find(ModeOptions, _settings.Mode == SnakeMode.SharedFruit ? "shared" : "battle") ?? ModeOptions[0];
        set
        {
            if (value is null) return;
            _settings.Mode = value.Value == "shared" ? SnakeMode.SharedFruit : SnakeMode.BattleRoyale;
            _settings.Normalize();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSharedMode));
            RefreshUi();
        }
    }

    /// <summary>是不是共享果玩法。</summary>
    public bool IsSharedMode => _settings.Mode == SnakeMode.SharedFruit;

    /// <summary>边缘规则选中项。</summary>
    public OptionItem<string> SelectedEdge
    {
        get => OptionItem<string>.Find(EdgeOptions,
            _settings.EdgeMode == SnakeEdgeMode.Stop ? "stop" : _settings.EdgeMode == SnakeEdgeMode.Wrap ? "wrap" : "die")
            ?? EdgeOptions[0];
        set
        {
            if (value is null) return;
            _settings.EdgeMode = value.Value switch
            {
                "stop" => SnakeEdgeMode.Stop,
                "wrap" => SnakeEdgeMode.Wrap,
                _ => SnakeEdgeMode.Die,
            };
            OnPropertyChanged();
        }
    }

    /// <summary>AI 等级选中项。</summary>
    public OptionItem<double> SelectedAiLevel
    {
        get => OptionItem<double>.Find(AiLevelOptions, _settings.AiLevel) ?? AiLevelOptions[2];
        set
        {
            if (value is null) return;
            _settings.AiLevel = (int)value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiLevelDescription));
        }
    }

    /// <summary>AI 等级说明。</summary>
    public string AiLevelDescription => SnakeAi.LevelDescription(_settings.AiLevel);

    // ---------------------------------------------------------------- 设置绑定

    /// <summary>棋盘宽。</summary>
    public int BoardWidth
    {
        get => _settings.Width;
        set
        {
            _settings.Width = value;
            _settings.Normalize();
            OnPropertyChanged();
        }
    }

    /// <summary>棋盘高。</summary>
    public int BoardHeight
    {
        get => _settings.Height;
        set
        {
            _settings.Height = value;
            _settings.Normalize();
            OnPropertyChanged();
        }
    }

    /// <summary>区域数。</summary>
    public int RegionCount
    {
        get => _settings.RegionCount;
        set { _settings.RegionCount = value; _settings.Normalize(); OnPropertyChanged(); }
    }

    /// <summary>人数上限。</summary>
    public int MaxPlayers
    {
        get => _settings.MaxPlayers;
        set { _settings.MaxPlayers = value; _settings.Normalize(); OnPropertyChanged(); }
    }

    /// <summary>果子上限。</summary>
    public int MaxFruits
    {
        get => _settings.MaxFruits;
        set { _settings.MaxFruits = value; _settings.Normalize(); OnPropertyChanged(); }
    }

    /// <summary>命数。</summary>
    public int Lives
    {
        get => _settings.Lives;
        set { _settings.Lives = value; _settings.Normalize(); OnPropertyChanged(); }
    }

    /// <summary>倒计时秒数。</summary>
    public int CountdownSeconds
    {
        get => _settings.CountdownSeconds;
        set { _settings.CountdownSeconds = value; _settings.Normalize(); OnPropertyChanged(); }
    }

    /// <summary>限时（秒）。</summary>
    public int TimeLimitSeconds
    {
        get => Cfg.TimeLimitSeconds;
        set
        {
            Cfg.TimeLimitSeconds = Math.Max(0, value);
            _settings = Cfg.Settings;
            _settings.Normalize();
            OnPropertyChanged();
        }
    }

    /// <summary>速度（格/秒）。</summary>
    public double Speed
    {
        get => _settings.Speed;
        set { _settings.Speed = value; _settings.Normalize(); OnPropertyChanged(); RestartTimerIfRunning(); }
    }

    /// <summary>AI 数量。</summary>
    public int AiCount
    {
        get => _settings.AiCount;
        set { _settings.AiCount = value; _settings.Normalize(); OnPropertyChanged(); }
    }

    /// <summary>开加速。</summary>
    public bool SpeedBoostEnabled
    {
        get => _settings.SpeedBoostEnabled;
        set { _settings.SpeedBoostEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>加速倍率。</summary>
    public double SpeedBoostMultiplier
    {
        get => _settings.SpeedBoostMultiplier;
        set { _settings.SpeedBoostMultiplier = Math.Clamp(value, 1.2, 4); OnPropertyChanged(); }
    }

    /// <summary>重生保留长度。</summary>
    public bool RespawnKeepsLength
    {
        get => _settings.RespawnKeepsLength;
        set { _settings.RespawnKeepsLength = value; OnPropertyChanged(); }
    }

    /// <summary>出生点爆炸。</summary>
    public bool SpawnBombEnabled
    {
        get => _settings.SpawnBombEnabled;
        set { _settings.SpawnBombEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>共享果断 combo。</summary>
    public bool ComboBreaksOnMiss
    {
        get => _settings.ComboBreaksOnMiss;
        set { _settings.ComboBreaksOnMiss = value; OnPropertyChanged(); }
    }

    // ---------------------------------------------------------------- 单机开局

    /// <summary>单机开局：自己 + N 个 AI。</summary>
    private void StartLocal()
    {
        if (_running) return;
        if (_session is not null) { SetNotice("联机时由房主开局", "DangerBrush"); return; }

        _settings = Cfg.Settings;
        _settings.Normalize();
        Cfg.Apply(_settings);

        _game = new SnakeGame(_settings, _random);

        var me = new SnakePlayer { Id = "me", Name = Cfg.EffectivePlayerName };
        _game.AddPlayer(me);
        _myId = "me";

        for (var i = 0; i < Math.Min(_settings.AiCount, _settings.MaxPlayers - 1); i++)
        {
            _game.AddPlayer(new SnakePlayer
            {
                Id = $"ai{i + 1}",
                Name = $"电脑 {i + 1}",
                IsAi = true,
                AiAlgorithm = _settings.AiLevel,
            });
        }

        var error = _game.Start();
        if (error.Length > 0) { SetNotice(error, "DangerBrush"); return; }

        ResultText = "";
        StartLoop();
        StatusText = "开局倒计时…";
        RefreshUi();
    }

    // ---------------------------------------------------------------- 循环

    private void StartLoop()
    {
        _running = true;
        _timer ??= new DispatcherTimer();
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        RestartTimerIfRunning();
        OnPropertyChanged(nameof(IsRunning));
    }

    private void StopLoop()
    {
        _running = false;
        if (_timer is not null) _timer.Stop();
        OnPropertyChanged(nameof(IsRunning));
        StatusText = "已停止";
    }

    private void RestartTimerIfRunning()
    {
        if (_timer is null || !_running) return;
        if (_timer.IsEnabled) _timer.Stop();

        var speed = Math.Max(1, _settings.Speed);
        if (_isBoostKeyDown) speed *= Math.Max(1.2, _settings.SpeedBoostMultiplier);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, 1000 / speed));
        _timer.Start();
    }

    private void SetBoost(bool down)
    {
        if (!_settings.SpeedBoostEnabled) return;
        if (_isBoostKeyDown == down) return;
        _isBoostKeyDown = down;
        OnPropertyChanged(nameof(IsBoosting));
        RestartTimerIfRunning();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running) return;

        // 房主：本地跑引擎，AI 自己走，然后广播
        if (_isHost || _session is null)
        {
            DriveAi();
            _game.Tick();
            SyncCountdown();
            RefreshUi();

            if (_game.IsOver) FinishLocal();
            else BroadcastIfHost();
            return;
        }

        // 客户端：只渲染（等房主的状态）
    }

    private void DriveAi()
    {
        foreach (var player in _game.Players)
        {
            if (!player.IsAi || !player.CanAct) continue;
            var direction = SnakeAi.ChooseDirection(_game, player.Id, player.AiAlgorithm, _random);
            if (direction is { } d) _game.SetDirection(player.Id, d);
        }
    }

    private void SyncCountdown()
    {
        CountdownLeft = _game.CountdownRemaining;
        if (CountdownLeft > 0) StatusText = $"开局倒计时 {CountdownLeft} 秒…";
        else if (!_game.IsOver) StatusText = _isBoostKeyDown ? "加速中！" : "对局进行中";
    }

    private void FinishLocal()
    {
        StopLoop();
        ResultText = _game.ResultText;
        StatusText = _game.ResultText;
        RefreshUi();
        BroadcastIfHost();
    }

    // ---------------------------------------------------------------- 输入

    /// <summary>转向（界面按键调用）。</summary>
    public void Turn(SnakeDirection direction)
    {
        if (_myId.Length == 0) return;
        if (_session is not null && !_isHost)
        {
            // 客户端：上报给房主
            _ = _session.SendDirectionAsync(direction);
            return;
        }
        _game.SetDirection(_myId, direction);
    }

    /// <summary>按住加速。</summary>
    public void BeginBoost() => SetBoost(true);

    /// <summary>松开加速。</summary>
    public void EndBoost() => SetBoost(false);

    // ---------------------------------------------------------------- 刷新

    private void RefreshUi()
    {
        Frame = SnakeRenderFrame.FromGame(_game);

        var ranking = _game.ComputeRanking();
        Players.Clear();
        foreach (var (player, score) in ranking)
        {
            var status = !player.Snake.IsAlive
                ? player.Lives > 0 ? $"等待复活（{player.RespawnIn}）" : "阵亡"
                : player.Snake.IsGhost ? "虚化保护" : "存活";

            Players.Add(new SnakePlayerRow(
                player.Id, player.Name, player.Stats.Fruits, score, status,
                player.Id == _myId, player.IsAi));
        }

        OnPropertyChanged(nameof(Frame));
        OnPropertyChanged(nameof(Players));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(AiLevelDescription));
    }

    // ---------------------------------------------------------------- 联机（房主权威）

    /// <summary>是不是房主。</summary>
    public bool IsHost => _isHost;

    /// <summary>房间状态文字。</summary>
    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    /// <summary>联机操作进行中。</summary>
    public bool NetBusy => _netBusy;

    /// <summary>能不能开房 / 加入。</summary>
    public bool CanJoin => _session is null;

    /// <summary>能不能离开。</summary>
    public bool CanLeave => _session is not null;

    /// <summary>我的玩家 id（界面高亮用）。</summary>
    public string MyPlayerId => _myId;

    /// <summary>联机徽标。</summary>
    public string NetBadgeText => _session is null ? "未联机"
        : _isHost ? $"房主 · {_session.MemberCount} 人" : $"已连上房主 · {_session.MemberCount} 人";

    /// <summary>开房命令。</summary>
    public AsyncRelayCommand StartHostCommand { get; private set; } = null!;

    /// <summary>加入命令。</summary>
    public AsyncRelayCommand JoinRoomCommand { get; private set; } = null!;

    /// <summary>离开命令。</summary>
    public AsyncRelayCommand LeaveRoomCommand { get; private set; } = null!;

    /// <summary>初始化联机命令（主构造函数里调用）。</summary>
    private void InitializeNetCommands()
    {
        StartHostCommand = new AsyncRelayCommand(_ => StartHostAsync(), _ => CanJoin);
        JoinRoomCommand = new AsyncRelayCommand(_ => JoinRoomAsync(), _ => CanJoin);
        LeaveRoomCommand = new AsyncRelayCommand(_ => LeaveRoomAsync(), _ => CanLeave);
    }

    private async Task StartHostAsync()
    {
        _netBusy = true;
        NotifyNet();
        try
        {
            var session = new SnakeNetSession(Cfg.EffectivePlayerName);
            WireSession(session);

            var error = await session.StartHostAsync(Cfg.ListenPort);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _isHost = true;
            _myId = SnakeNetSession.HostMemberId;
            SetNotice($"房间已开（端口 {session.ListenPort}），等朋友加入后点「开始游戏」", "SuccessBrush");
            await session.SendSettingsAsync(_settings);
        }
        finally
        {
            _netBusy = false;
            NotifyNet();
        }
    }

    private async Task JoinRoomAsync()
    {
        var address = (Cfg.LastJoinAddress ?? "").Trim();
        if (address.Length == 0) { SetNotice("请先填对方发来的地址", "DangerBrush"); return; }

        _netBusy = true;
        NotifyNet();
        try
        {
            var session = new SnakeNetSession(Cfg.EffectivePlayerName);
            WireSession(session);

            var error = await session.ConnectAsync(address);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _isHost = false;
            _myId = session.LocalMemberId;
            Cfg.LastJoinAddress = address;
            SetNotice("已连上房主，等房主开局", "SuccessBrush");
        }
        finally
        {
            _netBusy = false;
            NotifyNet();
        }
    }

    private async Task LeaveRoomAsync()
    {
        var session = _session;
        _session = null;
        _isHost = false;
        _myId = "";
        StopLoop();

        if (session is not null)
        {
            try { await session.StopAsync(); } catch { /* 忽略 */ }
            try { await session.DisposeAsync(); } catch { /* 忽略 */ }
        }

        SetNotice("已离开房间", "TextSecondaryBrush");
        NotifyNet();
    }

    private void WireSession(SnakeNetSession session)
    {
        session.PlayersChanged += () => Ui(NotifyNet);
        session.SettingsReceived += settings => Ui(() =>
        {
            _settings = settings;
            _settings.Normalize();
            Cfg.Apply(_settings);
            OnPropertyChanged(string.Empty);
            RefreshUi();
        });
        session.StateReceived += frame => Ui(() =>
        {
            if (_isHost) return;
            Frame = frame;
            OnPropertyChanged(nameof(Frame));
            Players.Clear();
            foreach (var p in frame.Rows) Players.Add(p);
            OnPropertyChanged(nameof(Players));
            ResultText = frame.ResultText;
            StatusText = frame.ResultText.Length > 0 ? frame.ResultText : "对局进行中";
        });
        session.DirectionReceived += (memberId, direction) => Ui(() =>
        {
            if (_isHost) _game.SetDirection(memberId, direction);
        });
    }

    private void BroadcastIfHost()
    {
        if (!_isHost || _session is null) return;
        _ = _session.BroadcastAsync(Frame, ResultText);
    }

    private void NotifyNet()
    {
        OnPropertyChanged(nameof(NetBadgeText));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanLeave));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(NetBusy));
    }

    private void SetNotice(string text, string brush)
    {
        NoticeText = text;
        _ = brush;
    }

    /// <summary>截图 / 演示：直接摆一局给人看。</summary>
    private void LoadDemoIfRequested()
    {
        if (Environment.GetEnvironmentVariable("YLT_SNAKE_DEMO") != "1") return;
        StartLocal();
        // 跑几步让蛇动起来
        for (var i = 0; i < 40; i++)
        {
            DriveAi();
            _game.Tick();
        }
        RefreshUi();
        SyncCountdown();
        StatusText = "演示局面";
    }
}
