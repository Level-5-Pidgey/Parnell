using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerRepricer.Services;

internal sealed class UniversalisApiClient : IDisposable
{
    // Default aggregated endpoint used for deterministic Universalis lookups.
    internal const string AggregatedBaseUrl = "https://universalis.app/api/v2/aggregated";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _httpClient;
    private readonly PluginLogger _log;
    private readonly bool _ownsClient;
    private bool _disposed;
    private readonly ConcurrentDictionary<StatsCacheKey, StatsCacheEntry> _statsCache = new();

    public UniversalisApiClient(PluginLogger log, HttpClient? httpClient = null)
    {
        _log = log;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task<decimal?> GetAveragePriceAsync(
        string baseUrl,
        string worldDcRegion,
        uint itemId,
        bool isHighQuality,
        CancellationToken cancellationToken = default)
    {
        var stats = await GetListingStatsAsync(baseUrl, worldDcRegion, itemId, isHighQuality, cacheDuration: TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        return stats?.AveragePrice;
    }

    public async Task<string?> GetAggregatedPayloadAsync(
        string baseUrl,
        string worldDcRegion,
        uint itemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(worldDcRegion))
            return null;

        var requestUri = BuildRequestUri(baseUrl, worldDcRegion, itemId);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning($"[Universalis] Raw request failed ({response.StatusCode}) for '{requestUri}'.");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[Universalis] Failed to fetch aggregated payload for item {itemId}.");
            return null;
        }
    }

    public async Task<UniversalisListingStats?> GetListingStatsAsync(
        string baseUrl,
        string worldDcRegion,
        uint itemId,
        bool isHighQuality,
        TimeSpan? cacheDuration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(worldDcRegion))
            return null;

        var duration = cacheDuration.GetValueOrDefault(TimeSpan.FromMinutes(5));
        var key = new StatsCacheKey(baseUrl.TrimEnd('/'), worldDcRegion, itemId, isHighQuality);

