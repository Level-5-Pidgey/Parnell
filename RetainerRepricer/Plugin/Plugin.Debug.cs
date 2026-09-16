using System;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using RetainerRepricer.Services;

namespace RetainerRepricer;

/// <summary>
/// Debug helpers invoked by the button-driven Debug Window.
/// </summary>
public unsafe sealed partial class Plugin
{
    internal void TestUniversalisGateForDebug()
    {
        if (!IsAddonOpen("ItemSearchResult"))
        {
            Log.Information("[RR][TestGate] ItemSearchResult is not open. Open market board listings first.");
            ChatGui.Print("[RetainerRepricer] Open ItemSearchResult first (/mbdump to verify).");
            return;
        }

        if (!TryGetCurrentMarketItemId(out var itemId))
        {
            Log.Warning("[RR][TestGate] Could not resolve current item id.");
            return;
        }

        var region = GetWorldDcRegionKey();
        if (string.IsNullOrWhiteSpace(region))
        {
            Log.Warning("[RR][TestGate] Could not resolve world/DC region.");
            return;
        }

        if (!Configuration.EnableUndercutPreventionGate)
        {
            Log.Information("[RR][TestGate] Gate is disabled in config. Using legacy behavior.");
        }
        else if (!Configuration.UseUniversalisApi)
        {
            Log.Information("[RR][TestGate] Universalis API is disabled in config. Using legacy behavior.");
        }

        var baseUrl = UniversalisApiClient.AggregatedBaseUrl;

        Log.Information($"[RR][TestGate] Fetching Universalis data for item {itemId}, region='{region}', HQ={_currentIsHq}");

        decimal avg;
        try
        {
            var result = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
            avg = result != null ? (result.Result ?? 0) : 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RR][TestGate] Universalis API call failed.");
            avg = 0;
        }

        if (avg <= 0)
        {
            Log.Information("[RR][TestGate] Universalis returned no data (or gate disabled). Using legacy row logic.");
            avg = 0;
        }

        var (marketAvg, marketCount) = TryGetMarketAverage();
        if (marketCount > 0 && avg > 0)
        {
            var threshold = marketAvg * (decimal)MarketValidationThreshold;
            if (avg > threshold)
            {
                Log.Information($"[RR][TestGate] API avg {avg:N0} > market avg {marketAvg:N0} * {MarketValidationThreshold:F1} = {threshold:N0}; using market avg instead.");
                avg = marketAvg;
            }
        }

        var floor = ComputeUniversalisFloor(avg, Configuration.UndercutPreventionPercent);
        var percentDisplay = Configuration.UndercutPreventionPercent * 100f;

