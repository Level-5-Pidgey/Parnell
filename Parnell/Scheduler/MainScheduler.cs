using System.Linq;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Parnell.Helpers;

namespace Parnell.Scheduler
{
    public unsafe class MainScheduler
    {
        public static bool Enabled { get; set; }

        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            if (Parnell.TaskManager.IsBusy)
            {
                return;
            }

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && addon->IsVisible)
            {
                ProcessRetainers();
            }
            else
            {
                if (!Utils.IsOccupied())
                {
                    if (EzThrottler.Check("InteractWithBellDelay"))
                    {
                        InteractWithBell.Enqueue(ProcessRetainers);
                    }
                }
                else
                {
                    EzThrottler.Throttle("InteractWithBellDelay", 2500, true);
                }
            }
        }

        private static void ProcessRetainers()
        {
            if (RetainerListHandlers.Retainers.Any())
            {
                if (!EzThrottler.Throttle(nameof(RetainerListHandlers.SelectRetainer), 2000))
                {
                    return;
                }
                
                var retainer = RetainerListHandlers.Retainers.First();
                
                Parnell.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainer(retainer.Name));
                Parnell.TaskManager.EnqueueDelay(200);
                Parnell.TaskManager.Enqueue(RetainerMarketboardHandler.EnqueueRetainerSteps);
                Parnell.TaskManager.EnqueueDelay(200);
                
                RetainerListHandlers.Retainers.Remove(retainer);
            }
            else
            {
                Parnell.TaskManager.Enqueue(() =>
                {
                    Enabled = false;
                    RetainerListHandlers.Retainers.Clear();

                    return true;
                });
            }
        }
    }
}
