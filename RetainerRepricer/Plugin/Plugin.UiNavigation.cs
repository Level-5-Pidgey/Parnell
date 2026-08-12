using System;
using System.Linq;

using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace RetainerRepricer;

/// <summary>
/// Centralizes helper methods for checking addon visibility and firing Dalamud callbacks.
/// </summary>
public unsafe sealed partial class Plugin
{
    private bool IsAddonVisible(string name)
    {
        var a = GameGui.GetAddonByName(name, 1);
        if (a.IsNull) return false;

        var u = (AtkUnitBase*)a.Address;
        return u != null && u->IsVisible;
    }

    private bool IsAddonOpen(string name)
        => !GameGui.GetAddonByName(name, 1).IsNull;

    private void CloseAddonIfOpen(string name)
    {
        var a = GameGui.GetAddonByName(name, 1);
        if (a.IsNull) return;

        var u = (AtkUnitBase*)a.Address;
        if (u == null || !u->IsVisible) return;

        Callback.Fire(u, updateState: true, -1);
    }

    private void CloseMarketWindows()
    {
        CloseAddonIfOpen("ItemHistory");
        CloseAddonIfOpen("ItemSearchResult");
    }

    private bool MarketWindowsStillOpen()
        => IsAddonOpen("ItemSearchResult") || IsAddonOpen("ItemHistory");

    private void CloseRetainerSellIfOpen()
    {
        var sellAddon = GameGui.GetAddonByName("RetainerSell", 1);
        if (!sellAddon.IsNull)
        {
            new AddonMaster.RetainerSell(sellAddon.Address).Cancel();
            return;
        }

        CloseAddonIfOpen("RetainerSell");
    }

    private bool TryClickRetainerListEntry(int index)
    {
        var addon = GameGui.GetAddonByName("RetainerList", 1);
        if (addon.IsNull) return false;

        try
        {
            var rl = new AddonMaster.RetainerList(addon.Address);
            var retainers = rl.Retainers;

            if (index < 0 || index >= retainers.Length) return false;

            var ok = retainers[index].Select();
            Log.Debug(ok
                ? $"[RL] Select retainer index={index}"
                : $"[RL] Retainer entry inactive index={index}");

            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RL] RetainerList select failed.");
            return false;
        }
    }

    private bool TrySelectSellItems()
    {
        var addon = GameGui.GetAddonByName("SelectString", 1);
        if (addon.IsNull) return false;

        // Addon row 2380 is "Sell items in your inventory on the market.".
        // Resolve it through game data so the text matches the current client language.
        const uint sellItemsAddonRowId = 2380;

        try
        {
            var ss = new AddonMaster.SelectString(addon.Address);
            var row = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(sellItemsAddonRowId);
            if (row is null)
            {
                Log.Warning($"[SS] Addon row {sellItemsAddonRowId} is unavailable; cannot select Sell items.");
                return false;
            }

            var localizedText = row.Value.Text.ToDalamudString().GetText();
            var matches = ss.Entries
                .Where(entry => string.Equals(entry.Text, localizedText, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length != 1)
            {
                var entries = string.Join(", ", ss.Entries.Select(entry => $"{entry.Index}='{entry.Text}'"));
                Log.Warning($"[SS] Expected one Sell items entry but found {matches.Length}; entries: {entries}");
                return false;
            }

            var match = matches[0];
            match.Select();
            Log.Debug($"[SS] Select Sell items index={match.Index}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SS] SelectString select failed.");
            return false;
        }
    }

    private bool TryAdvanceTalk()
    {
        var addon = GameGui.GetAddonByName("Talk", 1);
        if (addon.IsNull)
        {
            Log.Debug("[Talk] Addon not open.");
            return false;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
        {
            Log.Debug("[Talk] Addon not visible.");
            return false;
        }

        new AddonMaster.Talk(addon.Address).Click();
        Log.Debug("[Talk] Click advance");
        return true;
    }

    private bool FireRetainerSellListOpenItem(int slotIndex0)
    {
        var addon = GameGui.GetAddonByName("RetainerSellList", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, slotIndex0, 1);
        Log.Verbose($"[RSL] Open item callback (0, {slotIndex0}, 1)");
        return true;
    }

    private bool FireContextMenuDismiss()
    {
        var addon = GameGui.GetAddonByName("ContextMenu", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0, 0);
        Callback.Fire(unit, updateState: true, 1, 0);

        Log.Verbose("[CTX] Dismiss callbacks fired");
        return true;
    }

    private bool TrySelectRetainerContextMenuAdjustPrice()
    {
        var addon = GameGui.GetAddonByName("ContextMenu", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        try
        {
            var ctx = new AddonMaster.ContextMenu(addon.Address);
            var entries = ctx.Entries;
            if (entries.Length > 0)
            {
                var entry = entries[0];
                if (entry.Select())
                {
                    Log.Verbose("[CTX] Selected entry 0 (Adjust price)");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "[CTX] Failed selecting entry 0; fallback to callback.");
        }

        Callback.Fire(unit, updateState: true, 0, 0);
        Log.Verbose("[CTX] Fallback context menu callback (0,0) fired");
        return true;
    }

    private bool FireItemSearchResultOpenFilter()
    {
        var addon = GameGui.GetAddonByName("ItemSearchResult", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 1);
        Log.Verbose("[ISR] Open filter (callback 1)");
        return true;
    }

    private bool FireItemSearchFilterToggleHq()
    {
        var addon = GameGui.GetAddonByName("ItemSearchFilter", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 1, 1);
        Log.Verbose("[ISF] Toggle HQ (callback 1,1)");
        return true;
    }

    private bool FireItemSearchFilterAccept()
    {
        var addon = GameGui.GetAddonByName("ItemSearchFilter", 1);
        if (addon.IsNull) return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return false;

        Callback.Fire(unit, updateState: true, 0);
        Log.Verbose("[ISF] Accept filter (callback 0)");
        return true;
    }
}
