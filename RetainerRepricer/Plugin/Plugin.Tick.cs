using System;

using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Services;
using RetainerRepricer.Ui;

namespace RetainerRepricer;

/// <summary>
/// Main state-machine tick that advances repricing/selling flow.
/// </summary>
public unsafe sealed partial class Plugin
{
    private const double RetainerContextMenuTimeoutSeconds = 2.0d;
    #region Run tick (state machine)

    internal unsafe void TickRun()
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;

        if (_runOrigin == RunOrigin.AutoRetainerMenu)
        {
            if (_autoRetainerCleanupStartedUtc == DateTime.MinValue &&
                _autoRetainerRunStartedUtc != DateTime.MinValue &&
                now - _autoRetainerRunStartedUtc >= AutoRetainerRunTimeout)
            {
                RequestAutoRetainerCleanup("run timed out");
            }

            if (_autoRetainerCleanupStartedUtc != DateTime.MinValue &&
                now - _autoRetainerCleanupStartedUtc >= AutoRetainerCleanupTimeout)
            {
                CompleteAutoRetainerRun(originRestored: false);
                return;
            }
        }

        if ((now - _lastActionUtc).TotalSeconds < ActionIntervalSeconds)
            return;

        // Snapshot addon visibility once per tick (cheaper and keeps checks consistent for this tick).
        var retainerListVisible = IsAddonVisible("RetainerList");
        var talkVisible = IsAddonVisible("Talk");
        var selectStringVisible = IsAddonVisible("SelectString");
        var retainerSellListVisible = IsAddonVisible("RetainerSellList");
        var retainerSellVisible = IsAddonVisible("RetainerSell");
        var marketOpen = IsAddonOpen("ItemSearchResult");
        var itemHistoryOpen = IsAddonOpen("ItemHistory");
        var contextMenuVisible = IsAddonVisible("ContextMenu");

