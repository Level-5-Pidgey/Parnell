using System;

using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using RetainerRepricer.Services;

namespace RetainerRepricer;

/// <summary>
/// Handles the optional Universalis price gate that backs off suspicious reprices.
/// </summary>
public unsafe sealed partial class Plugin
{
    private void ResetUniversalisGateState()
    {
        _universalisGateTask = null;
        _universalisGateKey = null;
        _universalisGateAverage = null;
        _universalisPriceFloor = null;
    }

    private bool TryGetCurrentMarketItemId(out uint itemId)
    {
        itemId = 0;

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        var agent = agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent == null)
            return false;

        var itemSearch = (AgentItemSearch*)agent;
        if (itemSearch == null)
            return false;

        var id = itemSearch->ResultItemId;
        if (id == 0)
            return false;

        itemId = id;
        return true;
    }

    private string? GetWorldDcRegionKey()
    {
        if (!Player.Available)
            return null;

        var worldName = Player.CurrentWorldName;
        if (!string.IsNullOrWhiteSpace(worldName))
            return worldName;

        var dcName = Player.CurrentDataCenterName;
        if (!string.IsNullOrWhiteSpace(dcName))
            return dcName;

        return null;
    }

    private UniversalisGateStatus UpdateUniversalisGate(out int floorPrice)
    {
        floorPrice = 0;

        if (!Configuration.EnableUndercutPreventionGate || !Configuration.UseUniversalisApi)
            return UniversalisGateStatus.Disabled;

        var baseUrl = UniversalisApiClient.AggregatedBaseUrl;

        if (!TryGetCurrentMarketItemId(out var itemId))
        {
            Log.Warning("[RR][Gate] Unable to resolve current item id for Universalis lookup; skipping item.");
            return UniversalisGateStatus.Failed;
        }

        var region = GetWorldDcRegionKey();
        if (string.IsNullOrWhiteSpace(region))
        {
            Log.Warning("[RR][Gate] Unable to resolve world/DC region; skipping item.");
            return UniversalisGateStatus.Failed;
        }

        var key = new UniversalisGateKey(itemId, _currentIsHq, region, baseUrl);
        if (_universalisGateKey is not { } existing || existing != key)
        {
            _universalisGateKey = key;
            _universalisGateAverage = null;
            _universalisGateTask = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
            Log.Debug($"[RR][Gate] Requesting Universalis average for item {itemId} (region='{region}', HQ={_currentIsHq}).");
        }

        if (_universalisGateAverage is { } avg)
        {
            floorPrice = ComputeUniversalisFloor(avg, Configuration.UndercutPreventionPercent);
            return UniversalisGateStatus.Ready;
        }

        var task = _universalisGateTask;
        if (task == null)
            return UniversalisGateStatus.Failed;

        if (!task.IsCompleted)
            return UniversalisGateStatus.Pending;

        decimal? averageResult;
        try
        {
            averageResult = task.Result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RR][Gate] Universalis request failed; skipping item.");
            _universalisGateTask = null;
            return UniversalisGateStatus.Failed;
        }

        _universalisGateTask = null;

        if (averageResult is null || averageResult <= 0)
        {
            Log.Warning($"[RR][Gate] Universalis returned no data for item {itemId} (region='{region}', HQ={_currentIsHq}); skipping item.");
            return UniversalisGateStatus.Failed;
        }

        _universalisGateAverage = averageResult;

        var (marketAvg, marketCount) = TryGetMarketAverage();
        if (marketCount > 0)
        {
            var threshold = marketAvg * (decimal)MarketValidationThreshold;
            if (averageResult.Value > threshold)
            {
                Log.Information($"[RR][Gate] API avg {averageResult.Value:N0} > market avg {marketAvg:N0} * {MarketValidationThreshold:F1} = {threshold:N0}; using market avg instead.");
                _universalisGateAverage = marketAvg;
                averageResult = marketAvg;
            }
        }

        floorPrice = ComputeUniversalisFloor(averageResult.Value, Configuration.UndercutPreventionPercent);
        Log.Debug($"[RR][Gate] Universalis average={averageResult.Value:0} floor={floorPrice} for item {itemId} (region='{region}', HQ={_currentIsHq}).");
        return UniversalisGateStatus.Ready;
    }

    private (decimal Average, int Count) TryGetMarketAverage()
    {
        var list = _uiReader.GetMarketList();
        if (list == null)
            return (0, 0);

        var count = list->GetItemCount();
        if (count <= 0)
            return (0, 0);

        decimal sum = 0;
        var validCount = 0;
        var maxRows = Math.Min(count, 10);

        for (int i = 0; i < maxRows; i++)
        {
            if (!TryReadMarketRow(i, out var price, out _, out var isHq))
                continue;

            if (isHq != _currentIsHq)
                continue;

            sum += price;
            validCount++;
        }

        if (validCount == 0)
            return (0, 0);

        return (sum / validCount, validCount);
    }

    private static int ComputeUniversalisFloor(decimal averagePrice, float configuredPercent)
    {
        if (averagePrice <= 0)
            return 0;

        var percent = Math.Clamp(configuredPercent, 0.1f, 0.9f);
        var multiplier = (decimal)percent;
        var raw = (int)Math.Floor(averagePrice * multiplier);
        return raw < 1 ? 1 : raw;
    }

    private enum UniversalisGateStatus
    {
        Disabled,
        Pending,
        Ready,
        Failed,
    }

    private readonly record struct UniversalisGateKey(uint ItemId, bool IsHq, string Region, string BaseUrl);
}
