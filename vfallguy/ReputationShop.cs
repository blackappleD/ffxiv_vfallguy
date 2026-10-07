using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace vfallguy;

// 金碟声誉（物品 41629）及其兑换商店的数据
public static class ReputationShop
{
    public const uint CurrencyItemId = 41629;
    public const uint RegistratorNpcId = 1046440; // 节目登记员

    public record ShopItem(uint ItemId, string Name, uint IconId, uint Cost, Item Row);

    private static List<ShopItem>? _items;

    // 金碟声誉的持有上限（物品堆叠数，国服为 10000）
    public static int Cap => (int)(Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(CurrencyItemId)?.StackSize ?? 10000);

    public static unsafe int GetCurrency()
    {
        var cm = CurrencyManager.Instance();
        if (cm != null && cm->HasItem(CurrencyItemId))
            return (int)cm->GetItemCount(CurrencyItemId);
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        return Math.Max(im->GetInventoryItemCount(CurrencyItemId), (int)im->GetItemCountInContainer(CurrencyItemId, InventoryType.Currency));
    }

    // 与 GBR 购买清单的计数方式一致：背包 + 兵装库 + 身上装备
    private static readonly InventoryType[] OwnedContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.EquippedItems,
    ];

    public static unsafe int GetOwnedCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null)
            return 0;
        var total = 0;
        foreach (var type in OwnedContainers)
            total += (int)im->GetItemCountInContainer(itemId, type);
        return total;
    }

    // 还需要购买的物品：已勾选、未学习、持有数未达目标、且当前声誉买得起至少一个
    public static (uint ItemId, uint TargetQuantity)[] GetPendingRequests(Configuration config)
    {
        var currency = GetCurrency();
        var items = Items.ToDictionary(i => i.ItemId);
        return [.. config.AutoShopItems
            .Where(kv => kv.Value > 0 && items.TryGetValue(kv.Key, out var item)
                && item.Cost <= currency && !IsLearned(item) && GetOwnedCount(kv.Key) < kv.Value)
            .Select(kv => (kv.Key, kv.Value))];
    }

    // 声誉已满，或剩余空间不足最近记录中单次最大获得量（下一次就会溢出）
    public static bool IsFull(Configuration config)
    {
        var space = Cap - GetCurrency();
        var nextGain = config.Records.Count > 0 ? config.Records.TakeLast(20).Max(r => r.Amount) : 1;
        return space < nextGain;
    }

    // 可学习物品（坐骑、宠物、乐谱、肖像等）已学习时返回 true
    public static bool IsLearned(ShopItem item)
    {
        try
        {
            return Service.UnlockState.IsItemUnlockable(item.Row) && Service.UnlockState.IsItemUnlocked(item.Row);
        }
        catch (Exception e)
        {
            Service.Log.Debug($"Failed to check unlock state for {item.ItemId}: {e.Message}");
            return false;
        }
    }

    // 所有以金碟声誉为货币的兑换物品
    public static IReadOnlyList<ShopItem> Items => _items ??= LoadItems();

    private static List<ShopItem> LoadItems()
    {
        var result = new Dictionary<uint, ShopItem>();
        try
        {
            foreach (var shop in Service.DataManager.GetExcelSheet<SpecialShop>())
            {
                foreach (var entry in shop.Item)
                {
                    // 注意不能用 FirstOrDefault：Lumina 的默认结构体没有数据页，读取属性会空引用
                    uint cost = 0;
                    foreach (var c in entry.ItemCosts)
                    {
                        if (c.ItemCost.RowId == CurrencyItemId)
                        {
                            cost = c.CurrencyCost;
                            break;
                        }
                    }
                    if (cost == 0)
                        continue;
                    foreach (var receive in entry.ReceiveItems)
                    {
                        if (receive.Item.RowId == 0 || result.ContainsKey(receive.Item.RowId) || receive.Item.ValueNullable is not { } row)
                            continue;
                        result[row.RowId] = new(row.RowId, row.Name.ExtractText(), row.Icon, cost, row);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to load reputation shop items: {e}");
        }
        Service.Log.Debug($"Loaded {result.Count} reputation shop items");
        return [.. result.Values.OrderByDescending(i => i.Cost).ThenBy(i => i.ItemId)];
    }
}
