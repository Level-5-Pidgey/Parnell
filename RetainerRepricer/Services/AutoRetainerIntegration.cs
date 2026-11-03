using AutoRetainerAPI;
using System;
using System.Collections.Generic;
using System.Linq;

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
        _api.OnCharacterPostprocessStep += OnCharacterPostprocessStep;
    }

    internal bool IsAvailable => !_disposed && _api.Ready;

    internal bool SyncRetainerCharacters()
    {
        if (!IsAvailable)
            return false;

        var changed = false;
        try
        {
            var knownNames = _plugin.Configuration.GetAllRetainerNames()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var characterId in _api.GetRegisteredCharacters())
            {
                var character = _api.GetOfflineCharacterData(characterId);
                if (string.IsNullOrWhiteSpace(character.Name) || string.IsNullOrWhiteSpace(character.World))
                    continue;

                foreach (var retainer in character.RetainerData)
                {
                    if (!knownNames.Contains(retainer.Name))
                        continue;

                    if (!owners.TryGetValue(retainer.Name, out var characters))
                        owners[retainer.Name] = characters = new(StringComparer.OrdinalIgnoreCase);

                    characters.Add($"{character.Name}@{character.World}");
                }
            }

            foreach (var owner in owners)
            {
                if (owner.Value.Count == 1)
                    changed |= _plugin.Configuration.SetRetainerCharacter(owner.Key, owner.Value.Single());
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("[RR][AutoRetainer] Could not read retainer character groups: {Error}", ex.Message);
        }

        return changed;
    }

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

    private void OnCharacterPostprocessStep()
    {
        if (_disposed)
            return;

        _plugin.OnAutoRetainerCharacterPostprocessStep();
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
        _api.OnCharacterPostprocessStep -= OnCharacterPostprocessStep;
        Complete();
        _api.Dispose();
        _disposed = true;
    }
}
