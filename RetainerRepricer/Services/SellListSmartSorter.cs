using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerRepricer.Services;

internal sealed class SellListSmartSorter : IDisposable
{

    private readonly Configuration _config;
    private readonly UniversalisApiClient _universalis;
    private readonly PluginLogger _log;
    private readonly Func<string?> _regionProvider;
    private readonly SemaphoreSlim _sortLock = new(1, 1);

    private bool _disposed;

    public SellListSmartSorter(
        Configuration config,
        UniversalisApiClient universalis,
        PluginLogger log,
        Func<string?> regionProvider)
    {
        _config = config;
        _universalis = universalis;
        _log = log;
        _regionProvider = regionProvider;
    }

    public bool IsEnabled
        => _config.PluginEnabled &&
           _config.UseUniversalisApi &&
           _config.EnableUniversalisSmartSort;

    public bool IsSorting => _sortLock.CurrentCount == 0;

    public DateTime LastSortUtc => _config.GetSmartSortLastRunUtc();

    public bool IsRefreshDue()
    {
        if (!IsEnabled)
            return false;

        var minutes = Math.Max(1, _config.SmartSortRefreshMinutes);
        var next = LastSortUtc + TimeSpan.FromMinutes(minutes);
        return DateTime.UtcNow >= next;
    }

    public Task<bool> TrySortAsync(string reason, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return Task.FromResult(false);

        if (!force && !IsRefreshDue())
        {
            _log.Verbose("[RR][SmartSort] Skipping: not due for refresh (last={LastRun}, next={NextRun})", LastSortUtc, LastSortUtc + TimeSpan.FromMinutes(Math.Max(1, _config.SmartSortRefreshMinutes)));
            return Task.FromResult(false);
        }

        return SortInternalAsync(reason, cancellationToken);
    }

    public Task<bool> ForceSortAsync(string reason, CancellationToken cancellationToken = default)
        => IsEnabled ? SortInternalAsync(reason, cancellationToken) : Task.FromResult(false);

    private async Task<bool> SortInternalAsync(string reason, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return false;

        await _sortLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var region = _regionProvider() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(region))
            {
                _log.Warning("[RR][SmartSort] Cannot run smart sort: missing world or data center context.");
                return false;
            }

            var entries = _config.GetSellListOrdered().ToList();
            if (entries.Count <= 1)
                return false;

            _log.Information($"[RR][SmartSort] Starting smart sort ({reason}); items={entries.Count} region='{region}'.");

            var (velocityWeight, priceWeight) = _config.GetSmartSortWeights();

            var scored = new List<ScoredEntry>(entries.Count);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stats = await _universalis.GetListingStatsAsync(
                        UniversalisApiClient.AggregatedBaseUrl,
                        region,
                        entry.ItemId,
                        entry.IsHq,
                        cacheDuration: TimeSpan.FromMinutes(5),
                        cancellationToken)
                    .ConfigureAwait(false);

                var velocityScore = ComputeVelocityScore(stats?.DailySaleVelocity ?? 0d);
                var priceScore = ComputePriceScore(stats?.AveragePrice);
                var composite = (velocityScore * velocityWeight) + (priceScore * priceWeight);

                if (stats?.DailySaleVelocity == null && stats?.AveragePrice == null)
                {
                    _log.Debug("[RR][SmartSort] Item {ItemId}: no velocity/price data, score=0", entry.ItemId);
                }

                scored.Add(new ScoredEntry
                {
                    Entry = entry,
                    Composite = composite,
                    VelocityScore = velocityScore,
                    PriceScore = priceScore,
                });
            }

            var ordered = scored
                .OrderByDescending(s => s.Composite)
                .ThenBy(s => s.Entry.SortOrder <= 0 ? int.MaxValue : s.Entry.SortOrder)
                .ThenBy(s => string.IsNullOrWhiteSpace(s.Entry.Name) ? "zzz" : s.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Entry.ItemId)
                .ThenBy(s => s.Entry.IsHq ? 1 : 0)
                .Select(s => s.Entry)
                .ToList();

            var changed = false;
            for (var i = 0; i < ordered.Count; i++)
            {
                var desired = i + 1;
                if (ordered[i].SortOrder != desired)
                {
                    ordered[i].SortOrder = desired;
                    changed = true;
                }
            }

            if (changed)
            {
                _config.SetSmartSortLastRunUtc(DateTime.UtcNow);
                _config.Save();
                _log.Information("[RR][SmartSort] Completed smart sort; configuration updated.");
            }
            else
            {
                _config.SetSmartSortLastRunUtc(DateTime.UtcNow);
                _config.Save();
                _log.Information("[RR][SmartSort] Smart sort completed with no priority changes.");
            }

            return changed;
        }
        finally
        {
            _sortLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sortLock.Dispose();
    }

    private double ComputeVelocityScore(double? velocity)
    {
        if (velocity is null || velocity <= 0)
            return 0d;

        var clamped = Math.Min(VelocityCap, velocity.Value);
        var numerator = Math.Log10(clamped + 1d);
        var denominator = Math.Log10(VelocityLogBase);
        var score = denominator <= 0 ? 0d : numerator / denominator;
        return score > 1d ? 1d : score;
    }

    private double ComputePriceScore(decimal? price)
    {
        if (price is null || price <= 0)
            return 0d;

        var numerator = Math.Log10((double)price.Value + 1d);
        var denominator = Math.Log10(PriceLogBase);
        var score = denominator <= 0 ? 0d : numerator / denominator;
        return score > 1d ? 1d : score;
    }

    private sealed class ScoredEntry
    {
        public Configuration.SellListEntry Entry { get; set; } = null!;
        public double Composite { get; set; }
        public double VelocityScore { get; set; }
        public double PriceScore { get; set; }
    }

    private double VelocityCap => _config.SmartSortVelocityCap > 0d ? _config.SmartSortVelocityCap : 100d;
    private double VelocityLogBase => _config.SmartSortVelocityLogBase > 1d ? _config.SmartSortVelocityLogBase : 101d;
    private double PriceLogBase => _config.SmartSortPriceLogBase > 1d ? _config.SmartSortPriceLogBase : 100001d;
}
