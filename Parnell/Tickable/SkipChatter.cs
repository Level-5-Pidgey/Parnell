using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Parnell.Scheduler;

namespace Parnell.Tickable;

internal static unsafe class SkipChatter
{
    public static void Tick()
    {
        if (Parnell.TaskManager.IsBusy || (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && (MainScheduler.Enabled || Parnell.TaskManager.IsBusy)))
        {
            if(GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible)
            {
                new AddonMaster.Talk((nint)addon).Click();
            }
        }
    }
}
