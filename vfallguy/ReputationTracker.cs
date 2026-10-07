using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using System;
using System.Text.RegularExpressions;

namespace vfallguy;

// 监听聊天栏中的金碟声誉获得消息，记录并统计每小时获得量
public partial class ReputationTracker : IDisposable
{
    public const int MaxRecords = 200;

    public Configuration Config { get; }
    public event Action<int>? ReputationGained;

    private DateTime _lastUpdate = DateTime.MinValue;
    private DateTime _nextSave = DateTime.MaxValue;

    [GeneratedRegex(@"获得了\s*([\d,]+)\s*个金[碟蝶]声誉")]
    private static partial Regex ReputationRegex();

    public ReputationTracker(Configuration config)
    {
        Config = config;
        Service.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Service.ChatGui.ChatMessage -= OnChatMessage;
        Config.Save();
    }

    public double TrackedHours => Config.TrackedSeconds / 3600;
    public double PerHour => TrackedHours > 0 ? Config.TotalReputation / TrackedHours : 0;
    public double PerReward => Config.RewardCount > 0 ? (double)Config.TotalReputation / Config.RewardCount : 0;

    // 每帧调用；active 表示当前处于刷取状态，只有这段时间计入统计时长
    public void Update(DateTime now, bool active)
    {
        if (active && _lastUpdate != DateTime.MinValue)
        {
            // 限制单帧增量，避免卡顿或长时间挂起时把时间算进去
            Config.TrackedSeconds += Math.Clamp((now - _lastUpdate).TotalSeconds, 0, 5);
            if (_nextSave == DateTime.MaxValue)
                _nextSave = now.AddSeconds(60);
        }
        _lastUpdate = now;

        if (now >= _nextSave)
        {
            Config.Save();
            _nextSave = DateTime.MaxValue;
        }
    }

    public void Reset()
    {
        Config.StatsSince = DateTime.Now;
        Config.TrackedSeconds = 0;
        Config.TotalReputation = 0;
        Config.RewardCount = 0;
        Config.Records.Clear();
        Config.Save();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (IsPlayerChat(message.LogKind))
            return;

        var match = ReputationRegex().Match(message.Message.TextValue);
        if (!match.Success || !int.TryParse(match.Groups[1].Value.Replace(",", ""), out var amount))
            return;

        Service.Log.Debug($"Gained {amount} reputation");
        Config.TotalReputation += amount;
        Config.RewardCount++;
        Config.Records.Add(new() { Time = DateTime.Now, Amount = amount });
        if (Config.Records.Count > MaxRecords)
            Config.Records.RemoveRange(0, Config.Records.Count - MaxRecords);
        Config.Save();

        ReputationGained?.Invoke(amount);
    }

    // 忽略玩家发言，防止有人在聊天里打出同样的文字
    private static bool IsPlayerChat(XivChatType type) => type is
        XivChatType.Say or XivChatType.Shout or XivChatType.Yell or XivChatType.TellIncoming or XivChatType.TellOutgoing or
        XivChatType.Party or XivChatType.CrossParty or XivChatType.Alliance or XivChatType.FreeCompany or XivChatType.NoviceNetwork or
        XivChatType.PvPTeam or XivChatType.Echo or XivChatType.CustomEmote or XivChatType.StandardEmote or
        XivChatType.Ls1 or XivChatType.Ls2 or XivChatType.Ls3 or XivChatType.Ls4 or XivChatType.Ls5 or XivChatType.Ls6 or XivChatType.Ls7 or XivChatType.Ls8 or
        XivChatType.CrossLinkShell1 or XivChatType.CrossLinkShell2 or XivChatType.CrossLinkShell3 or XivChatType.CrossLinkShell4 or
        XivChatType.CrossLinkShell5 or XivChatType.CrossLinkShell6 or XivChatType.CrossLinkShell7 or XivChatType.CrossLinkShell8;
}
