using System;

using Dalamud.Plugin.Services;

namespace RetainerRepricer;

/// <summary>
/// Hooks the Dalamud framework tick so the state machine advances at a controlled pace.
/// </summary>
public unsafe sealed partial class Plugin
{
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        if ((now - _lastFrameworkTickUtc).TotalSeconds < FrameworkTickIntervalSeconds)
            return;

        try
        {
            TickRun();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RR] Unhandled automation error.");
            if (_runOrigin == RunOrigin.AutoRetainerMenu)
                RequestAutoRetainerCleanup("unhandled automation error");
            else
                StopRun();
        }
        _lastFrameworkTickUtc = now;
    }
}
