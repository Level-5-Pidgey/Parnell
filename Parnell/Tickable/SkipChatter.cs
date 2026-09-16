using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Parnell.Scheduler;

namespace Parnell.Tickable;

internal unsafe class SkipChatter
{
    private readonly TaskManager _taskManager;
    private readonly MainScheduler _mainScheduler;

    public SkipChatter(TaskManager taskManager, MainScheduler mainScheduler)
    {
        _taskManager = taskManager;
        _mainScheduler = mainScheduler;
    }
    
    public void Tick()
    {
        if (_taskManager.IsBusy || (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && (_mainScheduler.Enabled || _taskManager.IsBusy)))
        {
            if(GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible)
            {
                new AddonMaster.Talk((nint)addon).Click();
            }
        }
    }
}
