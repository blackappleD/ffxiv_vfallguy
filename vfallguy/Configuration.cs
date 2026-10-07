using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace vfallguy;

[Serializable]
public class ReputationRecord
{
    public DateTime Time { get; set; }
    public int Amount { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // 获得金碟声誉后立即退出副本
    public bool LeaveOnReputation { get; set; } = true;

    // 金碟声誉统计（自上次重置起）
    public DateTime StatsSince { get; set; } = DateTime.Now;
    public double TrackedSeconds { get; set; }
    public long TotalReputation { get; set; }
    public int RewardCount { get; set; }
    public List<ReputationRecord> Records { get; set; } = [];

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
