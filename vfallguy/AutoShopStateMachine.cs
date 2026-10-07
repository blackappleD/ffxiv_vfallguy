using Dalamud.Game.ClientState.Conditions;
using System;
using System.Linq;
using System.Numerics;

namespace vfallguy;

// 金碟声誉自动购物：达到阈值 -> 推送购买清单给 GBR -> GBR 购买 -> 走回节目登记员 -> 继续自动报名。
// 达到阈值但没有需要购买的物品时跳过，继续自动报名；购物流程出错时通过 Failed 事件通知关闭自动报名。
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
    private int _currencyAtStart;
    // 声誉保持为该值期间不再触发购物（上次购物一件没买成，或 GBR 认为没有待购物品），避免反复触发
    private int? _suppressedAtCurrency;
    private bool _skipNotified;

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
                UpdateIdle(now, canStart);
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
                if (remaining >= _currencyAtStart)
                {
                    // 一件没买成：声誉变化之前不再触发，避免买不到时反复往返
                    _suppressedAtCurrency = remaining;
                    Service.ChatGui.Print($"[vfallguy] 本次没有买到物品，正在走回节目登记员继续自动报名");
                }
                else
                {
                    Service.ChatGui.Print($"[vfallguy] 购买完成，剩余金碟声誉 {remaining}，正在走回节目登记员");
                }
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

    private void UpdateIdle(DateTime now, bool canStart)
    {
        var currency = ReputationShop.GetCurrency();
        if (_suppressedAtCurrency is { } suppressed && suppressed != currency)
            _suppressedAtCurrency = null;
        if (currency < _config.AutoShopThreshold)
            _skipNotified = false;

        if (!canStart || !_config.AutoShopEnabled || currency < _config.AutoShopThreshold || _suppressedAtCurrency != null)
            return;

        if (ReputationShop.GetPendingRequests(_config).Length == 0)
        {
            Skip(currency);
            return;
        }
        Begin(now, currency);
    }

    // 达到阈值但没有需要购买的物品：不购物，继续自动报名
    private void Skip(int currency)
    {
        if (!_skipNotified)
        {
            Service.ChatGui.Print($"[vfallguy] 金碟声誉 {currency} 已达到阈值，但没有需要购买的物品，继续自动报名");
            _skipNotified = true;
        }
    }

    private void Begin(DateTime now, int currency)
    {
        Service.Log.Info($"AutoShop: triggered at {currency}/{_config.AutoShopThreshold}");
        if (!_ipc.GbrReady)
        {
            Fail(_ipc.GbrVersion == 0 ? "GatherBuddy Reborn 未加载"
                : _ipc.GbrVersion < GatherBuddyIPC.RequiredGbrIpcVersion ? "GatherBuddy Reborn 版本过低，请更新"
                : "GatherBuddy Reborn 的购买清单接口未注册，请更新 GatherBuddy Reborn");
            return;
        }
        if (!_ipc.NavReady)
        {
            Fail("vnavmesh 未加载或导航网格未就绪");
            return;
        }
        Service.ChatGui.Print($"[vfallguy] 金碟声誉达到 {currency}，开始自动购物");
        _currencyAtStart = currency;
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

        var requests = ReputationShop.GetPendingRequests(_config);
        if (requests.Length == 0)
        {
            SkipAndResume(_currencyAtStart);
            return;
        }

        var written = _ipc.ReplaceList(requests);
        if (written == int.MinValue)
        {
            Fail("调用 GBR 购买清单接口失败（接口未注册或已卸载），请更新 GatherBuddy Reborn");
            return;
        }
        if (written is GatherBuddyIPC.ReplaceBusy or GatherBuddyIPC.ReplaceNotReady)
        {
            _nextAttempt = now.AddSeconds(RetryInterval);
            return;
        }
        if (written == 0)
        {
            Fail("GBR 无法为所选物品找到支持自动购买的商人（当前 GBR 版本可能不支持金碟声誉兑换员）");
            return;
        }
        if (written < 0)
        {
            Fail($"写入 GBR 购买清单失败（{written}）");
            return;
        }
        if (written < requests.Length)
        {
            Service.Log.Warning($"AutoShop: GBR resolved only {written}/{requests.Length} items");
            Service.ChatGui.Print($"[vfallguy] GBR 只写入了 {written}/{requests.Length} 个物品，其余物品无法自动购买");
        }

        var started = _ipc.StartList();
        if (started == int.MinValue)
        {
            Fail("调用 GBR 启动购买接口失败（接口未注册或已卸载）");
            return;
        }
        switch (started)
        {
            case GatherBuddyIPC.StartStarted or GatherBuddyIPC.StartAlreadyRunning or GatherBuddyIPC.StartWaitingForPreviousInteraction:
                Enter(State.WaitShop, now);
                break;
            case GatherBuddyIPC.StartVendorDataLoading or GatherBuddyIPC.StartLocationDataLoading:
                _nextAttempt = now.AddSeconds(RetryInterval);
                break;
            case GatherBuddyIPC.StartNoPendingEntries:
                // GBR 认为都已达到目标数量（计数口径可能与 vfallguy 略有不同），跳过并继续报名
                SkipAndResume(ReputationShop.GetCurrency());
                break;
            default:
                Fail($"启动 GBR 购买失败（{started}）: {_ipc.ListStatus()}");
                break;
        }
    }

    // 还没开始移动就发现无需购买：回到闲置并在声誉变化前不再触发
    private void SkipAndResume(int currency)
    {
        _state = State.Idle;
        _suppressedAtCurrency = currency;
        Skip(currency);
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
