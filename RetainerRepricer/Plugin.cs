using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Services;
using RetainerRepricer.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin : IDalamudPlugin
{
    #region Dalamud services

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    internal static PluginLogger Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    #endregion

    #region Constants

    private const string CommandName = "/repricer";
    private const string CommandAlias = "/rr";
    private const string CommandHelp = "help|? | start [price|sell] | stop | config | logs (default opens Sell List)";
    private const string ChatTag = "Retainer Repricer";
    private const ushort InfoTagColor = 34;
    private const ushort ErrorTagColor = 14;
    private const ushort SuccessTagColor = 45;

    #endregion

    #region Config-driven tuning accessors

    private int UndercutAmount => Configuration.UndercutAmount;
    private float MarketValidationThreshold => Configuration.MarketValidationThreshold;
    private double ActionIntervalSeconds => Configuration.ActionIntervalSeconds;
    private double RetainerSyncIntervalSeconds => Configuration.RetainerSyncIntervalSeconds;
    private double ItemSearchResultThrottleBackoffSeconds => Configuration.ItemSearchResultThrottleBackoffSeconds;
    private double MbBaseIntervalSeconds => Configuration.MbBaseIntervalSeconds;
    private double MbIntervalMinSeconds => Configuration.MbIntervalMinSeconds;
    private double MbIntervalMaxSeconds => Configuration.MbIntervalMaxSeconds;
    private double MbJitterMaxSeconds => Configuration.MbJitterMaxSeconds;
    private double IsrNoItemsSettleSeconds => Configuration.IsrNoItemsSettleSeconds;
    private double IsrHqFilterInitialDelaySeconds => Configuration.IsrHqFilterInitialDelaySeconds;
    private double IsrHqFilterUiDebounceSeconds => Configuration.IsrHqFilterUiDebounceSeconds;
    private double IsrHqFilterOpenRetrySeconds => Configuration.IsrHqFilterOpenRetrySeconds;
    private double IsrHqFilterPostOpenSeconds => Configuration.IsrHqFilterPostOpenSeconds;
    private double FrameworkTickIntervalSeconds => Configuration.FrameworkTickIntervalSeconds;

    #endregion

    #region Public/plugin-facing state

    public Configuration Configuration { get; }
    public readonly WindowSystem WindowSystem = new("RetainerRepricer");

    internal bool IsRunning;
    private bool _dismissContextMenuNextTick;

    private readonly UniversalisApiClient _universalisClient;
    private readonly SellListSmartSorter _smartSorter;
    private readonly SellListInventoryPruner _sellListPruner;
    private readonly PluginLogBuffer _pluginLogBuffer;
    private readonly AutoRetainerIntegration _autoRetainerIntegration;
    private Task<bool>? _pendingSmartSortTask;
    private bool _smartSortKickoffDone;
    private SellListInventoryPruner.SellListInventoryPruneResult? _lastPruneResult;
    private bool _autoPruneRunThisCycle;
    private bool _pendingPostSellPrune;

    #endregion

    #region Windows / UI helpers

    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }
    private LogWindow LogWindow { get; }
    private MinCountPopup MinCountPopup { get; }
    private ContextMenuManager ContextMenu { get; }

    private readonly Ui.UiReader _uiReader;
    internal Ui.UiReader UiReader => _uiReader;
    internal PluginLogBuffer PluginLogBuffer => _pluginLogBuffer;

    #endregion

    #region Lifecycle

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commandManager,
        IPluginLog log,
        IGameGui gameGui)
    {
        PluginInterface = pi;
        CommandManager = commandManager;
        GameGui = gameGui;

        ECommonsMain.Init(pi, this);

        Configuration = pi.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pi);

        _pluginLogBuffer = new PluginLogBuffer();
        Log = new PluginLogger(log, _pluginLogBuffer);

        _universalisClient = new UniversalisApiClient(Log);
        _smartSorter = new SellListSmartSorter(Configuration, _universalisClient, Log, GetWorldDcRegionKey);
        _sellListPruner = new SellListInventoryPruner(Configuration, Log);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        LogWindow = new LogWindow(this);
        MinCountPopup = new MinCountPopup(Configuration);
        ContextMenu = new ContextMenuManager(this, Configuration, MinCountPopup);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(MinCountPopup);

        _uiReader = new Ui.UiReader(GameGui);
        _autoRetainerIntegration = new AutoRetainerIntegration(this);

        _mbIntervalSec = Configuration.MbBaseIntervalSeconds;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = CommandHelp
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = CommandHelp
        });

        pi.UiBuilder.Draw += WindowSystem.Draw;
        pi.UiBuilder.OpenConfigUi += ToggleConfigUi;
        pi.UiBuilder.OpenMainUi += OpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        Log.Information($"[{pi.Manifest.Name}] loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Framework.Update -= OnFrameworkUpdate;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        LogWindow.Dispose();
        MinCountPopup.Dispose();
        ContextMenu.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        _universalisClient.Dispose();
        _smartSorter.Dispose();

        BestEffortCleanupAutoRetainerUi();
        _autoRetainerIntegration.Dispose();

        ECommonsMain.Dispose();
    }

    #endregion

    #region Retainer list sync

    private const double RetainerSyncThrottleLogIntervalSeconds = 1.0d;

    internal unsafe List<string> ReadRetainerNames()
    {
        var names = new List<string>();

        var list = _uiReader.GetRetainerList();
        if (list == null) return names;

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            names.Add(name.Trim('\'', ' '));
        }

        return names;
    }

    internal void SyncRetainersIntoConfig(IEnumerable<string> names)
    {
        var changed = false;
        var addedRetainerLabels = new List<string>();
        var rowIndex = -1;

        foreach (var n in names)
        {
            rowIndex++;
            var behavior = Configuration.GetRetainerBehavior(n);

            if (!Configuration.RetainersEnabled.ContainsKey(n))
            {
                Configuration.SetRetainerEnabled(n, behavior.Enabled);
                addedRetainerLabels.Add(GetRetainerLabelForLog(rowIndex));
                changed = true;
            }
        }

        if (addedRetainerLabels.Count > 0)
        {
            Log.Information("[RR] Sync: Added {Count} new retainer(s) to configuration: {Retainers}",
                addedRetainerLabels.Count, string.Join(", ", addedRetainerLabels));
        }

        if (changed) Configuration.Save();
    }

    internal unsafe void TrySyncRetainersThrottled()
    {
        var rl = GameGui.GetAddonByName("RetainerList", 1);
        if (rl.IsNull) return;

        var now = DateTime.UtcNow;
        if ((now - _lastRetainerSyncUtc).TotalSeconds < RetainerSyncIntervalSeconds)
        {
            if ((now - _lastRetainerSyncThrottleLogUtc).TotalSeconds >= RetainerSyncThrottleLogIntervalSeconds)
            {
                Log.Verbose("[RR] Sync: Throttled, skipping this cycle");
                _lastRetainerSyncThrottleLogUtc = now;
            }

            return;
        }

        SyncRetainersIntoConfig(ReadRetainerNames());
        _lastRetainerSyncUtc = now;
        _lastRetainerSyncThrottleLogUtc = DateTime.MinValue;
    }

    internal void RebuildMyRetainersSet()
    {
        _myRetainers.Clear();
        _myRetainerLabels.Clear();

        var rowIndex = 0;
        foreach (var name in Configuration.GetAllRetainerNames())
        {
            _myRetainers.Add(name);
            _myRetainerLabels[name] = GetRetainerLabelForLog(rowIndex);

            rowIndex++;
        }
    }

    internal int GetStackSizeCap(uint itemId, InventoryType? inventoryType = null)
    {
        if (inventoryType == InventoryType.Crystals)
            return 9999;

        if (itemId >= 2 && itemId <= 19)
            return 9999;

        return 99;
    }

    #endregion

    #region Start/stop helpers

    internal bool IsQuickListVisible()
        => IsAddonVisible("RetainerSellList");

    internal bool CanStartQuickList()
    {
        if (IsRunning || !IsAddonVisible("RetainerSellList"))
            return false;

        var listed = ReadCurrentRetainerSellListCount();
        return listed.HasValue && listed.Value < 20;
    }

    internal bool StartQuickListFromInventory(int container, int slot)
    {
        if (!CanStartQuickList())
        {
            Log.Verbose("[RR][QuickList] Start rejected: RetainerSellList unavailable, full, or a run is active.");
            return false;
        }

        var inventoryType = (InventoryType)container;
        if (!_uiReader.TryGetInventorySlot(inventoryType, slot, out var inventorySlot) || inventorySlot == null)
            return false;

        var itemId = inventorySlot->GetBaseItemId();
        if (itemId == 0 || inventorySlot->Quantity <= 0)
            return false;

        var itemRow = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId);
        if (!Ui.UiReader.IsInventorySlotSellable(inventorySlot, itemRow))
            return false;

        var isHq = inventorySlot->IsHighQuality();
        var maxStackSize = GetStackSizeCap(itemId, inventoryType);
        var stackSize = Math.Min((int)inventorySlot->Quantity, maxStackSize);
        var listedCount = ReadCurrentRetainerSellListCount();
        if (!listedCount.HasValue || listedCount.Value >= 20 || stackSize <= 0)
            return false;

        ResetRunState();
        _runMode = RunMode.PriceAndSell;
        IsRunning = true;
        _runPhase = RunPhase.Sell_OpenRetainerSellFromInventory;
        _lastActionUtc = DateTime.MinValue;
        _quickListRun = true;
        _currentRetainerAllowsReprice = true;
        _currentRetainerAllowsSell = true;
        _listedCountThisRetainer = listedCount.Value;
        _sellCapacityThisRetainer = 20 - listedCount.Value;
        _currentSellItemId = itemId;
        _currentSellItemIsHq = isHq;
        _currentSellStackSize = stackSize;
        _pendingSellSlot = new InventorySlotRef { Container = container, Slot = slot };
        _hasPendingSellSlot = true;
        _processingListedItem = false;
        BeginNewListingAttempt(_pendingSellSlot, itemId, isHq, stackSize, listedCount.Value, (int)inventorySlot->Quantity,
            DateTime.UtcNow);
        FireContextMenuDismiss();

        Log.Information("[RR][QuickList] Started itemId={ItemId} hq={IsHq} quantity={Quantity} container={Container} slot={Slot}",
            itemId, isHq, stackSize, container, slot);
        return true;
    }

    internal unsafe bool StartRunFromRetainerList(RunMode mode = RunMode.PriceAndSell, bool notifyChatOnFailure = false)
    {
        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            NotifyStartFailed("RetainerList not open. Summon retainers first.", notifyChatOnFailure);
            return false;
        }

        ResetRunState();
        _runMode = mode;
        _runOrigin = RunOrigin.RetainerList;

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim('\'', ' ');

            var behavior = Configuration.GetRetainerBehavior(name);
            if (!behavior.Enabled)
                continue;

            _retainerRowOrder.Add(new RetainerRowEntry
            {
                RowIndex = i,
                Name = name,
                AllowSell = behavior.AllowSell,
                AllowReprice = behavior.AllowReprice,
            });
        }

        if (_retainerRowOrder.Count == 0)
        {
            NotifyStartFailed("No enabled retainers found in the list.", notifyChatOnFailure);
            StopRun();
            return false;
        }

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        var rowSummary = string.Join(",", _retainerRowOrder.Select(r => r.RowIndex));
        Log.Information($"[RR] Start ({DescribeRunMode(mode)}). Enabled rows = {rowSummary}");
        return true;
    }

    private unsafe bool StartRun(RunMode mode = RunMode.PriceAndSell, bool notifyChatOnFailure = false)
    {
        if (IsRunning)
        {
            NotifyStartFailed("Already running.", notifyChatOnFailure);
            return false;
        }

        if (!IsAddonVisible("RetainerList"))
        {
            NotifyStartFailed("Retainer List is not open. Use a summoning bell first.", notifyChatOnFailure);
            return false;
        }

        ResetRunState();
        _runMode = mode;
        _runOrigin = RunOrigin.RetainerList;

        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            NotifyStartFailed("Retainer List is not readable right now.", notifyChatOnFailure);
            return false;
        }

        var count = list->GetItemCount();
        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            name = name.Trim('\'', ' ');

            var behavior = Configuration.GetRetainerBehavior(name);
            if (!behavior.Enabled)
                continue;

            _retainerRowOrder.Add(new RetainerRowEntry
            {
                RowIndex = i,
                Name = name,
                AllowSell = behavior.AllowSell,
                AllowReprice = behavior.AllowReprice,
            });
        }

        if (_retainerRowOrder.Count == 0)
        {
            NotifyStartFailed("No enabled retainers found in the list.", notifyChatOnFailure);
            return false;
        }

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        var rowSummary = string.Join(",", _retainerRowOrder.Select(r => r.RowIndex));
        Log.Information($"[RR] Started ({DescribeRunMode(mode)}). Enabled retainers: {rowSummary}");
        return true;
    }

    internal void StopRun()
    {
        if (IsRunning && _runOrigin == RunOrigin.AutoRetainerMenu)
        {
            RequestAutoRetainerCleanup("stop requested");
            return;
        }

        StopRunImmediately("[RR] Stopped.");
    }

    private void StopRunImmediately(string logMessage)
    {
        IsRunning = false;
        _runPhase = RunPhase.Idle;
        _runMode = RunMode.PriceAndSell;
        _runOrigin = RunOrigin.RetainerList;
        _autoRetainerRunStartedUtc = DateTime.MinValue;
        _autoRetainerCleanupStartedUtc = DateTime.MinValue;

        _retainerRowOrder.Clear();
        _retainerRowPos = -1;
        _currentRetainerAllowsReprice = false;
        _currentRetainerAllowsSell = false;
        _currentRetainerRowIndex = -1;
        _currentRetainerName = string.Empty;

        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        _sellQueue.Clear();
        _pendingSmartSortTask = null;
        _smartSortKickoffDone = false;
        _autoPruneRunThisCycle = false;
        _pendingPostSellPrune = false;
        _sellCapacityThisRetainer = 0;
        _soldThisRetainer = 0;

        _processingListedItem = true;
        _quickListRun = false;
        _currentSellItemId = 0;
        _currentSellItemIsHq = false;
        _currentSellStackSize = 0;
        _hasPendingSellSlot = false;
        ResetNewListingAttempt();
        _failedSellSlotKeys.Clear();
        if (Configuration.EnablePerRetainerCaps)
        {
            _retainerSellCounts.Clear();
            _retainerExistingSellCounts.Clear();
        }
        _needsExistingListingScan = false;

        ResetUniversalisGateState();

        Log.Information(logMessage);
    }

    private void ResetRunState()
    {
        ResetUniversalisGateState();

        RebuildMyRetainersSet();

        _retainerRowOrder.Clear();
        _retainerRowPos = -1;

        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        _sellQueue.Clear();
        _pendingSmartSortTask = null;
        _smartSortKickoffDone = false;
        _autoPruneRunThisCycle = false;
        _pendingPostSellPrune = false;
        _sellCapacityThisRetainer = 0;
        _soldThisRetainer = 0;

        _processingListedItem = true;
        _quickListRun = false;
        _currentSellItemId = 0;
        _currentSellItemIsHq = false;
        _currentSellStackSize = 0;
        _hasPendingSellSlot = false;
        ResetNewListingAttempt();
        _failedSellSlotKeys.Clear();
        if (Configuration.EnablePerRetainerCaps)
        {
            _retainerSellCounts.Clear();
            _retainerExistingSellCounts.Clear();
        }
        _needsExistingListingScan = false;

        // Reset pacing so one bad throttle event doesn't slow the whole plugin permanently.
        _mbIntervalSec = MbBaseIntervalSeconds;
        _lastMbQueryUtc = DateTime.MinValue;

        _isrOpenedUtc = DateTime.MinValue;
        _isrNoItemsConfirm = 0;

        _isrThrottleRetried = false;
        _isrThrottleUntilUtc = DateTime.MinValue;
        _isrNeedApplyHqFilter = false;
        _isrAllowFilterAfterUtc = DateTime.MinValue;
        _isrHqFilterApplied = false;
        _isrHqFilterRequestedUtc = DateTime.MinValue;
        _isrHqFilterFallbackTried = false;
        _isrHqFilterVisibleUtc = DateTime.MinValue;

        _stagedDesiredPrice = null;
        _stagedReferenceSeller = string.Empty;
        _stagedReferenceIsMine = false;
        _hasAppliedStagedPrice = false;

        _lastRetainerSyncUtc = DateTime.MinValue;
        _lastRetainerSyncThrottleLogUtc = DateTime.MinValue;
    }

    private static string DescribeRunMode(RunMode mode)
        => mode switch
        {
            RunMode.PriceOnly => "price-only",
            RunMode.SellOnly => "sell-only",
            _ => "price+sell",
        };

    private void NotifyStartFailed(string reason, bool notifyChat)
    {
        Log.Information($"[RR] Start blocked: {reason}");
        if (notifyChat)
            PrintError($"Cannot start: {reason}");
    }

    private void PrintChat(string message, ushort tagColor)
    {
        ChatGui.Print(message, ChatTag, tagColor);
    }

    private void PrintInfo(string message)
    {
        PrintChat(message, InfoTagColor);
    }

    private void PrintError(string message)
    {
        PrintChat(message, ErrorTagColor);
    }

    private void PrintSuccess(string message)
    {
        PrintChat(message, SuccessTagColor);
    }

    private string GetCompletionMessage()
        => _runMode switch
        {
            RunMode.PriceOnly => "Repricing run complete.",
            RunMode.SellOnly => "Sell-only run complete.",
            _ => "Repricing and selling run complete.",
        };

    #endregion

    #region Decision helpers

    private int DecideNewPrice(int lowestPrice, bool sellerIsMine)
    {
        if (sellerIsMine) return lowestPrice;

        var v = lowestPrice - UndercutAmount;
        return v < 1 ? 1 : v;
    }

    private unsafe bool IsSelectStringReady()
    {
        var addon = GameGui.GetAddonByName("SelectString", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        try
        {
            var ss = new AddonMaster.SelectString(addon.Address);
            return ss.EntryCount > 2;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool IsRetainerListReady()
    {
        var list = _uiReader.GetRetainerList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        for (int i = 0; i < count; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            if (!string.IsNullOrWhiteSpace(name))
                return true;
        }

        return false;
    }

    #endregion

    #region Inventory selling helpers

    /// <summary>
    /// Scans bags (Inventory1-4) once. Returns:
    /// - totalCount across all stacks found
    /// - first sellable slotRef (container, slot) if any
    /// </summary>
    private Ui.UiReader.InventoryLookupResult FindItemInInventory(uint baseItemId, bool isHq, out InventorySlotRef slotRef)
    {
        slotRef = default;
        var result = _uiReader.FindItemInInventory(baseItemId, isHq, _failedSellSlotKeys);

        if (result.FoundSellable)
        {
            slotRef = new InventorySlotRef { Container = result.Container, Slot = result.Slot };
        }

        return result;
    }

    /// <summary>
    /// Clicks an InventoryGrid slot to open RetainerSell for a new listing.
    /// This only works if InventoryGrid is actually visible.
    /// </summary>
    private unsafe bool TryOpenRetainerSellFromInventory(InventorySlotRef slotRef)
        => _uiReader.TryOpenRetainerSellFromInventory(slotRef.Container, slotRef.Slot);

    private bool TryReadInventorySlotState(InventorySlotRef slotRef, out uint baseItemId, out bool isHq, out int quantity)
    {
        baseItemId = 0;
        isHq = false;
        quantity = 0;

        if (!_uiReader.TryGetInventorySlot((InventoryType)slotRef.Container, slotRef.Slot, out var inventorySlot) || inventorySlot == null)
            return false;

        if (inventorySlot->Quantity <= 0)
            return false;

        baseItemId = inventorySlot->GetBaseItemId();
        isHq = inventorySlot->IsHighQuality();
        quantity = (int)inventorySlot->Quantity;
        return true;
    }

    private static long GetInventorySlotKey(InventorySlotRef slotRef)
        => ((long)(uint)slotRef.Container << 32) | (uint)slotRef.Slot;

    private int GetCurrentExpectedListedCount()
        => Math.Clamp(_listedCountThisRetainer + _soldThisRetainer, 0, 20);

    private int? ReadCurrentRetainerSellListCount()
    {
        var listed = _uiReader.ReadRetainerSellListListedCount();
        return listed.HasValue
            ? Math.Clamp(listed.Value, 0, 20)
            : null;
    }

    private void ResetNewListingAttempt()
    {
        _newListingAttemptState = NewListingAttemptState.None;
        _attemptedSellSlot = default;
        _hasAttemptedSellSlot = false;
        _attemptedSellItemId = 0;
        _attemptedSellItemIsHq = false;
        _attemptedSellStackSize = 0;
        _attemptedSellSlotQuantityBefore = -1;
        _attemptedListedCountBefore = 0;
        _attemptedSellStartedUtc = DateTime.MinValue;
    }

    private void BeginNewListingAttempt(InventorySlotRef slotRef, uint itemId, bool isHq, int stackSize, int listedCountBefore,
        int slotQuantityBefore, DateTime now)
    {
        _newListingAttemptState = NewListingAttemptState.PendingOpen;
        _attemptedSellSlot = slotRef;
        _hasAttemptedSellSlot = true;
        _attemptedSellItemId = itemId;
        _attemptedSellItemIsHq = isHq;
        _attemptedSellStackSize = stackSize;
        _attemptedListedCountBefore = listedCountBefore;
        _attemptedSellSlotQuantityBefore = slotQuantityBefore;
        _attemptedSellStartedUtc = now;
    }

    private void RememberFailedAttemptedSlot()
    {
        if (!_hasAttemptedSellSlot)
            return;

        _failedSellSlotKeys.Add(GetInventorySlotKey(_attemptedSellSlot));
    }

    #endregion

    #region Market reading helpers

    private unsafe bool TryReadMarketRow(int rowIndex, out int unitPrice, out string seller, out bool isHq)
    {
        unitPrice = 0;
        seller = string.Empty;
        isHq = false;

        var list = _uiReader.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0 || rowIndex < 0 || rowIndex >= count) return false;

        var r = list->GetItemRenderer(rowIndex);
        if (r == null) return false;

        isHq = _uiReader.RowIsHq(r);

        var unitRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
        var sellerRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.SellerNodeId);

        var parsed = Ui.UiReader.ParseGil(unitRaw);
        if (parsed == null || parsed.Value <= 0) return false;

        unitPrice = parsed.Value;
        seller = (sellerRaw ?? string.Empty).Trim('\'', ' ');

        return true;
    }

    private unsafe bool TryPickReferenceListing(out int lowestPrice, out string lowestSeller, out bool usedUniversalisFallback)
    {
        lowestPrice = 0;
        lowestSeller = string.Empty;
        usedUniversalisFallback = false;

        var list = _uiReader.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        var gateFloor = _universalisPriceFloor;

        if (_currentIsHq)
        {
            var max = Math.Min(count, 10);
            for (int i = 0; i < max; i++)
            {
                if (!TryReadMarketRow(i, out var price, out var seller, out var isHq)) continue;
                if (!isHq) continue;

                if (gateFloor.HasValue && price < gateFloor.Value)
                {
                    Log.Debug($"[RR][Gate] HQ row {i} price {price} below floor {gateFloor.Value}; skipping row.");
                    continue;
                }

                Log.Debug($"[RR] Market HQ ref row={i} price={price} seller='{GetSellerLabelForLog(seller)}'");
                lowestPrice = price;
                lowestSeller = seller;
                return true;
            }

            if (gateFloor.HasValue)
            {
                lowestPrice = ResolveUniversalisFallbackPrice(gateFloor.Value, out var fallbackSeller);
                lowestSeller = fallbackSeller;
                usedUniversalisFallback = true;

                if (fallbackSeller == "[UniversalisAverage]")
                {
                    Log.Information($"[RR][Gate] No HQ listings met the floor {gateFloor.Value}; using Universalis average {lowestPrice}.");
                }
                else
                {
                    Log.Information($"[RR][Gate] No HQ listings met the floor {gateFloor.Value}; using floor price.");
                }

                return true;
            }

            Log.Information("[RR] HQ item has no visible HQ rows on the first page.");
            return false;
        }

        if (gateFloor.HasValue)
        {
            var max = Math.Min(count, 10);
            for (int i = 0; i < max; i++)
            {
                if (!TryReadMarketRow(i, out var price, out var seller, out var isHq)) continue;

                if (price < gateFloor.Value)
                {
                    Log.Debug($"[RR][Gate] Row {i} price {price} below floor {gateFloor.Value}; skipping row.");
                    continue;
                }

                var qualityLabel = isHq ? "HQ" : "NQ";
                Log.Debug($"[RR] Market gated ref row={i} price={price} seller='{GetSellerLabelForLog(seller)}' quality={qualityLabel}");
                lowestPrice = price;
                lowestSeller = seller;
                return true;
            }

            lowestPrice = ResolveUniversalisFallbackPrice(gateFloor.Value, out var fallbackSellerNq);
            lowestSeller = fallbackSellerNq;
            usedUniversalisFallback = true;

            if (fallbackSellerNq == "[UniversalisAverage]")
            {
                Log.Information($"[RR][Gate] No listings met the floor {gateFloor.Value}; using Universalis average {lowestPrice}.");
            }
            else
            {
                Log.Information($"[RR][Gate] No listings met the floor {gateFloor.Value}; using floor price.");
            }

            return true;
        }

        // NQ without gate: row 0 is the reference.
        if (!TryReadMarketRow(0, out var p0, out var s0, out _)) return false;

        Log.Debug($"[RR] Market NQ ref row0 price={p0} seller='{GetSellerLabelForLog(s0)}'");
        lowestPrice = p0;
        lowestSeller = s0;
        return true;
    }

    private int ResolveUniversalisFallbackPrice(int gateFloor, out string sellerLabel)
    {
        if (_universalisGateAverage is { } average && average > 0)
        {
            var avgPrice = (int)Math.Floor(average);
            if (avgPrice < 1)
                avgPrice = 1;

            sellerLabel = "[UniversalisAverage]";
            return avgPrice;
        }

        sellerLabel = "[UniversalisFloor]";
        return gateFloor < 1 ? 1 : gateFloor;
    }
    private unsafe bool IsAnyHqVisibleInFirstPage()
    {
        var list = _uiReader.GetMarketList();
        if (list == null) return false;

        var count = list->GetItemCount();
        if (count <= 0) return false;

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            // Reuse existing row-read path so we don't trust unpopulated nodes
            if (!TryReadMarketRow(i, out _, out _, out var isHq)) continue;
            if (isHq) return true;
        }

        return false;
    }

    #endregion

    #region Window helpers

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void OpenMainUi() => ToggleConfigUi();
    internal void OpenSellListTab() => ConfigWindow.OpenSellListTab();
    internal void OpenSettingsTab() => ConfigWindow.OpenSettingsTab();
    internal void OpenLogWindow() => LogWindow.Open();

    internal bool SmartSortEnabled => _smartSorter.IsEnabled;
    internal bool SmartSortIsSorting => _smartSorter.IsSorting;
    internal DateTime SmartSortLastRunUtc => _smartSorter.LastSortUtc;
    internal bool SmartSortRefreshDue => _smartSorter.IsRefreshDue();
    internal SellListInventoryPruner.SellListInventoryPruneResult? LastPruneResult => _lastPruneResult;

    internal Task<bool> RequestSmartSortAsync(string reason, bool force = false)
    {
        RunAutoPruneIfEnabled($"smart_sort:{reason}", latchRun: false);

        if (force)
            return _smartSorter.ForceSortAsync(reason);

        return _smartSorter.TrySortAsync(reason, force: false);
    }

    internal void NotifySmartSortSettingChanged()
    {
        // Reset pending state so a new run can force refresh immediately.
        _pendingSmartSortTask = null;
        _smartSortKickoffDone = false;
    }

    internal SellListInventoryPruner.SellListInventoryPruneResult RunInventoryPruneManual(string reason = "manual_button")
        => RunPruneInternal(reason);

    private SellListInventoryPruner.SellListInventoryPruneResult? RunAutoPruneIfEnabled(string reason, bool latchRun)
    {
        if (!Configuration.AutoPruneMissingInventory)
            return null;

        if (latchRun && _autoPruneRunThisCycle)
            return null;

        var result = RunPruneInternal(reason);

        if (latchRun)
            _autoPruneRunThisCycle = true;

        return result;
    }

    private SellListInventoryPruner.SellListInventoryPruneResult RunPruneInternal(string reason)
    {
        var result = _sellListPruner.Run(reason);
        _lastPruneResult = result;
        return result;
    }

    private void QueuePostSellPruneIfNeeded()
    {
        if (!Configuration.AutoPruneMissingInventory)
            return;

        if (_soldThisRetainer <= 0)
            return;

        _pendingPostSellPrune = true;
    }

    private void RunPendingSellPrune()
    {
        if (!_pendingPostSellPrune)
            return;

        RunAutoPruneIfEnabled("sell_cycle_complete", latchRun: false);
        _pendingPostSellPrune = false;
    }

    private void TransitionToExitToRetainerList()
    {
        QueuePostSellPruneIfNeeded();
        _runPhase = RunPhase.ExitToRetainerList;
    }

    private void PrepareRetainerForProcessing(RetainerRowEntry entry)
    {
        _sellListCountCaptured = false;
        _listedCountThisRetainer = 0;
        _slotIndexToOpen = 0;

        _sellQueue.Clear();
        if (Configuration.EnablePerRetainerCaps)
            _retainerSellCounts.Clear();
        _retainerExistingSellCounts.Clear();
        _needsExistingListingScan = Configuration.EnablePerRetainerCaps;

        foreach (var sellEntry in Configuration.GetSellListOrdered())
        {
            if (sellEntry.ItemId == 0)
                continue;

            sellEntry.EnsureRetainerCaps();
            sellEntry.EnsureRetainerStackSizes();
            _sellQueue.Add(new SellCandidate
            {
                ItemId = sellEntry.ItemId,
                IsHq = sellEntry.IsHq,
                MinCountToSell = Math.Max(1, sellEntry.MinCountToSell),
                Name = sellEntry.Name ?? string.Empty,
                RetainerCaps = sellEntry.RetainerCaps,
                RetainerStackSizes = sellEntry.RetainerStackSizes,
                StackSizeMax = GetStackSizeCap(sellEntry.ItemId),
            });
        }

        _sellCapacityThisRetainer = 0;
        _soldThisRetainer = 0;
        _processingListedItem = true;
        _currentSellItemId = 0;
        _currentSellItemIsHq = false;
        _currentSellStackSize = 0;
        _hasPendingSellSlot = false;
        ResetNewListingAttempt();
        _failedSellSlotKeys.Clear();

        _mbIntervalSec = MbBaseIntervalSeconds;
        _lastMbQueryUtc = DateTime.MinValue;

        _currentRetainerAllowsReprice = entry.AllowReprice;
        _currentRetainerAllowsSell = entry.AllowSell;
        _currentRetainerRowIndex = entry.RowIndex;
        _currentRetainerName = entry.Name ?? string.Empty;
    }

    #endregion
}
