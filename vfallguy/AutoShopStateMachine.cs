using Dalamud.Game.ClientState.Conditions;
using System;
using System.Linq;
using System.Numerics;

namespace vfallguy;

// 金碟声誉自动购物：达到阈值 -> 推送购买清单给 GBR -> GBR 购买 -> 走回节目登记员 -> 继续自动报名。
// 任何一步失败、或买完后声誉仍不低于阈值，都会通过 Failed 事件通知关闭自动报名，避免声誉溢出。
public class AutoShopStateMachine : IDisposable
{
    private enum State
    {
        Idle,
        PushList,       // 写入并启动 GBR 购买清单（商店数据加载中时重试）
        WaitShop,       // 等待 GBR 购买完成
        ReturnToNpc,    // 走回节目登记员
        WaitArrival,
    }

    private const float RegistratorRange = 3;
    private const double RetryInterval = 2, PushTimeout = 60, ShopTimeout = 600, ReturnTimeout = 30, ArrivalTimeout = 60;

    private readonly Configuration _config;
    private readonly GatherBuddyIPC _ipc;
    private State _state = State.Idle;
    private DateTime _stateStart;
    private DateTime _nextAttempt;

    public event Action<string>? Failed;

    public bool IsBusy => _state != State.Idle;
    public string StatusText => _state switch
    {
        State.PushList => "正在写入 GBR 购买清单...",
        State.WaitShop => $"GBR 购买中: {_ipc.ListStatus()}",
        State.ReturnToNpc or State.WaitArrival => "正在走回节目登记员...",
        _ => "",
    };

    public AutoShopStateMachine(Configuration config, GatherBuddyIPC ipc)
    {
        _config = config;
        _ipc = ipc;
    }

    public void Dispose() => Abort();

    // canStart：当前可以开始购物（在大厅、开启了自动报名、没有排队或切换场景）
    public void Update(DateTime now, bool canStart)
    {
        switch (_state)
        {
            case State.Idle:
                if (canStart && _config.AutoShopEnabled && ReputationShop.GetCurrency() >= _config.AutoShopThreshold)
                    Begin(now);
                break;

            case State.PushList:
                if (now >= _nextAttempt)
                    TryPushAndStart(now);
                break;

            case State.WaitShop:
                // 刚启动时 GBR 可能还没进入忙碌状态
                if ((now - _stateStart).TotalSeconds < 2 || _ipc.IsListBusy())
                {
                    if ((now - _stateStart).TotalSeconds > ShopTimeout)
                    {
                        _ipc.StopList();
                        Fail("GBR 购买超时");
                    }
                    break;
                }
                var (outcome, hitCurrencyLimit) = _ipc.LastRun();
                Service.Log.Info($"AutoShop: run finished, outcome={outcome}, hitCurrencyLimit={hitCurrencyLimit}");
                if (outcome is GatherBuddyIPC.OutcomeFailed or GatherBuddyIPC.OutcomeStopped)
                {
                    Fail($"GBR 购买未完成: {_ipc.ListStatus()}");
                    break;
                }
                var remaining = ReputationShop.GetCurrency();
                if (remaining >= _config.AutoShopThreshold)
                {
                    Fail($"购买后金碟声誉仍有 {remaining}（阈值 {_config.AutoShopThreshold}），请在购物清单中添加更多物品或提高目标数量");
                    break;
                }
                Service.ChatGui.Print($"[vfallguy] 购买完成，剩余金碟声誉 {remaining}，正在走回节目登记员");
                Enter(State.ReturnToNpc, now);
                break;

            case State.ReturnToNpc:
                if ((now - _stateStart).TotalSeconds > ReturnTimeout)
                {
                    Fail("走回节目登记员超时");
                    break;
                }
                if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.OccupiedInEvent])
                    break; // 等待商店窗口关闭
                if (FindRegistrator() is not { } dest)
                {
                    Fail("找不到节目登记员");
                    break;
                }
                if (DistanceToPlayer(dest) <= RegistratorRange + 1)
                {
                    Finish();
                    break;
                }
                if (!_ipc.MoveCloseTo(dest, RegistratorRange))
                {
                    Fail("vnavmesh 寻路失败");
                    break;
                }
                Enter(State.WaitArrival, now);
                break;

