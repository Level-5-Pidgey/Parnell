using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private static readonly TimeSpan AutoRetainerRunTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutoRetainerCleanupTimeout = TimeSpan.FromSeconds(30);

    internal bool IsAutoRetainerAvailable => _autoRetainerIntegration.IsAvailable;

    internal bool ShouldRequestAutoRetainerPostprocess(string retainerName)
    {
        if (!Configuration.PluginEnabled || !Configuration.EnableAutoRetainerIntegration || IsRunning)
            return false;

        if (string.IsNullOrWhiteSpace(retainerName))
        {
            Log.Warning("[RR][AutoRetainer] Ignoring an empty retainer name.");
            return false;
        }

        var behavior = Configuration.GetRetainerBehavior(retainerName);
        if (!behavior.Enabled)
            return false;

        if (!TryGetRetainerMarketItemCount(retainerName, out var marketItemCount))
        {
            Log.Warning("[RR][AutoRetainer] Could not find market data for retainer '{Retainer}'.", retainerName);
            return false;
        }

        var hasRepriceWork = behavior.AllowReprice && marketItemCount > 0;
        var hasSellWork = behavior.AllowSell && marketItemCount < 20 && HasEligibleSellInventory(retainerName);

        Log.Debug(
            "[RR][AutoRetainer] Eligibility for '{Retainer}': listed={Listed}, reprice={Reprice}, sell={Sell}.",
            retainerName,
            marketItemCount,
            hasRepriceWork,
            hasSellWork);

        return hasRepriceWork || hasSellWork;
    }

    internal bool StartAutoRetainerRun(string retainerName)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(retainerName))
        {
            Log.Warning("[RR][AutoRetainer] Assigned run could not start because Retainer Repricer is busy or the name is invalid.");
            return false;
        }

        if (!Configuration.PluginEnabled || !Configuration.EnableAutoRetainerIntegration)
        {
            Log.Information("[RR][AutoRetainer] Assigned run skipped because the integration is disabled.");
            return false;
        }

        var behavior = Configuration.GetRetainerBehavior(retainerName);
        if (!behavior.Enabled)
        {
            Log.Information("[RR][AutoRetainer] Assigned run skipped because the retainer is disabled.");
            return false;
        }

        ResetRunState();
        ValidateRepricingCacheCharacter();
        _runMode = RunMode.PriceAndSell;
        _runOrigin = RunOrigin.AutoRetainerMenu;
        _autoRetainerRunStartedUtc = DateTime.UtcNow;
        _autoRetainerCleanupStartedUtc = DateTime.MinValue;

        _retainerRowOrder.Add(new RetainerRowEntry
        {
            RowIndex = -1,
            Name = retainerName.Trim(),
            AllowSell = behavior.AllowSell,
            AllowReprice = behavior.AllowReprice,
        });

        IsRunning = true;
        _runPhase = RunPhase.NeedOpen;
        _lastActionUtc = DateTime.MinValue;

        Log.Information("[RR][AutoRetainer] Started automatic run for '{Retainer}'.", retainerName);
        return true;
    }

    internal void OnAutoRetainerCharacterPostprocessStep()
    {
        ClearRepricingCache("AutoRetainer finished the character");
    }

    private bool TryGetRetainerMarketItemCount(string retainerName, out int marketItemCount)
    {
        marketItemCount = 0;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return false;

        foreach (var retainer in manager->Retainers)
        {
            if (retainer.RetainerId == 0)
                continue;

            if (!string.Equals(retainer.NameString, retainerName, StringComparison.OrdinalIgnoreCase))
                continue;

            marketItemCount = Math.Clamp(retainer.MarketItemCount, (byte)0, (byte)20);
            return true;
        }

        return false;
    }

    private bool HasEligibleSellInventory(string retainerName)
    {
        var ignoredSlots = new HashSet<long>();
        foreach (var entry in Configuration.GetSellListOrdered())
        {
            if (entry.ItemId == 0)
                continue;

            var stackCap = GetStackSizeCap(entry.ItemId);
            var threshold = Math.Clamp(entry.MinCountToSell, 1, stackCap);

            if (Configuration.EnablePerRetainerCaps)
            {
                if (entry.GetRetainerCapOrDefault(retainerName) <= 0)
                    continue;

                var stackSize = entry.GetRetainerStackSize(retainerName);
                if (stackSize > 0)
                    threshold = Math.Clamp(stackSize, 1, stackCap);
            }

            var inventory = _uiReader.FindItemInInventory(entry.ItemId, entry.IsHq, ignoredSlots);
            if (inventory.FoundSellable && inventory.TotalCount >= threshold)
                return true;
        }

        return false;
    }

    private void RequestAutoRetainerCleanup(string reason)
    {
        if (_runOrigin != RunOrigin.AutoRetainerMenu || !IsRunning)
            return;

        if (_autoRetainerCleanupStartedUtc == DateTime.MinValue)
        {
            Log.Warning("[RR][AutoRetainer] Cancelling automatic run: {Reason}", reason);
            _autoRetainerCleanupStartedUtc = DateTime.UtcNow;
        }

        TransitionToExitToRetainerList();
        _lastActionUtc = DateTime.MinValue;
    }

    private void CompleteAutoRetainerRun(bool originRestored)
    {
        if (_runOrigin != RunOrigin.AutoRetainerMenu)
            return;

        var retainerName = _currentRetainerName;
        if (originRestored)
            Log.Information("[RR][AutoRetainer] Finished '{Retainer}' and restored the retainer menu.", retainerName);
        else
            Log.Warning("[RR][AutoRetainer] Releasing '{Retainer}' without confirming menu restoration.", retainerName);

        _autoRetainerIntegration.Complete();
        StopRunImmediately("[RR][AutoRetainer] Automatic run stopped.");
    }

    private void BestEffortCleanupAutoRetainerUi()
    {
        if (_runOrigin != RunOrigin.AutoRetainerMenu)
            return;

        CloseMarketWindows();
        if (IsAddonVisible("ContextMenu"))
            FireContextMenuDismiss();
        CloseRetainerSellIfOpen();
        CloseAddonIfOpen("RetainerSellList");
    }
}
