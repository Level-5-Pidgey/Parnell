using System.Collections.Generic;
using Dalamud.Game.Network.Structures;

namespace Parnell.Services;

public class MarketData
{
    public IMarketBoardCurrentOfferings? Offerings { get; set; }
    public IMarketBoardHistory? History { get; set; }
}
