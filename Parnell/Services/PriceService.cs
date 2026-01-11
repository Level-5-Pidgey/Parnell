using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Network.Structures;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace Parnell.Services;

public class PriceService
{
    private readonly Dictionary<uint, MarketData> _marketData = new();

    public PriceService()
    {
    }

    public void UpdateOfferings(IMarketBoardCurrentOfferings offerings)
    {
        if (offerings.ItemListings.Count == 0) return;

        var itemId = offerings.ItemListings[0].ItemId;
        if (!_marketData.TryGetValue(itemId, out var value))
        {
            value = new MarketData();
            _marketData[itemId] = value;
        }

        value.Offerings = offerings;
    }

    public void UpdateHistory(IMarketBoardHistory history)
    {
        if (history.HistoryListings.Count == 0) return;

        var itemId = history.ItemId;
        if (!_marketData.ContainsKey(itemId))
        {
            _marketData[itemId] = new MarketData();
        }
        _marketData[itemId].History = history;
    }

    public uint GetAppropriatePriceForItem(uint itemId, bool isHq)
    {
        if (!_marketData.TryGetValue(itemId, out var marketData))
        {
            return 0;
        }

        var itemData = Svc.Data.GetExcelSheet<Item>()?.GetRow(itemId);
        var npcSellPrice = itemData?.PriceLow ?? 0;

        uint cheapestListing = 0;
        if (marketData.Offerings != null)
        {
            var listings = marketData.Offerings.ItemListings.Where(l => l.IsHq == isHq).ToList();
            cheapestListing = listings.Min(x => x.PricePerUnit);
        }

        uint averageHistoryPrice = 0;
        // Only go off of history if there's no listings for this item.
        if (cheapestListing == 0)
        {
            
            if (marketData.History != null)
            {
                var history = marketData.History.HistoryListings.Where(h => h.IsHq == isHq).ToList();
                if (history.Any())
                {
                    averageHistoryPrice = (uint)history
                                                .GetRange(0, 5)
                                                .Average(h => h.SalePrice);
                }
            }
        }

        var potentialPrices = new List<uint> { npcSellPrice };
        if (cheapestListing > 0)
        {
            potentialPrices.Add(cheapestListing);
        }
        if (averageHistoryPrice > 0)
        {
            potentialPrices.Add(averageHistoryPrice);
        }

        return potentialPrices.Max();
    }
}
