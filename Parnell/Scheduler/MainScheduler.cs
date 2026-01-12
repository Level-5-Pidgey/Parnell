using System;
using System.Linq;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Parnell.Helpers;

namespace Parnell.Scheduler;

public unsafe class MainScheduler
{
    public static bool Enabled { get; set; }
    private static string? singleRetainerName;
    private static Action? singleRetainerCallback;

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

    public static void EnqueueSingleRetainer(string name, Action? callback = null)
    {
        if (Enabled)
        {
            Svc.Log.Info("Scheduler is already running, ignoring single retainer request.");
            callback?.Invoke();
            return;
        }
        
        Svc.Log.Info($"Enqueuing single retainer {name} for processing.");
        singleRetainerName = name;
        singleRetainerCallback = callback;
        Enabled = true;
    }
    
    private static void ProcessSingleRetainer(string name)
    {
        Parnell.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainer(name));
        Parnell.TaskManager.EnqueueDelay(200);
        Parnell.TaskManager.Enqueue(RetainerMarketboardHandler.EnqueueRetainerSteps);
    }

    private static void ProcessRetainers()
    {
        if (singleRetainerName != null)
        {
            ProcessSingleRetainer(singleRetainerName);
            singleRetainerName = null;
            Parnell.TaskManager.Enqueue(() =>
            {
                singleRetainerCallback?.Invoke();
                singleRetainerCallback = null;
                Enabled = false;
                return true;
            });
        }
        else
        {
            ProcessNextRetainerInQueue();
        }
    }

    private static void ProcessNextRetainerInQueue()
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
