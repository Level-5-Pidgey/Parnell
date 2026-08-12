using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerRepricer;

public enum ContextMenuQuickAddModifier
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 3,
    LeftShift = 4,
    RightShift = 5,
    LeftCtrl = 6,
    RightCtrl = 7,
    LeftAlt = 8,
    RightAlt = 9,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    #region Dalamud config versioning

    // Dalamud serialization schema version; bump when config layout changes.
    public int Version { get; set; } = 2;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        _pluginInterface = pi;
    }

    public void Save()
        => _pluginInterface?.SavePluginConfig(this);

    #endregion

    #region Global toggles

    // Master enable switch. When off: no runs, no context menu injection, no overlays.
    // Config UI stays usable so it’s easy to turn back on.
    public bool PluginEnabled { get; set; } = true;

    // Allows AutoRetainer to hand the currently selected retainer to this plugin after venture processing.
    public bool EnableAutoRetainerIntegration { get; set; } = false;

    // Optional: when enabled, per-retainer sell caps become available in the Sell List.
    public bool EnablePerRetainerCaps { get; set; } = false;

    // Controls whether ImGui tooltips are shown while hovering configuration controls.
    public bool ShowTooltips { get; set; } = true;

    // Shows the Min Count popup when adding via context menu; when off, items add instantly with defaults.
    public bool EnableContextMenuPopup { get; set; } = true;

    // Optional modifier that turns inventory right-click into an immediate add-to-sell-list action.
    public ContextMenuQuickAddModifier ContextMenuQuickAddModifier { get; set; } = ContextMenuQuickAddModifier.None;

    #endregion

    #region Run behavior

    // If enabled, the plugin will close the RetainerList window when a full run finishes.
    // Keeps UI tidy by exiting the retainer bell automatically after each run.
    public bool CloseRetainerListAddon { get; set; } = true;

    #endregion

    #region Retainer enable/disable

    // Tracks per-retainer enablement (case-insensitive) synced from the RetainerList UI.
    public Dictionary<string, bool> RetainersEnabled { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RetainerBehavior> RetainerSettings { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> GetAllRetainerNames()
    {
        EnsureRetainerDictionaries();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in RetainersEnabled.Keys)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name))
                yield return name;
        }

        foreach (var name in RetainerSettings.Keys)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(name))
                yield return name;
        }
    }

    public bool IsRetainerEnabled(string name)
        => GetRetainerBehavior(name).Enabled;

    public void SetRetainerEnabled(string name, bool enabled)
    {
        EnsureRetainerDictionaries();
        var key = NormalizeRetainerName(name);
        RetainersEnabled[key] = enabled;
        var behavior = EnsureRetainerBehavior(key);
        behavior.Enabled = enabled;
    }

    public bool RetainerAllowsSell(string name)
    {
        var behavior = GetRetainerBehavior(name);
        return behavior.Enabled && behavior.AllowSell;
    }

    public bool RetainerAllowsReprice(string name)
    {
        var behavior = GetRetainerBehavior(name);
        return behavior.Enabled && behavior.AllowReprice;
    }

    public void SetRetainerSellEnabled(string name, bool allowSell)
    {
        var behavior = EnsureRetainerBehavior(name);
        behavior.AllowSell = allowSell;
    }

    public void SetRetainerRepriceEnabled(string name, bool allowReprice)
    {
        var behavior = EnsureRetainerBehavior(name);
        behavior.AllowReprice = allowReprice;
    }

    public RetainerBehavior GetRetainerBehavior(string name)
        => EnsureRetainerBehavior(name);

    private RetainerBehavior EnsureRetainerBehavior(string name)
    {
        EnsureRetainerDictionaries();
        var key = NormalizeRetainerName(name);
        if (!RetainerSettings.TryGetValue(key, out var behavior) || behavior == null)
        {
            var enabled = RetainersEnabled.TryGetValue(key, out var existing)
                ? existing
                : true;

            behavior = new RetainerBehavior
            {
                Enabled = enabled,
                AllowSell = true,
                AllowReprice = true,
            };

            RetainerSettings[key] = behavior;
        }

        behavior.Normalize();
        return behavior;
    }

    internal static string NormalizeRetainerName(string name)
        => (name ?? string.Empty).Trim();

    private void EnsureRetainerDictionaries()
    {
        RetainersEnabled ??= new(StringComparer.OrdinalIgnoreCase);
        RetainerSettings ??= new(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Overlay (visual only)

    // Controls overlay rendering on top of RetainerList plus stored offsets.
    public bool OverlayEnabled { get; set; } = true;
    public float OverlayOffsetX { get; set; } = 0f;
    public float OverlayOffsetY { get; set; } = 0f;
    // Remembers whether the offset sliders are expanded in the config UI.
    public bool ShowOverlayOffsetControls { get; set; } = false;

    // Controls whether the Advanced tab is visible in the config UI.
    public bool ShowAdvancedSettingsTab { get; set; } = false;

    #endregion

    #region Pricing gate

    // Enables the Universalis-based price floor gate for repricing existing listings.
    public bool EnableUndercutPreventionGate { get; set; } = true;
    // Allows HTTP calls to Universalis for both gate logic and empty-market fallback.
    public bool UseUniversalisApi { get; set; } = true;
    // Percentage of Universalis average that becomes the minimum acceptable price floor (10–90%).
    public float UndercutPreventionPercent { get; set; } = 0.5f;

    /// <summary>
    /// When market board returns "No items found", use Universalis average sale price
    /// to determine listing price instead of skipping the item.
    /// </summary>
    // When the market board shows no listings, fall back to Universalis averages instead of skipping.
    public bool UseUniversalisForEmptyMarket { get; set; } = true;

    // Smart sort controls (Universalis-backed sell order weighting)
    public bool EnableUniversalisSmartSort { get; set; } = false;
    public float SmartSortVelocityWeight { get; set; } = 0.555f;
    public float SmartSortPriceWeight { get; set; } = 0.445f;
    public int SmartSortRefreshMinutes { get; set; } = 30;
    public long SmartSortLastRunTicks { get; set; } = 0;

    // Inventory pruning (removes stale sell entries before sorting/selling)
    public bool AutoPruneMissingInventory { get; set; } = false;

    public (float velocityWeight, float priceWeight) GetSmartSortWeights()
    {
        var v = SmartSortVelocityWeight;
        var p = SmartSortPriceWeight;
        if (v < 0f) v = 0f;
        if (p < 0f) p = 0f;
        var sum = v + p;
        if (sum <= 0f)
            return (0.555f, 0.445f);
        return (v / sum, p / sum);
    }

    public DateTime GetSmartSortLastRunUtc()
        => SmartSortLastRunTicks <= 0 ? DateTime.MinValue : new DateTime(SmartSortLastRunTicks, DateTimeKind.Utc);

    public void SetSmartSortLastRunUtc(DateTime utc)
        => SmartSortLastRunTicks = utc <= DateTime.MinValue ? 0 : utc.ToUniversalTime().Ticks;

    #endregion

    #region Advanced automation tuning

    // Pricing aggression / validation controls.
    public int UndercutAmount { get; set; } = 1;
    public float MarketValidationThreshold { get; set; } = 2.0f;

    // Automation pacing (seconds unless noted).
    public double ActionIntervalSeconds { get; set; } = 0.15d;
    public double RetainerSyncIntervalSeconds { get; set; } = 2.0d;
    public double ItemSearchResultThrottleBackoffSeconds { get; set; } = 1.0d;
    public double MbBaseIntervalSeconds { get; set; } = 1.5d;
    public double MbIntervalMinSeconds { get; set; } = 1.0d;
    public double MbIntervalMaxSeconds { get; set; } = 2.0d;
    public double MbJitterMaxSeconds { get; set; } = 0.10d;
    public double IsrNoItemsSettleSeconds { get; set; } = 0.5d;
    public double IsrHqFilterInitialDelaySeconds { get; set; } = 1.25d;
    public double IsrHqFilterUiDebounceSeconds { get; set; } = 0.20d;
    public double IsrHqFilterOpenRetrySeconds { get; set; } = 0.30d;
    public double IsrHqFilterPostOpenSeconds { get; set; } = 0.15d;
    public double FrameworkTickIntervalSeconds { get; set; } = 0.075d;

    // Smart sort scoring constants.
    public double SmartSortVelocityCap { get; set; } = 100.0d;
    public double SmartSortVelocityLogBase { get; set; } = 101.0d;
    public double SmartSortPriceLogBase { get; set; } = 100001.0d;

    #endregion

    #region Sell list

    // HQ glyph suffix (matches in-game suffix style).
    public const char HqGlyphChar = '\uE03C';
    public const int RetainerCapDefault = 1;
    public const int RetainerCapMax = 20;

    [Serializable]
    public sealed class SellListEntry
    {
        // Base (Lumina) item id (NQ row id).
        public uint ItemId { get; set; }

        // Quality key for this SellList entry.
        public bool IsHq { get; set; }

        // Display name (we will bake HQ glyph in as suffix for HQ entries).
        public string Name { get; set; } = string.Empty;

        // Per-quality threshold (count only that quality).
        public int MinCountToSell { get; set; } = 1;

        // Explicit ordering (1..N). Lower numbers run earlier.
        public int SortOrder { get; set; }

        // Optional per-retainer caps for new listings; defaults to 1 per retainer when unset.
        public Dictionary<string, int> RetainerCaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Optional per-retainer stack sizes (0 = any available amount).
        public Dictionary<string, int> RetainerStackSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Protect this entry from automatic pruning even if it is missing from inventory.
        public bool PreserveFromAutoPrune { get; set; }

        public void EnsureRetainerCaps()
        {
            RetainerCaps ??= new(StringComparer.OrdinalIgnoreCase);
        }

        public void EnsureRetainerStackSizes()
        {
            RetainerStackSizes ??= new(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetRetainerCap(string name, out int cap)
        {
            EnsureRetainerCaps();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
            {
                cap = RetainerCapDefault;
                return false;
            }

            if (RetainerCaps.TryGetValue(key, out cap))
                return true;

            cap = RetainerCapDefault;
            return false;
        }

        public int GetRetainerCapOrDefault(string name)
        {
            TryGetRetainerCap(name, out var cap);
            return cap;
        }

        public void SetRetainerCap(string name, int value)
        {
            EnsureRetainerCaps();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            var clamped = Math.Clamp(value, 0, RetainerCapMax);

            if (clamped == RetainerCapDefault)
            {
                RetainerCaps.Remove(key);
                return;
            }

            RetainerCaps[key] = clamped;
        }

        public void ClearRetainerCap(string name)
        {
            EnsureRetainerCaps();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            RetainerCaps.Remove(key);
        }

        public int GetRetainerStackSize(string name)
        {
            EnsureRetainerStackSizes();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return 0;

            return RetainerStackSizes.TryGetValue(key, out var value) ? value : 0;
        }

        public void SetRetainerStackSize(string name, int value, int stackCap)
        {
            EnsureRetainerStackSizes();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            var clamped = Math.Clamp(value, 0, stackCap);

            if (clamped == 0)
                RetainerStackSizes.Remove(key);
            else
                RetainerStackSizes[key] = clamped;

            RefreshMinCountFromStackSizes();
        }

        public void ClearRetainerStackSize(string name)
        {
            EnsureRetainerStackSizes();
            var key = NormalizeRetainerName(name);
            if (key.Length == 0)
                return;

            if (RetainerStackSizes.Remove(key))
                RefreshMinCountFromStackSizes();
        }

        public void RefreshMinCountFromStackSizes()
        {
            EnsureRetainerStackSizes();
            if (RetainerStackSizes.Count == 0)
                return;

            var minPositive = int.MaxValue;
            foreach (var kvp in RetainerStackSizes)
            {
                if (kvp.Value <= 0)
                {
                    minPositive = int.MaxValue;
                    break;
                }

                if (kvp.Value < minPositive)
                    minPositive = kvp.Value;
            }

            if (minPositive != int.MaxValue)
                MinCountToSell = Math.Clamp(minPositive, 1, 999);
        }
    }

    // Key = (baseItemId << 1) | (isHq ? 1 : 0)
    internal static ulong MakeSellKey(uint baseItemId, bool isHq)
        => ((ulong)baseItemId << 1) | (isHq ? 1UL : 0UL);

    // Stored as a single dictionary so HQ/NQ don’t collide.
    // Persisted sell list keyed by (itemId, HQ flag) for deterministic lookups.
    public Dictionary<ulong, SellListEntry> SellList { get; set; } = new();

    // Convenience: display name builder (HQ suffix).
    internal static string BuildDisplayName(string name, bool isHq)
    {
        var n = (name ?? string.Empty).Trim();
        if (isHq)
            return (n + " " + HqGlyphChar).Trim(); // suffix (matches game convention)
        return n;
    }

    public bool TryGetSellEntryKeyByDisplayName(string displayName, out ulong key)
    {
        key = 0;
        if (SellList == null || SellList.Count == 0)
            return false;

        foreach (var entry in SellList.Values)
        {
            if (entry == null)
                continue;

            if (string.Equals(entry.Name, displayName, StringComparison.OrdinalIgnoreCase))
            {
                key = MakeSellKey(entry.ItemId, entry.IsHq);
                return true;
            }
        }

        return false;
    }

    public bool HasSellItem(uint baseItemId, bool isHq)
        => SellList.ContainsKey(MakeSellKey(baseItemId, isHq));

    public bool TryAddSellItem(uint baseItemId, bool isHq, string name, int? priorityOverride = null)
    {
        if (baseItemId == 0) return false;

        var key = MakeSellKey(baseItemId, isHq);
        if (SellList.ContainsKey(key)) return false;

        var nextOrder = GetNextSortOrder();

        SellList[key] = new SellListEntry
        {
            ItemId = baseItemId,
            IsHq = isHq,
            Name = BuildDisplayName(name, isHq),
            MinCountToSell = 1,
            SortOrder = nextOrder,
            PreserveFromAutoPrune = false,
        };

        if (priorityOverride.HasValue)
            TrySetSellItemOrder(baseItemId, isHq, priorityOverride.Value);

        return true;
    }

    public bool TryAddSellItemWithMinCount(uint baseItemId, bool isHq, string name, int minCount, int? priorityOverride = null)
    {
        if (baseItemId == 0) return false;

        var key = MakeSellKey(baseItemId, isHq);
        if (SellList.ContainsKey(key)) return false;

        var clampedMinCount = Math.Clamp(minCount, 1, 999);
        var nextOrder = GetNextSortOrder();

        SellList[key] = new SellListEntry
        {
            ItemId = baseItemId,
            IsHq = isHq,
            Name = BuildDisplayName(name, isHq),
            MinCountToSell = clampedMinCount,
            SortOrder = nextOrder,
            PreserveFromAutoPrune = false,
        };

        if (priorityOverride.HasValue)
            TrySetSellItemOrder(baseItemId, isHq, priorityOverride.Value);

        return true;
    }

    public bool RemoveSellItem(uint baseItemId, bool isHq)
    {
        var removed = SellList.Remove(MakeSellKey(baseItemId, isHq));
        if (removed)
            NormalizeSellListOrder(saveChanges: false);
        return removed;
    }

    public void ClearSellList()
        => SellList.Clear();

    public void SetSellItemPreserveFlag(uint baseItemId, bool isHq, bool preserve)
    {
        var key = MakeSellKey(baseItemId, isHq);
        if (!SellList.TryGetValue(key, out var entry) || entry == null)
            return;

        entry.PreserveFromAutoPrune = preserve;
    }

    public int RemoveSellEntries(Func<SellListEntry, bool> predicate)
    {
        if (predicate == null)
            throw new ArgumentNullException(nameof(predicate));

        if (SellList == null || SellList.Count == 0)
            return 0;

        var keysToRemove = new List<ulong>();

        foreach (var kvp in SellList)
        {
            var entry = kvp.Value;
            if (entry == null)
                continue;

            if (predicate(entry))
                keysToRemove.Add(kvp.Key);
        }

        if (keysToRemove.Count == 0)
            return 0;

        foreach (var key in keysToRemove)
            SellList.Remove(key);

        NormalizeSellListOrder(saveChanges: false);
        return keysToRemove.Count;
    }

    public IEnumerable<SellListEntry> GetSellListOrdered()
    {
        NormalizeSellListOrder(saveChanges: false);
        return SellList.Values
            .OrderBy(e => e.SortOrder <= 0 ? int.MaxValue : e.SortOrder)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0);
    }

    public bool TrySetSellItemOrder(uint baseItemId, bool isHq, int requestedOrder)
    {
        var key = MakeSellKey(baseItemId, isHq);
        if (!SellList.TryGetValue(key, out var entry))
            return false;

        NormalizeSellListOrder(saveChanges: false);

        var ordered = SellList.Values
            .OrderBy(e => e.SortOrder <= 0 ? int.MaxValue : e.SortOrder)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0)
            .ToList();

        ordered.Remove(entry);

        var maxPosition = ordered.Count + 1;
        var clamped = Math.Clamp(requestedOrder, 1, maxPosition);
        ordered.Insert(clamped - 1, entry);

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var desired = i + 1;
            if (ordered[i].SortOrder != desired)
            {
                ordered[i].SortOrder = desired;
                changed = true;
            }
        }

        return changed;
    }

    public bool NormalizeSellListOrder(bool saveChanges = true)
    {
        if (SellList.Count == 0)
            return false;

        var ordered = SellList.Values
            .OrderBy(e => e.SortOrder <= 0 ? int.MaxValue : e.SortOrder)
            .ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? "zzz" : e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ItemId)
            .ThenBy(e => e.IsHq ? 1 : 0)
            .ToList();

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var desired = i + 1;
            if (ordered[i].SortOrder != desired)
            {
                ordered[i].SortOrder = desired;
                changed = true;
            }
        }

        if (changed && saveChanges)
            Save();

        return changed;
    }

    public int GetAppendSortOrder()
    {
        NormalizeSellListOrder(saveChanges: false);
        return SellList.Count + 1;
    }

    private int GetNextSortOrder()
    {
        NormalizeSellListOrder(saveChanges: false);

        if (SellList.Count == 0)
            return 1;

        var maxOrder = SellList.Values
            .Select(e => e.SortOrder)
            .DefaultIfEmpty(0)
            .Max();

        return maxOrder < 1 ? SellList.Count + 1 : maxOrder + 1;
    }

    #endregion

    #region Retainer behavior model

    [Serializable]
    public sealed class RetainerBehavior
    {
        public bool Enabled { get; set; } = true;
        public bool AllowSell { get; set; } = true;
        public bool AllowReprice { get; set; } = true;

        public void Normalize()
        {
            AllowSell = AllowSell;
            AllowReprice = AllowReprice;
        }
    }

    #endregion
}
