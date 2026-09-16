using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace RetainerRepricer;

/// <summary>
/// Holds the deterministic state machine definitions and related runtime state containers.
/// </summary>
public unsafe sealed partial class Plugin
{
    /// <summary>Phases the automation moves through while repricing and selling.</summary>
    private enum RunPhase
    {
        /// <summary>Idle and not attached to the Retainer List.</summary>
        Idle,

        /// <summary>Waiting to click the next enabled retainer row inside RetainerList.</summary>
        NeedOpen,

        /// <summary>Talk dialog is expected; send the advance click until it closes.</summary>
        WaitingTalk,

        /// <summary>Waiting for SelectString to populate its entries so we can pick Sell Items.</summary>
        WaitingSelectString,

        /// <summary>RetainerSellList should be visible and readable before we proceed.</summary>
        WaitingRetainerSellList,

        /// <summary>Open the current RetainerSellList slot.</summary>
        OpeningSellItem,

        /// <summary>Wait for RetainerSell to appear and dismiss any ContextMenu that blocks it.</summary>
        WaitingRetainerSell,

        /// <summary>Read the active listing’s HQ flag and asking price.</summary>
        CaptureSellContext,

        /// <summary>Click Compare Prices (or observe that the market is already open).</summary>
        OpenComparePrices,

        /// <summary>ItemSearchResult must become visible, populated, and optionally HQ-filtered.</summary>
        WaitingItemSearchResult,

        /// <summary>Evaluate market data, apply Universalis gates, and decide on a desired price.</summary>
        ReadMarketAndApplyPrice,

        /// <summary>Fallback path when ItemSearchResult reports no items and we need Universalis data.</summary>
        WaitingUniversalisNoItemsFallback,

        /// <summary>Close market UI before firing the input callback that sets the new price.</summary>
        CloseMarketThenApply,

        /// <summary>Confirm the listing after the UI reflects the new asking price.</summary>
        ConfirmAfterApply,

        /// <summary>Scan the Sell List configuration for inventory candidates.</summary>
        Sell_FindNextItemInInventory,

        /// <summary>Click an inventory slot to open RetainerSell for a new listing.</summary>
        Sell_OpenRetainerSellFromInventory,

        /// <summary>Close RetainerSell/market windows before moving on.</summary>
        CleanupAfterItem,

        /// <summary>Wait to return to RetainerSellList so we can pick the next item.</summary>
        WaitingRetainerSellListAfterItem,

        /// <summary>Unwind any stray addons and return to RetainerList.</summary>
        ExitToRetainerList,
    }

    private RunPhase _runPhase = RunPhase.Idle;

    private enum RunOrigin
    {
        RetainerList,
        AutoRetainerMenu,
    }

    private RunOrigin _runOrigin = RunOrigin.RetainerList;
    private DateTime _autoRetainerRunStartedUtc = DateTime.MinValue;
    private DateTime _autoRetainerCleanupStartedUtc = DateTime.MinValue;

    internal enum RunMode
    {
        PriceAndSell,
        PriceOnly,
        SellOnly,
    }

    private RunMode _runMode = RunMode.PriceAndSell;

    private bool ShouldReprice => _runMode != RunMode.SellOnly;
    private bool ShouldSell => _runMode != RunMode.PriceOnly;
    private bool ShouldRepriceThisRetainer => ShouldReprice && _currentRetainerAllowsReprice;
    private bool ShouldSellThisRetainer => ShouldSell && _currentRetainerAllowsSell;

    private DateTime _lastActionUtc = DateTime.MinValue;
    private DateTime _lastFrameworkTickUtc = DateTime.MinValue;

    private readonly RetainerCycleState _retainerCycle = new();
    private readonly SellWorkflowState _sellState = new();
    private readonly MarketContextState _marketState = new();
    private readonly UniversalisGateState _universalisState = new();

    private bool _currentRetainerAllowsReprice;
    private bool _currentRetainerAllowsSell;
    private int _currentRetainerRowIndex = -1;
    private string _currentRetainerName = string.Empty;

    private List<RetainerRowEntry> _retainerRowOrder => _retainerCycle.RowOrder;

    private int _retainerRowPos
    {
        get => _retainerCycle.RowPos;
        set => _retainerCycle.RowPos = value;
    }

    private DateTime _lastRetainerSyncUtc
    {
        get => _retainerCycle.LastRetainerSyncUtc;
        set => _retainerCycle.LastRetainerSyncUtc = value;
    }

