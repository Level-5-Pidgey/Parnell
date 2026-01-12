using System.Collections.Generic;
using Dalamud.Game.Network.Structures;

namespace Parnell.Services;

public class MarketData
{
    public List<IMarketBoardItemListing> Listings { get; set; } = new();
    public List<IMarketBoardHistoryListing> History { get; set; } = new();
}