        Log.Information("[RR][TestGate] === Universalis Results ===");
        Log.Information($"[RR][TestGate] Item ID: {itemId}");
        Log.Information($"[RR][TestGate] Region: {region}");
        Log.Information($"[RR][TestGate] API Average: {(avg > 0 ? avg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Market Average ({marketCount} listings): {(marketAvg > 0 ? marketAvg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Config Percent: {percentDisplay:0}%");
        Log.Information($"[RR][TestGate] Floor Price: {(floor > 0 ? floor.ToString("N0") : "N/A")}");
        Log.Information("[RR][TestGate] ========================");

        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Warning("[RR][TestGate] Could not get market list.");
            return;
        }

        var count = list->GetItemCount();
        if (count <= 0)
        {
            Log.Information("[RR][TestGate] No listings in market.");
            return;
        }

        var maxRows = Math.Min(count, 10);
        var selectedRow = -1;
        var selectedPrice = 0;

        for (int i = 0; i < maxRows; i++)
        {
            if (!TryReadMarketRow(i, out var price, out var seller, out var isHq))
                continue;

            bool passes = floor <= 0 || price >= floor;
            var status = passes ? "PASS" : "SKIP (below floor)";

            Log.Information($"[RR][TestGate] Row {i}: price={price:N0} hq={isHq} seller='{GetSellerLabelForLog(seller)}' => {status}");

            if (selectedRow < 0 && passes)
            {
                selectedRow = i;
                selectedPrice = price;
            }
        }

        if (selectedRow < 0 && floor > 0)
        {
            selectedRow = -1;
            selectedPrice = floor;
            Log.Information($"[RR][TestGate] No rows passed floor. Using floor price: {floor:N0}");
        }
        else if (selectedRow < 0)
        {
            if (TryReadMarketRow(0, out var p0, out var s0, out _))
            {
                selectedRow = 0;
                selectedPrice = p0;
                Log.Information($"[RR][TestGate] No floor set, using row 0: {p0:N0}");
            }
            else
            {
                Log.Information("[RR][TestGate] Could not read any market rows.");
                return;
            }
        }

        var undercutPrice = Math.Max(1, selectedPrice - 1);
        Log.Information("[RR][TestGate] === Final Decision ===");
        Log.Information($"[RR][TestGate] Selected Row: {(selectedRow >= 0 ? selectedRow.ToString() : "FLOOR")}");
        Log.Information($"[RR][TestGate] Selected Price: {selectedPrice:N0}");
        Log.Information($"[RR][TestGate] Undercut Price: {undercutPrice:N0}");
        Log.Information("[RR][TestGate] ========================");

        ChatGui.Print("[RetainerRepricer] TestGate complete - check log for details.");
    }

    internal void DumpMarketRowsForDebug()
    {
        if (!IsAddonOpen("ItemSearchResult"))
        {
            Log.Information("[MB] ItemSearchResult is not open. Open market board listings first.");
            return;
        }

        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Information("[MB] Market list not found. Open ItemSearchResult first.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[MB] renderer count = {count}");

        int visibleCount = 0;
        int hiddenCount = 0;
        int drawZeroCount = 0;
        int drawHundredCount = 0;
        int drawHundredTwoCount = 0;

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var seller = _uiReader.ReadRendererText(r, Ui.NodePaths.SellerNodeId);
            var unitRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
            var qtyRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.QuantityNodeId);

            var hqState = _uiReader.DumpHqIconState(r, i, s => Log.Information(s));
            var isHq = _uiReader.RowIsHq(r);

            if (hqState.HasValue)
            {
                var state = hqState.Value;
                if (state.Visible)
                    visibleCount++;
                else
                    hiddenCount++;

                if (state.DrawFlags == 0)
                    drawZeroCount++;

                if (state.DrawFlags == 0x100)
                    drawHundredCount++;

                if (state.DrawFlags == 0x102)
                    drawHundredTwoCount++;
            }

            var unit = Ui.UiReader.ParseGil(unitRaw);
            var qty = int.TryParse(qtyRaw, out var q) ? q : 0;

            Log.Information($"[MB] row {i}: seller={GetSellerLabelForLog(seller ?? string.Empty)} unit={unit} qty={qty} hq={(isHq ? "HQ" : "NQ")}");
        }

        Log.Information($"[MB][HQ] summary: visible={visibleCount} hidden={hiddenCount} draw0={drawZeroCount} draw100={drawHundredCount} draw102={drawHundredTwoCount}");
    }

    internal void DumpRetainerRowsForDebug()
    {
        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            Log.Information("[RL] RetainerList not found. Open the summoning bell RetainerList.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[RL] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            Log.Information($"[RL] row {i}: {GetRetainerLabelForLog(i)}");
        }
    }

    internal void DumpInventoryByItemIdForDebug(uint itemId)
    {
        var itemRow = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        Log.Information("[INV][Dump] Searching all inventory slots for itemId={ItemId}.", itemId);

        if (!itemRow.HasValue)
        {
            Log.Warning("[INV][Dump] Item sheet row not found for itemId={ItemId}.", itemId);
            return;
        }

        Log.Information("[INV][Dump] Sheet: name='{Name}' isUntradable={IsUntradable}.",
            itemRow.Value.Name.ToString(), itemRow.Value.IsUntradable);

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Log.Warning("[INV][Dump] InventoryManager is unavailable.");
            return;
        }

        var containers = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            InventoryType.Crystals,
        };

        var matchCount = 0;
        foreach (var containerType in containers)
        {
            var container = inventory->GetInventoryContainer(containerType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->Quantity <= 0 || slot->GetBaseItemId() != itemId)
                    continue;

                matchCount++;
                var flags = slot->GetFlags();
                var spiritbondOrCollectability = slot->GetSpiritbondOrCollectability();
                var currentSellable = Ui.UiReader.IsInventorySlotSellable(slot, itemRow);
                var reason = GetInventorySellabilityReason(slot, itemRow);

                Log.Information(
                    "[INV][Dump] container={Container}({ContainerId}) slot={Slot} rawItemId={RawItemId} baseItemId={BaseItemId} fullItemId={FullItemId} quantity={Quantity} flags=0x{Flags:X2}({FlagNames}) hq={Hq} collectable={Collectable} collectability={Collectability} spiritbondOrCollectability={SpiritbondOrCollectability} condition={Condition} conditionPercent={ConditionPercent} symbolic={Symbolic} linkedContainer={LinkedContainer} linkedSlot={LinkedSlot} glamourId={GlamourId} crafterContentId={CrafterContentId} eventId={EventId} pluginSellable={Sellable} reason={Reason}",
                    containerType,
                    (int)containerType,
                    slotIndex,
                    slot->ItemId,
                    slot->GetBaseItemId(),
                    slot->GetItemId(),
                    slot->Quantity,
                    (byte)flags,
                    flags,
                    slot->IsHighQuality(),
                    slot->IsCollectable(),
                    slot->GetCollectability(),
                    spiritbondOrCollectability,
                    slot->Condition,
                    slot->GetConditionPercentage(),
                    slot->IsSymbolic,
                    slot->LinkedInventoryType,
                    slot->LinkedItemSlot,
                    slot->GetGlamourId(),
                    slot->GetCrafterContentId(),
                    slot->EventId,
                    currentSellable,
                    reason);

                for (byte materiaIndex = 0; materiaIndex < 5; materiaIndex++)
                {
                    Log.Information("[INV][Dump] container={Container} slot={Slot} materia[{Index}] id={MateriaId} grade={Grade}",
                        containerType,
                        slotIndex,
                        materiaIndex,
                        slot->GetMateriaId(materiaIndex),
                        slot->GetMateriaGrade(materiaIndex));
                }

                Log.Information("[INV][Dump] container={Container} slot={Slot} stain[0]={Stain0} stain[1]={Stain1}",
                    containerType,
                    slotIndex,
                    slot->GetStain(0),
                    slot->GetStain(1));
            }
        }

        Log.Information("[INV][Dump] Completed itemId={ItemId}; matchingSlots={MatchCount}.", itemId, matchCount);
    }

    internal void DumpInventoryGridForDebug(uint itemId)
    {
        Log.Information("[INV][Grid] Dumping visible renderer candidates for itemId={ItemId}.", itemId);
        _uiReader.DumpInventoryGridState(itemId, message => Log.Information(message));
        Log.Information("[INV][Grid] Targeted InventoryGrid dump complete for itemId={ItemId}.", itemId);
    }

    private static string GetInventorySellabilityReason(InventoryItem* slot, Item? itemRow)
    {
        if (slot == null)
            return "null_slot";

        if (!itemRow.HasValue)
            return "missing_item_sheet";

        if (itemRow.Value.IsUntradable)
            return "item_sheet_untradable";

        if (!slot->IsCollectable() && slot->GetSpiritbondOrCollectability() != 0)
            return "spiritbond_or_collectability_nonzero";

        return "passes_current_filter";
    }
}
