using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Parnell.Helpers;

namespace Parnell.Tickable;

public unsafe class MainScheduler
{
    public static bool Enabled { get; set; }

    public static void Tick(Parnell plugin)
    {
        if (!Enabled)
        {
            return;
        }

        // Check if we already have the retainer list open
        if (AddonHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && addon->IsVisible)
        {
            
        }
        // If not, check if the player is next to a retainer bell
        else
        {
            if (!Utils.IsOccupied())
            {
                if (EzThrottler.Check("InteractWithBellDelay"))
                {
                    
                }
            }
            else
            {
                EzThrottler.Throttle("InteractWithBellDelay", 2500, true);
            }
        }
    }
}
