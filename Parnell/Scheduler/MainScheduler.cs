using System;
using System.Collections.Generic;
using System.Linq;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Parnell.Helpers;
using Parnell.Services;

namespace Parnell.Scheduler;

public unsafe class MainScheduler
{
    public bool Enabled { get; set; }
    private string? singleRetainerName;
    private Action? singleRetainerCallback;
    
    public List<Retainer> Retainers { get; set; } = [];
    
    private readonly TaskManager taskManager;
    private readonly PriceService priceService;
    private readonly RetainerListHandlers retainerListHandlers;
    private readonly RetainerMarketboardHandler retainerMarketboardHandler;
    private readonly InteractWithBell interactWithBell;

    public MainScheduler(TaskManager taskManager, PriceService priceService, RetainerListHandlers retainerListHandlers, RetainerMarketboardHandler retainerMarketboardHandler, InteractWithBell interactWithBell)
    {
        this.taskManager = taskManager;
        this.priceService = priceService;
        this.retainerListHandlers = retainerListHandlers;
        this.retainerMarketboardHandler = retainerMarketboardHandler;
        this.interactWithBell = interactWithBell;
    }

    public void Tick()
    {
        if (!Enabled)
        {
            return;
        }

        if (taskManager.IsBusy)
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
                    interactWithBell.Enqueue(ProcessRetainers);
                }
            }
            else
            {
                EzThrottler.Throttle("InteractWithBellDelay", 2500, true);
            }
        }
    }

    public void EnqueueSingleRetainer(string name, Action? callback = null)
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
    
    private void ProcessSingleRetainer(string name)
    {
        taskManager.Enqueue(() => retainerListHandlers.SelectRetainer(name));
        taskManager.EnqueueDelay(200);
        taskManager.Enqueue(retainerMarketboardHandler.EnqueueRetainerSteps);
    }

    private void ProcessRetainers()
    {
        if (singleRetainerName != null)
        {
            ProcessSingleRetainer(singleRetainerName);
            singleRetainerName = null;
            taskManager.Enqueue(() =>
            {
                singleRetainerCallback?.Invoke();
                singleRetainerCallback = null;
                Enabled = false;
                priceService.Clear();
                return true;
            });
        }
        else
        {
            ProcessNextRetainerInQueue();
        }
    }

    private void ProcessNextRetainerInQueue()
    {
        if (Retainers.Any())
        {
            if (!EzThrottler.Throttle(nameof(RetainerListHandlers.SelectRetainer), 2000))
            {
                return;
            }
            
            var retainer = Retainers.First();
            
            taskManager.Enqueue(() => retainerListHandlers.SelectRetainer(retainer.Name));
            taskManager.EnqueueDelay(200);
            taskManager.Enqueue(retainerMarketboardHandler.EnqueueRetainerSteps);
            taskManager.EnqueueDelay(200);
            
            Retainers.Remove(retainer);
        }
        else
        {
            taskManager.Enqueue(() =>
            {
                Enabled = false;
                Retainers.Clear();
                priceService.Clear();

                return true;
            });
        }
    }
}
