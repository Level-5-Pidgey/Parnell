using System.Collections.Generic;
using System.Linq;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Parnell.Scheduler;

public static unsafe class RetainerListHandlers
{
    
    public static List<Retainer> Retainers { get; set; } = [];
    public static bool? SelectRetainer(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            InternalLog.Error("Retainer name cannot be empty.");
            return false;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) ||
            !GenericHelpers.IsAddonReady(retainerList))
        {
            return false;
        }

        var list = new AddonMaster.RetainerList(retainerList);
        var target = list.Retainers.FirstOrDefault(x => x.Name == name);
        if (target is null)
        {
            InternalLog.Error($"Could not find retainer: {name}");
            return false;
        }

        target.Select();
        return true;

    }

    public static bool? MarkRetainerAsDone(string name)
    {
        var target = Retainers.FirstOrDefault(x => x.Name == name);
        if (target is null)
        {
            Svc.Log.Error($"Couldn't find retainer: {name}");
            return false;
        }

        Retainers.Remove(target);
        return true;
    }
}

public class Retainer(RetainerManager.Retainer retainer)
{
    public bool Available { get; set; } = retainer.Available;
    public uint Gil { get; set; } = retainer.Gil;
    public byte MarketItemCount { get; set; } = retainer.MarketItemCount;
    public string Name { get; set; } = retainer.NameString;
    public uint MarketExpire { get; set; } = retainer.MarketExpire;
}
