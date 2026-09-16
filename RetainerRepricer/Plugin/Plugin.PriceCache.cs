using System;
using System.Collections.Generic;

using ECommons.GameHelpers;

namespace RetainerRepricer;

public unsafe sealed partial class Plugin
{
    private bool TryGetRepricingCacheKey(string itemName, bool isHq, out RepricingCacheKey key)
    {
        key = default;

        if (!TryGetActiveCharacterMarket(out var characterId, out var world))
        {
            ClearRepricingCache("active character unavailable");
            return false;
        }

        EnsureRepricingCacheCharacter(characterId);

        var normalizedName = itemName.Trim();
        if (normalizedName.Length == 0)
            return false;

        key = new RepricingCacheKey(world, normalizedName, isHq);
        return true;
    }

    private void StartFreshRepricingCache(string reason)
    {
        ClearRepricingCache(reason);

        if (TryGetActiveCharacterMarket(out var characterId, out _))
            _marketState.RepricingCacheCharacterId = characterId;
    }

    private void ValidateRepricingCacheCharacter()
    {
        if (_marketState.RepricingCacheCharacterId == 0 && _marketState.RepricingCache.Count == 0)
            return;

        if (!TryGetActiveCharacterMarket(out var characterId, out _))
        {
            ClearRepricingCache("active character unavailable");
            return;
        }

        EnsureRepricingCacheCharacter(characterId);
    }

    private void EnsureRepricingCacheCharacter(ulong characterId)
    {
        var owner = _marketState.RepricingCacheCharacterId;
        if (owner != 0 && owner != characterId)
            ClearRepricingCache("active character changed");

        _marketState.RepricingCacheCharacterId = characterId;
    }

    private static bool TryGetActiveCharacterMarket(out ulong characterId, out string world)
    {
        characterId = 0;
        world = string.Empty;

        if (!Player.Available || Player.CID == 0)
            return false;

        var currentWorld = Player.CurrentWorldName;
        if (string.IsNullOrWhiteSpace(currentWorld))
            return false;

        characterId = Player.CID;
        world = currentWorld.Trim();
        return true;
    }

    private bool TryUseCachedRepricingDecision(RepricingCacheKey key, int currentPrice)
    {
        if (!_marketState.RepricingCache.TryGetValue(key, out var cached))
            return false;

        Log.Information(
            "[RR][PriceCache] Hit item='{Item}' quality={Quality} world='{World}' desired={Desired}.",
            key.ItemName,
            key.IsHq ? "HQ" : "NQ",
            key.World,
            cached.DesiredPrice);

        if (currentPrice == cached.DesiredPrice)
        {
            Log.Information($"[RR] Price unchanged ({cached.DesiredPrice}); skipping apply.");
            _runPhase = RunPhase.CleanupAfterItem;
        }
        else
        {
            _stagedDesiredPrice = cached.DesiredPrice;
            _stagedReferenceSeller = cached.ReferenceSeller;
            _stagedReferenceIsMine = cached.ReferenceIsMine;
            _hasAppliedStagedPrice = false;

            Log.Information(
                "[RR] Stage cached apply: current={Current} desired={Desired} seller='{Seller}' mine={Mine}",
                currentPrice,
                cached.DesiredPrice,
                GetSellerLabelForLog(cached.ReferenceSeller),
                cached.ReferenceIsMine);

            // A previous item's market window can still be closing when the next
            // RetainerSell opens. Match the non-cached path by actively closing it.
            CloseMarketWindows();
            _runPhase = RunPhase.CloseMarketThenApply;
        }

        return true;
    }

    private void CacheCurrentRepricingDecision(int desiredPrice, string referenceSeller, bool referenceIsMine)
    {
        if (_marketState.CurrentRepricingCacheKey is not { } key)
            return;

        if (!TryGetActiveCharacterMarket(out var characterId, out var world))
        {
            ClearRepricingCache("active character unavailable");
            return;
        }

        EnsureRepricingCacheCharacter(characterId);
        if (!string.Equals(key.World, world, StringComparison.OrdinalIgnoreCase))
            return;

        _marketState.RepricingCache[key] = new CachedRepricingDecision(
            desiredPrice,
            referenceSeller,
            referenceIsMine);

        Log.Debug(
            "[RR][PriceCache] Store item='{Item}' quality={Quality} world='{World}' desired={Desired}.",
            key.ItemName,
            key.IsHq ? "HQ" : "NQ",
            key.World,
            desiredPrice);
    }

    private void RecoverAfterRetainerSellClosed(string phase, DateTime now)
    {
        Log.Warning(
            "[RR] RetainerSell closed externally while {Phase}; returning to the sell list instead of waiting indefinitely.",
            phase);

        if (!_processingListedItem)
            _newListingAttemptState = NewListingAttemptState.AwaitingResult;

        _stagedDesiredPrice = null;
        _stagedReferenceSeller = string.Empty;
        _stagedReferenceIsMine = false;
        _hasAppliedStagedPrice = false;

        CloseMarketWindows();
        ClearRetainerContextMenuExpectation();

        _runPhase = RunPhase.CleanupAfterItem;
        _lastActionUtc = now;
    }

    private void ClearRepricingCache(string reason)
    {
        var count = _marketState.RepricingCache.Count;
        _marketState.RepricingCache.Clear();
        _marketState.RepricingCacheCharacterId = 0;
        _marketState.CurrentRepricingCacheKey = null;

        if (count > 0)
            Log.Debug("[RR][PriceCache] Cleared {Count} entr{Suffix}: {Reason}.", count, count == 1 ? "y" : "ies", reason);
    }

    private readonly record struct RepricingCacheKey(string World, string ItemName, bool IsHq);

    private readonly record struct CachedRepricingDecision(
        int DesiredPrice,
        string ReferenceSeller,
        bool ReferenceIsMine);

    private sealed class RepricingCacheKeyComparer : IEqualityComparer<RepricingCacheKey>
    {
        public static RepricingCacheKeyComparer Instance { get; } = new();

        public bool Equals(RepricingCacheKey x, RepricingCacheKey y)
            => x.IsHq == y.IsHq &&
               string.Equals(x.World, y.World, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.ItemName, y.ItemName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(RepricingCacheKey obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.World),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemName),
                obj.IsHq);
    }
}
