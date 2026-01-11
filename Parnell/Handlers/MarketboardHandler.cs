using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Parnell.Helpers;

namespace Parnell.Handlers;

public class MarketboardHandler : IDisposable
{
    public MarketboardHandler()
    {
        Parnell.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
        Parnell.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetupHistory);
        Parnell.MarketBoard.OfferingsReceived += MarketboardOfferingsReceived;
        Parnell.MarketBoard.HistoryReceived += MarketboardHistoryReceived;
    }

    private unsafe bool IsOwnRetainer(ulong retainerId)
    {
        var retainerManager = RetainerManager.Instance();
        for (uint i = 0; i < retainerManager->GetRetainerCount(); ++i)
        {
            if (retainerId == retainerManager->GetRetainerBySortedIndex(i)->RetainerId)
            {
                return true;
            }
        }

        return false;
    }

    private unsafe void ItemSearchResultPostSetupHistory(AddonEvent type, AddonArgs args)
    {
        // Only action this event whilst standing at a retainer bell.
        if (!Parnell.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            return;
        }
        
        IntPtr addon = args.Addon;
        if (addon == IntPtr.Zero)
        {
            return;
        }

        //TODO remove me if necessary.
        return;
        var searchResultAddon = (AddonItemSearchResult*)addon;
        // Show history window
        var history = searchResultAddon->History->AtkComponentBase.OwnerNode;
        Utils.SendClick(addon, EventType.CHANGE, 23, history, IntPtr.Zero);
    }
    
    private unsafe void ItemSearchResultPostSetup(AddonEvent type, AddonArgs args)
    {
        // Might need this listener to check if this is a new request or not.
    }
    
    public void Dispose()
    {
        Parnell.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
        Parnell.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetupHistory);
        Parnell.MarketBoard.OfferingsReceived -= MarketboardOfferingsReceived;
        Parnell.MarketBoard.HistoryReceived -= MarketboardHistoryReceived;
        GC.SuppressFinalize(this);
    }

    private void MarketboardHistoryReceived(IMarketBoardHistory history)
    {
        Parnell.PriceService.UpdateHistory(history);
    }

    private void MarketboardOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
    {
        Parnell.PriceService.UpdateOfferings(currentOfferings);
    }
}