        if (_statsCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Stats;

        var requestUri = BuildRequestUri(baseUrl, worldDcRegion, itemId);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning($"[Universalis] Request failed ({response.StatusCode}) for '{requestUri}'.");
                return null;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<AggregatedResponse>(contentStream, _jsonOptions, cancellationToken).ConfigureAwait(false);

            var result = payload?.Results?.FirstOrDefault();
            if (result == null)
            {
                _log.Debug($"[Universalis] No results for item {itemId} (HQ={isHighQuality}).");
                return null;
            }

            var quality = isHighQuality ? result.Hq : result.Nq;
            if (quality == null)
            {
                _log.Debug($"[Universalis] Missing quality bucket for item {itemId} (HQ={isHighQuality}).");
                return null;
            }

            var avgPrice = quality.AverageSalePrice?.GetBestPrice();
            if (avgPrice is null)
            {
                var fallbackPrice = quality.GetFallbackPrice(out var fallbackSource);
                if (fallbackPrice is not null)
                {
                    _log.Debug($"[Universalis] Missing average price for item {itemId} (HQ={isHighQuality}); using {fallbackSource} price {fallbackPrice.Value:0}.");
                    avgPrice = fallbackPrice;
                }
            }
            var velocity = quality.DailySaleVelocity?.GetBestQuantity();
            var lastUpload = result.GetLatestUploadUtc();

            if (avgPrice is null && velocity is null)
                return null;

            var stats = new UniversalisListingStats(avgPrice, velocity, lastUpload);

            _statsCache[key] = new StatsCacheEntry
            {
                Stats = stats,
                ExpiresUtc = DateTime.UtcNow + duration,
            };

            return stats;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[Universalis] Failed to fetch listing stats for item {itemId}.");
            return null;
        }
    }

    private static string BuildRequestUri(string baseUrl, string worldDcRegion, uint itemId)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var encodedRegion = Uri.EscapeDataString(worldDcRegion);
        return $"{trimmedBase}/{encodedRegion}/{itemId}";
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_ownsClient)
            _httpClient.Dispose();

        _disposed = true;
    }

    private sealed class AggregatedResponse
    {
        [JsonPropertyName("results")]
        public List<AggregatedItem> Results { get; set; } = new();
    }

    private sealed class AggregatedItem
    {
        [JsonPropertyName("itemId")]
        public uint ItemId { get; set; }

        [JsonPropertyName("nq")]
        public AggregatedQuality? Nq { get; set; }

        [JsonPropertyName("hq")]
        public AggregatedQuality? Hq { get; set; }

        [JsonPropertyName("worldUploadTimes")]
        public List<AggregatedUploadTime> WorldUploadTimes { get; set; } = new();

        public DateTime? GetLatestUploadUtc()
        {
            if (WorldUploadTimes == null || WorldUploadTimes.Count == 0)
                return null;

            var max = WorldUploadTimes.Max(t => t.Timestamp);
            if (max <= 0)
                return null;

            return DateTimeOffset.FromUnixTimeMilliseconds(max).UtcDateTime;
        }
    }

    private sealed class AggregatedQuality
    {
        [JsonPropertyName("averageSalePrice")]
        public AggregatedAverage? AverageSalePrice { get; set; }

        [JsonPropertyName("dailySaleVelocity")]
        public AggregatedVelocity? DailySaleVelocity { get; set; }

        [JsonPropertyName("minListing")]
        public AggregatedListingPoint? MinListing { get; set; }

        [JsonPropertyName("recentPurchase")]
        public AggregatedPurchasePoint? RecentPurchase { get; set; }

        public decimal? GetFallbackPrice(out string source)
        {
            var recent = RecentPurchase?.GetBestPrice();
            if (recent is { } recentPrice && recentPrice > 0)
            {
                source = "recent purchase";
                return recentPrice;
            }

            var minListing = MinListing?.GetBestPrice();
            if (minListing is { } minListingPrice && minListingPrice > 0)
            {
                source = "min listing";
                return minListingPrice;
            }

            source = string.Empty;
            return null;
        }
    }

    private sealed class AggregatedAverage
    {
        [JsonPropertyName("world")]
        public AggregatedAveragePrice? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedAveragePrice? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedAveragePrice? Region { get; set; }

        public decimal? GetBestPrice()
            => World?.Price ?? Dc?.Price ?? Region?.Price;
    }

    private sealed class AggregatedAveragePrice
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }
    }

    private sealed class AggregatedListingPoint
    {
        [JsonPropertyName("world")]
        public AggregatedListingEntry? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedListingEntry? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedListingEntry? Region { get; set; }

        public decimal? GetBestPrice()
            => World?.Price ?? Dc?.Price ?? Region?.Price;
    }

    private sealed class AggregatedListingEntry
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("worldId")]
        public uint? WorldId { get; set; }
    }

    private sealed class AggregatedPurchasePoint
    {
        [JsonPropertyName("world")]
        public AggregatedPurchaseEntry? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedPurchaseEntry? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedPurchaseEntry? Region { get; set; }

        public decimal? GetBestPrice()
            => World?.Price ?? Dc?.Price ?? Region?.Price;
    }

    private sealed class AggregatedPurchaseEntry
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; set; }

        [JsonPropertyName("worldId")]
        public uint? WorldId { get; set; }
    }

    private sealed class AggregatedVelocity
    {
        [JsonPropertyName("world")]
        public AggregatedVelocityValue? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedVelocityValue? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedVelocityValue? Region { get; set; }

        public double? GetBestQuantity()
            => World?.Quantity ?? Dc?.Quantity ?? Region?.Quantity;
    }

    private sealed class AggregatedVelocityValue
    {
        [JsonPropertyName("quantity")]
        public double? Quantity { get; set; }
    }

    private sealed class AggregatedUploadTime
    {
        [JsonPropertyName("worldId")]
        public uint WorldId { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    private readonly record struct StatsCacheKey(string BaseUrl, string Region, uint ItemId, bool IsHq);

    private sealed class StatsCacheEntry
    {
        public UniversalisListingStats Stats { get; init; } = null!;
        public DateTime ExpiresUtc { get; init; }
    }
}

public sealed record UniversalisListingStats(decimal? AveragePrice, double? DailySaleVelocity, DateTime? LastUploadUtc);