    private DateTime _lastRetainerSyncThrottleLogUtc
    {
        get => _retainerCycle.LastRetainerSyncThrottleLogUtc;
        set => _retainerCycle.LastRetainerSyncThrottleLogUtc = value;
    }

    private bool _sellListCountCaptured
    {
        get => _sellState.SellListCountCaptured;
        set => _sellState.SellListCountCaptured = value;
    }

    private int _listedCountThisRetainer
    {
        get => _sellState.ListedCountThisRetainer;
        set => _sellState.ListedCountThisRetainer = value;
    }

    private int _slotIndexToOpen
    {
        get => _sellState.SlotIndexToOpen;
        set => _sellState.SlotIndexToOpen = value;
    }

    private List<SellCandidate> _sellQueue => _sellState.SellQueue;

    private int _sellCapacityThisRetainer
    {
        get => _sellState.SellCapacityThisRetainer;
        set => _sellState.SellCapacityThisRetainer = value;
    }

    private int _soldThisRetainer
    {
        get => _sellState.SoldThisRetainer;
        set => _sellState.SoldThisRetainer = value;
    }

    private bool _processingListedItem
    {
        get => _sellState.ProcessingListedItem;
        set => _sellState.ProcessingListedItem = value;
    }

    private bool _quickListRun
    {
        get => _sellState.QuickListRun;
        set => _sellState.QuickListRun = value;
    }

    private uint _currentSellItemId
    {
        get => _sellState.CurrentSellItemId;
        set => _sellState.CurrentSellItemId = value;
    }

    private bool _currentSellItemIsHq
    {
        get => _sellState.CurrentSellItemIsHq;
        set => _sellState.CurrentSellItemIsHq = value;
    }

    private int _currentSellStackSize
    {
        get => _sellState.CurrentSellStackSize;
        set => _sellState.CurrentSellStackSize = value;
    }

    private InventorySlotRef _pendingSellSlot
    {
        get => _sellState.PendingSellSlot;
        set => _sellState.PendingSellSlot = value;
    }

    private bool _hasPendingSellSlot
    {
        get => _sellState.HasPendingSellSlot;
        set => _sellState.HasPendingSellSlot = value;
    }

    private NewListingAttemptState _newListingAttemptState
    {
        get => _sellState.ListingAttemptState;
        set => _sellState.ListingAttemptState = value;
    }

    private InventorySlotRef _attemptedSellSlot
    {
        get => _sellState.AttemptedSellSlot;
        set => _sellState.AttemptedSellSlot = value;
    }

    private bool _hasAttemptedSellSlot
    {
        get => _sellState.HasAttemptedSellSlot;
        set => _sellState.HasAttemptedSellSlot = value;
    }

    private uint _attemptedSellItemId
    {
        get => _sellState.AttemptedSellItemId;
        set => _sellState.AttemptedSellItemId = value;
    }

    private bool _attemptedSellItemIsHq
    {
        get => _sellState.AttemptedSellItemIsHq;
        set => _sellState.AttemptedSellItemIsHq = value;
    }

    private int _attemptedSellStackSize
    {
        get => _sellState.AttemptedSellStackSize;
        set => _sellState.AttemptedSellStackSize = value;
    }

    private int _attemptedSellSlotQuantityBefore
    {
        get => _sellState.AttemptedSellSlotQuantityBefore;
        set => _sellState.AttemptedSellSlotQuantityBefore = value;
    }

    private int _attemptedListedCountBefore
    {
        get => _sellState.AttemptedListedCountBefore;
        set => _sellState.AttemptedListedCountBefore = value;
    }

    private DateTime _attemptedSellStartedUtc
    {
        get => _sellState.AttemptedSellStartedUtc;
        set => _sellState.AttemptedSellStartedUtc = value;
    }

    private HashSet<long> _failedSellSlotKeys
        => _sellState.FailedSellSlotKeys;

    private bool _awaitingRetainerContextMenu
    {
        get => _sellState.AwaitingRetainerContextMenu;
        set => _sellState.AwaitingRetainerContextMenu = value;
    }

    private DateTime _retainerContextMenuRequestedUtc
    {
        get => _sellState.RetainerContextMenuRequestedUtc;
        set => _sellState.RetainerContextMenuRequestedUtc = value;
    }

    private Dictionary<ulong, int> _retainerSellCounts
        => _sellState.RetainerSellCounts;

    private Dictionary<ulong, int> _retainerExistingSellCounts
        => _sellState.RetainerExistingSellCounts;

    private bool _needsExistingListingScan
    {
        get => _sellState.NeedsExistingListingScan;
        set => _sellState.NeedsExistingListingScan = value;
    }

