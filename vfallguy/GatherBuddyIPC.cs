using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using System;
using System.Numerics;

namespace vfallguy;

// 通过 IPC 调用 GatherBuddy Reborn 的商店购买清单和 vnavmesh 的寻路。
// 订阅对象创建时不需要对方插件已加载，可用性在每次调用时检查。
public class GatherBuddyIPC : IDisposable
{
    public const int RequiredGbrIpcVersion = 5;
    public const string ListName = "vfallguy 金碟声誉";

    // GBR VendorBuyListStartResult 中需要区分的值
    public const int StartStarted = 0, StartAlreadyRunning = 1, StartAutomationUnavailable = 2, StartWaitingForPreviousInteraction = 3,
        StartNoPendingEntries = 6, StartVendorDataLoading = 7, StartLocationDataLoading = 8;
    // GBR VendorBuyListReplaceByName 错误码
    public const int ReplaceBusy = -3, ReplaceNotReady = -4;
    // GBR RunOutcome
    public const int OutcomeFailed = 3, OutcomeStopped = 4;

    private readonly ICallGateSubscriber<int> _gbrVersion;
    private readonly ICallGateSubscriber<string, (uint, uint)[], int> _gbrReplace;
    private readonly ICallGateSubscriber<string, int> _gbrStart;
    private readonly ICallGateSubscriber<bool> _gbrIsBusy;
    private readonly ICallGateSubscriber<string> _gbrStatus;
    private readonly ICallGateSubscriber<(int, bool)> _gbrLastRun;
    private readonly ICallGateSubscriber<object> _gbrStop;
    private readonly ICallGateSubscriber<bool> _navIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _navMoveCloseTo;
    private readonly ICallGateSubscriber<bool> _navPathfindInProgress;
    private readonly ICallGateSubscriber<bool> _navPathIsRunning;
    private readonly ICallGateSubscriber<object> _navStop;

    public GatherBuddyIPC(IDalamudPluginInterface pi)
    {
        _gbrVersion = pi.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
        _gbrReplace = pi.GetIpcSubscriber<string, (uint, uint)[], int>("GatherBuddyReborn.VendorBuyListReplaceByName");
        _gbrStart = pi.GetIpcSubscriber<string, int>("GatherBuddyReborn.VendorBuyListStart");
        _gbrIsBusy = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.VendorBuyListIsBusy");
        _gbrStatus = pi.GetIpcSubscriber<string>("GatherBuddyReborn.VendorBuyListStatusText");
        _gbrLastRun = pi.GetIpcSubscriber<(int, bool)>("GatherBuddyReborn.VendorBuyListLastRun");
        _gbrStop = pi.GetIpcSubscriber<object>("GatherBuddyReborn.VendorBuyListStop");
        _navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _navMoveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _navPathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _navPathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _navStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public void Dispose() { }

    private int _cachedGbrVersion;
    private bool _cachedNavReady;
    private DateTime _nextAvailabilityCheck;

    // GBR 未加载时返回 0；可用性每秒检查一次，避免界面每帧调用未注册的 IPC 抛异常
    public int GbrVersion
    {
        get
        {
            RefreshAvailability();
            return _cachedGbrVersion;
        }
    }
    public bool GbrReady => GbrVersion >= RequiredGbrIpcVersion;

    public int ReplaceList((uint ItemId, uint TargetQuantity)[] items) => Try(() => _gbrReplace.InvokeFunc(ListName, items), int.MinValue);
    public int StartList() => Try(() => _gbrStart.InvokeFunc(ListName), int.MinValue);
    public bool IsListBusy() => Try(() => _gbrIsBusy.InvokeFunc(), false);
    public string ListStatus() => Try(() => _gbrStatus.InvokeFunc(), "");
    public (int Outcome, bool HitCurrencyLimit) LastRun() => Try(() => _gbrLastRun.InvokeFunc(), (-1, false));
    public void StopList() => Try(() => { _gbrStop.InvokeAction(); return true; }, false);

    public bool NavReady
    {
        get
        {
            RefreshAvailability();
            return _cachedNavReady;
        }
    }
    public bool MoveCloseTo(Vector3 dest, float range) => Try(() => _navMoveCloseTo.InvokeFunc(dest, false, range), false);
    public bool NavBusy => Try(() => _navPathfindInProgress.InvokeFunc() || _navPathIsRunning.InvokeFunc(), false);
    public void StopNav() => Try(() => { _navStop.InvokeAction(); return true; }, false);

    private void RefreshAvailability()
    {
        var now = DateTime.Now;
        if (now < _nextAvailabilityCheck)
            return;
        _nextAvailabilityCheck = now.AddSeconds(1);
        _cachedGbrVersion = Try(() => _gbrVersion.InvokeFunc(), 0);
        _cachedNavReady = Try(() => _navIsReady.InvokeFunc(), false);
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try
        {
            return f();
        }
        catch (IpcNotReadyError)
        {
            // 对方插件未加载
            return fallback;
        }
        catch (Exception e)
        {
            Service.Log.Error($"IPC call failed: {e}");
            return fallback;
        }
    }
}
