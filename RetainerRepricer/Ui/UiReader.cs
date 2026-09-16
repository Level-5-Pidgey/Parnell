using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Dalamud.Game;
using Dalamud.Plugin.Services;

using ECommons.Automation;
using ECommons.DalamudServices;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace RetainerRepricer.Ui;

/// <summary>
/// Thin helpers for reading Dalamud UI state without guessing at node timing.
/// </summary>
internal sealed unsafe class UiReader
{
    public readonly record struct HqIconState(bool Visible, NodeFlags Flags, uint DrawFlags, byte Alpha);

    #region Constants

    // Observed HQ marker glyph inside RetainerSell item-name payload.
    // This shows up alongside SeString/control payload bytes, so don't treat it as "last char".
    public const char RetainerSell_HqGlyphChar = '\uE03C';

    // Inventory containers we scan for SellList items (bags only).
    private static readonly InventoryType[] SellScanContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    private static readonly string[] InventoryGridAddonNames =
    {
        "InventoryGrid",
        "InventoryGrid0",
        "InventoryGrid1",
        "InventoryGrid0E",
        "InventoryGrid1E",
        "InventoryGrid2E",
        "InventoryGrid3E",
    };

    #endregion

    #region Types

    public enum ItemSearchResultStatus
    {
        None,
        NoItemsFound,
        PleaseWaitRetry,
        OtherMessage,
    }

    public enum InventoryLookupStatus
    {
        NotFound = 0,
        FoundOnlyUnsellable = 1,
        FoundSellable = 2,
    }

    public readonly struct InventoryLookupResult
    {
        public InventoryLookupResult(
            InventoryLookupStatus status,
            int container,
            int slot,
            int totalCount,
            uint firstUnsellableSpiritbondOrCollectability)
        {
            Status = status;
            Container = container;
            Slot = slot;
            TotalCount = totalCount;
            FirstUnsellableSpiritbondOrCollectability = firstUnsellableSpiritbondOrCollectability;
        }

        public InventoryLookupStatus Status { get; }

        public int Container { get; }

        public int Slot { get; }

        public int TotalCount { get; }

        public uint FirstUnsellableSpiritbondOrCollectability { get; }

        public bool FoundSellable => Status == InventoryLookupStatus.FoundSellable;
    }

    #endregion

    #region Fields / lifecycle

    private static readonly Lazy<ItemSearchResultStatusStrings> ItemSearchResultStatusStringsLoader = new(LoadItemSearchResultStatusStrings);

    private static bool IsJapaneseClient
        => Svc.ClientState?.ClientLanguage == ClientLanguage.Japanese;

    private readonly IGameGui _gui;

    public UiReader(IGameGui gui)
        => _gui = gui;

    #endregion

    #region Core addon/unit helpers

    private AtkUnitBase* GetVisibleUnitBase(string addonName, int index = 1)
    {
        var addon = _gui.GetAddonByName(addonName, index);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible) return null;