    private HashSet<string> _myRetainers => _marketState.MyRetainers;

    private Dictionary<string, string> _myRetainerLabels => _marketState.MyRetainerLabels;

    private bool _currentIsHq
    {
        get => _marketState.CurrentIsHq;
        set => _marketState.CurrentIsHq = value;
    }

    private int? _stagedDesiredPrice
    {
        get => _marketState.StagedDesiredPrice;
        set => _marketState.StagedDesiredPrice = value;
    }

    private string _stagedReferenceSeller
    {
        get => _marketState.StagedReferenceSeller;
        set => _marketState.StagedReferenceSeller = value;
    }

    private bool _stagedReferenceIsMine
    {
        get => _marketState.StagedReferenceIsMine;
        set => _marketState.StagedReferenceIsMine = value;
    }

    private bool _hasAppliedStagedPrice
    {
        get => _marketState.HasAppliedStagedPrice;
        set => _marketState.HasAppliedStagedPrice = value;
    }

    private bool _isrThrottleRetried
    {
        get => _marketState.IsrThrottleRetried;
        set => _marketState.IsrThrottleRetried = value;
    }

    private DateTime _isrThrottleUntilUtc
    {
        get => _marketState.IsrThrottleUntilUtc;
        set => _marketState.IsrThrottleUntilUtc = value;
    }

    private double _mbIntervalSec
    {
        get => _marketState.MbIntervalSeconds;
        set => _marketState.MbIntervalSeconds = value;
    }

    private DateTime _lastMbQueryUtc
    {
        get => _marketState.LastMbQueryUtc;
        set => _marketState.LastMbQueryUtc = value;
    }

    private DateTime _isrOpenedUtc
    {
        get => _marketState.IsrOpenedUtc;
        set => _marketState.IsrOpenedUtc = value;
    }

    private int _isrNoItemsConfirm
    {
        get => _marketState.IsrNoItemsConfirm;
        set => _marketState.IsrNoItemsConfirm = value;
    }

    private bool _isrNeedApplyHqFilter
    {
        get => _marketState.IsrNeedApplyHqFilter;
        set => _marketState.IsrNeedApplyHqFilter = value;
    }

    private bool _isrHqFilterApplied
    {
        get => _marketState.IsrHqFilterApplied;
        set => _marketState.IsrHqFilterApplied = value;
    }

    private DateTime _isrHqFilterRequestedUtc
    {
        get => _marketState.IsrHqFilterRequestedUtc;
        set => _marketState.IsrHqFilterRequestedUtc = value;
    }

    private DateTime _isrHqFilterVisibleUtc
    {
        get => _marketState.IsrHqFilterVisibleUtc;
        set => _marketState.IsrHqFilterVisibleUtc = value;
    }

    private bool _isrHqFilterFallbackTried
    {
        get => _marketState.IsrHqFilterFallbackTried;
        set => _marketState.IsrHqFilterFallbackTried = value;
    }

    private DateTime _isrAllowFilterAfterUtc
    {
        get => _marketState.IsrAllowFilterAfterUtc;
        set => _marketState.IsrAllowFilterAfterUtc = value;
    }

    private Task<decimal?>? _universalisGateTask
    {
        get => _universalisState.PendingTask;
        set => _universalisState.PendingTask = value;
    }

    private UniversalisGateKey? _universalisGateKey
    {
        get => _universalisState.GateKey;
        set => _universalisState.GateKey = value;
    }

    private decimal? _universalisGateAverage
    {
        get => _universalisState.AveragePrice;
        set => _universalisState.AveragePrice = value;
    }

    private string DescribeCurrentRetainerForLog()
    {
        return GetRetainerLabelForLog(_currentRetainerRowIndex);
    }

    private static string GetRetainerLabelForLog(int rowIndex)
        => rowIndex < 0
            ? "retainer?"
            : $"retainer{(rowIndex + 1).ToString(CultureInfo.InvariantCulture)}";

    private string GetSellerLabelForLog(string seller)
    {
        if (string.IsNullOrWhiteSpace(seller))
            return string.Empty;

        if (_myRetainerLabels.TryGetValue(seller, out var retainerLabel))
            return retainerLabel;

        return _myRetainers.Contains(seller)
            ? "retainer?"
            : seller;
    }

    private int? _universalisPriceFloor
    {
        get => _universalisState.PriceFloor;
        set => _universalisState.PriceFloor = value;
    }

    private struct SellCandidate
    {
        public uint ItemId;
        public bool IsHq;
        public int MinCountToSell;
        public string Name;
        public Dictionary<string, int>? RetainerCaps;
        public Dictionary<string, int>? RetainerStackSizes;
        public int StackSizeMax;
    }

