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
        if (ImGui.Button("Queue"))
            _automation.RegisterForDuty();
        ImGui.SameLine();
        if (ImGui.Button("Leave"))
            _automation.LeaveDuty();
        ImGui.SameLine();
        ImGui.TextUnformatted($"Num players in duty: {_numPlayersInDuty} (autoleave: {(_autoLeaveAt == DateTime.MaxValue ? "never" : $"in {(_autoLeaveAt - _now).TotalSeconds:f1}s")})");

        ImGui.Checkbox("Auto register", ref _autoJoin);
        if (_autoJoin)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.SliderFloat("Delay###j", ref _autoJoinDelay, 0, 10);
            }
        }
        ImGui.Checkbox("Auto leave if not solo", ref _autoLeaveIfNotSolo);
        if (_autoLeaveIfNotSolo)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.SliderFloat("Delay###l", ref _autoLeaveDelay, 0, 10);
                ImGui.SliderInt("Limit", ref _autoLeaveLimit, 1, 23);
            }
        }
        var leaveOnReputation = _reputation.Config.LeaveOnReputation;
        if (ImGui.Checkbox("获得金碟声誉后立即退出", ref leaveOnReputation))
        {
            _reputation.Config.LeaveOnReputation = leaveOnReputation;
            _reputation.Config.Save();
        }
        ImGui.Checkbox("Show AOE zones", ref _showAOEs);
        ImGui.Checkbox("Show AOE debug text", ref _showAOEText);
        ImGui.Checkbox("Show proposed path", ref _showPathfind);

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
    }

    private void OnAutoShopFailed(string reason)
    {
        // 无法花掉金碟声誉时停止刷取，防止溢出
        _autoJoin = false;
        Service.ChatGui.PrintError($"[vfallguy] 自动购物失败：{reason}。已关闭自动报名以防金碟声誉溢出。");
    }

    private void OnReputationGained(int amount)
    {
        if (!_reputation.Config.LeaveOnReputation || Service.ClientState.TerritoryType != 1165 || !Service.Condition[ConditionFlag.BoundByDuty])
            return;
        Service.Log.Debug($"Leaving after gaining {amount} reputation");
        Service.ChatGui.Print($"获得 {amount} 金碟声誉，立即退出副本");
        _automation.LeaveDuty();
    }

    private void DrawReputationStats()
    {
        if (!ImGui.CollapsingHeader("金碟声誉统计", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var cfg = _reputation.Config;
        var tracked = TimeSpan.FromSeconds(cfg.TrackedSeconds);
        ImGui.TextUnformatted($"平均每小时: {_reputation.PerHour:f0}");
        ImGui.TextUnformatted($"累计获得: {cfg.TotalReputation} (共 {cfg.RewardCount} 次, 平均每次 {_reputation.PerReward:f1})");
        ImGui.TextUnformatted($"统计时长: {(int)tracked.TotalHours}:{tracked.Minutes:d2}:{tracked.Seconds:d2} (自 {cfg.StatsSince:yyyy-MM-dd HH:mm} 起)");
        if (ImGui.Button("重置统计") && ImGui.GetIO().KeyCtrl)
            _reputation.Reset();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按住 Ctrl 点击以重置");

        if (cfg.Records.Count > 0 && ImGui.TreeNode($"最近记录 ({cfg.Records.Count})###reprecords"))
        {
            using (var table = ImRaii.Table("reprecords", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new(0, 150)))
            {
                if (table)
                {
                    ImGui.TableSetupColumn("时间");
                    ImGui.TableSetupColumn("数量");
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
        if (!ImGui.CollapsingHeader("金碟声誉自动购物"))
            return;

        var cfg = _reputation.Config;
        var cap = ReputationShop.Cap;
        ImGui.TextUnformatted($"当前金碟声誉: {ReputationShop.GetCurrency():N0} / {cap:N0}");

        var enabled = cfg.AutoShopEnabled;
        if (ImGui.Checkbox("开启自动购物", ref enabled))
        {
            cfg.AutoShopEnabled = enabled;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启 Auto register 时，在大厅中金碟声誉达到阈值后，通过 GatherBuddy Reborn 购买下方勾选的物品，再走回节目登记员继续报名");

        var threshold = cfg.AutoShopThreshold;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("触发阈值", ref threshold, 100, 1000))
        {
            cfg.AutoShopThreshold = Math.Clamp(threshold, 0, cap);
            cfg.Save();
        }

        if (_autoShop.IsBusy)
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), _autoShop.StatusText);
            ImGui.SameLine();
            if (ImGui.Button("中止"))
            {
                // 不再购物时必须停止刷取，否则声誉会溢出
                _autoShop.Abort();
                _autoJoin = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("中止购物并关闭 Auto register");
        }
        else if (cfg.AutoShopEnabled)
        {
            var gbrVersion = _ipc.GbrVersion;
            if (gbrVersion == 0)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "需要安装并启用 GatherBuddy Reborn");
            else if (gbrVersion < GatherBuddyIPC.RequiredGbrIpcVersion)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $"GatherBuddy Reborn 版本过低（IPC {gbrVersion}，需要 {GatherBuddyIPC.RequiredGbrIpcVersion}+）");
            if (!_ipc.NavReady)
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "需要安装并启用 vnavmesh");
        }

        DrawAutoShopItems(cfg);
    }

    private void DrawAutoShopItems(Configuration cfg)
    {
        ImGui.TextUnformatted("勾选要购买的物品，数量为目标持有数（已学习的物品会自动跳过）:");
        using (var table = ImRaii.Table("autoshopitems", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new(0, 250)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("花费", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("持有", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("目标数量", ImGuiTableColumnFlags.WidthFixed, 110);
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
                        ImGui.TextUnformatted(learned ? $"{item.Name}（已学习）" : item.Name);

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
