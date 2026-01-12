using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Parnell.Handlers;

public class MarketboardHandler : IDisposable
{
    public MarketboardHandler()
    {
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

    
    public void Dispose()
    {
        Parnell.MarketBoard.OfferingsReceived -= MarketboardOfferingsReceived;
        Parnell.MarketBoard.HistoryReceived -= MarketboardHistoryReceived;
        GC.SuppressFinalize(this);
    }

    private void MarketboardHistoryReceived(IMarketBoardHistory history)
    {
        var listings = history.HistoryListings;
        if (listings.Count == 0)
        {
            return;
        }

        Parnell.PriceService.UpdateHistory(history.ItemId, listings);
    }

    private void MarketboardOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
    {
        var listings = currentOfferings.ItemListings;
        if (listings.Count == 0)
        {
            return;
        }

        var itemId = listings[0]?.ItemId;
        if (!itemId.HasValue)
        {
            return;
        }

        Parnell.PriceService.UpdateOfferings(itemId.Value, listings
                                                           .Where(x => !IsOwnRetainer(x.RetainerId))
                                                           .ToArray()
        );
    }
}
