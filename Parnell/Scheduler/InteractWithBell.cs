using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.DalamudServices.Legacy;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Parnell.Helpers;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace Parnell.Scheduler;

public class InteractWithBell
{
    private readonly TaskManager taskManager;

    public InteractWithBell(TaskManager taskManager)
    {
        this.taskManager = taskManager;
    }
    
    public void Enqueue(bool interact = true)
    {
        Enqueue(null, interact);
    }

    public unsafe void Enqueue(Action? callback, bool interact = true)
    {
        taskManager.Enqueue(TargetReachableRetainerBell);

        if (!interact)
        {
            return;
        }
        
        taskManager.Enqueue(() =>
        {
            var targetedBell = Svc.Targets.Target;
            if (targetedBell is null)
            {
                return false;
            }
            
            if (!InValidInteractionDistance(targetedBell))
            {
                return false;
            }
            
            TargetSystem.Instance()->InteractWithObject((GameObject*)targetedBell.Address, false);
            InternalLog.Debug($"Interacted with {targetedBell}");
            return true;
        });

        if (callback != null)
        {
            taskManager.EnqueueDelay(100);
            taskManager.Enqueue(callback.Invoke);
        }
    }

    private bool TargetReachableRetainerBell()
    {
        var bell = Svc.Objects.GetNearestGameObject(x =>
                                                        x.ObjectKind is ObjectKind.HousingEventObject or ObjectKind.EventObj &&
                                                        x.Name.ToString().EqualsIgnoreCaseAny(Lang.BellName));
        if (bell is null)
        {
            InternalLog.Error("Could not find nearby retainer bell");
            return false;
        }

        if (Utils.IsOccupied())
        {
            InternalLog.Debug("Character is occupied.");
            return false;
        }
        
        if (!InValidInteractionDistance(bell))
        {
            return false;
        }

        if (!AddonHelpers.GenericThrottle)
        {
            return false;
        }

        Svc.Targets.SetTarget(bell);
        return true;
    }

    private static bool InValidInteractionDistance(IGameObject target)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player is null)
        {
            InternalLog.Error("Could not find player");
            return false;
        }

        var distanceToTarget = Vector3.Distance(player.Position, target.Position);
        var interactionDistance = GetInteractionDistance(target);
        if (distanceToTarget > interactionDistance || !target.IsTargetable)
        {
            InternalLog.Warning(
                $"Unable to interact with target, distance is {distanceToTarget}, needs to be less than {interactionDistance}.");
            return false;
        }

        return true;
    }
    
    private static float GetInteractionDistance(IGameObject bell)
    {
        if (Inns.List.Contains(Svc.ClientState.TerritoryType))
        {
            return 4.75f;
        }

        return bell.ObjectKind switch
        {
            ObjectKind.Housing => 6.5f,
            _ => 4.6f
        };
    }
}