        return unit;
    }

    private int FindVisibleAddonIndex(string addonName, int max = 5)
    {
        for (int i = 1; i <= max; i++)
        {
            var a = _gui.GetAddonByName(addonName, i);
            if (a.IsNull) continue;

            var u = (AtkUnitBase*)a.Address;
            if (u != null && u->IsVisible)
                return i;
        }

        return -1;
    }

    private static AtkResNode* FindNodeById(AtkUldManager* uld, int nodeId)
    {
        if (uld == null) return null;

        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    private static AtkResNode* FindNodeById(AtkComponentBase* comp, ushort nodeId)
    {
        if (comp == null) return null;

        var uld = &comp->UldManager;
        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    private static string? ReadTextNode(AtkResNode* node)
    {
        if (node == null) return null;
        if (node->Type != NodeType.Text) return null;

        return ((AtkTextNode*)node)->NodeText.ToString();
    }

    #endregion

    #region Generic node readers

    public AtkComponentBase* GetAddonComponent(string addonName, int componentNodeId)
    {
        var unit = GetVisibleUnitBase(addonName, 1);
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, componentNodeId);
        if (n == null) return null;

        var compNode = (AtkComponentNode*)n;
        return compNode->Component;
    }

    public string? ReadAddonTextNode(string addonName, int nodeId)
    {
        var unit = GetVisibleUnitBase(addonName, 1);
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, nodeId);
        return ReadTextNode(n);
    }

    public string? ReadComponentTextNode(AtkComponentBase* comp, ushort nodeId)
    {
        var n = FindNodeById(comp, nodeId);
        return ReadTextNode(n);
    }

    #endregion

    #region Renderer helpers (list item renderers)

    public string? ReadRendererText(AtkComponentListItemRenderer* renderer, ushort nodeId)
        => GetRendererTextByNodeId(renderer, nodeId);

    public string? GetRendererTextByNodeId(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = &renderer->UldManager;
        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId != nodeId) continue;

            return ReadTextNode(n);
        }

        return null;
    }

    public AtkResNode* GetRendererNodeById(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = &renderer->UldManager;
        var nodes = uld->NodeList;
        var count = uld->NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    #endregion

    #region Market board (ItemSearchResult)

    /// <summary>
    /// Returns the ItemSearchResult list component when it exists, even if the window is still animating in.
    /// </summary>
    public AtkComponentList* GetMarketList()
    {
        // Don't require visible here; the list can be valid while the window animates in.
        var addon = _gui.GetAddonByName("ItemSearchResult", 1);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, NodePaths.ItemSearchResult_ListNodeId);
        if (n == null) return null;

        var compNode = (AtkComponentNode*)n;
        var comp = compNode->Component;
        return comp != null ? (AtkComponentList*)comp : null;
    }

    /// <summary>
    /// Checks a market row renderer for the HQ icon flag instead of trusting text decorations.
    /// </summary>
    public bool RowIsHq(AtkComponentListItemRenderer* renderer)
    {
        var state = GetHqIconState(renderer);
        if (!state.HasValue)
            return false;

        return state.Value.Visible;
    }

    public HqIconState? GetHqIconState(AtkComponentListItemRenderer* renderer)
    {
        var n = GetRendererNodeById(renderer, NodePaths.HqIconNodeId);
        if (n == null)
            return null;

        return new HqIconState(
            Visible: n->IsVisible(),
            Flags: n->NodeFlags,
            DrawFlags: n->DrawFlags,
            Alpha: n->Color.A);
    }

    public HqIconState? DumpHqIconState(AtkComponentListItemRenderer* renderer, int rowIndex, Action<string> log)
    {
        var state = GetHqIconState(renderer);
        if (!state.HasValue)
        {
            log($"[HQ] row {rowIndex}: node {NodePaths.HqIconNodeId} not found");
            return null;
        }

        var value = state.Value;
        log($"[HQ] row {rowIndex}: visible={value.Visible} flags=0x{value.Flags:X} draw=0x{value.DrawFlags:X} alpha={value.Alpha}");
        return value;
    }

    public string GetItemSearchResultErrorMessage()
    {
        // Message is empty/null when there's no error/status.
        var raw = ReadAddonTextNode("ItemSearchResult", NodePaths.ItemSearchResult_ErrorMessageNodeId);
        return NormalizeStatusText(raw);
    }

    /// <summary>
    /// Normalizes the ItemSearchResult status string and classifies it into one of the well-known outcomes.
    /// </summary>
    public ItemSearchResultStatus GetItemSearchResultStatus(out string message)
    {
        message = GetItemSearchResultErrorMessage();
        if (string.IsNullOrWhiteSpace(message))
            return ItemSearchResultStatus.None;

        var statuses = ItemSearchResultStatusStringsLoader.Value;
        if (statuses.NoItems.Contains(message))
            return ItemSearchResultStatus.NoItemsFound;

        if (statuses.PleaseWait.Contains(message))
            return ItemSearchResultStatus.PleaseWaitRetry;

        return ItemSearchResultStatus.OtherMessage;
    }

    private static string NormalizeStatusText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        var lastWasSpace = false;

        foreach (var ch in raw)
        {
            if (char.IsControl(ch))
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length == 0 || lastWasSpace)
                    continue;

                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    #endregion

    #region ItemSearchResult status string helpers

    private static readonly string[] DefaultNoItemsStatusFallbacks =
    {
        "No items found.",
        "No items found",
    };

    private static readonly string[] DefaultPleaseWaitStatusFallbacks =
    {
        "Please wait and try your search again.",
        "Please wait and try your search again",
    };

    private static ItemSearchResultStatusStrings LoadItemSearchResultStatusStrings()
    {
        var noItems = new HashSet<string>(StringComparer.Ordinal);
        var pleaseWait = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fallback in DefaultNoItemsStatusFallbacks)
            AddNormalizedStatus(noItems, fallback);

        foreach (var fallback in DefaultPleaseWaitStatusFallbacks)
            AddNormalizedStatus(pleaseWait, fallback);

        try
        {
            var sheet = Svc.Data.GetExcelSheet<Addon>();
            if (sheet != null)
            {
                AddAddonRowString(noItems, sheet.GetRowOrDefault(1959));
                AddAddonRowString(pleaseWait, sheet.GetRowOrDefault(1997));
            }
        }
        catch
        {
            // Dalamud data unavailable; fall back to English literals.
        }

        return new ItemSearchResultStatusStrings(noItems, pleaseWait);
    }

    private static void AddAddonRowString(HashSet<string> bucket, Addon? row)
    {
        if (row == null)
            return;

        AddNormalizedStatus(bucket, row.Value.Text.ToString());
    }

    private static void AddNormalizedStatus(HashSet<string> bucket, string? raw)
    {
        var normalized = NormalizeStatusText(raw);
        if (!string.IsNullOrEmpty(normalized))
            bucket.Add(normalized);
    }

    private sealed class ItemSearchResultStatusStrings
    {
        public ItemSearchResultStatusStrings(HashSet<string> noItems, HashSet<string> pleaseWait)
        {
            NoItems = noItems;
            PleaseWait = pleaseWait;
        }

        public HashSet<string> NoItems { get; }

        public HashSet<string> PleaseWait { get; }
    }

    #endregion

    #region Inventory (new listings)

    /// <summary>
    /// Does a single pass over the four bag containers and returns the first slot plus total count for that item/quality.
    /// </summary>
    public InventoryLookupResult FindItemInInventory(uint baseItemId, bool isHq, HashSet<long>? excludedSlotKeys = null)
    {
        var container = 0;
        var slot = 0;
        var totalCount = 0;

        if (baseItemId == 0)
            return new InventoryLookupResult(InventoryLookupStatus.NotFound, 0, 0, 0, 0);

        var itemRow = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        if (!itemRow.HasValue)
            return new InventoryLookupResult(InventoryLookupStatus.NotFound, 0, 0, 0, 0);

        var inv = InventoryManager.Instance();
        if (inv == null)
            return new InventoryLookupResult(InventoryLookupStatus.NotFound, 0, 0, 0, 0);

        var foundSellable = false;
        var foundMatchingUnsellable = false;
        uint firstUnsellableSpiritbondOrCollectability = 0;

        foreach (var type in SellScanContainers)
        {
            var cont = inv->GetInventoryContainer(type);
            if (cont == null || !cont->IsLoaded)
                continue;

            for (var i = 0; i < cont->Size; i++)
            {
                var s = cont->GetInventorySlot(i);
                if (s == null) continue;
                if (s->Quantity <= 0) continue;

                // Base item match (must compare BASE id, not the full ItemId which differs for HQ).
                var slotBaseId = s->GetBaseItemId();
                if (slotBaseId != baseItemId) continue;

                // Quality match
                var slotIsHq = IsSlotHq(s);
                if (slotIsHq != isHq) continue;

                if (!IsInventorySlotSellable(s, itemRow))
                {
                    if (!foundMatchingUnsellable)
                    {
                        foundMatchingUnsellable = true;
                        firstUnsellableSpiritbondOrCollectability = s->GetSpiritbondOrCollectability();
                    }

                    continue;
                }

                totalCount += (int)s->Quantity;
                if (totalCount >= 999999)
                    totalCount = 999999;

                var slotKey = ((long)(uint)type << 32) | (uint)i;
                if (!foundSellable && (excludedSlotKeys == null || !excludedSlotKeys.Contains(slotKey)))
                {
                    container = (int)type;
                    slot = i;
                    foundSellable = true;
                }
            }
        }

        if (foundSellable)
            return new InventoryLookupResult(InventoryLookupStatus.FoundSellable, container, slot, totalCount, 0);

        if (foundMatchingUnsellable)
            return new InventoryLookupResult(InventoryLookupStatus.FoundOnlyUnsellable, 0, 0, 0,
                firstUnsellableSpiritbondOrCollectability);

        return new InventoryLookupResult(InventoryLookupStatus.NotFound, 0, 0, 0, 0);
    }

    public bool TryGetInventorySlot(InventoryType containerType, int slotIndex, out InventoryItem* inventorySlot)
    {
        inventorySlot = null;

        if (slotIndex < 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        var container = inv->GetInventoryContainer(containerType);
        if (container == null || !container->IsLoaded || slotIndex >= container->Size)
            return false;

        inventorySlot = container->GetInventorySlot(slotIndex);
        return inventorySlot != null;
    }

    public static bool IsInventorySlotSellable(InventoryItem* inventorySlot, Item? itemRow)
    {
        if (inventorySlot == null)
            return false;

        if (!itemRow.HasValue)
            return false;

        if (itemRow.Value.IsUntradable)
            return false;

        return inventorySlot->IsCollectable() || inventorySlot->GetSpiritbondOrCollectability() == 0;
    }

    private static bool IsSlotHq(FFXIVClientStructs.FFXIV.Client.Game.InventoryItem* s)
    {
        return s->IsHighQuality();
    }

    /// <summary>
    /// Fires the InventoryGrid callback that opens RetainerSell for the given container/slot when the grid is visible.
    /// </summary>
    public bool TryOpenRetainerSellFromInventory(int container, int slot)
    {
        var unit = ResolveInventoryGridUnit();
        if (unit == null)
            return false;

        // Observed callback signature (InventoryGrid):
        // Callback.Fire(unit, 15, container, slot)
        Callback.Fire(unit, updateState: true, 15, container, slot);
        return true;
    }

    public void DumpInventoryGridState(uint itemId, Action<string> log)
    {
        var itemRow = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        if (!itemRow.HasValue)
        {
            log($"[INV][Grid] Item sheet row not found for itemId={itemId}.");
            return;
        }

        var targetLocations = new List<(InventoryType Container, int Slot)>();
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var containerType in SellScanContainers)
            {
                var container = inventory->GetInventoryContainer(containerType);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot != null && slot->Quantity > 0 && slot->GetBaseItemId() == itemId)
                        targetLocations.Add((containerType, slotIndex));
                }
            }
        }

        log($"[INV][Grid] Target itemId={itemId} itemSheetIconId={itemRow.Value.Icon} locations={string.Join(", ", targetLocations.Select(x => $"{x.Container}:{x.Slot}"))}");

        if (targetLocations.Count == 0)
        {
            log($"[INV][Grid] No loaded inventory location found for itemId={itemId}.");
            return;
        }

        var rendererCount = 0;
        foreach (var location in targetLocations)
        {
            foreach (var addonName in GetInventoryGridAddonNames(location.Container))
            {
                for (var addonIndex = 1; addonIndex <= 10; addonIndex++)
                {
                    var addon = _gui.GetAddonByName(addonName, addonIndex);
                    if (addon.IsNull)
                        continue;

                    var unit = (AtkUnitBase*)addon.Address;
                    if (unit == null || !unit->IsVisible)
                        continue;

                    var grid = (AddonInventoryGrid*)unit;
                    var slotIndex = location.Slot;
                    if (slotIndex < 0 || slotIndex >= 35)
                        continue;

                    var dragDrop = grid->Slots[slotIndex].Value;
                    if (dragDrop == null)
                    {
                        log($"[INV][Grid] target={location.Container}:{location.Slot} addon={addonName} index={addonIndex} slot={slotIndex} component=null");
                        continue;
                    }

                    rendererCount++;
                    var icon = dragDrop->AtkComponentIcon;
                    var iconImage = icon == null ? null : icon->IconImage;
                    var ownerNode = dragDrop->AtkResNode;

                    var ownerState = ownerNode == null
                        ? "owner=null"
                        : $"ownerVisible={ownerNode->IsVisible()} ownerFlags=0x{ownerNode->NodeFlags:X} ownerDraw=0x{ownerNode->DrawFlags:X} ownerAlpha={ownerNode->Color.A}";
                    var iconState = icon == null
                        ? "icon=null"
                        : $"iconId={icon->IconId} iconFlags=0x{icon->Flags:X}";
                    var imageState = iconImage == null
                        ? "image=null"
                        : $"imageVisible={iconImage->IsVisible()} imageFlags=0x{iconImage->NodeFlags:X} imageDraw=0x{iconImage->DrawFlags:X} imageColor=({iconImage->Color.R},{iconImage->Color.G},{iconImage->Color.B},{iconImage->Color.A})";

                    log($"[INV][Grid] target={location.Container}:{location.Slot} addon={addonName} index={addonIndex} slot={slotIndex} componentFlags=0x{dragDrop->ComponentFlags:X} acceptedType={dragDrop->AcceptedType} visibilityFlags={dragDrop->VisibilityFlags} inputMode={dragDrop->InputMode} dragFlags=0x{dragDrop->Flags:X}({dragDrop->Flags}) {iconState} {imageState} {ownerState}");
                }
            }
        }

        log(rendererCount == 0
            ? $"[INV][Grid] No visible mapped renderer found for itemId={itemId}."
            : $"[INV][Grid] Found {rendererCount} visible mapped renderer(s) for itemId={itemId}.");
    }

    private static string[] GetInventoryGridAddonNames(InventoryType containerType)
        => containerType switch
        {
            InventoryType.Inventory1 => new[] { "InventoryGrid0" },
            InventoryType.Inventory2 => new[] { "InventoryGrid1" },
            InventoryType.Inventory3 => new[] { "InventoryGrid0E" },
            InventoryType.Inventory4 => new[] { "InventoryGrid1E" },
            InventoryType.Crystals => new[] { "InventoryGrid2E", "InventoryGrid3E" },
            _ => Array.Empty<string>(),
        };

    private AtkUnitBase* ResolveInventoryGridUnit()
    {
        foreach (var addonName in InventoryGridAddonNames)
        {
            var unit = FindVisibleInventoryGrid(addonName);
            if (unit != null)
                return unit;
        }

        return null;
    }

    private AtkUnitBase* FindVisibleInventoryGrid(string addonName)
    {
        const int maxIndexSearch = 10;
        var idx = FindVisibleAddonIndex(addonName, maxIndexSearch);
        if (idx < 0)
            return null;

        var addon = _gui.GetAddonByName(addonName, idx);
        if (addon.IsNull)
            return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsVisible)
            return null;

        return unit;
    }

    #endregion

    #region RetainerList

    /// <summary>
    /// Returns the visible RetainerList component so callers can count or read renderer text nodes.
    /// </summary>
    public AtkComponentList* GetRetainerList()
    {
        var unit = GetVisibleUnitBase("RetainerList", 1);
        if (unit == null) return null;

        var n = FindNodeById(&unit->UldManager, NodePaths.RetainerListNodeId);
        if (n == null) return null;

        var compNode = (AtkComponentNode*)n;
        var comp = compNode->Component;
        return comp != null ? (AtkComponentList*)comp : null;
    }

    #endregion

    #region RetainerSell (item name + asking price)

    public string? ReadRetainerSellItemNameRaw()
        => ReadAddonTextNode("RetainerSell", NodePaths.RetainerSell_ItemNameNodeId);

    // Back-compat shim (existing callers).
    public string? ReadRetainerSellItemName()
        => ReadRetainerSellItemNameRaw();
    public string SanitizeRetainerSellItemName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var allowExtendedCharacters = IsJapaneseClient;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (IsAllowedRetainerSellCharacter(ch, allowExtendedCharacters))
                sb.Append(ch);
        }

        var s = sb.ToString().Trim();

        // Known prefix leak: "H'I(" before the real name, no closing ')'.
        var openParen = s.IndexOf('(');
        if (openParen >= 0 && openParen <= 6 && s.IndexOf(')', openParen + 1) < 0)
            s = s[(openParen + 1)..].TrimStart();

        // Prefix leak variant: "H%I&Name..." (and similar).
        // If there's an '&' very early (before the first space), assume payload prefix and cut after '&'.
        s = StripEarlyDelimiterPrefix(s);

        // Tail artifacts (IH / IH', etc.).
        s = StripRetainerSellPayloadArtifacts(s);

        // Normalize whitespace.
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static bool IsAllowedRetainerSellCharacter(char ch, bool allowExtendedCharacters)
    {
        if (ch == RetainerSell_HqGlyphChar)
            return true;

        if (char.IsControl(ch))
            return false;

        if (!allowExtendedCharacters)
            return ch >= 0x20 && ch <= 0x7E;

        return true;
    }

    private static string StripEarlyDelimiterPrefix(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        var firstSpace = s.IndexOf(' ');
        if (firstSpace < 0) firstSpace = s.Length;

        var amp = s.IndexOf('&');
        if (amp >= 0 && amp < firstSpace && amp <= 8)
            return s[(amp + 1)..].TrimStart();

        return s;
    }

    private static string StripRetainerSellPayloadArtifacts(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        s = s.Trim();

        if (s.EndsWith("IH'", StringComparison.Ordinal))
            s = s[..^3].TrimEnd();
        else if (s.EndsWith("IH", StringComparison.Ordinal))
            s = s[..^2].TrimEnd();

        while (s.Length > 0)
        {
            var last = s[^1];
            if (last == '\'' || last == ')' || last == '(')
                s = s[..^1].TrimEnd();
            else
                break;
        }

        return s;
    }


    public bool RetainerSellNameContainsHqGlyph(string? raw)
        => !string.IsNullOrEmpty(raw) && raw.IndexOf(RetainerSell_HqGlyphChar) >= 0;

    public bool IsRetainerSellItemHq()
        => RetainerSellNameContainsHqGlyph(ReadRetainerSellItemNameRaw());

    public string GetRetainerSellItemNameDisplay(bool stripHqGlyph = true)
    {
        var raw = ReadRetainerSellItemNameRaw();
        var cleaned = SanitizeRetainerSellItemName(raw);

        if (stripHqGlyph)
            cleaned = cleaned.Replace(RetainerSell_HqGlyphChar.ToString(), string.Empty).Trim();

        return cleaned;
    }

    public string? ReadRetainerSellAskingPriceText()
    {
        var comp = GetAddonComponent("RetainerSell", NodePaths.RetainerSell_AskingPriceNumericNodeId);
        if (comp == null) return null;

        return ReadComponentTextNode(comp, NodePaths.RetainerSell_AskingPriceTextNodeId);
    }

    public int? ReadRetainerSellAskingPrice()
        => ParseGil(ReadRetainerSellAskingPriceText());

    #endregion

    #region RetainerSellList (listed count)

    public string? ReadRetainerSellListCountText()
        => ReadAddonTextNode("RetainerSellList", NodePaths.RetainerSellList_CountNodeId);

    public int? ReadRetainerSellListListedCount()
        => ParseListedCount(ReadRetainerSellListCountText());

    public (int? listed, int? max) ReadRetainerSellListListedAndMax()
    {
        var raw = ReadRetainerSellListCountText();
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        // Parse "N/20" (max may change later).
        int left = 0, right = 0;
        bool anyLeft = false, anyRight = false;
        bool onRight = false;

        foreach (var ch in raw)
        {
            if (ch == '/') { onRight = true; continue; }

            if (ch >= '0' && ch <= '9')
            {
                if (!onRight)
                {
                    anyLeft = true;
                    left = (left * 10) + (ch - '0');
                }
                else
                {
                    anyRight = true;
                    right = (right * 10) + (ch - '0');
                }
            }
        }

        return (anyLeft ? left : null, anyRight ? right : null);
    }

    #endregion

    #region Parsing helpers

    public static int? ParseGil(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        long value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > int.MaxValue) return int.MaxValue;
            }
        }

        return any ? (int)value : null;
    }

    public static int? ParseListedCount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        int value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch == '/') break;

            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > 999) return 999;
            }
        }

        return any ? value : null;
    }

    #endregion

    #region Debug helpers

    public void DumpAddonNodeList(string addonName, Action<string> log)
    {
        var addon = _gui.GetAddonByName(addonName, 1);
        if (addon.IsNull)
        {
            log("[NodeList] addon not open");
            return;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null)
        {
            log("[NodeList] unit null");
            return;
        }

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;

        log($"[NodeList] {addonName} NodeListCount={count}");

        if (nl == null || count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;

            log($"[NodeList] i={i} id={n->NodeId} type={n->Type} x={n->X} y={n->Y}");
        }
    }

    public static string DumpNonAsciiCodepoints(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "<null/empty>";

        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (ch < 0x20 || ch > 0x7E)
                sb.Append($"U+{(int)ch:X4} ");
        }

        return sb.Length == 0 ? "<none>" : sb.ToString().Trim();
    }

    #endregion
}
