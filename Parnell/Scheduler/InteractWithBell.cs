using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.DalamudServices;
using ECommons.DalamudServices.Legacy;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Parnell.Helpers;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace Parnell.Scheduler;

public static class InteractWithBell
{
    public static void Enqueue(bool interact = true)
    {
        Enqueue(null, interact);
    }

    public static unsafe void Enqueue(Action? callback, bool interact = true)
    {
        Parnell.TaskManager.Enqueue(TargetReachableRetainerBell);

        if (!interact)
        {
            return;
        }
        
        Parnell.TaskManager.Enqueue(() =>
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
            Parnell.TaskManager.EnqueueDelay(100);
            Parnell.TaskManager.Enqueue(callback);
        }
    }

    private static bool TargetReachableRetainerBell()
    {
        var bell = Svc.Objects.GetNearestGameObject(x =>
                                                        x.ObjectKind is ObjectKind.Housing or ObjectKind.EventObj &&
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
