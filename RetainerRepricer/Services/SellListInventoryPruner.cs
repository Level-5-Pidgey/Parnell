using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;

namespace RetainerRepricer.Services;

internal unsafe sealed class SellListInventoryPruner
{
    private static readonly InventoryType[] SellScanContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    private readonly Configuration _config;
    private readonly PluginLogger _log;

    public SellListInventoryPruner(Configuration config, PluginLogger log)
    {
        _config = config;
        _log = log;
    }

    public SellListInventoryPruneResult Run(string reason, bool saveConfig = true)
    {
        var snapshot = CaptureBagCounts();
        return Run(reason, snapshot, saveConfig);
    }

    public SellListInventoryPruneResult Run(string reason, IReadOnlyDictionary<ulong, int> bagCounts, bool saveConfig = true)
    {
        var removedEntries = new List<SellListInventoryPruneResult.PrunedEntry>();

        var removedCount = _config.RemoveSellEntries(entry =>
        {
            if (entry.PreserveFromAutoPrune)
                return false;

            var key = Configuration.MakeSellKey(entry.ItemId, entry.IsHq);
            var inventoryCount = bagCounts.TryGetValue(key, out var count) ? count : 0;

            if (inventoryCount > 0)
                return false;

            removedEntries.Add(new SellListInventoryPruneResult.PrunedEntry
            {
                ItemId = entry.ItemId,
                IsHq = entry.IsHq,
                Name = entry.Name ?? string.Empty,
            });

            return true;
        });

        if (removedCount > 0 && saveConfig)
            _config.Save();

        var result = new SellListInventoryPruneResult(
            reason,
            DateTime.UtcNow,
            removedEntries,
            bagCounts);

        if (removedCount > 0)
        {
            _log.Information("[RR][Prune] Removed {Removed} sell-list item(s) missing from bags ({Reason}).", removedCount, reason);
        }
        else
        {
            _log.Verbose("[RR][Prune] No sell-list entries removed ({Reason}).", reason);
        }

        return result;
    }

    private static Dictionary<ulong, int> CaptureBagCounts()
    {
        var counts = new Dictionary<ulong, int>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return counts;

        foreach (var type in SellScanContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null)
                    continue;

                if (slot->Quantity <= 0)
                    continue;

                var baseItemId = slot->GetBaseItemId();
                if (baseItemId == 0)
                    continue;

                var isHq = slot->IsHighQuality();
                var key = Configuration.MakeSellKey(baseItemId, isHq);
                var slotQuantity = (int)slot->Quantity;

                if (counts.TryGetValue(key, out var existing))
                {
                    var total = existing + slotQuantity;
                    counts[key] = total >= 999999 ? 999999 : total;
                }
                else
                {
                    counts[key] = slotQuantity;
                }
            }
        }

        return counts;
    }

    internal sealed class SellListInventoryPruneResult
    {
        public SellListInventoryPruneResult(
            string reason,
            DateTime completedUtc,
            IReadOnlyList<PrunedEntry> removedEntries,
            IReadOnlyDictionary<ulong, int> inventorySnapshot)
        {
            Reason = reason;
            CompletedUtc = completedUtc;
            RemovedEntries = removedEntries ?? Array.Empty<PrunedEntry>();
            InventorySnapshot = inventorySnapshot ?? new Dictionary<ulong, int>();
        }

        public string Reason { get; }
        public DateTime CompletedUtc { get; }
        public IReadOnlyList<PrunedEntry> RemovedEntries { get; }
        public IReadOnlyDictionary<ulong, int> InventorySnapshot { get; }
        public bool ItemsRemoved => RemovedEntries.Count > 0;
        public int RemovedCount => RemovedEntries.Count;

        internal sealed class PrunedEntry
        {
            public uint ItemId { get; init; }
            public bool IsHq { get; init; }
            public string Name { get; init; } = string.Empty;
        }
    }
}
