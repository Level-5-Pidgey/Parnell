using AutoRetainerAPI;
using System;

namespace RetainerRepricer.Services;

internal sealed class AutoRetainerIntegration : IDisposable
{
    private readonly Plugin _plugin;
    private readonly AutoRetainerApi _api;
    private string? _requestedRetainer;
    private bool _claimed;
    private bool _disposed;

    internal AutoRetainerIntegration(Plugin plugin)
    {
        _plugin = plugin;
        _api = new AutoRetainerApi();
        _api.OnRetainerPostprocessStep += OnRetainerPostprocessStep;
        _api.OnRetainerReadyToPostprocess += OnRetainerReadyToPostprocess;
    }

    internal bool IsAvailable => !_disposed && _api.Ready;

    private void OnRetainerPostprocessStep(string retainerName)
    {
        if (_disposed || _claimed || _requestedRetainer != null)
            return;

        try
        {
            if (!_plugin.ShouldRequestAutoRetainerPostprocess(retainerName))
                return;

            _requestedRetainer = retainerName;
            _api.RequestRetainerPostprocess();
            Plugin.Log.Information("[RR][AutoRetainer] Requested post-process for '{Retainer}'.", retainerName);
        }
        catch (Exception ex)
        {
            _requestedRetainer = null;
            Plugin.Log.Error(ex, "[RR][AutoRetainer] Failed to request retainer post-processing.");
        }
    }

    private void OnRetainerReadyToPostprocess(string retainerName)
    {
        if (_disposed)
            return;

        var requestedRetainer = _requestedRetainer;
        _requestedRetainer = null;
        _claimed = true;

        try
        {
            if (string.IsNullOrWhiteSpace(requestedRetainer) ||
                !string.Equals(requestedRetainer, retainerName, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.Warning(
                    "[RR][AutoRetainer] Ready event did not match the pending request (requested='{Requested}', ready='{Ready}').",
                    requestedRetainer ?? string.Empty,
                    retainerName ?? string.Empty);
                Complete();
                return;
            }

            if (!_plugin.StartAutoRetainerRun(retainerName))
                Complete();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[RR][AutoRetainer] Failed to start the assigned post-process run.");
            Complete();
        }
    }

    internal void Complete()
    {
        if (!_claimed)
            return;

        _claimed = false;
        try
        {
            _api.FinishRetainerPostProcess();
            Plugin.Log.Information("[RR][AutoRetainer] Released retainer post-process lock.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[RR][AutoRetainer] Failed to release retainer post-process lock.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _api.OnRetainerPostprocessStep -= OnRetainerPostprocessStep;
        _api.OnRetainerReadyToPostprocess -= OnRetainerReadyToPostprocess;
        Complete();
        _api.Dispose();
        _disposed = true;
    }
}