            case State.WaitArrival:
                if (FindRegistrator() is not { } target)
                {
                    _ipc.StopNav();
                    Fail("找不到节目登记员");
                }
                else if (DistanceToPlayer(target) <= RegistratorRange + 1)
                {
                    _ipc.StopNav();
                    Finish();
                }
                else if ((now - _stateStart).TotalSeconds > ArrivalTimeout)
                {
                    _ipc.StopNav();
                    Fail("走回节目登记员超时");
                }
                else if ((now - _stateStart).TotalSeconds > 1 && !_ipc.NavBusy)
                {
                    // 寻路结束但没走到（例如被打断），重新寻路
                    _state = State.ReturnToNpc;
                }
                break;
        }
    }

    public void Abort()
    {
        if (_state == State.WaitShop)
            _ipc.StopList();
        if (_state == State.WaitArrival)
            _ipc.StopNav();
        _state = State.Idle;
    }

    private void Begin(DateTime now)
    {
        var currency = ReputationShop.GetCurrency();
        Service.Log.Info($"AutoShop: triggered at {currency}/{_config.AutoShopThreshold}");
        if (!_ipc.GbrReady)
        {
            Fail(_ipc.GbrVersion == 0 ? "GatherBuddy Reborn 未加载" : "GatherBuddy Reborn 版本过低，请更新");
            return;
        }
        if (!_ipc.NavReady)
        {
            Fail("vnavmesh 未加载或导航网格未就绪");
            return;
        }
        Service.ChatGui.Print($"[vfallguy] 金碟声誉达到 {currency}，开始自动购物");
        Enter(State.PushList, now);
        _nextAttempt = now;
    }

    private void TryPushAndStart(DateTime now)
    {
        if ((now - _stateStart).TotalSeconds > PushTimeout)
        {
            Fail("GBR 商店数据长时间未就绪");
            return;
        }

        var requests = BuildRequests();
        if (requests.Length == 0)
        {
            Fail("购物清单中没有可购买的物品（未勾选或都已学习）");
            return;
        }

        var written = _ipc.ReplaceList(requests);
        if (written is GatherBuddyIPC.ReplaceBusy or GatherBuddyIPC.ReplaceNotReady)
        {
            _nextAttempt = now.AddSeconds(RetryInterval);
            return;
        }
        if (written <= 0)
        {
            Fail($"写入 GBR 购买清单失败（{written}）");
            return;
        }
        if (written < requests.Length)
            Service.Log.Warning($"AutoShop: GBR resolved only {written}/{requests.Length} items");

        var started = _ipc.StartList();
        switch (started)
        {
            case GatherBuddyIPC.StartStarted or GatherBuddyIPC.StartAlreadyRunning or GatherBuddyIPC.StartWaitingForPreviousInteraction:
                Enter(State.WaitShop, now);
                break;
            case GatherBuddyIPC.StartVendorDataLoading or GatherBuddyIPC.StartLocationDataLoading:
                _nextAttempt = now.AddSeconds(RetryInterval);
                break;
            case GatherBuddyIPC.StartNoPendingEntries:
                Fail("清单中的物品都已达到目标持有数量");
                break;
            default:
                Fail($"启动 GBR 购买失败（{started}）: {_ipc.ListStatus()}");
                break;
        }
    }

    // 已学习的可学习物品不再购买
    private (uint, uint)[] BuildRequests()
    {
        var items = ReputationShop.Items.ToDictionary(i => i.ItemId);
        return [.. _config.AutoShopItems
            .Where(kv => kv.Value > 0 && items.TryGetValue(kv.Key, out var item) && !ReputationShop.IsLearned(item))
            .Select(kv => (kv.Key, kv.Value))];
    }

    private void Finish()
    {
        Service.ChatGui.Print("[vfallguy] 已回到节目登记员，继续自动报名");
        _state = State.Idle;
    }

    private void Fail(string reason)
    {
        Service.Log.Warning($"AutoShop failed: {reason}");
        Abort();
        Failed?.Invoke(reason);
    }

    private void Enter(State state, DateTime now)
    {
        _state = state;
        _stateStart = now;
    }

    private static Vector3? FindRegistrator()
        => Service.ObjectTable.FirstOrDefault(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc && o.BaseId == ReputationShop.RegistratorNpcId)?.Position;

    private static float DistanceToPlayer(Vector3 pos)
        => Service.ObjectTable.LocalPlayer is { } player ? Vector3.Distance(player.Position, pos) : float.MaxValue;
}
