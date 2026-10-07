using Dalamud.Plugin;
using System;
using System.Numerics;

namespace vfallguy;

// 通过 IPC 调用 GatherBuddy Reborn 的自动购物功能和 vnavmesh 的寻路
public class GatherBuddyIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly Func<string, int>? _vendorBuyListStart;
    private readonly Func<bool>? _vendorBuyListIsBusy;
    private readonly Func<string>? _vendorBuyListStatusText;
    private readonly Func<(int, bool)>? _vendorBuyListLastRun;
    private readonly Action? _vendorBuyListStop;
    private readonly Func<Vector3, bool, bool>? _vnavmeshMoveTo;
    private readonly Func<bool>? _vnavmeshIsRunning;
    private readonly Action? _vnavmeshStop;

    public bool GatherBuddyAvailable { get; }
    public bool VNavmeshAvailable { get; }

    public GatherBuddyIPC(IDalamudPluginInterface pi)
    {
        _pi = pi;
        try
        {
            _vendorBuyListStart = pi.GetIpcSubscriber<string, int>("GatherBuddyReborn.VendorBuyListStart").InvokeFunc;
            _vendorBuyListIsBusy = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.VendorBuyListIsBusy").InvokeFunc;
            _vendorBuyListStatusText = pi.GetIpcSubscriber<string>("GatherBuddyReborn.VendorBuyListStatusText").InvokeFunc;
            _vendorBuyListLastRun = pi.GetIpcSubscriber<(int, bool)>("GatherBuddyReborn.VendorBuyListLastRun").InvokeFunc;
            _vendorBuyListStop = pi.GetIpcSubscriber<object>("GatherBuddyReborn.VendorBuyListStop").InvokeAction;
            GatherBuddyAvailable = true;
        }
        catch
        {
            GatherBuddyAvailable = false;
        }

        try
        {
            _vnavmeshMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc;
            _vnavmeshIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc;
            _vnavmeshStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction;
            VNavmeshAvailable = true;
        }
        catch
        {
            VNavmeshAvailable = false;
        }
    }

    public void Dispose() { }

    public int StartVendorBuyList(string listName)
    {
        if (_vendorBuyListStart == null)
            return -1;
        try { return _vendorBuyListStart(listName); }
        catch (Exception e) { Service.Log.Error($"GBR VendorBuyListStart failed: {e}"); return -1; }
    }

    public bool IsVendorBuyListBusy()
    {
        if (_vendorBuyListIsBusy == null)
            return false;
        try { return _vendorBuyListIsBusy(); }
        catch { return false; }
    }

    public string GetVendorBuyListStatus()
    {
        if (_vendorBuyListStatusText == null)
            return "";
        try { return _vendorBuyListStatusText(); }
        catch { return ""; }
    }

    public (int outcome, bool hitCurrencyLimit) GetVendorBuyListLastRun()
    {
        if (_vendorBuyListLastRun == null)
            return (-1, false);
        try { return _vendorBuyListLastRun(); }
        catch { return (-1, false); }
    }

    public void StopVendorBuyList()
    {
        try { _vendorBuyListStop?.Invoke(); }
        catch (Exception e) { Service.Log.Error($"GBR VendorBuyListStop failed: {e}"); }
    }

    public bool PathfindAndMoveTo(Vector3 pos, bool fly)
    {
        if (_vnavmeshMoveTo == null)
            return false;
        try { return _vnavmeshMoveTo(pos, fly); }
        catch (Exception e) { Service.Log.Error($"vnavmesh MoveTo failed: {e}"); return false; }
    }

    public bool IsPathfindRunning()
    {
        if (_vnavmeshIsRunning == null)
            return false;
        try { return _vnavmeshIsRunning(); }
        catch { return false; }
    }

    public void StopPathfind()
    {
        try { _vnavmeshStop?.Invoke(); }
        catch (Exception e) { Service.Log.Error($"vnavmesh Stop failed: {e}"); }
    }
}
