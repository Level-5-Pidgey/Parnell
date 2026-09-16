using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Parnell.Services;

namespace Parnell.Handlers;

public class MarketboardHandler : IDisposable
{
    private readonly PriceService priceService;
    private readonly IMarketBoard marketBoard;

    public MarketboardHandler(PriceService priceService, IMarketBoard marketBoard)
    {
        this.priceService = priceService;
        this.marketBoard = marketBoard;
        this.marketBoard.OfferingsReceived += MarketboardOfferingsReceived;
        this.marketBoard.HistoryReceived += MarketboardHistoryReceived;
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
        marketBoard.OfferingsReceived -= MarketboardOfferingsReceived;
        marketBoard.HistoryReceived -= MarketboardHistoryReceived;
        GC.SuppressFinalize(this);
    }

    private void MarketboardHistoryReceived(IMarketBoardHistory history)
    {
        var listings = history.HistoryListings;
        if (listings.Count == 0)
        {
            return;
        }

        priceService.UpdateHistory(history.ItemId, listings);
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

        priceService.UpdateOfferings(itemId.Value, listings
                                                           .Where(x => !IsOwnRetainer(x.RetainerId))
                                                           .ToArray()
        );
    }
}