        switch (_runPhase)
        {
            #region Retainer navigation

            case RunPhase.NeedOpen:
                {
                    if (_runOrigin == RunOrigin.RetainerList && !retainerListVisible)
                        return;

                    if (_runOrigin == RunOrigin.AutoRetainerMenu && !selectStringVisible)
                        return;

                    if (_runOrigin == RunOrigin.RetainerList && !IsRetainerListReady())
                    {
                        Log.Verbose("[RR] RetainerList visible but names not populated yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    RunAutoPruneIfEnabled("run_start", latchRun: true);

                    if (SmartSortEnabled && !_smartSortKickoffDone)
                    {
                        if (_pendingSmartSortTask == null)
                            _pendingSmartSortTask = _smartSorter.ForceSortAsync("run_start");

                        if (_pendingSmartSortTask != null && !_pendingSmartSortTask.IsCompleted)
                        {
                            if (_pendingSmartSortTask.IsFaulted)
                            {
                                Log.Warning(_pendingSmartSortTask.Exception?.GetBaseException(), "[RR][SmartSort] Run-start sort failed.");
                                _pendingSmartSortTask = null;
                                _smartSortKickoffDone = true;
                            }
                            else
                            {
                                _lastActionUtc = now;
                                return;
                            }
                        }

                        if (_pendingSmartSortTask is { IsCompleted: true })
                        {
                            _pendingSmartSortTask = null;
                            _smartSortKickoffDone = true;
                        }
                    }
                    else if (!SmartSortEnabled)
                    {
                        _smartSortKickoffDone = true;
                    }

                    _retainerRowPos++;
                    if (_retainerRowPos >= _retainerRowOrder.Count)
                    {
                        if (_runOrigin == RunOrigin.AutoRetainerMenu)
                        {
                            CompleteAutoRetainerRun(originRestored: selectStringVisible);
                            return;
                        }

                        Log.Information("[RR] Finished: processed all enabled retainers.");

                        if (Configuration.CloseRetainerListAddon)
                        {
                            Log.Information("[RR] Closing RetainerList (CloseRetainerListAddon enabled).");
                            CloseAddonIfOpen("RetainerList");
                        }

                        PrintSuccess(GetCompletionMessage());
                        StopRun();
                        return;
                    }

                    var entry = _retainerRowOrder[_retainerRowPos];
                    PrepareRetainerForProcessing(entry);

                    if (_runOrigin == RunOrigin.AutoRetainerMenu)
                    {
                        Log.Information("[RR][AutoRetainer] Opening Sell items for the assigned retainer.");
                        _runPhase = RunPhase.WaitingSelectString;
                        _lastActionUtc = DateTime.MinValue;
                        return;
                    }

                    Log.Information($"[RR] Opening retainer {_retainerRowPos + 1}/{_retainerRowOrder.Count}");

                    TryClickRetainerListEntry(_currentRetainerRowIndex);

                    _runPhase = RunPhase.WaitingTalk;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingTalk:
                {
                    // Some setups skip Talk entirely and jump straight to SelectString.
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            Log.Verbose("[RR] SelectString visible but not ready.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] SelectString opened for {DescribeCurrentRetainerForLog()}; selecting Sell items.");
                        if (!TrySelectSellItems())
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug($"[RR] Talk open for {DescribeCurrentRetainerForLog()}; advancing.");
                        var ok = TryAdvanceTalk();
                        Log.Debug(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _runPhase = RunPhase.WaitingSelectString;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingSelectString:
                {
                    if (selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            Log.Verbose("[RR] SelectString visible but not ready.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] SelectString opened for {DescribeCurrentRetainerForLog()}; selecting Sell items.");
                        if (!TrySelectSellItems())
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.WaitingRetainerSellList;
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug($"[RR] Talk open (late) for {DescribeCurrentRetainerForLog()}; advancing.");
                        var ok = TryAdvanceTalk();
                        Log.Debug(ok ? "[RR] Talk click sent." : "[RR] Talk click failed.");

                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellList:
                {
                    if (!retainerSellListVisible) return;

                    // Don't latch listed count until we can read it reliably.
                    if (!_sellListCountCaptured)
                    {
                        var raw = _uiReader.ReadRetainerSellListCountText();
                        var listedOpt = _uiReader.ReadRetainerSellListListedCount();

                        if (listedOpt is null || string.IsNullOrWhiteSpace(raw))
                        {
                            Log.Verbose($"[RR] RetainerSellList visible but count not readable (raw='{raw ?? "null"}').");
                            _lastActionUtc = now;
                            return;
                        }

                        _sellListCountCaptured = true;

                        var listed = listedOpt.Value;
                        _listedCountThisRetainer = Math.Clamp(listed, 0, 20);
                        _slotIndexToOpen = 0;

                        _sellCapacityThisRetainer = Math.Max(0, 20 - _listedCountThisRetainer);
                        _soldThisRetainer = 0;

                        Log.Information($"[RR] Entered RetainerSellList. Listed={_listedCountThisRetainer}; new sells capacity={_sellCapacityThisRetainer}");

                        if (_listedCountThisRetainer <= 0)
                            _needsExistingListingScan = false;
                    }

                    var retainerLabel = DescribeCurrentRetainerForLog();
                    var needListingScan = Configuration.EnablePerRetainerCaps && _needsExistingListingScan && _listedCountThisRetainer > 0;

                    if ((ShouldRepriceThisRetainer && _listedCountThisRetainer > 0) || needListingScan)
                    {
                        _runPhase = RunPhase.OpeningSellItem;
                    }
                    else if (ShouldSellThisRetainer)
                    {
                        if (!ShouldRepriceThisRetainer)
                        {
                            if (!ShouldReprice)
                                Log.Information("[RR] Repricing skipped (run mode = sell-only).");
                            else
                                Log.Information($"[RR] Repricing skipped for {retainerLabel}: retainer setting disables repricing.");
                        }
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                    }
                    else
                    {
                        if (!ShouldRepriceThisRetainer)
                        {
                            if (!ShouldReprice)
                                Log.Information("[RR] Repricing skipped (run mode = sell-only).");
                            else
                                Log.Information($"[RR] Repricing skipped for {retainerLabel}: retainer setting disables repricing.");
                        }

                        if (!ShouldSellThisRetainer)
                        {
                            if (!ShouldSell)
                                Log.Information("[RR] Selling skipped (run mode = price-only).");
                            else
                                Log.Information($"[RR] Selling skipped for {retainerLabel}: retainer setting disables selling.");
                        }

                        TransitionToExitToRetainerList();
                    }

                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Repricing existing listed items

            case RunPhase.OpeningSellItem:
                {
                    var needListingScan = Configuration.EnablePerRetainerCaps && _needsExistingListingScan;

                    if (!ShouldRepriceThisRetainer && !needListingScan)
                    {
                        if (ShouldSellThisRetainer)
                            _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        else
                            TransitionToExitToRetainerList();
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellListVisible) return;

                    if (_slotIndexToOpen < 0) _slotIndexToOpen = 0;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        if (needListingScan)
                        {
                            _needsExistingListingScan = false;
                            if (ShouldSellThisRetainer)
                                _runPhase = RunPhase.Sell_FindNextItemInInventory;
                            else
                                TransitionToExitToRetainerList();
                        }
                        else
                        {
                            _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        }
                        _lastActionUtc = now;
                        return;
                    }

                    var idx = Math.Clamp(_slotIndexToOpen, 0, 19);
                    Log.Debug($"[RR] Opening sell slot {idx} ({_slotIndexToOpen + 1}/{_listedCountThisRetainer})");

                    _processingListedItem = true;

                    if (!FireRetainerSellListOpenItem(idx))
                        return;

                    ArmRetainerContextMenuExpectation(now);

                    _runPhase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSell:
                {
                    if (contextMenuVisible)
                    {
                        if (_awaitingRetainerContextMenu)
                        {
                            if (TrySelectRetainerContextMenuAdjustPrice())
                            {
                                ClearRetainerContextMenuExpectation();
                                _lastActionUtc = now;
                                return;
                            }

                            if (HasRetainerContextMenuTimedOut(now))
                            {
                                Log.Warning($"[RR] Retainer context menu timed out on slot {_slotIndexToOpen + 1}/{_listedCountThisRetainer}; skipping.");
                                FireContextMenuDismiss();
                                ClearRetainerContextMenuExpectation();
                                _slotIndexToOpen++;
                                _runPhase = RunPhase.OpeningSellItem;
                                _lastActionUtc = now;
                                return;
                            }

                            _lastActionUtc = now;
                            return;
                        }

                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (_awaitingRetainerContextMenu && HasRetainerContextMenuTimedOut(now))
                    {
                        Log.Warning($"[RR] Retainer context menu never appeared for slot {_slotIndexToOpen + 1}/{_listedCountThisRetainer}; skipping.");
                        ClearRetainerContextMenuExpectation();
                        _slotIndexToOpen++;
                        _runPhase = RunPhase.OpeningSellItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellVisible) return;

                    if (_awaitingRetainerContextMenu)
                        ClearRetainerContextMenuExpectation();

                    Log.Debug($"[RR] RetainerSell opened (listedItem={_processingListedItem}).");

                    _runPhase = RunPhase.CaptureSellContext;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Repricing pipeline

            case RunPhase.CaptureSellContext:
                {
                    if (!retainerSellVisible) return;

                    // Wait until the fields we rely on are readable.
                    var priceOpt = _uiReader.ReadRetainerSellAskingPrice();
                    if (priceOpt is null)
                    {
                        Log.Verbose("[RR] RetainerSell visible but asking price not readable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    _currentIsHq = _uiReader.IsRetainerSellItemHq();

                    // Always use the display-safe name (raw payload can leak printable junk).
                    var name = _uiReader.GetRetainerSellItemNameDisplay(stripHqGlyph: true);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Log.Verbose("[RR] RetainerSell visible but item name not stable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    ResetUniversalisGateState();
                    _marketState.CurrentRepricingCacheKey = null;

                    Log.Information($"[RR] Sell capture: item='{name}' currentPrice={priceOpt.Value}");

                    if (_processingListedItem)
                        RecordExistingListingForCaps(name, _currentIsHq);

                    var shouldResolvePrice = _processingListedItem
                        ? ShouldRepriceThisRetainer
                        : ShouldSellThisRetainer;

                    if (shouldResolvePrice && TryGetRepricingCacheKey(name, _currentIsHq, out var cacheKey))
                    {
                        _marketState.CurrentRepricingCacheKey = cacheKey;
                        if (TryUseCachedRepricingDecision(cacheKey, priceOpt.Value))
                        {
                            _lastActionUtc = now;
                            return;
                        }
                    }

                    if (_processingListedItem)
                    {
                        if (ShouldRepriceThisRetainer)
                        {
                            _runPhase = RunPhase.OpenComparePrices;
                        }
                        else if (Configuration.EnablePerRetainerCaps && _needsExistingListingScan)
                        {
                            _runPhase = RunPhase.CleanupAfterItem;
                        }
                        else
                        {
                            if (ShouldSellThisRetainer)
                                _runPhase = RunPhase.Sell_FindNextItemInInventory;
                            else
                                TransitionToExitToRetainerList();
                        }
                    }
                    else
                    {
                        if (!ShouldSellThisRetainer)
                        {
                            Log.Warning("[RR][Sell] Unexpected inventory capture while selling disabled; aborting listing.");
                            _runPhase = RunPhase.CleanupAfterItem;
                        }
                        else
                        {
                            _runPhase = RunPhase.OpenComparePrices;
                        }
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.OpenComparePrices:
                {
                    if (!retainerSellVisible) return;

                    _isrThrottleRetried = false;
                    _isrThrottleUntilUtc = DateTime.MinValue;

                    if (marketOpen)
                    {
                        Log.Debug("[RR] ItemSearchResult already open; skipping Compare Prices click.");

                        _isrOpenedUtc = DateTime.MinValue;
                        _isrNoItemsConfirm = 0;

                        _isrNeedApplyHqFilter = false; // fallback only; don't filter immediately
                        _isrHqFilterApplied = false;
                        _isrHqFilterRequestedUtc = DateTime.MinValue;
                        _isrHqFilterFallbackTried = false;
                        _isrAllowFilterAfterUtc = DateTime.MinValue;
                        _isrHqFilterVisibleUtc = DateTime.MinValue;

                        _runPhase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    var addon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (addon.IsNull) return;

                    // Space out Compare Prices calls to avoid "Please wait..." throttles.
                    var jitter = Random.Shared.NextDouble() * MbJitterMaxSeconds;
                    if (_lastMbQueryUtc != DateTime.MinValue &&
                        (now - _lastMbQueryUtc).TotalSeconds < (_mbIntervalSec + jitter))
                    {
                        Log.Verbose("[RR] Compare Prices pacing gate hit.");
                        _lastActionUtc = now;
                        return;
                    }

                    _lastMbQueryUtc = now;
                    new AddonMaster.RetainerSell(addon.Address).ComparePrices();

                    _isrOpenedUtc = DateTime.MinValue;
                    _isrNoItemsConfirm = 0;

                    _isrNeedApplyHqFilter = false; // fallback only; don't filter immediately
                    _isrHqFilterApplied = false;
                    _isrHqFilterRequestedUtc = DateTime.MinValue;
                    _isrHqFilterFallbackTried = false;
                    _isrAllowFilterAfterUtc = DateTime.MinValue;
                    _isrHqFilterVisibleUtc = DateTime.MinValue;

                    Log.Debug($"[RR] ComparePrices clicked (interval={_mbIntervalSec:0.00}s).");
                    _runPhase = RunPhase.WaitingItemSearchResult;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingItemSearchResult:
                {
                    if (!marketOpen) return;

                    // If HQ item: apply the HQ filter so HQ rows are rendered immediately.
                    // This avoids list virtualization where only 10 visible rows have populated text.
                    if (_isrNeedApplyHqFilter && !_isrHqFilterApplied)
                    {
                        // Respect global filter timing gate
                        if (_isrAllowFilterAfterUtc != DateTime.MinValue && now < _isrAllowFilterAfterUtc)
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        var filterVisible = IsAddonVisible("ItemSearchFilter");
                        if (!filterVisible)
                            _isrHqFilterVisibleUtc = DateTime.MinValue;

                        // If filter UI is visible, toggle HQ and accept
                        if (filterVisible)
                        {
                            if (_isrHqFilterVisibleUtc == DateTime.MinValue)
                            {
                                _isrHqFilterVisibleUtc = now;
                                _lastActionUtc = now;
                                return;
                            }

                            if ((now - _isrHqFilterVisibleUtc).TotalSeconds < IsrHqFilterUiDebounceSeconds)
                            {
                                _lastActionUtc = now;
                                return;
                            }

                            FireItemSearchFilterToggleHq();
                            FireItemSearchFilterAccept();

                            _isrHqFilterApplied = true;
                            _isrNeedApplyHqFilter = false;

                            // reset ISR tracking so rows repopulate cleanly
                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            Log.Debug("[RR] HQ filter applied; waiting for ISR to repopulate.");
                            _lastActionUtc = now;
                            return;
                        }

                        // Filter window not open yet → request it
                        if (_isrHqFilterRequestedUtc == DateTime.MinValue ||
                            (now - _isrHqFilterRequestedUtc).TotalSeconds >= IsrHqFilterOpenRetrySeconds)
                        {
                            FireItemSearchResultOpenFilter();
                            _isrHqFilterRequestedUtc = now;

                            // brief pause to allow the filter window to appear
                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterPostOpenSeconds);

                            _lastActionUtc = now;
                            return;
                        }

                        _lastActionUtc = now;
                        return;
                    }

                    var status = _uiReader.GetItemSearchResultStatus(out var msg);

                    if (_isrOpenedUtc == DateTime.MinValue)
                        _isrOpenedUtc = now;

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        var age = (now - _isrOpenedUtc).TotalSeconds;
                        if (age < IsrNoItemsSettleSeconds)
                        {
                            Log.Verbose("[RR] ISR 'No items found' seen too early; waiting.");
                            _lastActionUtc = now;
                            return;
                        }

                        _isrNoItemsConfirm++;
                        if (_isrNoItemsConfirm < 2)
                        {
                            Log.Verbose("[RR] ISR 'No items found' confirm pass 1/2.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listCheck = _uiReader.GetMarketList();
                        var rows = listCheck != null ? listCheck->GetItemCount() : 0;
                        if (rows > 0)
                        {
                            Log.Debug($"[RR] ISR message says none, but rows={rows}; ignoring message.");
                            _isrNoItemsConfirm = 0;
                            _runPhase = RunPhase.ReadMarketAndApplyPrice;
                            _lastActionUtc = now;
                            return;
                        }

                        if (Configuration.UseUniversalisForEmptyMarket && Configuration.UseUniversalisApi)
                        {
                            Log.Information($"[RR] ISR: '{msg}' -> no competing listings; attempting Universalis fallback.");
                            ResetUniversalisGateState();
                            _runPhase = RunPhase.WaitingUniversalisNoItemsFallback;
                        }
                        else
                        {
                            if (_isrHqFilterApplied)
                            {
                                Log.Information($"[RR] ISR: '{msg}' -> HQ filter found no HQ competitors; skipping repricing.");
                            }
                            else
                            {
                                Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skipping repricing.");
                            }

                            _runPhase = RunPhase.CleanupAfterItem;
                        }
                        _lastActionUtc = now;
                        return;
                    }
                    else
                    {
                        _isrNoItemsConfirm = 0;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        // If we're in the middle of applying the HQ filter, do NOT retry ComparePrices.
                        // Filtering triggers a repopulation and can temporarily surface "Please wait".
                        if (_isrNeedApplyHqFilter && !_isrHqFilterApplied)
                        {
                            Log.Verbose("[RR] ISR throttle while applying HQ filter; waiting (no ComparePrices retry).");
                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            _lastActionUtc = now;
                            return;
                        }

                        if (now < _isrThrottleUntilUtc)
                        {
                            Log.Verbose("[RR] ISR throttle backoff window active.");
                            _lastActionUtc = now;
                            return;
                        }

                        if (!_isrThrottleRetried && _isrThrottleUntilUtc == DateTime.MinValue)
                        {
                            _mbIntervalSec = Math.Clamp(_mbIntervalSec + 0.08, MbIntervalMinSeconds, MbIntervalMaxSeconds);

                            _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);
                            Log.Debug("[RR] ISR throttle: backing off then retrying once.");
                            return;
                        }

                        if (!_isrThrottleRetried)
                        {
                            var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                            if (sellAddon.IsNull) return;

                            _lastMbQueryUtc = now;
                            new AddonMaster.RetainerSell(sellAddon.Address).ComparePrices();

                            _isrThrottleRetried = true;
                            _isrThrottleUntilUtc = DateTime.MinValue;

                            Log.Debug("[RR] ISR throttle: retry sent.");
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Information("[RR] ISR throttle persists after one retry; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Information($"[RR] ISR message: '{msg}' -> skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var list = _uiReader.GetMarketList();
                    if (list == null)
                    {
                        Log.Verbose("[RR] ISR not ready: market list null.");
                        _lastActionUtc = now;
                        return;
                    }

                    var count = list->GetItemCount();
                    if (count <= 0)
                    {
                        Log.Verbose("[RR] ISR not ready: 0 rows.");
                        _lastActionUtc = now;
                        return;
                    }

                    // Renderer can exist before nodes/text are actually populated.
                    var r0 = list->GetItemRenderer(0);
                    if (r0 == null || r0->UldManager.NodeList == null || r0->UldManager.NodeListCount <= 0)
                    {
                        Log.Verbose("[RR] ISR not ready: row0 renderer nodes not ready.");
                        _lastActionUtc = now;
                        return;
                    }

                    var unitRaw = _uiReader.ReadRendererText(r0, Ui.NodePaths.UnitPriceNodeId);
                    if (Ui.UiReader.ParseGil(unitRaw) is null)
                    {
                        Log.Verbose("[RR] ISR not ready: row0 unit price not parsable yet.");
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] ItemSearchResult ready (rows={count}).");
                    _mbIntervalSec = Math.Clamp(_mbIntervalSec - 0.02, MbIntervalMinSeconds, MbIntervalMaxSeconds);

                    _runPhase = RunPhase.ReadMarketAndApplyPrice;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ReadMarketAndApplyPrice:
                {
                    if (!retainerSellVisible) return;
                    if (!marketOpen) return;

                    var status = _uiReader.GetItemSearchResultStatus(out var msg);
                    if (_isrOpenedUtc == DateTime.MinValue)
                        _isrOpenedUtc = now;

                    if (status == Ui.UiReader.ItemSearchResultStatus.NoItemsFound)
                    {
                        var age = (now - _isrOpenedUtc).TotalSeconds;
                        if (age < IsrNoItemsSettleSeconds)
                        {
                            Log.Verbose("[RR] ISR 'No items found' seen too early; waiting.");
                            _lastActionUtc = now;
                            return;
                        }

                        _isrNoItemsConfirm++;
                        if (_isrNoItemsConfirm < 2)
                        {
                            Log.Verbose("[RR] ISR 'No items found' confirm pass 1/2.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listCheck = _uiReader.GetMarketList();
                        var rows = listCheck != null ? listCheck->GetItemCount() : 0;
                        if (rows <= 0)
                        {
                            if (_isrHqFilterApplied)
                            {
                                Log.Information($"[RR] ISR: '{msg}' -> HQ filter found no HQ competitors; skipping repricing.");
                            }
                            else
                            {
                                Log.Information($"[RR] ISR: '{msg}' -> no competing listings; skipping repricing.");
                            }

                            _runPhase = RunPhase.CleanupAfterItem;
                            _lastActionUtc = now;
                            return;
                        }

                        Log.Debug($"[RR] ISR message says none, but rows={rows}; ignoring message.");
                        _isrNoItemsConfirm = 0;
                    }
                    else
                    {
                        _isrNoItemsConfirm = 0;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.PleaseWaitRetry)
                    {
                        // Optional: backoff a little so we don't hammer the gate
                        _isrThrottleUntilUtc = now.AddSeconds(ItemSearchResultThrottleBackoffSeconds);

                        Log.Debug("[RR] ISR throttle surfaced during read; returning to ISR gate.");
                        _runPhase = RunPhase.WaitingItemSearchResult;
                        _lastActionUtc = now;
                        return;
                    }

                    if (status == Ui.UiReader.ItemSearchResultStatus.OtherMessage)
                    {
                        Log.Information($"[RR] ISR message: '{msg}' -> skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (Configuration.EnableUndercutPreventionGate && Configuration.UseUniversalisApi)
                    {
                        var gateStatus = UpdateUniversalisGate(out var floorPrice);
                        if (gateStatus == UniversalisGateStatus.Pending)
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        if (gateStatus == UniversalisGateStatus.Failed)
                        {
                            Log.Information("[RR][Gate] Skipping repricing: Universalis gate failed for this item.");
                            _runPhase = RunPhase.CleanupAfterItem;
                            _lastActionUtc = now;
                            return;
                        }

                        _universalisPriceFloor = gateStatus == UniversalisGateStatus.Ready
                            ? floorPrice
                            : null;
                    }
                    else
                    {
                        _universalisPriceFloor = null;
                    }

                    if (!TryPickReferenceListing(out var lowestPrice, out var lowestSeller, out var usedUniversalisFallback))
                    {
                        // If this is an HQ item and we didn't see HQ in the first page,
                        // try applying the HQ filter ONCE as a fallback to avoid list virtualization.
                        if (_currentIsHq && !_isrHqFilterFallbackTried)
                        {
                            if (IsAnyHqVisibleInFirstPage())
                            {
                                Log.Debug("[RR] HQ became visible in first page; retrying pick without filtering.");
                                _lastActionUtc = now;
                                return; // next tick TryPickReferenceListing will succeed
                            }

                            Log.Information("[RR] HQ item has no visible HQ rows on the first page; applying HQ filter fallback once.");

                            _isrHqFilterFallbackTried = true;

                            // Arm the filter state machine
                            _isrNeedApplyHqFilter = true;
                            _isrHqFilterApplied = false;
                            _isrHqFilterRequestedUtc = DateTime.MinValue;

                            // delay before attempting HQ filter so ISR can settle
                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterInitialDelaySeconds);

                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            _runPhase = RunPhase.WaitingItemSearchResult;
                            _lastActionUtc = now;
                            return;
                        }

                        // If fallback already tried (or NQ item), skip.
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (_currentIsHq && usedUniversalisFallback && !_isrHqFilterFallbackTried)
                    {
                        if (!IsAnyHqVisibleInFirstPage())
                        {
                            Log.Information("[RR] Universalis fallback triggered without HQ visibility; applying HQ filter once.");

                            _isrHqFilterFallbackTried = true;

                            _isrNeedApplyHqFilter = true;
                            _isrHqFilterApplied = false;
                            _isrHqFilterRequestedUtc = DateTime.MinValue;

                            _isrAllowFilterAfterUtc = now.AddSeconds(IsrHqFilterInitialDelaySeconds);

                            _isrNoItemsConfirm = 0;
                            _isrOpenedUtc = now;

                            _runPhase = RunPhase.WaitingItemSearchResult;
                            _lastActionUtc = now;
                            return;
                        }
                    }

                    var referenceIsMine =
                        !string.IsNullOrWhiteSpace(lowestSeller) &&
                        _myRetainers.Contains(lowestSeller);

                    var desired = usedUniversalisFallback
                        ? lowestPrice
                        : DecideNewPrice(lowestPrice, referenceIsMine);

                    CacheCurrentRepricingDecision(desired, lowestSeller, referenceIsMine);

                    var sellAddonPeek = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddonPeek.IsNull) return;

                    var current = new AddonMaster.RetainerSell(sellAddonPeek.Address).AskingPrice;

                    if (current == desired)
                    {
                        Log.Information($"[RR] Price unchanged ({desired}); skipping apply.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    _stagedDesiredPrice = desired;
                    _stagedReferenceSeller = lowestSeller;
                    _stagedReferenceIsMine = referenceIsMine;
                    _hasAppliedStagedPrice = false;

                    Log.Information($"[RR] Stage apply: current={current} desired={desired} seller='{GetSellerLabelForLog(lowestSeller)}' mine={referenceIsMine}");

                    CloseMarketWindows();

                    _runPhase = RunPhase.CloseMarketThenApply;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingUniversalisNoItemsFallback:
                {
                    if (!marketOpen) return;

                    if (!TryGetCurrentMarketItemId(out var itemId))
                    {
                        Log.Warning("[RR][NoItems] Unable to resolve current item id for Universalis lookup; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var region = GetWorldDcRegionKey();
                    if (string.IsNullOrWhiteSpace(region))
                    {
                        Log.Warning("[RR][NoItems] Unable to resolve world/DC region; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var baseUrl = UniversalisApiClient.AggregatedBaseUrl;
                    var key = new UniversalisGateKey(itemId, _currentIsHq, region, baseUrl);

                    if (_universalisGateKey != key)
                    {
                        _universalisGateKey = key;
                        _universalisGateAverage = null;
                        _universalisGateTask = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
                        Log.Debug($"[RR][NoItems] Requesting Universalis average for item {itemId} (HQ={_currentIsHq}, region='{region}').");
                    }

                    var task = _universalisGateTask;
                    if (task == null)
                    {
                        Log.Warning("[RR][NoItems] Universalis task was null; skipping item.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (!task.IsCompleted)
                    {
                        _lastActionUtc = now;
                        return;
                    }

                    try
                    {
                        var result = task.Result;
                        if (result.HasValue)
                        {
                            _universalisGateAverage = result.Value;
                            var price = (int)Math.Floor(result.Value);
                            if (price < 1) price = 1;

                            _stagedDesiredPrice = price;
                            _stagedReferenceSeller = "Universalis Average";
                            _stagedReferenceIsMine = false;
                            _hasAppliedStagedPrice = false;

                            CacheCurrentRepricingDecision(price, _stagedReferenceSeller, referenceIsMine: false);

                            Log.Information($"[RR][NoItems] Using Universalis average {result.Value:0} -> {price} for item {itemId} (HQ={_currentIsHq}).");

                            CloseMarketWindows();
                            _runPhase = RunPhase.CloseMarketThenApply;
                        }
                        else
                        {
                            Log.Information($"[RR][NoItems] Universalis returned no data for item {itemId}; skipping.");
                            _runPhase = RunPhase.CleanupAfterItem;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"[RR][NoItems] Universalis request failed for item {itemId}; skipping.");
                        _runPhase = RunPhase.CleanupAfterItem;
                    }

                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CloseMarketThenApply:
                {
                    if (MarketWindowsStillOpen())
                    {
                        CloseMarketWindows();
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellVisible)
                    {
                        RecoverAfterRetainerSellClosed("waiting to apply price", now);
                        return;
                    }

                    if (_stagedDesiredPrice is null)
                    {
                        Log.Warning("[RR] Staged price was null; bailing to cleanup.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    var current = _uiReader.ReadRetainerSellAskingPrice();
                    if (current is null)
                    {
                        Log.Verbose("[RR] Waiting RetainerSell stable (asking price unreadable).");
                        _lastActionUtc = now;
                        return;
                    }

                    if (_hasAppliedStagedPrice)
                    {
                        if (current.Value != _stagedDesiredPrice.Value)
                        {
                            Log.Verbose($"[RR] Waiting price apply... ui={current.Value} desired={_stagedDesiredPrice.Value}");
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.ConfirmAfterApply;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] Apply after market close: {current.Value} -> {_stagedDesiredPrice.Value}");

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null) return;

                    Callback.Fire(unit, updateState: true, 2, _stagedDesiredPrice.Value);

                    _hasAppliedStagedPrice = true;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.ConfirmAfterApply:
                {
                    if (!retainerSellVisible)
                    {
                        RecoverAfterRetainerSellClosed("waiting to confirm price", now);
                        return;
                    }

                    if (_stagedDesiredPrice is null)
                    {
                        Log.Warning("[RR] Staged price was null; bailing to cleanup.");
                        _runPhase = RunPhase.CleanupAfterItem;
                        _lastActionUtc = now;
                        return;
                    }

                    if (MarketWindowsStillOpen())
                    {
                        CloseMarketWindows();
                        _lastActionUtc = now;
                        return;
                    }

                    var desired = _stagedDesiredPrice.Value;
                    var uiPrice = _uiReader.ReadRetainerSellAskingPrice();
                    if (uiPrice == null || uiPrice.Value != desired)
                    {
                        Log.Verbose($"[RR] Waiting price apply... ui={(uiPrice?.ToString() ?? "null")} desired={desired}");
                        _lastActionUtc = now;
                        return;
                    }

                    var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
                    if (sellAddon.IsNull) return;

                    var unit = (AtkUnitBase*)sellAddon.Address;
                    if (unit == null || !unit->IsVisible) return;

                    if (_currentSellStackSize > 0)
                    {
                        var desiredQuantity = Math.Clamp(_currentSellStackSize, 1, 9999);
                        try
                        {
                            new AddonMaster.RetainerSell(unit).Quantity = desiredQuantity;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[RR] Failed to set sell quantity; proceeding with default amount.");
                        }
                    }

                    Log.Information($"[RR] Confirm price={desired}");

                    Callback.Fire(unit, updateState: true, 0);

                    if (!_processingListedItem)
                        _newListingAttemptState = NewListingAttemptState.AwaitingResult;

                    _stagedDesiredPrice = null;
                    _hasAppliedStagedPrice = false;

                    _runPhase = RunPhase.CleanupAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.CleanupAfterItem:
                {
                    CloseMarketWindows();
                    CloseRetainerSellIfOpen();
                    ClearRetainerContextMenuExpectation();

                    Log.Verbose("[RR] Cleanup complete (market closed, exited RetainerSell).");

                    _isrNeedApplyHqFilter = false;
                    _isrHqFilterApplied = false;
                    _isrHqFilterRequestedUtc = DateTime.MinValue;
                    _isrHqFilterFallbackTried = false;
                    _isrAllowFilterAfterUtc = DateTime.MinValue;
                    _isrHqFilterVisibleUtc = DateTime.MinValue;
                    _isrNoItemsConfirm = 0;

                    _runPhase = RunPhase.WaitingRetainerSellListAfterItem;
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.WaitingRetainerSellListAfterItem:
                {
                    if (!retainerSellListVisible) return;
                    if (retainerSellVisible) return;

                    if (!_processingListedItem)
                    {
                        var listedNow = ReadCurrentRetainerSellListCount();
                        if (!listedNow.HasValue)
                        {
                            Log.Verbose("[RR][Sell] RetainerSellList returned but listed count is not readable yet.");
                            _lastActionUtc = now;
                            return;
                        }

                        var listedBefore = _attemptedListedCountBefore;
                        var listedAfter = listedNow.Value;
                        var succeeded = false;

                        if (listedAfter > listedBefore)
                        {
                            succeeded = true;
                            Log.Information($"[RR][Sell] Listing confirmed by count change: {listedBefore} -> {listedAfter}.");
                        }
                        else if (_hasAttemptedSellSlot)
                        {
                            if (TryReadInventorySlotState(_attemptedSellSlot, out var slotItemId, out var slotIsHq, out var slotQuantity))
                            {
                                if (slotItemId == _attemptedSellItemId && slotIsHq == _attemptedSellItemIsHq)
                                {
                                    if (_attemptedSellSlotQuantityBefore >= 0 && slotQuantity < _attemptedSellSlotQuantityBefore)
                                    {
                                        succeeded = true;
                                        Log.Information($"[RR][Sell] Listing confirmed by slot quantity change on container={_attemptedSellSlot.Container} slot={_attemptedSellSlot.Slot}: {_attemptedSellSlotQuantityBefore} -> {slotQuantity}.");
                                    }
                                    else if (_attemptedSellSlotQuantityBefore >= 0)
                                    {
                                        Log.Warning($"[RR][Sell] Listing failed: listed count stayed at {listedAfter} and attempted slot still holds itemId={slotItemId} hq={slotIsHq} qty={slotQuantity}.");
                                    }
                                    else
                                    {
                                        Log.Warning($"[RR][Sell] Listing result ambiguous: listed count stayed at {listedAfter} and attempted slot still holds itemId={slotItemId} hq={slotIsHq}, but pre-attempt quantity was unavailable.");
                                    }
                                }
                                else
                                {
                                    succeeded = true;
                                    Log.Information($"[RR][Sell] Listing confirmed by slot content change on container={_attemptedSellSlot.Container} slot={_attemptedSellSlot.Slot}.");
                                }
                            }
                            else
                            {
                                succeeded = true;
                                Log.Information($"[RR][Sell] Listing confirmed because attempted slot container={_attemptedSellSlot.Container} slot={_attemptedSellSlot.Slot} no longer contains the original stack.");
                            }
                        }
                        else
                        {
                            Log.Warning($"[RR][Sell] Listing result ambiguous: listed count stayed at {listedAfter} and no attempted slot snapshot exists.");
                        }

                        _processingListedItem = true;
                        _hasPendingSellSlot = false;
                        ClearRetainerContextMenuExpectation();

                        if (succeeded)
                        {
                            _newListingAttemptState = NewListingAttemptState.Succeeded;

                            if (Configuration.EnablePerRetainerCaps && _attemptedSellItemId != 0)
                            {
                                var sellKey = Configuration.MakeSellKey(_attemptedSellItemId, _attemptedSellItemIsHq);
                                var updated = _retainerSellCounts.TryGetValue(sellKey, out var existing)
                                    ? existing + 1
                                    : 1;
                                _retainerSellCounts[sellKey] = updated;
                                Log.Debug($"[RR][Sell] Incremented retainer cap counter for itemId={_attemptedSellItemId} hq={_attemptedSellItemIsHq}: {updated}.");
                            }

                            _soldThisRetainer++;
                            Log.Information($"[RR] New listings: {_soldThisRetainer}/{_sellCapacityThisRetainer}");
                        }
                        else
                        {
                            _newListingAttemptState = NewListingAttemptState.Failed;
                            RememberFailedAttemptedSlot();
                        }

                        _currentSellItemId = 0;
                        _currentSellItemIsHq = false;
                        _currentSellStackSize = 0;
                        ResetNewListingAttempt();

                        if (_quickListRun)
                        {
                            Log.Information($"[RR][QuickList] Completed success={succeeded}.");
                            StopRun();
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _slotIndexToOpen++;

                    if (_slotIndexToOpen >= _listedCountThisRetainer)
                    {
                        _needsExistingListingScan = false;
                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _runPhase = RunPhase.OpeningSellItem;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Selling pipeline (after repricing)

            case RunPhase.Sell_FindNextItemInInventory:
                {
                    if (!ShouldSellThisRetainer)
                    {
                        if (!ShouldSell)
                            Log.Information("[RR] Sell phase skipped (run mode = price-only).");
                        else
                            Log.Information($"[RR] Sell phase skipped for {DescribeCurrentRetainerForLog()}: retainer setting disables selling.");
                        TransitionToExitToRetainerList();
                        _lastActionUtc = now;
                        return;
                    }

                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0)
                    {
                        Log.Information("[RR] Sell skipped: retainer is full (20/20).");
                        TransitionToExitToRetainerList();
                        _lastActionUtc = now;
                        return;
                    }

                    if (_soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        Log.Information($"[RR] Sell complete: reached capacity {_soldThisRetainer}/{_sellCapacityThisRetainer}.");
                        TransitionToExitToRetainerList();
                        _lastActionUtc = now;
                        return;
                    }

                    if (_sellQueue.Count == 0)
                    {
                        Log.Debug("[RR] Sell skipped: SellList is empty.");
                        TransitionToExitToRetainerList();
                        _lastActionUtc = now;
                        return;
                    }

                    var normalizedRetainerName = Configuration.NormalizeRetainerName(_currentRetainerName);
                    var hasRetainerName = Configuration.EnablePerRetainerCaps && normalizedRetainerName.Length > 0;

                    foreach (var c in _sellQueue)
                    {
                        if (c.ItemId == 0)
                            continue;

                        var (threshold, desiredQuantity) = ResolveStackSizeForRetainer(c, hasRetainerName ? normalizedRetainerName : string.Empty);

                        if (hasRetainerName)
                        {
                            var requestedCap = Configuration.RetainerCapDefault;

                            if (c.RetainerCaps is { Count: > 0 } caps && caps.TryGetValue(normalizedRetainerName, out var storedCap))
                                requestedCap = storedCap;

                            var capValue = Math.Clamp(requestedCap, 0, Configuration.RetainerCapMax);
                            if (capValue <= 0)
                            {
                                Log.Verbose($"[RR][Sell] Retainer cap blocks itemId={c.ItemId} hq={c.IsHq} on {DescribeCurrentRetainerForLog()} (cap=0).");
                                continue;
                            }

                            var sellKey = Configuration.MakeSellKey(c.ItemId, c.IsHq);
                            var newCountSoFar = _retainerSellCounts.TryGetValue(sellKey, out var newCount)
                                ? newCount
                                : 0;
                            var existingCount = _retainerExistingSellCounts.TryGetValue(sellKey, out var already)
                                ? already
                                : 0;
                            var usedCount = existingCount + newCountSoFar;

                            if (usedCount >= capValue)
                            {
                                Log.Verbose($"[RR][Sell] Retainer cap reached for itemId={c.ItemId} hq={c.IsHq} on {DescribeCurrentRetainerForLog()}: {usedCount}/{capValue} (existing={existingCount}, new={newCountSoFar}).");
                                continue;
                            }
                        }

                        var inventoryLookup = FindItemInInventory(c.ItemId, c.IsHq, out var slotRef);
                        if (!inventoryLookup.FoundSellable)
                        {
                            if (inventoryLookup.Status == UiReader.InventoryLookupStatus.FoundOnlyUnsellable)
                            {
                                Log.Verbose($"[RR] Sell candidate skipped: itemId={c.ItemId} hq={c.IsHq} present but not sellable (spiritbondOrCollectability={inventoryLookup.FirstUnsellableSpiritbondOrCollectability})");
                            }
                            else
                            {
                                Log.Verbose($"[RR] Sell candidate not in inventory: itemId={c.ItemId} hq={c.IsHq}");
                            }

                            continue;
                        }

                        var totalCount = inventoryLookup.TotalCount;

                        if (totalCount < threshold)
                        {
                            Log.Verbose($"[RR] Sell candidate skipped: itemId={c.ItemId} hq={c.IsHq} count={totalCount} < threshold={threshold}");
                            continue;
                        }

                        _currentSellItemId = c.ItemId;
                        _currentSellItemIsHq = c.IsHq;
                        _currentSellStackSize = desiredQuantity;
                        _pendingSellSlot = slotRef;
                        _hasPendingSellSlot = true;

                        var listedCountBefore = GetCurrentExpectedListedCount();
                        if (retainerSellListVisible)
                        {
                            var listedSnapshot = ReadCurrentRetainerSellListCount();
                            if (listedSnapshot.HasValue)
                                listedCountBefore = listedSnapshot.Value;
                        }

                        var slotQuantityBefore = -1;
                        if (!TryReadInventorySlotState(slotRef, out _, out _, out slotQuantityBefore))
                            slotQuantityBefore = -1;

                        BeginNewListingAttempt(slotRef, c.ItemId, c.IsHq, desiredQuantity, listedCountBefore,
                            slotQuantityBefore, now);

                        Log.Debug($"[RR] Sell candidate: itemId={c.ItemId} hq={c.IsHq} count={totalCount} threshold={threshold} container={slotRef.Container} slot={slotRef.Slot} (cap {_soldThisRetainer}/{_sellCapacityThisRetainer})");

                        _runPhase = RunPhase.Sell_OpenRetainerSellFromInventory;
                        _lastActionUtc = now;
                        return;

                    }

                    Log.Information("[RR] Sell complete: no eligible SellList items remain for this retainer.");
                    TransitionToExitToRetainerList();
                    _lastActionUtc = now;
                    return;
                }

            case RunPhase.Sell_OpenRetainerSellFromInventory:
                {
                    if (!retainerSellListVisible) return;

                    if (_sellCapacityThisRetainer <= 0 || _soldThisRetainer >= _sellCapacityThisRetainer)
                    {
                        if (_quickListRun)
                        {
                            StopRun();
                            _lastActionUtc = now;
                            return;
                        }

                        TransitionToExitToRetainerList();
                        _lastActionUtc = now;
                        return;
                    }

                    if (!_hasPendingSellSlot)
                    {
                        Log.Warning("[RR] Sell_Open: no pending slot; returning to scan.");
                        if (_quickListRun)
                        {
                            StopRun();
                            _lastActionUtc = now;
                            return;
                        }

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    _processingListedItem = false;
                    ClearRetainerContextMenuExpectation();

                    if (_newListingAttemptState == NewListingAttemptState.None)
                    {
                        var listedCountBefore = GetCurrentExpectedListedCount();
                        var slotQuantityBefore = -1;
                        if (!TryReadInventorySlotState(_pendingSellSlot, out var slotItemId, out var slotIsHq, out slotQuantityBefore))
                        {
                            slotItemId = _currentSellItemId;
                            slotIsHq = _currentSellItemIsHq;
                            slotQuantityBefore = -1;
                        }

                        BeginNewListingAttempt(_pendingSellSlot, slotItemId, slotIsHq, _currentSellStackSize,
                            listedCountBefore, slotQuantityBefore, now);
                    }

                    var ok = TryOpenRetainerSellFromInventory(_pendingSellSlot);
                    if (!ok)
                    {
                        Log.Information($"[RR] Sell_Open: failed to open RetainerSell from inventory (itemId={_currentSellItemId}); skipping.");
                        if (_quickListRun)
                        {
                            StopRun();
                            _lastActionUtc = now;
                            return;
                        }

                        _newListingAttemptState = NewListingAttemptState.Failed;
                        RememberFailedAttemptedSlot();
                        _hasPendingSellSlot = false;
                        _pendingSellSlot = default;
                        _currentSellItemId = 0;
                        _currentSellItemIsHq = false;
                        _currentSellStackSize = 0;
                        ResetNewListingAttempt();

                        _runPhase = RunPhase.Sell_FindNextItemInInventory;
                        _lastActionUtc = now;
                        return;
                    }

                    Log.Debug($"[RR] Sell_Open: requested RetainerSell from inventory (itemId={_currentSellItemId}).");

                    _newListingAttemptState = NewListingAttemptState.PendingConfirm;

                    _hasPendingSellSlot = false;
                    _pendingSellSlot = default;

                    _runPhase = RunPhase.WaitingRetainerSell;
                    _lastActionUtc = now;
                    return;
                }

            #endregion

            #region Exit unwind

            case RunPhase.ExitToRetainerList:
                {
                    if (marketOpen || itemHistoryOpen)
                    {
                        Log.Debug("[RR] Exit: closing market windows.");
                        CloseMarketWindows();
                        _lastActionUtc = now;
                        return;
                    }

                    if (contextMenuVisible)
                    {
                        Log.Debug("[RR] Exit: dismissing ContextMenu.");
                        FireContextMenuDismiss();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellVisible)
                    {
                        Log.Debug("[RR] Exit: closing RetainerSell.");
                        CloseRetainerSellIfOpen();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerSellListVisible)
                    {
                        Log.Debug("[RR] Exit: closing RetainerSellList.");
                        CloseAddonIfOpen("RetainerSellList");
                        _lastActionUtc = now;
                        return;
                    }

                    if (_runOrigin == RunOrigin.AutoRetainerMenu && selectStringVisible)
                    {
                        if (!IsSelectStringReady())
                        {
                            _lastActionUtc = now;
                            return;
                        }

                        RunPendingSellPrune();
                        CompleteAutoRetainerRun(originRestored: true);
                        return;
                    }

                    if (selectStringVisible)
                    {
                        Log.Debug("[RR] Exit: closing SelectString.");
                        CloseAddonIfOpen("SelectString");
                        _lastActionUtc = now;
                        return;
                    }

                    if (talkVisible)
                    {
                        Log.Debug("[RR] Exit: advancing/closing Talk.");
                        TryAdvanceTalk();
                        _lastActionUtc = now;
                        return;
                    }

                    if (retainerListVisible)
                    {
                        RunPendingSellPrune();
                        Log.Debug("[RR] Exit: back on RetainerList; continuing.");
                        _runPhase = RunPhase.NeedOpen;
                        _lastActionUtc = now;
                        return;
                    }

                    _lastActionUtc = now;
                    return;
                }

            #endregion

            default:
                return;
        }
    }

    private void ArmRetainerContextMenuExpectation(DateTime now)
    {
        _awaitingRetainerContextMenu = true;
        _retainerContextMenuRequestedUtc = now;
        Log.Verbose("[RR][CTX] Awaiting RetainerSell context menu.");
    }

    private void ClearRetainerContextMenuExpectation()
    {
        _awaitingRetainerContextMenu = false;
        _retainerContextMenuRequestedUtc = DateTime.MinValue;
    }

    private bool HasRetainerContextMenuTimedOut(DateTime now)
    {
        if (!_awaitingRetainerContextMenu) return false;
        if (_retainerContextMenuRequestedUtc == DateTime.MinValue) return false;
        return (now - _retainerContextMenuRequestedUtc).TotalSeconds >= RetainerContextMenuTimeoutSeconds;
    }

    private void RecordExistingListingForCaps(string itemName, bool isHq)
    {
        if (!Configuration.EnablePerRetainerCaps)
            return;

        var trimmed = (itemName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return;

        var displayName = Configuration.BuildDisplayName(trimmed, isHq);
        if (!Configuration.TryGetSellEntryKeyByDisplayName(displayName, out var key))
            return;

        var updated = _retainerExistingSellCounts.TryGetValue(key, out var existing)
            ? existing + 1
            : 1;
        _retainerExistingSellCounts[key] = updated;
        Log.Verbose($"[RR][Caps] Existing listing count for '{displayName}' now {updated}.");
    }

    private (int Threshold, int DesiredQuantity) ResolveStackSizeForRetainer(SellCandidate candidate, string normalizedRetainerName)
    {
        var cap = candidate.StackSizeMax > 0 ? candidate.StackSizeMax : 99;
        var baseline = Math.Clamp(candidate.MinCountToSell, 1, cap);

        if (!Configuration.EnablePerRetainerCaps || string.IsNullOrEmpty(normalizedRetainerName) || candidate.RetainerStackSizes is not { Count: > 0 })
            return (baseline, 0);

        if (!candidate.RetainerStackSizes.TryGetValue(normalizedRetainerName, out var overrideValue))
            return (baseline, 0);

        if (overrideValue <= 0)
            return (baseline, 0);

        var desired = Math.Clamp(overrideValue, 1, cap);
        return (desired, desired);
    }

    #endregion
}
