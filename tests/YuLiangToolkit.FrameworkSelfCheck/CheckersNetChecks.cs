using YuLiangToolkit.Tools.BoardGames.Checkers;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 跳棋联机自检：真回环（本机 127.0.0.1 开房 → 客户端连上 → 选座 → 走棋 → 状态广播）。
///
/// <para><b>只绑 127.0.0.1，不联外网。</b>验证的是「房主权威」这条链路：
/// 客户端上报选座 / 走棋，房主跑规则并广播完整状态。</para>
/// </summary>
internal static class CheckersNetChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckLoopback(fn);
        CheckStateRoundTrip(fn);
    }

    private static void CheckLoopback(CheckFn check)
    {
        var host = new CheckersNetSession("房主");
        var client = new CheckersNetSession("好友");

        try
        {
            var hostError = host.StartHostAsync(0).GetAwaiter().GetResult();
            check("[跳棋·联机] 房主能在本机开房间",
                hostError.Length == 0 && host.ListenPort > 0,
                hostError.Length > 0 ? hostError : $"端口 {host.ListenPort}");

            if (hostError.Length > 0) return;

            var sitReceived = new TaskCompletionSource<(string MemberId, CheckersPoint Point)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var moveReceived = new TaskCompletionSource<(int, int, int, int)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stateReceived = new TaskCompletionSource<CheckersNetState>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            host.SitRequested += (memberId, point) => sitReceived.TrySetResult((memberId, point));
            host.MoveRequested += (memberId, fx, fy, tx, ty) => moveReceived.TrySetResult((fx, fy, tx, ty));
            client.StateReceived += state => stateReceived.TrySetResult(state);

            var joinError = client.ConnectAsync($"127.0.0.1:{host.ListenPort}").GetAwaiter().GetResult();
            check("[跳棋·联机] 客户端能连上房主",
                joinError.Length == 0 && !client.IsHost,
                joinError.Length > 0 ? joinError : "已连上");

            if (joinError.Length > 0) return;

            // ① 客户端请求选座
            client.SendSitAsync(CheckersPoint.North).GetAwaiter().GetResult();
            check("[跳棋·联机] 客户端选座能到房主（房主收到要坐的角）",
                sitReceived.Task.Wait(TimeSpan.FromSeconds(5))
                && sitReceived.Task.Result.Point == CheckersPoint.North,
                sitReceived.Task.IsCompleted ? sitReceived.Task.Result.Point.ToString() : "（没收到）");

            // ② 房主广播状态 → 客户端收到
            var state = new CheckersNetState
            {
                Seats = new[] { "n=好友|好友", "", "", "", "", "" },
                PlayerCount = 2,
                CurrentPlayerId = "好友",
                Pieces = new[] { "8,0,好友" },
                Started = true,
                LastText = "开局",
            };
            host.BroadcastAsync(state).GetAwaiter().GetResult();

            check("[跳棋·联机] 房主广播状态能到客户端（座位 / 棋子 / 轮到谁）",
                stateReceived.Task.Wait(TimeSpan.FromSeconds(5))
                && stateReceived.Task.Result.Seats.Length == 6
                && stateReceived.Task.Result.Pieces.Length == 1
                && stateReceived.Task.Result.CurrentPlayerId == "好友",
                stateReceived.Task.IsCompleted ? "已收到" : "（没收到）");

            // ③ 客户端上报走棋 → 房主收到
            client.SendMoveAsync(8, 0, 8, 1).GetAwaiter().GetResult();
            check("[跳棋·联机] 客户端上报走棋能到房主",
                moveReceived.Task.Wait(TimeSpan.FromSeconds(5))
                && moveReceived.Task.Result == (8, 0, 8, 1),
                moveReceived.Task.IsCompleted ? moveReceived.Task.Result.ToString() : "（没收到）");

            // ④ 客户端退出座位
            var leaveReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.LeaveSeatRequested += memberId => leaveReceived.TrySetResult(memberId);
            client.SendLeaveSeatAsync().GetAwaiter().GetResult();

            check("[跳棋·联机] 客户端能请求退出座位",
                leaveReceived.Task.Wait(TimeSpan.FromSeconds(5))
                && leaveReceived.Task.Result == "好友",
                leaveReceived.Task.IsCompleted ? leaveReceived.Task.Result : "（没收到）");
        }
        finally
        {
            try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* 忽略 */ }
            try { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* 忽略 */ }
        }
    }

    private static void CheckStateRoundTrip(CheckFn check)
    {
        // 状态包能不能被序列化 / 反序列化（联机时走 JSON）
        var state = new CheckersNetState
        {
            Seats = new[] { "n=甲|甲", "", "", "", "", "" },
            PlayerCount = 3,
            CurrentPlayerId = "甲",
            Pieces = new[] { "1,2,甲", "3,4,乙" },
            Started = true,
            AllowSingleStep = false,
            MustClearHomeFirst = true,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<CheckersNetState>(json);

        check("[跳棋·联机] 状态包 JSON 往返正确",
            back is not null
            && back.PlayerCount == 3
            && back.Seats.Length == 6
            && back.Pieces.Length == 2
            && back.Started
            && !back.AllowSingleStep,
            back is null ? "反序列化失败" : $"{back.Pieces.Length} 枚棋子");

        check("[跳棋·联机] 房主 id 常量稳定（客户端靠它判断房主）",
            CheckersNetSession.HostMemberId == "host", "");
    }
}
