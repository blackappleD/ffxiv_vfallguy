using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace vfallguy;

public class MainWindow : Window, IDisposable
{
    private GameEvents _gameEvents = new();
    private DebugDrawer _drawer = new();
    private AutoJoinLeave _automation = new();
    private ReputationTracker _reputation;
    private GatherBuddyIPC _ipc;
    private AutoShopStateMachine _autoShop;
    private Map? _map;
    private DateTime _now;
    private Vector3 _prevPos;
    private Vector3 _movementDirection;
    private float _movementSpeed;
    private bool _autoJoin;
    private bool _autoLeaveIfNotSolo;
    private bool _showAOEs;
    private bool _showAOEText;
    private bool _showPathfind;
    private DateTime _autoJoinAt = DateTime.MaxValue;
    private DateTime _autoLeaveAt = DateTime.MaxValue;
    private int _numPlayersInDuty;
    private float _autoJoinDelay = 0.5f;
    private float _autoLeaveDelay = 3;
    private int _autoLeaveLimit = 1;

    public MainWindow(Configuration config) : base("vfailguy")
    {
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        _reputation = new(config);
        _reputation.ReputationGained += OnReputationGained;
        _ipc = new(Service.PluginInterface);
        _autoShop = new(config, _ipc);
        _autoShop.Failed += OnAutoShopFailed;
    }

    public void Dispose()
    {
        _map?.Dispose();
        _gameEvents.Dispose();
        _automation.Dispose();
        _reputation.Dispose();
        _autoShop.Dispose();
        _ipc.Dispose();
    }

    public unsafe override void PreOpenCheck()
    {
        _automation.Update();
        _drawer.Update();

        _now = DateTime.Now;
        var playerPos = Service.ObjectTable.LocalPlayer?.Position ?? new();
        _movementDirection = playerPos - _prevPos;
        _prevPos = playerPos;
        _movementSpeed = _movementDirection.Length() / Framework.Instance()->FrameDeltaTime;
        _movementDirection = _movementDirection.NormalizedXZ();

        IsOpen = Service.ClientState.TerritoryType is 1165 or 1197;

        UpdateMap();
        UpdateAutoShop();
        UpdateAutoJoin();
        UpdateAutoLeave();
        UpdateReputationTracking();
        DrawOverlays();

        _drawer.DrawWorldPrimitives();
    }

