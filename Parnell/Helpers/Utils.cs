using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Parnell.Helpers;

public static class Utils
{
    public static bool IsOccupied()
    {
        return Parnell.Condition[ConditionFlag.Occupied] 
            || Parnell.Condition[ConditionFlag.OccupiedInCutSceneEvent] 
            || Parnell.Condition[ConditionFlag.OccupiedInEvent] 
            || Parnell.Condition[ConditionFlag.OccupiedInQuestEvent] 
            || Parnell.Condition[ConditionFlag.OccupiedSummoningBell] 
            || Parnell.Condition[ConditionFlag.Fishing] 
            || Parnell.Condition[ConditionFlag.Crafting] 
            || Parnell.Condition[ConditionFlag.BetweenAreas];
    }

    public static unsafe void InteractWith(IGameObject? obj)
    {
        if (obj == null) return;
        var target = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        if (target == null) return;
        TargetSystem.Instance()->InteractWithObject(target, false);
    }
    
    public static IGameObject? GetNearestGameObject(this IObjectTable objectTable, Func<IGameObject, bool> predicate)
    {
        IGameObject? closest = null;
        var closestDist = float.MaxValue;

        var player = Player.Object;
        if (player is null) return null;
        
        foreach (var obj in objectTable)
        {
            if (predicate(obj))
            {
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = obj;
                }
            }
        }
        
        return closest;
    }
    
    public static unsafe void SendClick(IntPtr arg1, EventType arg2, uint arg3, void* target, IntPtr arg5)
    {
        var listener = (AtkEventListener*)arg1;

        var arg4 = Marshal.AllocHGlobal(0x40);
        for (var i = 0; i < 0x40; i++)
            Marshal.WriteByte(arg4, i, 0);

        Marshal.WriteIntPtr(arg4, 0x8, new IntPtr(target));
        Marshal.WriteIntPtr(arg4, 0x10, arg1);

        if (arg5 == IntPtr.Zero)
        {
            arg5 = Marshal.AllocHGlobal(0x40);
            for (var i = 0; i < 0x40; i++)
                Marshal.WriteByte(arg5, i, 0);
        }

        listener->ReceiveEvent((AtkEventType)arg2, (int)arg3, (AtkEvent*)arg4, (AtkEventData*)arg5);

        Marshal.FreeHGlobal(arg4);
        Marshal.FreeHGlobal(arg5);
    }
}
