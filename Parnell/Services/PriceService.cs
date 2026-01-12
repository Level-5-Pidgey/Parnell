using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Network.Structures;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace Parnell.Services;

public class PriceService
{
    private readonly Dictionary<uint, MarketData> marketData = new();

    public PriceService()
    {
    }

    public void UpdateOfferings(uint itemId, IReadOnlyCollection<IMarketBoardItemListing> listings)
    {
        if (listings.Count == 0) return;

        if (!marketData.TryGetValue(itemId, out var value))
        {
            value = new MarketData();
            marketData[itemId] = value;
        }

        var existingListings = value.Listings.ToDictionary(l => l.ListingId);
        foreach (var newListing in listings)
        {
            existingListings[newListing.ListingId] = newListing;
        }
        value.Listings = existingListings.Values.ToList();
    }

    public void UpdateHistory(uint itemId, IReadOnlyCollection<IMarketBoardHistoryListing> history)
    {
        if (history.Count == 0) return;

        if (!marketData.TryGetValue(itemId, out var value))
        {
            value = new MarketData();
            marketData[itemId] = value;
        }

        var existingHistory = new HashSet<IMarketBoardHistoryListing>(value.History, new MarketBoardHistoryListingComparer());
        foreach(var newEntry in history)
        {
            existingHistory.Add(newEntry);
        }
        value.History = existingHistory.ToList();
    }

    private class MarketBoardHistoryListingComparer : IEqualityComparer<IMarketBoardHistoryListing>
    {
        public bool Equals(IMarketBoardHistoryListing? x, IMarketBoardHistoryListing? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.PurchaseTime == y.PurchaseTime &&
                   x.Quantity == y.Quantity &&
                   x.SalePrice == y.SalePrice &&
                   x.IsHq == y.IsHq;
        }

        public int GetHashCode(IMarketBoardHistoryListing obj)
        {
            return HashCode.Combine(obj.PurchaseTime, obj.Quantity, obj.SalePrice, obj.IsHq);
        }
    }

    public bool HasDataOnItem(uint itemId) => marketData.ContainsKey(itemId);

    public uint GetAppropriatePriceForItem(uint itemId, bool isHq)
    {
        if (!marketData.TryGetValue(itemId, out var data))
        {
            return 0;
        }

        var itemData = Svc.Data.GetExcelSheet<Item>()?.GetRow(itemId);
        var npcSellPrice = itemData?.PriceLow ?? 0;

        uint cheapestListing = 0;
        var listings = data.Listings.Where(l => l.IsHq == isHq).OrderBy(l => l.PricePerUnit).ToList();
        if (listings.Count > 0)
        {
            cheapestListing = listings[0].PricePerUnit;
        }

        if (listings.Count > 1)
        {
            var secondCheapest = listings[1].PricePerUnit;
            if (cheapestListing < secondCheapest * 0.66 && cheapestListing > npcSellPrice)
            {
                Svc.Log.Info($"Extreme undercut detected on {itemData?.Name ?? "Unknown Item"}. Lowest price: {cheapestListing}, 2nd lowest price: {secondCheapest}. Using 2nd lowest price to determine new price.");
                cheapestListing = secondCheapest;
            }
        }

        uint averageHistoryPrice = 0;
        // Only go off of history if there's no listings for this item.
        if (cheapestListing == 0)
        {
            var history = data.History.Where(h => h.IsHq == isHq).ToList();
            if (history.Count != 0)
            {
                averageHistoryPrice = (uint) Math.Round(history
                                                        .Take(5)
                                                        .Average(h => h.SalePrice), MidpointRounding.AwayFromZero);
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

        var potentialMax = potentialPrices.Max();
        if (potentialMax <= 1)
        {
            return 0;
        }
        
        return potentialMax-1;
    }
}