    public unsafe override void Draw()
    {
        if (ImGui.Button($"{Loc.Get("Queue")}###queue"))
            _automation.RegisterForDuty();
        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Get("Leave")}###leave"))
            _automation.LeaveDuty();
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Format("PlayersInDuty", _numPlayersInDuty, _autoLeaveAt == DateTime.MaxValue ? Loc.Get("AutoLeaveNever") : Loc.Format("AutoLeaveIn", (_autoLeaveAt - _now).TotalSeconds)));

        ImGui.Checkbox($"{Loc.Get("AutoRegister")}###autojoin", ref _autoJoin);
        if (_autoJoin)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.SliderFloat($"{Loc.Get("Delay")}###j", ref _autoJoinDelay, 0, 10);
            }
        }
        ImGui.Checkbox($"{Loc.Get("AutoLeaveNotSolo")}###autoleave", ref _autoLeaveIfNotSolo);
        if (_autoLeaveIfNotSolo)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.SliderFloat($"{Loc.Get("Delay")}###l", ref _autoLeaveDelay, 0, 10);
                ImGui.SliderInt($"{Loc.Get("Limit")}###limit", ref _autoLeaveLimit, 1, 23);
            }
        }
        var leaveOnReputation = _reputation.Config.LeaveOnReputation;
        if (ImGui.Checkbox($"{Loc.Get("LeaveOnReputation")}###leaveonrep", ref leaveOnReputation))
        {
            _reputation.Config.LeaveOnReputation = leaveOnReputation;
            _reputation.Config.Save();
        }
        ImGui.Checkbox($"{Loc.Get("ShowAOEs")}###showaoe", ref _showAOEs);
        ImGui.Checkbox($"{Loc.Get("ShowAOEText")}###showaoetext", ref _showAOEText);
        ImGui.Checkbox($"{Loc.Get("ShowPath")}###showpath", ref _showPathfind);
        DrawLanguageSelector();

        DrawReputationStats();
        DrawAutoShop();

        if (_map != null)
        {
            var strats = _map.Strats();
            if (strats.Length > 0)
                ImGui.TextUnformatted(strats);
            ImGui.TextUnformatted($"Pos: {_map.PlayerPos}");
            ImGui.TextUnformatted($"Path: {_map.PathSkip}-{_map.Path.Count}");
            ImGui.TextUnformatted($"Speed: {_movementSpeed}");

            //foreach (var aoe in _map.AOEs.Where(aoe => aoe.NextActivation != default))
            //{
            //    var nextActivation = (aoe.NextActivation - _now).TotalSeconds;
            //    using (ImRaii.PushColor(ImGuiCol.Text, nextActivation < 0 ? 0xff0000ff : 0xffffffff))
            //        ImGui.TextUnformatted($"{aoe.Type} R{aoe.R1} @ {aoe.Origin}: activate in {nextActivation:f3}, repeat={aoe.Repeat}, seqd={aoe.SeqDelay}");
            //}
        }
    }

    private void UpdateMap()
    {
        if (Service.Condition[ConditionFlag.BetweenAreas])
            return;

        Type? mapType = null;
        if (IsOpen)
        {
            if (Service.ClientState.TerritoryType == 1197)
            {
                //mapType = typeof(MapTest);
            }
            else
            {
                var pos = Service.ObjectTable.LocalPlayer!.Position;
                mapType = pos switch
                {
                    //{ X: >= -20 and <= 20, Z: >= -400 and <= -100 } => typeof(Map1A),
                    { X: >= -40 and <= 40, Z: >= 100 and <= 350 } => typeof(Map3),
                    _ => null
                };
            }
        }

        if (_map?.GetType() != mapType)
        {
            _map?.Dispose();
            _map = null;
            if (mapType != null)
                _map = (Map?)Activator.CreateInstance(mapType, _gameEvents);
        }

        _map?.Update();
    }

    private void UpdateAutoJoin()
    {
        bool wantAutoJoin = _autoJoin && _automation.Idle && IsOpen && Service.ClientState.TerritoryType == 1197 && !Service.Condition[ConditionFlag.WaitingForDutyFinder] && !Service.Condition[ConditionFlag.BetweenAreas];
        // 自动购物期间暂停自动报名
        if (_autoShop.IsBusy)
            wantAutoJoin = false;
        if (!wantAutoJoin)
        {
            _autoJoinAt = DateTime.MaxValue;
        }
        else if (_autoJoinAt == DateTime.MaxValue)
        {
            Service.Log.Debug($"Auto-joining in {_autoJoinDelay:f2}s...");
            _autoJoinAt = _now.AddSeconds(_autoJoinDelay);
        }
        else if (_now >= _autoJoinAt)
        {
            Service.Log.Debug($"Auto-joining");
            _automation.RegisterForDuty();
            _autoJoinAt = DateTime.MaxValue;
        }
    }

    private void UpdateAutoLeave()
    {
        _numPlayersInDuty = Service.ClientState.TerritoryType == 1165 && Service.Condition[ConditionFlag.BoundByDuty] && !Service.Condition[ConditionFlag.BetweenAreas]
            ? Service.ObjectTable.Count(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
            : 0;
        bool wantAutoLeave = _autoLeaveIfNotSolo && _numPlayersInDuty > _autoLeaveLimit && _automation.Idle;
        if (!wantAutoLeave)
        {
            _autoLeaveAt = DateTime.MaxValue;
        }
        else if (_autoLeaveAt == DateTime.MaxValue)
        {
            Service.Log.Debug($"Auto-leaving in {_autoLeaveDelay:f2}s...");
            _autoLeaveAt = _now.AddSeconds(_autoLeaveDelay);
        }
        else if (_now >= _autoLeaveAt)
        {
            Service.Log.Debug($"Auto-leaving: {_numPlayersInDuty} players");
            _automation.LeaveDuty();
            _autoLeaveAt = DateTime.MaxValue;
        }
    }

    private void UpdateReputationTracking()
    {
        // 计时范围：在副本内，或在大厅中排队/开启自动报名；在大厅闲置不计入
        bool inDuty = Service.ClientState.TerritoryType == 1165;
        bool queueing = Service.ClientState.TerritoryType == 1197 && (_autoJoin || Service.Condition[ConditionFlag.WaitingForDutyFinder]);
        _reputation.Update(_now, inDuty || queueing);
    }

    private void UpdateAutoShop()
    {
        bool canStart = _autoJoin && _automation.Idle && Service.ClientState.TerritoryType == 1197 && Service.ObjectTable.LocalPlayer != null
            && !Service.Condition[ConditionFlag.BoundByDuty] && !Service.Condition[ConditionFlag.WaitingForDutyFinder] && !Service.Condition[ConditionFlag.BetweenAreas];
        _autoShop.Update(_now, canStart);

        // 在报名之前检查：声誉刷满（下一次获得会溢出）时停止自动报名
        var cfg = _reputation.Config;
        if (cfg.StopWhenFull && canStart && !_autoShop.IsBusy && ReputationShop.IsFull(cfg))
        {
            _autoJoin = false;
            Service.ChatGui.Print($"[vfallguy] {Loc.Format("ChatFull", ReputationShop.GetCurrency(), ReputationShop.Cap)}");
        }
    }

    private void OnAutoShopFailed(string reason)
    {
        // 无法花掉金碟声誉时停止刷取，防止溢出
        _autoJoin = false;
        Service.ChatGui.PrintError($"[vfallguy] {Loc.Format("ChatShopFailed", reason)}");
    }

    private void OnReputationGained(int amount)
    {
        if (!_reputation.Config.LeaveOnReputation || Service.ClientState.TerritoryType != 1165 || !Service.Condition[ConditionFlag.BoundByDuty])
            return;
        Service.Log.Debug($"Leaving after gaining {amount} reputation");
        Service.ChatGui.Print($"[vfallguy] {Loc.Format("ChatLeaveOnReputation", amount)}");
        _automation.LeaveDuty();
    }

    private void DrawLanguageSelector()
    {
        var cfg = _reputation.Config;
        var followLabel = Loc.Format("LanguageFollowDalamud", Loc.GetLanguageName(Loc.DalamudLanguage));
        var preview = string.IsNullOrEmpty(cfg.Language) ? followLabel : Loc.GetLanguageName(cfg.Language);
        ImGui.SetNextItemWidth(200);
        using var combo = ImRaii.Combo($"{Loc.Get("Language")} / Language###language", preview);
        if (!combo)
            return;
        if (ImGui.Selectable(followLabel, string.IsNullOrEmpty(cfg.Language)))
            SetLanguage("");
        foreach (var (code, name) in Loc.SupportedLanguages)
            if (ImGui.Selectable(name, cfg.Language == code))
                SetLanguage(code);
    }

    private void SetLanguage(string code)
    {
        _reputation.Config.Language = code;
        _reputation.Config.Save();
        Loc.Apply();
    }

    private void DrawReputationStats()
    {
        if (!ImGui.CollapsingHeader($"{Loc.Get("StatsHeader")}###stats", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var cfg = _reputation.Config;
        var tracked = TimeSpan.FromSeconds(cfg.TrackedSeconds);
        ImGui.TextUnformatted(Loc.Format("StatsPerHour", _reputation.PerHour));
        ImGui.TextUnformatted(Loc.Format("StatsTotal", cfg.TotalReputation, cfg.RewardCount, _reputation.PerReward));
        ImGui.TextUnformatted(Loc.Format("StatsTracked", (int)tracked.TotalHours, tracked.Minutes, tracked.Seconds, cfg.StatsSince));
        if (ImGui.Button($"{Loc.Get("StatsReset")}###statsreset") && ImGui.GetIO().KeyCtrl)
            _reputation.Reset();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get("StatsResetTooltip"));

        if (cfg.Records.Count > 0 && ImGui.TreeNode($"{Loc.Format("StatsRecords", cfg.Records.Count)}###reprecords"))
        {
            using (var table = ImRaii.Table("reprecords", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new(0, 150)))
            {
                if (table)
                {
                    ImGui.TableSetupColumn(Loc.Get("ColTime"));
                    ImGui.TableSetupColumn(Loc.Get("ColAmount"));
                    ImGui.TableHeadersRow();
                    for (int i = cfg.Records.Count - 1; i >= 0; --i)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{cfg.Records[i].Time:MM-dd HH:mm:ss}");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{cfg.Records[i].Amount}");
                    }
                }
            }
            ImGui.TreePop();
        }
    }

    private void DrawAutoShop()
    {
        if (!ImGui.CollapsingHeader($"{Loc.Get("ShopHeader")}###autoshop"))
            return;

        var cfg = _reputation.Config;
        var cap = ReputationShop.Cap;
        ImGui.TextUnformatted(Loc.Format("ShopCurrent", ReputationShop.GetCurrency(), cap));

        var enabled = cfg.AutoShopEnabled;
        if (ImGui.Checkbox($"{Loc.Get("ShopEnable")}###shopenable", ref enabled))
        {
            cfg.AutoShopEnabled = enabled;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get("ShopEnableTooltip"));

        var stopWhenFull = cfg.StopWhenFull;
        if (ImGui.Checkbox($"{Loc.Get("StopWhenFull")}###stopwhenfull", ref stopWhenFull))
        {
            cfg.StopWhenFull = stopWhenFull;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get("StopWhenFullTooltip"));

        var threshold = cfg.AutoShopThreshold;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt($"{Loc.Get("ShopThreshold")}###threshold", ref threshold, 100, 1000))
        {
            cfg.AutoShopThreshold = Math.Clamp(threshold, 0, cap);
            cfg.Save();
        }

        if (_autoShop.IsBusy)
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), _autoShop.StatusText);
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Get("ShopAbort")}###shopabort"))
            {
                // 不再购物时必须停止刷取，否则声誉会溢出
                _autoShop.Abort();
                _autoJoin = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Get("ShopAbortTooltip"));
        }
        else if (cfg.AutoShopEnabled)
        {
            var gbrVersion = _ipc.GbrVersion;
            if (gbrVersion == 0)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), Loc.Get("WarnGbrMissing"));
            else if (gbrVersion < GatherBuddyIPC.RequiredGbrIpcVersion)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), Loc.Format("WarnGbrOutdated", gbrVersion, GatherBuddyIPC.RequiredGbrIpcVersion));
            else if (!_ipc.GbrListApiRegistered)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), Loc.Get("WarnGbrApiMissing"));
            if (!_ipc.NavReady)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), Loc.Get("WarnNavMissing"));
        }

        DrawAutoShopItems(cfg);
    }

    private void DrawAutoShopItems(Configuration cfg)
    {
        ImGui.TextUnformatted(Loc.Get("ShopItemsHint"));
        using (var table = ImRaii.Table("autoshopitems", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new(0, 250)))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Loc.Get("ColItem"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Loc.Get("ColCost"), ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn(Loc.Get("ColOwned"), ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn(Loc.Get("ColTarget"), ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();
                var iconSize = new Vector2(ImGui.GetTextLineHeight());
                foreach (var item in ReputationShop.Items)
                {
                    using var id = ImRaii.PushId((int)item.ItemId);
                    var learned = ReputationShop.IsLearned(item);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var selected = cfg.AutoShopItems.ContainsKey(item.ItemId);
                    if (ImGui.Checkbox("##sel", ref selected))
                    {
                        if (selected)
                            cfg.AutoShopItems[item.ItemId] = 1;
                        else
                            cfg.AutoShopItems.Remove(item.ItemId);
                        cfg.Save();
                    }
                    ImGui.SameLine();
                    ImGui.Image(Service.TextureProvider.GetFromGameIcon(new(item.IconId)).GetWrapOrEmpty().Handle, iconSize);
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff808080, learned))
                        ImGui.TextUnformatted(learned ? Loc.Format("ItemLearned", item.Name) : item.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{item.Cost}");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{ReputationShop.GetOwnedCount(item.ItemId)}");

                    ImGui.TableNextColumn();
                    if (cfg.AutoShopItems.TryGetValue(item.ItemId, out var target))
                    {
                        var qty = (int)target;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputInt("##qty", ref qty, 1, 10))
                        {
                            cfg.AutoShopItems[item.ItemId] = (uint)Math.Clamp(qty, 1, 999);
                            cfg.Save();
                        }
                    }
                }
            }
        }
    }

    private void DrawOverlays()
    {
        if (_map == null || Service.Condition[ConditionFlag.BetweenAreas])
            return;

        if (_showPathfind)
        {
            var from = _map.PlayerPos;
            for (int i = _map.PathSkip; i < _map.Path.Count; ++i)
            {
                var wp = _map.Path[i];
                var delay = (wp.StartMoveAt - _now).TotalSeconds;
                _drawer.DrawWorldLine(from, wp.Dest, i > 0 ? 0xff00ffff : delay <= 0 ? 0xff00ff00 : 0xff0000ff);
                if (delay > 0)
                    _drawer.DrawWorldText(from, 0xff0000ff, $"{delay:f3}");
                from = wp.Dest;
            }
        }

        foreach (var aoe in _map.AOEs.Where(aoe => aoe.NextActivation != default))
        {
            var nextActivation = (aoe.NextActivation - _now).TotalSeconds;
            if (nextActivation < 2.5f)
            {
                var (aoeEnter, aoeExit) = _movementSpeed > 0 ? aoe.Intersect(_map.PlayerPos, _movementDirection) : aoe.Contains(_map.PlayerPos) ? (0, float.PositiveInfinity) : (float.NaN, float.NaN);
                var delay = !float.IsNaN(aoeEnter) ? aoe.ActivatesBetween(_now, aoeEnter * Map.InvSpeed - 0.1f, aoeExit * Map.InvSpeed + 0.1f) : 0;
                var color = delay > 0 ? 0xff0000ff : 0xff00ffff;
                if (_showAOEs)
                {
                    aoe.Draw(_drawer, color);
                }
                if (_showAOEText)
                {
                    var text = $"{nextActivation:f3} [{aoeEnter * Map.InvSpeed:f2}-{aoeExit * Map.InvSpeed:f2}, {delay:f2}]";
                    var dir = (aoe.Origin - _map.PlayerPos).NormalizedXZ();
                    var (enter, exit) = aoe.Intersect(_map.PlayerPos, dir);
                    var textPos = _map.PlayerPos + dir * MathF.Max(enter, 0);
                    _drawer.DrawWorldText(textPos, color, text);
                }
            }
        }
    }
}