    private struct InventorySlotRef
    {
        public int Container;
        public int Slot;
    }

    private enum NewListingAttemptState
    {
        None,
        PendingOpen,
        PendingConfirm,
        AwaitingResult,
        Succeeded,
        Failed,
    }

    private sealed class RetainerCycleState
    {
        public List<RetainerRowEntry> RowOrder { get; } = new();
        public int RowPos { get; set; } = -1;
        public DateTime LastRetainerSyncUtc { get; set; } = DateTime.MinValue;
        public DateTime LastRetainerSyncThrottleLogUtc { get; set; } = DateTime.MinValue;
    }

    private readonly struct RetainerRowEntry
    {
        public int RowIndex { get; init; }
        public string Name { get; init; }
        public bool AllowSell { get; init; }
        public bool AllowReprice { get; init; }
    }

    private sealed class SellWorkflowState
    {
        public bool SellListCountCaptured { get; set; }
        public int ListedCountThisRetainer { get; set; }
        public int SlotIndexToOpen { get; set; }
        public List<SellCandidate> SellQueue { get; } = new();
        public int SellCapacityThisRetainer { get; set; }
        public int SoldThisRetainer { get; set; }
        public bool ProcessingListedItem { get; set; } = true;
        public bool QuickListRun { get; set; }
        public uint CurrentSellItemId { get; set; }
        public bool CurrentSellItemIsHq { get; set; }
        public int CurrentSellStackSize { get; set; }
        public InventorySlotRef PendingSellSlot { get; set; }
        public bool HasPendingSellSlot { get; set; }
        public NewListingAttemptState ListingAttemptState { get; set; }
        public InventorySlotRef AttemptedSellSlot { get; set; }
        public bool HasAttemptedSellSlot { get; set; }
        public uint AttemptedSellItemId { get; set; }
        public bool AttemptedSellItemIsHq { get; set; }
        public int AttemptedSellStackSize { get; set; }
        public int AttemptedSellSlotQuantityBefore { get; set; }
        public int AttemptedListedCountBefore { get; set; }
        public DateTime AttemptedSellStartedUtc { get; set; } = DateTime.MinValue;
        public HashSet<long> FailedSellSlotKeys { get; } = new();
        public bool AwaitingRetainerContextMenu { get; set; }
        public DateTime RetainerContextMenuRequestedUtc { get; set; } = DateTime.MinValue;
        public Dictionary<ulong, int> RetainerSellCounts { get; } = new();
        public Dictionary<ulong, int> RetainerExistingSellCounts { get; } = new();
        public bool NeedsExistingListingScan { get; set; }
    }

    private sealed class MarketContextState
    {
        public HashSet<string> MyRetainers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> MyRetainerLabels { get; } = new(StringComparer.Ordinal);
        public Dictionary<RepricingCacheKey, CachedRepricingDecision> RepricingCache { get; } =
            new(RepricingCacheKeyComparer.Instance);
        public ulong RepricingCacheCharacterId { get; set; }
        public RepricingCacheKey? CurrentRepricingCacheKey { get; set; }
        public bool CurrentIsHq { get; set; }
        public int? StagedDesiredPrice { get; set; }
        public string StagedReferenceSeller { get; set; } = string.Empty;
        public bool StagedReferenceIsMine { get; set; }
        public bool HasAppliedStagedPrice { get; set; }
        public bool IsrThrottleRetried { get; set; }
        public DateTime IsrThrottleUntilUtc { get; set; } = DateTime.MinValue;
        public double MbIntervalSeconds { get; set; } = 1.5d;
        public DateTime LastMbQueryUtc { get; set; } = DateTime.MinValue;
        public DateTime IsrOpenedUtc { get; set; } = DateTime.MinValue;
        public int IsrNoItemsConfirm { get; set; }
        public bool IsrNeedApplyHqFilter { get; set; }
        public bool IsrHqFilterApplied { get; set; }
        public DateTime IsrHqFilterRequestedUtc { get; set; } = DateTime.MinValue;
        public DateTime IsrHqFilterVisibleUtc { get; set; } = DateTime.MinValue;
        public bool IsrHqFilterFallbackTried { get; set; }
        public DateTime IsrAllowFilterAfterUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class UniversalisGateState
    {
        public Task<decimal?>? PendingTask { get; set; }
        public UniversalisGateKey? GateKey { get; set; }
        public decimal? AveragePrice { get; set; }
        public int? PriceFloor { get; set; }
    }
}
