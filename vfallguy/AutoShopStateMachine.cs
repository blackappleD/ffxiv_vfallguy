using System;
using System.Numerics;

namespace vfallguy;

// 自动购物状态机：检测金碟声誉达到阈值 -> 暂停自动报名 -> 调用 GBR 购物 -> 走回登记员 -> 恢复自动报名
public class AutoShopStateMachine : IDisposable
{
    private enum State
    {
        Idle,               // 闲置，监控金碟声誉
        Shopping,           // GBR 购物中
        WaitShopComplete,   // 等待购物完成
        ReturningToNPC,     // 走回登记员
        WaitArrival,        // 等待到达登记员
    }

    private readonly Configuration _config;
    private readonly GatherBuddyIPC _ipc;
    private State _state = State.Idle;
    private DateTime _stateEnterTime;
    private bool _autoJoinWasActive;

    // 糖豆人登记员位置（大厅 1197）
    private static readonly Vector3 RegistratorPos = new(0, 0.25f, -298);

    public bool IsBusy => _state != State.Idle;
    public string StatusText => _state switch
    {
        State.Shopping => "购物中...",
        State.WaitShopComplete => $"等待购物完成 ({_ipc.GetVendorBuyListStatus()})",
        State.ReturningToNPC => "返回登记员...",
        State.WaitArrival => "等待到达登记员...",
        _ => ""
    };

    public AutoShopStateMachine(Configuration config, GatherBuddyIPC ipc)
    {
        _config = config;
        _ipc = ipc;
    }

    public void Dispose()
    {
        if (_state != State.Idle)
            Abort();
    }

    // 每帧调用，autoJoinActive 表示当前自动报名是否开启
    public void Update(DateTime now, int currentMGP, bool autoJoinActive)
    {
        switch (_state)
        {
            case State.Idle:
                // 只在大厅、自动报名开启、达到阈值且 GBR/vnavmesh 可用时触发
                if (!_config.AutoShopEnabled || string.IsNullOrWhiteSpace(_config.AutoShopVendorList))
                    break;
                if (Service.ClientState.TerritoryType != 1197 || !autoJoinActive)
                    break;
                if (currentMGP < _config.AutoShopThreshold)
                    break;
                if (!_ipc.GatherBuddyAvailable || !_ipc.VNavmeshAvailable)
                {
                    Service.Log.Warning("AutoShop: GatherBuddy Reborn 或 vnavmesh 不可用");
                    Service.ChatGui.PrintError("[vfallguy] 自动购物需要 GatherBuddy Reborn 和 vnavmesh 插件");
                    _config.AutoShopEnabled = false;
                    _config.Save();
                    break;
                }

                Service.Log.Info($"AutoShop: 触发购物，当前声誉 {currentMGP}，阈值 {_config.AutoShopThreshold}");
                Service.ChatGui.Print($"[vfallguy] 金碟声誉达到 {currentMGP}，开始自动购物");
                _autoJoinWasActive = true;
                EnterState(State.Shopping, now);
                break;

            case State.Shopping:
                var result = _ipc.StartVendorBuyList(_config.AutoShopVendorList);
                if (result == -1) // GatherBuddyIpc.VendorBuyListNotFound
                {
                    Service.Log.Error($"AutoShop: 购物清单 '{_config.AutoShopVendorList}' 不存在");
                    Service.ChatGui.PrintError($"[vfallguy] 购物清单 '{_config.AutoShopVendorList}' 不存在，已停止自动购物");
                    _config.AutoShopEnabled = false;
                    _config.Save();
                    EnterState(State.Idle, now);
                }
                else if (result == 0) // StartResult.Success
                {
                    Service.Log.Debug("AutoShop: 购物已启动");
                    EnterState(State.WaitShopComplete, now);
                }
                else
                {
                    Service.Log.Warning($"AutoShop: StartVendorBuyList 返回 {result}");
                    EnterState(State.WaitShopComplete, now);
                }
                break;

            case State.WaitShopComplete:
                if (!_ipc.IsVendorBuyListBusy())
                {
                    var (outcome, hitLimit) = _ipc.GetVendorBuyListLastRun();
                    Service.Log.Info($"AutoShop: 购物完成，结果 {outcome}，是否达货币上限 {hitLimit}");
                    Service.ChatGui.Print($"[vfallguy] 购物完成{(hitLimit ? "（达到货币上限）" : "")}，返回登记员");
                    EnterState(State.ReturningToNPC, now);
                }
                else if ((now - _stateEnterTime).TotalSeconds > 180)
                {
                    Service.Log.Warning("AutoShop: 购物超时，强制停止");
                    _ipc.StopVendorBuyList();
                    EnterState(State.ReturningToNPC, now);
                }
                break;

            case State.ReturningToNPC:
                if (Service.ClientState.TerritoryType != 1197)
                {
                    Service.Log.Warning("AutoShop: 已离开大厅，中止返回");
                    EnterState(State.Idle, now);
                    break;
                }
                if (_ipc.PathfindAndMoveTo(RegistratorPos, false))
                {
                    Service.Log.Debug("AutoShop: 寻路已启动");
                    EnterState(State.WaitArrival, now);
                }
                else
                {
                    Service.Log.Warning("AutoShop: 寻路失败");
                    Service.ChatGui.PrintError("[vfallguy] 返回登记员失败，请手动返回");
                    EnterState(State.Idle, now);
                }
                break;

            case State.WaitArrival:
                var playerPos = Service.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                var dist = Vector3.Distance(playerPos, RegistratorPos);
                if (dist < 5 || !_ipc.IsPathfindRunning())
                {
                    _ipc.StopPathfind();
                    Service.Log.Info($"AutoShop: 已到达登记员（距离 {dist:f1}）");
                    Service.ChatGui.Print("[vfallguy] 已返回登记员，恢复自动报名");
                    EnterState(State.Idle, now);
                }
                else if ((now - _stateEnterTime).TotalSeconds > 60)
                {
                    Service.Log.Warning("AutoShop: 返回超时");
                    _ipc.StopPathfind();
                    Service.ChatGui.PrintError("[vfallguy] 返回登记员超时，请手动返回");
                    EnterState(State.Idle, now);
                }
                break;
        }
    }

    public bool ShouldSuspendAutoJoin()
        => _state != State.Idle && _autoJoinWasActive;

    private void EnterState(State newState, DateTime now)
    {
        _state = newState;
        _stateEnterTime = now;
        if (newState == State.Idle)
            _autoJoinWasActive = false;
    }

    public void Abort()
    {
        if (_state == State.WaitShopComplete || _state == State.Shopping)
            _ipc.StopVendorBuyList();
        if (_state == State.WaitArrival || _state == State.ReturningToNPC)
            _ipc.StopPathfind();
        _state = State.Idle;
        _autoJoinWasActive = false;
    }
}
