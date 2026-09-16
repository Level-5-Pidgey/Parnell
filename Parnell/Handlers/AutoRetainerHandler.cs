using System;
using AutoRetainerAPI;
using ECommons.DalamudServices;
using Parnell.Scheduler;

namespace Parnell.Handlers;

public class AutoRetainerHandler : IDisposable
{
    private readonly AutoRetainerApi autoRetainerApi = new();
    private readonly MainScheduler mainScheduler;

    public AutoRetainerHandler(MainScheduler mainScheduler)
    {
        this.mainScheduler = mainScheduler;
        autoRetainerApi.OnRetainerPostprocessStep += OnRetainerPostProcessStep;
    }

    private void OnRetainerPostProcessStep(string retainerName)
    {
        if (string.IsNullOrEmpty(retainerName))
        {
            Svc.Log.Error("Received empty retainer name from AutoRetainer, finishing post-process step immediately.");
            autoRetainerApi.FinishCharacterPostProcess();
            return;
        }

        Svc.Log.Info($"Received post-process step for {retainerName} from AutoRetainer.");
        mainScheduler.EnqueueSingleRetainer(retainerName, () => autoRetainerApi.FinishCharacterPostProcess());
    }
    
    public void Dispose()
    {
        autoRetainerApi.OnRetainerPostprocessStep -= OnRetainerPostProcessStep;
        GC.SuppressFinalize(this);
    }
}
