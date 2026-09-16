using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Common.Lua;

using RetainerRepricer.Ui;

namespace RetainerRepricer.Windows;

/// <summary>
/// Presents configuration controls for enabled retainers, market settings, and sell list management.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    #region Fields (plugin + config)

    private readonly Plugin _plugin;
    private readonly Configuration _config;

    #endregion

    #region UI-only state (not saved)

    private bool _confirmClearSellList;
    private string _sellListSearch = string.Empty;
    private bool _showAdvancedResetConfirmation;
    private DateTime _advancedResetConfirmationUtc;
    private static readonly Configuration DefaultConfig = new();
    private static readonly ContextMenuQuickAddModifier[] QuickAddModifierOptions =
        Enum.GetValues<ContextMenuQuickAddModifier>();
    private static readonly ContextMenuQuickListModifier[] QuickListModifierOptions =
        Enum.GetValues<ContextMenuQuickListModifier>();
    private bool _sellListTabWasOpenLastFrame;
    private bool _forceCollapseSellListRows;
    private bool _perRetainerCapsLastEnabled;
    private ConfigTabFocus? _pendingTabFocus;

    private enum ConfigTabFocus
    {
        SellList,
        Retainers,
        Settings,
        Advanced,
    }

    #endregion

    #region Lifecycle

    public ConfigWindow(Plugin plugin)
        : base("Retainer Repricer Configuration##Config")
    {
        _plugin = plugin;
        _config = plugin.Configuration;
        _perRetainerCapsLastEnabled = _config.EnablePerRetainerCaps;

        Flags = ImGuiWindowFlags.None;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(360, 180),
            MaximumSize = new(800, 600),
        };
    }

    public void Dispose()
    {
        // Nothing to dispose.
    }

    public void OpenSellListTab()
    {
        _pendingTabFocus = ConfigTabFocus.SellList;
        _forceCollapseSellListRows = true;
        IsOpen = true;
    }

    public void OpenSettingsTab()
    {
        _pendingTabFocus = ConfigTabFocus.Settings;
        IsOpen = true;
    }

    #endregion

    #region Draw

    public override void Draw()
    {
        if (_perRetainerCapsLastEnabled != _config.EnablePerRetainerCaps)
        {
            if (_config.EnablePerRetainerCaps)
                _forceCollapseSellListRows = true;
            _perRetainerCapsLastEnabled = _config.EnablePerRetainerCaps;
        }

        if (!ImGui.BeginTabBar("##rr_cfg_tabs"))
            return;

        // Order: Sell List, Retainers, Settings, Advanced (optional).
        var sellTabFlags = ConsumeTabFocus(ConfigTabFocus.SellList);
        var sellTabOpen = ImGui.BeginTabItem("MB Sell List", sellTabFlags);
        if (sellTabOpen)
        {
            if (!_sellListTabWasOpenLastFrame)
                _forceCollapseSellListRows = true;
            DrawSellListTab();
            ImGui.EndTabItem();
        }
        _sellListTabWasOpenLastFrame = sellTabOpen;

        if (BeginFocusedTab("Retainers", ConfigTabFocus.Retainers))
        {
            DrawRetainersTab();
            ImGui.EndTabItem();
        }

        if (BeginFocusedTab("Settings", ConfigTabFocus.Settings))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        if (_config.ShowAdvancedSettingsTab && BeginFocusedTab("Advanced", ConfigTabFocus.Advanced))
        {
            DrawAdvancedTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    #endregion

    #region Tabs

    private void DrawSellListTab()
    {
        DrawSellListSearch();

        var list = _config.SellList;
        var listCount = list?.Count ?? 0;

        DrawSellListActionRow(listCount);

        if (list == null || listCount == 0)
        {
            ImGui.TextDisabled("No items in sell list.");
            _confirmClearSellList = false;
            return;
        }

        ImGui.Separator();
        ImGui.Spacing();

        var rows = BuildFilteredSellListRows();
        var retainerNames = _config.GetAllRetainerNames()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ImGui.TextDisabled($"{rows.Count} item(s)");

        DrawSellListTable(rows, list.Count, retainerNames, _config.EnablePerRetainerCaps, _forceCollapseSellListRows);
        _forceCollapseSellListRows = false;
    }

    private void DrawSellListActionRow(int totalItemCount)
    {
        ImGui.Spacing();

        var startPos = ImGui.GetCursorPos();
        DrawSellListClearButtonInline(totalItemCount > 0);
        var afterClearY = ImGui.GetCursorPosY();

        var buttonWidth = 90f;
        var rightEdge = ImGui.GetWindowContentRegionMax().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var totalButtonsWidth = (buttonWidth * 2f) + spacing;
        var buttonX = Math.Max(ImGui.GetCursorPosX(), rightEdge - totalButtonsWidth);

        ImGui.SetCursorPos(new System.Numerics.Vector2(buttonX, startPos.Y));
        var statusBottom = DrawSellListUtilityButtons(buttonWidth);

        ImGui.SetCursorPosY(Math.Max(afterClearY, statusBottom));
    }

    private void DrawRetainersTab()
    {
        DrawRetainerEnableList();
    }

    private void DrawSettingsTab()
    {
        DrawPluginSettings();
        DrawPerRetainerToggle();
        DrawAutoPruneSettings();
        DrawPricingGateSettings();
        DrawAdvancedTabToggle();
        DrawOpenLogsButton();
    }

    private void DrawAdvancedTab()
    {
        DrawAdvancedOverlaySection();
        ImGui.Spacing();
        DrawAdvancedPricingSection();
        ImGui.Spacing();
        DrawAdvancedAutomationSection();
        ImGui.Spacing();
        DrawAdvancedMarketTimingSection();
        ImGui.Spacing();
        DrawAdvancedHqTimingSection();
        ImGui.Spacing();
        DrawAdvancedSmartSortSection();
        ImGui.Spacing();
        DrawAdvancedResetControls();
    }

    #endregion

    #region Sell list tab

    private void DrawSellListSearch()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Search");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sell_search", "Filter by item name...", ref _sellListSearch, 128);
        ImGui.Spacing();
    }

    private void DrawSellListClearButtonInline(bool hasItems)
    {
        if (!hasItems)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Clear list");
            ImGui.EndDisabled();
            return;
        }

        // Two-step confirm to avoid fat-finger clears.
        if (!_confirmClearSellList)
        {
            if (ImGui.Button("Clear list"))
                _confirmClearSellList = true;
            return;
        }

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f), "Clear all items?");
        if (ImGui.Button("Confirm clear"))
        {
            _config.ClearSellList();
            SaveConfig();
            _confirmClearSellList = false;
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            _confirmClearSellList = false;
    }

    private float DrawSellListUtilityButtons(float buttonWidth)
    {
        var pruneBottom = DrawPruneNowButton(buttonWidth);
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        var sortBottom = DrawSmartSortButton(buttonWidth);
        return Math.Max(pruneBottom, sortBottom);
    }

    private float DrawPruneNowButton(float buttonWidth)
    {
        if (ImGui.Button("Prune", new System.Numerics.Vector2(buttonWidth, 0f)))
            _ = _plugin.RunInventoryPruneManual("manual_sell_list_button");

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, "Removes sell items that are nowhere in your bags. Preserved entries are skipped.");
        }

        return ImGui.GetCursorPosY();
    }

    private float DrawSmartSortButton(float buttonWidth)
    {
        if (!_config.UseUniversalisApi)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Smart Sort", new System.Numerics.Vector2(buttonWidth, 0f));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                TooltipHelper.Show(_config, "Enable Universalis averages in Settings to unlock smart sorting.");
            ImGui.EndDisabled();
            return ImGui.GetCursorPosY();
        }

        var enabled = _plugin.SmartSortEnabled;
        var sorting = _plugin.SmartSortIsSorting;

        ImGui.BeginDisabled(!enabled || sorting);
        if (ImGui.Button("Smart Sort", new System.Numerics.Vector2(buttonWidth, 0f)))
            _ = _plugin.RequestSmartSortAsync("manual_button", force: true);
        ImGui.EndDisabled();

        if (!enabled)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                TooltipHelper.Show(_config, "Enable smart sort in Settings to use this feature.");
        }
        else if (sorting)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                TooltipHelper.Show(_config, "Smart sort is running...");
        }

        return ImGui.GetCursorPosY();
    }

    private System.Collections.Generic.List<Configuration.SellListEntry> BuildFilteredSellListRows()
    {
        var ordered = _config.GetSellListOrdered();

        var filter = (_sellListSearch ?? string.Empty).Trim();
        var filterLower = filter.ToLowerInvariant();
        var hasFilter = filterLower.Length > 0;

        static bool LooksLikeId(string s) => uint.TryParse(s, out _);

        uint idFilter = 0;
        var filterIsId = hasFilter && LooksLikeId(filterLower);
        if (filterIsId) uint.TryParse(filterLower, out idFilter);

        return ordered
            .Where(e =>
            {
                if (!hasFilter) return true;

                if (filterIsId)
                    return e.ItemId == idFilter;

                var name = e.Name ?? string.Empty;
                return name.ToLowerInvariant().Contains(filterLower);
            })
            .ToList();
    }

    private void DrawSellListTable(
        System.Collections.Generic.IReadOnlyList<Configuration.SellListEntry> rows,
        int totalItemCount,
        System.Collections.Generic.IReadOnlyList<string> retainerNames,
        bool perRetainerEnabled,
        bool forceCollapseRows)
    {
        var smartSortActive = _plugin.SmartSortEnabled;
        if (!ImGui.BeginTable(
                "##sell_list_table",
                5,
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.BordersV |
                ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Min Inv", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        DrawHeaderLabel("Priority", center: true);
        ImGui.TableSetColumnIndex(1);
        DrawHeaderLabel("Item", center: false);
        ImGui.TableSetColumnIndex(2);
        DrawHeaderLabel("Min Inv", center: true);
        ImGui.TableSetColumnIndex(3);
        DrawHeaderLabel("Keep", center: true);
        ImGui.TableSetColumnIndex(4);
        DrawHeaderLabel(string.Empty, center: true);

        (uint itemId, bool isHq)? removeKey = null;
        var priorityChanges = new System.Collections.Generic.List<(uint itemId, bool isHq, int newOrder)>();
        var rowIndex = 0;

        foreach (var e in rows)
        {
            ImGui.TableNextRow();

            // Use a stable ID scope per row so InputInt doesn’t collide.
            ImGui.PushID($"{e.ItemId}:{(e.IsHq ? 1 : 0)}");

            // Column 0: Priority input
            ImGui.TableSetColumnIndex(0);
            var priorityValue = e.SortOrder > 0 ? e.SortOrder : (rowIndex + 1);
            var priorityColumnWidth = ImGui.GetColumnWidth();
            var priorityInputWidth = Math.Clamp(priorityColumnWidth - 8f, 30f, 70f);
            BeginCenteredElement(priorityInputWidth);
            ImGui.SetNextItemWidth(priorityInputWidth);
            var priorityTooltip = smartSortActive
                ? "Priority is managed by smart sorting. Current order shown for reference."
                : "Lower numbers run first. Higher numbers push items later in the queue.";

            if (smartSortActive)
                ImGui.BeginDisabled();

            ImGui.InputInt(
                $"##priority_{e.ItemId}_{(e.IsHq ? 1 : 0)}",
                ref priorityValue,
                0,
                0);

            if (smartSortActive)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    TooltipHelper.Show(_config, priorityTooltip);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                var clamped = Math.Clamp(priorityValue, 1, Math.Max(1, totalItemCount));
                if (!smartSortActive && clamped != e.SortOrder)
                    priorityChanges.Add((e.ItemId, e.IsHq, clamped));
            }

            if (!smartSortActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                TooltipHelper.Show(_config, priorityTooltip);

            // Column 0: Item name
            ImGui.TableSetColumnIndex(1);
            var displayName = string.IsNullOrWhiteSpace(e.Name) ? $"(Unknown) [{e.ItemId}]" : e.Name;
            if (!perRetainerEnabled)
            {
                var disabledColor = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
                var treeFlags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.NoTreePushOnOpen;
                ImGui.PushStyleColor(ImGuiCol.Text, disabledColor);
                ImGui.TreeNodeEx($"##caps_disabled_{rowIndex}", treeFlags);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    TooltipHelper.Show(_config, "Enable per-retainer sell limits in Settings to edit this.");

                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.TextUnformatted(displayName);
            }
            else
            {
                if (forceCollapseRows)
                    ImGui.SetNextItemOpen(false, ImGuiCond.Always);
                var treeFlags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
                var open = ImGui.TreeNodeEx($"{displayName}##caps", treeFlags);
                if (ImGui.IsItemHovered())
                    TooltipHelper.Show(_config, "Expand to set per-retainer limits.");

                if (open)
                {
                    DrawRetainerCapEditor(e, retainerNames);
                    ImGui.TreePop();
                }
            }

            // Column 1: Min inventory editor
            ImGui.TableSetColumnIndex(2);

            var s = e.MinCountToSell.ToString();
            var minColumnWidth = ImGui.GetColumnWidth();
            var minInputWidth = Math.Clamp(minColumnWidth - 8f, 30f, 70f);
            var hasRetainers = retainerNames.Count > 0;
            var anyStacksCustom = perRetainerEnabled && hasRetainers && AnyRetainerUsesCustomStack(e, retainerNames);

            if (anyStacksCustom)
            {
                BeginCenteredElement(minInputWidth);
                ImGui.TextDisabled("—");
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    TooltipHelper.Show(_config, "Per-retainer stack sizes are active. Set all stacks to 0 to edit the minimum inventory threshold.");
            }
            else
            {
                BeginCenteredElement(minInputWidth);
                ImGui.SetNextItemWidth(minInputWidth);

                if (ImGui.InputText($"##minsell_{e.ItemId}_{(e.IsHq ? 1 : 0)}", ref s, 8, ImGuiInputTextFlags.CharsDecimal))
                {
                    if (int.TryParse(s, out var parsed))
                    {
                        if (parsed < 1) parsed = 1;
                        if (parsed > 999) parsed = 999;

                        if (parsed != e.MinCountToSell)
                        {
                            e.MinCountToSell = parsed;
                            SaveConfig();
                        }
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    TooltipHelper.Show(_config, "Item will be listed when you have at least this many in inventory.");
            }

            // Column 3: Preserve toggle
            ImGui.TableSetColumnIndex(3);
            var preserveValue = e.PreserveFromAutoPrune;
            BeginCenteredElement(30f);
            if (ImGui.Checkbox("##preserve", ref preserveValue))
            {
                e.PreserveFromAutoPrune = preserveValue;
                Plugin.Log.Information("[RR][Config] Sell item preserve flag updated itemId={ItemId} HQ={IsHq} preserve={Preserve}", e.ItemId, e.IsHq, preserveValue);
                SaveConfig();
            }

            if (ImGui.IsItemHovered())
                TooltipHelper.Show(_config, "Keep this entry even when auto prune runs.");

            // Column 4: Remove button
            ImGui.TableSetColumnIndex(4);
            var trashIcon = FontAwesomeIcon.Trash.ToIconString();
            ImGui.PushFont(UiBuilder.IconFont);
            var removeWidth = ImGui.CalcTextSize(trashIcon).X + (ImGui.GetStyle().FramePadding.X * 2f);
            BeginCenteredElement(removeWidth);
            if (ImGui.Button($"{trashIcon}##sell_{e.ItemId}_{(e.IsHq ? 1 : 0)}", new System.Numerics.Vector2(removeWidth, 0f)))
                removeKey = (e.ItemId, e.IsHq);
            ImGui.PopFont();

            if (ImGui.IsItemHovered())
                TooltipHelper.Show(_config, "Remove from sell list");

            ImGui.PopID();
            rowIndex++;
        }

        ImGui.EndTable();

        if (priorityChanges.Count > 0)
        {
            var changed = false;
            foreach (var change in priorityChanges)
                changed |= _config.TrySetSellItemOrder(change.itemId, change.isHq, change.newOrder);

            if (changed)
            {
                Plugin.Log.Information("[RR][Config] Sell List priority changed.");
                SaveConfig();
            }
        }

        if (removeKey.HasValue)
        {
            _config.RemoveSellItem(removeKey.Value.itemId, removeKey.Value.isHq);
            Plugin.Log.Information("[RR][Config] Removed item {ItemId} (HQ={IsHq}) from Sell List", removeKey.Value.itemId, removeKey.Value.isHq);
            SaveConfig();
        }
    }

    private void DrawRetainerCapEditor(Configuration.SellListEntry entry, System.Collections.Generic.IReadOnlyList<string> retainerNames)
    {
        ImGui.PushID($"retainer_caps_{entry.ItemId}_{(entry.IsHq ? 1 : 0)}");
        ImGui.Indent();
        ImGui.Spacing();

        if (retainerNames.Count == 0)
        {
            ImGui.TextDisabled("Open the Retainer List to capture names.");
            ImGui.Unindent();
            ImGui.PopID();
            return;
        }

        var stackCap = _plugin.GetStackSizeCap(entry.ItemId);

        if (ImGui.BeginTable("##retainer_caps_table", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Sell limit", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableSetupColumn("Stack size", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Retainer");
            ImGui.TableSetColumnIndex(1);
            DrawHeaderLabel("Sell limit", center: true);
            ImGui.TableSetColumnIndex(2);
            DrawHeaderLabel("Stack size", center: true);

            for (var i = 0; i < retainerNames.Count; i++)
            {
                var name = retainerNames[i];
                var label = string.IsNullOrWhiteSpace(name) ? "(Unknown retainer)" : name;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(label);

                ImGui.TableSetColumnIndex(1);
                var capValue = entry.GetRetainerCapOrDefault(name);
                var editorValue = capValue;
                ImGui.PushID(i);
                BeginCenteredElement(60f);
                ImGui.SetNextItemWidth(60f);
                if (ImGui.InputInt("##cap", ref editorValue))
                {
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        var clamped = Math.Clamp(editorValue, 0, Configuration.RetainerCapMax);
                        if (clamped != capValue)
                        {
                            entry.SetRetainerCap(name, clamped);
                            Plugin.Log.Information("[RR][Config] Retainer cap updated itemId={ItemId} HQ={IsHq} retainer={Retainer} cap={Cap}",
                                entry.ItemId, entry.IsHq, GetRetainerLabelForLog(i), clamped);
                            SaveConfig();
                        }
                    }
                }
                if (ImGui.IsItemHovered())
                    TooltipHelper.Show(_config, "0 - don't list\n1 - default listing\nN - post N listings");

                ImGui.TableSetColumnIndex(2);
                var stackValue = entry.GetRetainerStackSize(name);
                var stackEditorValue = stackValue;
                var stackDisabled = capValue <= 0;
                if (stackDisabled)
                    ImGui.BeginDisabled();

                BeginCenteredElement(60f);
                ImGui.SetNextItemWidth(60f);
                if (ImGui.InputInt("##stack", ref stackEditorValue))
                {
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        var clampedStack = Math.Clamp(stackEditorValue, 0, stackCap);
                        if (clampedStack != stackValue)
                        {
                            entry.SetRetainerStackSize(name, clampedStack, stackCap);
                            Plugin.Log.Information("[RR][Config] Retainer stack size updated itemId={ItemId} HQ={IsHq} retainer={Retainer} stack={Stack}",
                                entry.ItemId, entry.IsHq, GetRetainerLabelForLog(i), clampedStack);
                            SaveConfig();
                        }
                    }
                }

                if (stackDisabled)
                {
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        TooltipHelper.Show(_config, "Set the sell limit above 0 to edit stack sizes.");
                    ImGui.EndDisabled();
                }
                else if (ImGui.IsItemHovered())
                {
                    TooltipHelper.Show(_config, stackCap >= 1000
                        ? "0 = any amount; max = 9999."
                        : "0 = any amount; max = 99.");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Unindent();
        ImGui.PopID();
    }

    private static bool AnyRetainerUsesCustomStack(Configuration.SellListEntry entry, System.Collections.Generic.IReadOnlyList<string> retainerNames)
    {
        foreach (var name in retainerNames)
        {
            if (entry.GetRetainerStackSize(name) > 0)
                return true;
        }

        return false;
    }

    #endregion

    #region Retainers tab

    private void DrawRetainerEnableList()
    {
        var names = _config.GetAllRetainerNames()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            ImGui.TextUnformatted("Open the summoning bell retainer list, then reopen this config.");
            return;
        }

        ImGui.Spacing();

        DrawRetainerBulkButtons(names);

        ImGui.Spacing();

        DrawRetainerTable(names);
    }

    private void DrawRetainerBulkButtons(System.Collections.Generic.IReadOnlyList<string> names)
    {
        if (ImGui.Button("Enable all"))
        {
            var changed = false;
            foreach (var n in names)
            {
                if (!_config.IsRetainerEnabled(n))
                {
                    _config.SetRetainerEnabled(n, true);
                    changed = true;
                }
            }

            if (changed)
            {
                Plugin.Log.Information("[RR][Config] Enabled all {Count} retainers.", names.Count);
                SaveConfig();
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable all"))
        {
            var changed = false;
            foreach (var n in names)
            {
                if (_config.IsRetainerEnabled(n))
                {
                    _config.SetRetainerEnabled(n, false);
                    changed = true;
                }
            }

            if (changed)
            {
                Plugin.Log.Information("[RR][Config] Disabled all {Count} retainers.", names.Count);
                SaveConfig();
            }
        }
    }

    private void DrawRetainerTable(System.Collections.Generic.IReadOnlyList<string> names)
    {
        if (!ImGui.BeginTable(
                "##rr_retainers_table",
                4,
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.BordersOuter |
                ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Reprice", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("Sell", ImGuiTableColumnFlags.WidthFixed, 45f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        DrawHeaderLabel("Enable", center: true);
        ImGui.TableSetColumnIndex(1);
        DrawHeaderLabel("Retainer", center: false);
        ImGui.TableSetColumnIndex(2);
        DrawHeaderLabel("Reprice", center: true);
        ImGui.TableSetColumnIndex(3);
        DrawHeaderLabel("Sell", center: true);

        for (var row = 0; row < names.Count; row++)
        {
            var name = names[row];
            var behavior = _config.GetRetainerBehavior(name);
            ImGui.TableNextRow();
            ImGui.PushID(name);

            ImGui.TableSetColumnIndex(0);
            var enabled = behavior.Enabled;
            if (DrawCenteredCheckbox("##retainer_enable", ref enabled))
            {
                _config.SetRetainerEnabled(name, enabled);
                Plugin.Log.Information("[RR][Config] Retainer {Retainer} enabled={Enabled}", GetRetainerLabelForLog(row), enabled);
                SaveConfig();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                TooltipHelper.Show(_config, "Master toggle for this retainer. Disabling skips both repricing and selling.");
            }

            var disableScoped = !enabled;
            if (disableScoped)
                ImGui.BeginDisabled();

            ImGui.TableSetColumnIndex(1);
            if (string.IsNullOrWhiteSpace(name))
                ImGui.TextDisabled("(Unknown retainer)");
            else
                ImGui.TextUnformatted(name);

            ImGui.TableSetColumnIndex(2);
            var allowReprice = behavior.AllowReprice;
            if (DrawCenteredCheckbox("##retainer_reprice", ref allowReprice))
            {
                _config.SetRetainerRepriceEnabled(name, allowReprice);
                Plugin.Log.Information("[RR][Config] Retainer {Retainer} reprice={Reprice}", GetRetainerLabelForLog(row), allowReprice);
                SaveConfig();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                TooltipHelper.Show(_config, "Allows repricing existing listings for this retainer.");

            ImGui.TableSetColumnIndex(3);
            var allowSell = behavior.AllowSell;
            if (DrawCenteredCheckbox("##retainer_sell", ref allowSell))
            {
                _config.SetRetainerSellEnabled(name, allowSell);
                Plugin.Log.Information("[RR][Config] Retainer {Retainer} sell={Sell}", GetRetainerLabelForLog(row), allowSell);
                SaveConfig();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                TooltipHelper.Show(_config, "Allows creating new listings for this retainer.");

            if (disableScoped)
                ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static bool DrawCenteredCheckbox(string id, ref bool value)
    {
        var columnWidth = ImGui.GetColumnWidth();
        var frameHeight = ImGui.GetFrameHeight();
        var startPos = ImGui.GetCursorPos();
        var desiredX = startPos.X + Math.Max(0f, (columnWidth - frameHeight) * 0.5f);
        ImGui.SetCursorPosX(desiredX);
        return ImGui.Checkbox(id, ref value);
    }

    private static void DrawHeaderLabel(string label, bool center)
    {
        if (!center)
        {
            ImGui.TextUnformatted(label);
            return;
        }

        var columnWidth = ImGui.GetColumnWidth();
        var textWidth = ImGui.CalcTextSize(label).X;
        var startPos = ImGui.GetCursorPos();
        var desiredX = startPos.X + Math.Max(0f, (columnWidth - textWidth) * 0.5f);
        ImGui.SetCursorPosX(desiredX);
        ImGui.TextUnformatted(label);
    }

    private static void BeginCenteredElement(float elementWidth)
    {
        var columnWidth = ImGui.GetColumnWidth();
        var startPos = ImGui.GetCursorPos();
        var desiredX = startPos.X + Math.Max(0f, (columnWidth - elementWidth) * 0.5f);
        ImGui.SetCursorPosX(desiredX);
    }

    private ImGuiTabItemFlags ConsumeTabFocus(ConfigTabFocus tab)
    {
        if (_pendingTabFocus == tab)
        {
            _pendingTabFocus = null;
            return ImGuiTabItemFlags.SetSelected;
        }

        return ImGuiTabItemFlags.None;
    }

    private bool BeginFocusedTab(string label, ConfigTabFocus tab)
    {
        var flags = ConsumeTabFocus(tab);
        return ImGui.BeginTabItem(label, flags);
    }

    #endregion

    #region Plugin settings tab

    private void DrawPluginSettings()
    {
        var enabled = _config.PluginEnabled;
        if (ImGui.Checkbox("Enable plugin functionality", ref enabled))
        {
            _config.PluginEnabled = enabled;

            // If we disable the plugin, kill any active run immediately.
            if (!enabled && _plugin.IsRunning)
                _plugin.StopRun();

            Plugin.Log.Information("[RR][Config] Plugin enabled={Enabled}", enabled);
            SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config,
                "Master switch for the plugin.\n" +
                "Disabling stops any active run and turns off automation, overlay, and context menu."
            );
        }

        ImGui.Spacing();

        var autoRetainerEnabled = _config.EnableAutoRetainerIntegration;
        if (ImGui.Checkbox("Run after AutoRetainer processes a retainer", ref autoRetainerEnabled))
        {
            _config.EnableAutoRetainerIntegration = autoRetainerEnabled;
            Plugin.Log.Information("[RR][Config] AutoRetainer integration enabled={Enabled}", autoRetainerEnabled);
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config,
                "When AutoRetainer finishes a retainer, process that retainer's existing listings and eligible Sell List inventory before AutoRetainer continues."
            );
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_plugin.IsAutoRetainerAvailable ? "(AutoRetainer available)" : "(AutoRetainer not detected)");

        ImGui.Spacing();

        // Overlay toggle is separate from PluginEnabled, but PluginEnabled still gates display.
        var overlayEnabled = _config.OverlayEnabled;
        if (ImGui.Checkbox("Enable overlay window", ref overlayEnabled))
        {
            _config.OverlayEnabled = overlayEnabled;
            Plugin.Log.Information("[RR][Config] Overlay enabled={Enabled}", overlayEnabled);
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config,
                "Shows an overlay on the retainer list."
            );
        }

        ImGui.Spacing();

        var showContextPopup = _config.EnableContextMenuPopup;
        if (ImGui.Checkbox("Show context menu add popup", ref showContextPopup))
        {
            _config.EnableContextMenuPopup = showContextPopup;
            Plugin.Log.Information("[RR][Config] Context popup enabled={Enabled}", showContextPopup);
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config,
                "Uncheck to add context-menu items instantly with minimum inventory 1."
            );
        }

        ImGui.Spacing();

        DrawContextMenuQuickAddModifierSetting();

        ImGui.Spacing();

        DrawContextMenuQuickListModifierSetting();

        ImGui.Spacing();

        var showTooltips = _config.ShowTooltips;
        if (ImGui.Checkbox("Show UI tooltips", ref showTooltips))
        {
            _config.ShowTooltips = showTooltips;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Turns off all tooltip popups.");
        }

        ImGui.Spacing();

        var closeRetainerList = _config.CloseRetainerListAddon;
        if (ImGui.Checkbox("Close Retainer List when finished", ref closeRetainerList))
        {
            _config.CloseRetainerListAddon = closeRetainerList;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config,
                "Closes the retainer list when finished running.\n" +
                "Leave unchecked to keep it open."
            );
        }

        ImGui.Spacing();
    }

    private void DrawContextMenuQuickAddModifierSetting()
    {
        var modifier = SanitizeContextMenuQuickAddModifier(_config.ContextMenuQuickAddModifier);
        if (modifier != _config.ContextMenuQuickAddModifier)
        {
            _config.ContextMenuQuickAddModifier = modifier;
            SaveConfig();
        }

        var preview = GetContextMenuQuickAddModifierLabel(modifier);
        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo("Quick add modifier key", preview))
        {
            foreach (var option in QuickAddModifierOptions)
            {
                var isSelected = modifier == option;
                if (ImGui.Selectable(GetContextMenuQuickAddModifierLabel(option), isSelected))
                {
                    _config.ContextMenuQuickAddModifier = option;
                    Plugin.Log.Information("[RR][Config] Context quick add modifier={Modifier}", option);
                    SaveConfig();
                    modifier = option;
                }

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config,
                "Hold this modifier while right-clicking an inventory item to add it directly to the sell list."
            );
        }
    }

    private void DrawContextMenuQuickListModifierSetting()
    {
        var modifier = SanitizeContextMenuQuickListModifier(_config.ContextMenuQuickListModifier);
        if (modifier != _config.ContextMenuQuickListModifier)
        {
            _config.ContextMenuQuickListModifier = modifier;
            SaveConfig();
        }

        var preview = GetContextMenuQuickListModifierLabel(modifier);
        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo("Quick list modifier key", preview))
        {
            foreach (var option in QuickListModifierOptions)
            {
                var isSelected = modifier == option;
                if (ImGui.Selectable(GetContextMenuQuickListModifierLabel(option), isSelected))
                {
                    _config.ContextMenuQuickListModifier = option;
                    Plugin.Log.Information("[RR][Config] Context quick list modifier={Modifier}", option);
                    SaveConfig();
                    modifier = option;
                }

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config,
                "Hold this modifier while right-clicking an inventory item to list its full stack immediately."
            );
        }
    }

    private void DrawPerRetainerToggle()
    {
        var enabled = _config.EnablePerRetainerCaps;
        if (ImGui.Checkbox("Enable per-retainer sell limits", ref enabled))
        {
            _config.EnablePerRetainerCaps = enabled;
            Plugin.Log.Information("[RR][Config] Per-retainer sell limits enabled={Enabled}", enabled);
            SaveConfig();
            if (enabled)
                _forceCollapseSellListRows = true;
            _perRetainerCapsLastEnabled = enabled;
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Expand each Sell List row to set per-retainer limits.");
        }

        ImGui.Spacing();
    }

    private void DrawOverlayOffsetSettings()
    {
        ImGui.PushItemWidth(200f);

        var ox = _config.OverlayOffsetX;
        if (ImGui.SliderFloat("Offset X", ref ox, -1000f, 1000f, "%.0f"))
        {
            _config.OverlayOffsetX = ox;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig();
        }

        var oy = _config.OverlayOffsetY;
        if (ImGui.SliderFloat("Offset Y", ref oy, -1000f, 1000f, "%.0f"))
        {
            _config.OverlayOffsetY = oy;
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig();
        }

        ImGui.PopItemWidth();
    }

    private void DrawPricingGateSettings()
    {
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Universalis smart pricing");
        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, "Requires Universalis data to guard repricing and handle empty boards.");
        }

        var useUniversalis = _config.UseUniversalisApi;
        if (ImGui.Checkbox("Fetch Universalis averages", ref useUniversalis))
        {
            _config.UseUniversalisApi = useUniversalis;
            SaveConfig();
            _plugin.NotifySmartSortSettingChanged();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, 
                "Master switch for Universalis pricing protection and empty board handling."
            );
        }

        if (!useUniversalis)
        {
            ImGui.TextDisabled("Enable Universalis averages to configure smart pricing options.");
            return;
        }

        var gateEnabled = _config.EnableUndercutPreventionGate;
        if (ImGui.Checkbox("Enable Universalis-backed price gate", ref gateEnabled))
        {
            _config.EnableUndercutPreventionGate = gateEnabled;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, 
                "Compares in-game prices against Universalis averages to enforce a minimum price floor."
            );
        }

        var fallbackEnabled = _config.UseUniversalisForEmptyMarket;
        if (ImGui.Checkbox("Use Universalis fallback for empty boards", ref fallbackEnabled))
        {
            _config.UseUniversalisForEmptyMarket = fallbackEnabled;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, 
                "Uses Universalis averages for empty in-game listings instead of skipping."
            );
        }

        DrawSmartSortSettings();
    }

    private void DrawAutoPruneSettings()
    {
        var autoPrune = _config.AutoPruneMissingInventory;
        if (ImGui.Checkbox("Auto prune sell list before sorting/selling", ref autoPrune))
        {
            _config.AutoPruneMissingInventory = autoPrune;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "When enabled, the sell list is trimmed before smart sort or a run begins.");
        }

        ImGui.Spacing();
    }


    private void DrawAdvancedTabToggle()
    {
        ImGui.Separator();
        ImGui.Spacing();

        var showAdvanced = _config.ShowAdvancedSettingsTab;
        if (ImGui.Checkbox("Show advanced settings tab (expert)", ref showAdvanced))
        {
            _config.ShowAdvancedSettingsTab = showAdvanced;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Reveals advanced controls that could break automation if misconfigured.");
        }
    }

    private void DrawAdvancedOverlaySection()
    {
        ImGui.TextUnformatted("Overlay positioning");
        ImGui.Separator();
        ImGui.Spacing();
        DrawOverlayOffsetSettings();
    }

    private void DrawAdvancedPricingSection()
    {
        ImGui.TextUnformatted("Pricing aggression");
        ImGui.Separator();
        ImGui.Spacing();

        var undercut = _config.UndercutAmount;
        ImGui.PushItemWidth(200f);
        if (ImGui.SliderInt("Undercut amount", ref undercut, 0, 10))
        {
            undercut = Math.Clamp(undercut, 0, 10);
            if (undercut != _config.UndercutAmount)
            {
                _config.UndercutAmount = undercut;
                SaveConfig();
            }
        }

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, "Gil subtracted from the lowest competing listing (except your own).");
        }

        var validation = _config.MarketValidationThreshold;
        if (ImGui.SliderFloat("Market validation threshold", ref validation, 1.1f, 5f, "%.1f"))
        {
            validation = Math.Clamp(validation, 1.1f, 5f);
            if (Math.Abs(validation - _config.MarketValidationThreshold) > 0.0001f)
            {
                _config.MarketValidationThreshold = validation;
                SaveConfig();
            }
        }

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, "When Universalis averages exceed live market prices by more than this multiplier, the plugin trusts the market instead.");
        }

        if (!_config.UseUniversalisApi)
        {
            ImGui.PopItemWidth();
            ImGui.TextDisabled("Enable Universalis averages in Settings to adjust the floor.");
            return;
        }

        if (!_config.EnableUndercutPreventionGate)
        {
            ImGui.PopItemWidth();
            ImGui.TextDisabled("Enable the Universalis price gate in Settings to adjust the floor.");
            return;
        }

        var percent = _config.UndercutPreventionPercent * 100f;
        if (ImGui.SliderFloat("Universalis Avg Floor %", ref percent, 10f, 90f, "%.0f"))
        {
            var normalized = Math.Clamp(percent / 100f, 0.1f, 0.9f);
            if (Math.Abs(normalized - _config.UndercutPreventionPercent) > 0.0001f)
            {
                _config.UndercutPreventionPercent = normalized;
                SaveConfig();
            }
        }

        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Listings below this percent of the Universalis average will be ignored.");
        }
    }

    private void DrawAdvancedSmartSortSection()
    {
        ImGui.TextUnformatted("Smart sort tuning");
        ImGui.Separator();
        ImGui.Spacing();

        if (!_config.EnableUniversalisSmartSort)
        {
            ImGui.TextDisabled("Enable smart sell sorting in Settings to adjust these sliders.");
            return;
        }

        ImGui.PushItemWidth(200f);

        DrawFloatSlider(
            "Velocity cap##adv_ss_velocity_cap",
            () => _config.SmartSortVelocityCap,
            v => _config.SmartSortVelocityCap = v,
            10f,
            500f,
            "%.0f",
            "Daily sale velocity is clamped to this value before normalization.");

        DrawFloatSlider(
            "Velocity log base##adv_ss_velocity_log",
            () => _config.SmartSortVelocityLogBase,
            v => _config.SmartSortVelocityLogBase = v,
            2f,
            500f,
            "%.0f",
            "Log base controlling how aggressive velocity scores ramp.");

        var velocityWeight = _config.SmartSortVelocityWeight;
        if (ImGui.SliderFloat("Velocity weight", ref velocityWeight, 0.2f, 0.9f, "%.2f"))
        {
            velocityWeight = Math.Clamp(velocityWeight, 0.2f, 0.9f);
            _config.SmartSortVelocityWeight = velocityWeight;
            _config.SmartSortPriceWeight = 1f - velocityWeight;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Higher values favor fast-selling items; lower balances toward expensive items.");
        }

        DrawFloatSlider(
            "Price log base##adv_ss_price_log",
            () => _config.SmartSortPriceLogBase,
            v => _config.SmartSortPriceLogBase = v,
            100f,
            200000f,
            "%.0f",
            "Log base controlling how much price influences the score.");

        var priceWeight = _config.SmartSortPriceWeight;
        if (ImGui.SliderFloat("Price weight", ref priceWeight, 0.1f, 0.8f, "%.2f"))
        {
            priceWeight = Math.Clamp(priceWeight, 0.1f, 0.8f);
            _config.SmartSortPriceWeight = priceWeight;
            _config.SmartSortVelocityWeight = Math.Clamp(1f - priceWeight, 0.2f, 0.9f);
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Higher values favor expensive items; lower balances toward fast sellers.");
        }

        var refreshMinutes = _config.SmartSortRefreshMinutes;
        if (ImGui.SliderInt("Auto refresh", ref refreshMinutes, 5, 180))
        {
            refreshMinutes = Math.Clamp(refreshMinutes, 5, 180);
            _config.SmartSortRefreshMinutes = refreshMinutes;
            SaveConfig();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Smart sort auto-refreshes if the last refresh was more than this many minutes ago when a run starts.");
        }

        ImGui.PopItemWidth();

        var lastRun = _config.GetSmartSortLastRunUtc();
        var lastRunText = lastRun <= DateTime.MinValue ? "Never" : lastRun.ToLocalTime().ToString("g");
        ImGui.TextDisabled($"Last smart sort: {lastRunText}");

        ImGui.TextDisabled("Smart sort formula");
        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, 
                "composite = (velocityScore × velocityWeight) + (priceScore × priceWeight)\n" +
                "velocityScore = log10(min(cap, velocity)+1) / log10(base)\n" +
                "priceScore = log10(price+1) / log10(base)\n" +
                "Example: an item with 0.5 avg velocity and ~100k avg price will have an ~0.5 composite score with default values."
            );
        }
    }

    private void DrawAdvancedAutomationSection()
    {
        ImGui.TextUnformatted("Automation pacing");
        ImGui.Separator();
        ImGui.Spacing();

        DrawSecondsSlider(
            "Framework tick interval##adv_framework_tick",
            () => _config.FrameworkTickIntervalSeconds,
            v => _config.FrameworkTickIntervalSeconds = v,
            0.02f,
            0.2f,
            "Outer guard on how frequently automation logic evaluates.");

        DrawSecondsSlider(
            "Action interval##adv_action_interval",
            () => _config.ActionIntervalSeconds,
            v => _config.ActionIntervalSeconds = v,
            0.05f,
            0.5f,
            "Delay between automation actions. Lower values are faster but risk UI instability.");

        DrawSecondsSlider(
            "Retainer sync interval##adv_retainer_sync",
            () => _config.RetainerSyncIntervalSeconds,
            v => _config.RetainerSyncIntervalSeconds = v,
            0.5f,
            10f,
            "How often enabled retainer names are refreshed.");
    }

    private void DrawAdvancedMarketTimingSection()
    {
        ImGui.TextUnformatted("Market board timing");
        ImGui.Separator();
        ImGui.Spacing();

        DrawSecondsSlider(
            "MB base interval##adv_mb_base",
            () => _config.MbBaseIntervalSeconds,
            v => _config.MbBaseIntervalSeconds = v,
            0.5f,
            5f,
            "Target pacing between Compare Prices requests. This value adjusts up or down automatically based on recent market throttles.");

        DrawSecondsSlider(
            "MB min interval##adv_mb_min",
            () => _config.MbIntervalMinSeconds,
            v => _config.MbIntervalMinSeconds = v,
            0.3f,
            5f,
            "Lower bound for market pacing adjustments.");

        DrawSecondsSlider(
            "MB max interval##adv_mb_max",
            () => _config.MbIntervalMaxSeconds,
            v => _config.MbIntervalMaxSeconds = v,
            0.5f,
            6f,
            "Upper bound for market pacing adjustments.");

        DrawFloatSlider(
            "MB jitter##adv_mb_jitter",
            () => _config.MbJitterMaxSeconds,
            v => _config.MbJitterMaxSeconds = v,
            0f,
            1f,
            "%.2f s",
            "Random variance added to market pacing so requests don't occur on a strict cadence.");

        DrawSecondsSlider(
            "ISR throttle backoff##adv_isr_backoff",
            () => _config.ItemSearchResultThrottleBackoffSeconds,
            v => _config.ItemSearchResultThrottleBackoffSeconds = v,
            0.5f,
            5f,
            "Wait applied after the market board tells you to retry.");

        DrawSecondsSlider(
            "No items settle delay##adv_isr_settle",
            () => _config.IsrNoItemsSettleSeconds,
            v => _config.IsrNoItemsSettleSeconds = v,
            0.1f,
            2f,
            "Grace period before trusting an empty market board result.");

        NormalizeMarketIntervals();
    }

    private void DrawAdvancedHqTimingSection()
    {
        ImGui.TextUnformatted("HQ filter timing");
        ImGui.Separator();
        ImGui.Spacing();

        DrawSecondsSlider(
            "Initial HQ delay##adv_hq_initial",
            () => _config.IsrHqFilterInitialDelaySeconds,
            v => _config.IsrHqFilterInitialDelaySeconds = v,
            0.2f,
            3f,
            "Wait after ItemSearchResult opens before attempting the HQ filter fallback.");

        DrawSecondsSlider(
            "HQ UI debounce##adv_hq_debounce",
            () => _config.IsrHqFilterUiDebounceSeconds,
            v => _config.IsrHqFilterUiDebounceSeconds = v,
            0.05f,
            1f,
            "Time the HQ filter UI must remain visible before interacting.");

        DrawSecondsSlider(
            "HQ open retry##adv_hq_retry",
            () => _config.IsrHqFilterOpenRetrySeconds,
            v => _config.IsrHqFilterOpenRetrySeconds = v,
            0.05f,
            1f,
            "Delay between attempts to open the HQ filter window.");

        DrawSecondsSlider(
            "HQ post-open wait##adv_hq_post",
            () => _config.IsrHqFilterPostOpenSeconds,
            v => _config.IsrHqFilterPostOpenSeconds = v,
            0.05f,
            1f,
            "Wait after opening the filter window before issuing actions.");
    }

    private void DrawSecondsSlider(string label, Func<double> getter, Action<double> setter, float min, float max, string tooltip, string format = "%.2f s")
    {
        var existing = getter();
        var value = (float)existing;
        ImGui.PushItemWidth(200f);
        if (ImGui.SliderFloat(label, ref value, min, max, format))
        {
            var clamped = Math.Clamp(value, min, max);
            var newValue = Math.Round(clamped, 4);
            if (Math.Abs(newValue - existing) > 0.0001d)
            {
                setter(newValue);
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, tooltip);
        }
    }

    private void DrawFloatSlider(string label, Func<double> getter, Action<double> setter, float min, float max, string format, string tooltip)
    {
        var existing = getter();
        var value = (float)existing;
        ImGui.PushItemWidth(200f);
        if (ImGui.SliderFloat(label, ref value, min, max, format))
        {
            var clamped = Math.Clamp(value, min, max);
            var newValue = Math.Round(clamped, 4);
            if (Math.Abs(newValue - existing) > 0.0001d)
            {
                setter(newValue);
                SaveConfig();
            }
        }
        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, tooltip);
        }
    }

    private void NormalizeMarketIntervals()
    {
        var min = _config.MbIntervalMinSeconds;
        var max = _config.MbIntervalMaxSeconds;
        var baseInterval = _config.MbBaseIntervalSeconds;

        var changed = false;

        if (min > max)
        {
            max = min;
            changed = true;
        }

        if (baseInterval < min)
        {
            baseInterval = min;
            changed = true;
        }
        else if (baseInterval > max)
        {
            baseInterval = max;
            changed = true;
        }

        if (changed)
        {
            _config.MbIntervalMinSeconds = min;
            _config.MbIntervalMaxSeconds = max;
            _config.MbBaseIntervalSeconds = baseInterval;
            SaveConfig();
        }
    }

    private void DrawAdvancedResetControls()
    {
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset advanced settings to defaults"))
        {
            ResetAdvancedSettingsToDefaults();
            _showAdvancedResetConfirmation = true;
            _advancedResetConfirmationUtc = DateTime.UtcNow;
        }

        if (ImGui.IsItemHovered())
        {
            TooltipHelper.Show(_config, "Resets all advanced settings on this tab to defaults.");
        }

        if (_showAdvancedResetConfirmation)
        {
            if (DateTime.UtcNow - _advancedResetConfirmationUtc < TimeSpan.FromSeconds(3))
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.9f, 0.6f, 1f), "Defaults restored.");
            }
            else
            {
                _showAdvancedResetConfirmation = false;
            }
        }
    }

    private void ResetAdvancedSettingsToDefaults()
    {
        var changed = false;
        var smartSortChanged = false;

        if (Math.Abs(_config.OverlayOffsetX - DefaultConfig.OverlayOffsetX) > 0.001f)
        {
            _config.OverlayOffsetX = DefaultConfig.OverlayOffsetX;
            changed = true;
        }

        if (Math.Abs(_config.OverlayOffsetY - DefaultConfig.OverlayOffsetY) > 0.001f)
        {
            _config.OverlayOffsetY = DefaultConfig.OverlayOffsetY;
            changed = true;
        }

        if (_config.ShowOverlayOffsetControls)
        {
            _config.ShowOverlayOffsetControls = false;
            changed = true;
        }

        if (Math.Abs(_config.UndercutPreventionPercent - DefaultConfig.UndercutPreventionPercent) > 0.0001f)
        {
            _config.UndercutPreventionPercent = DefaultConfig.UndercutPreventionPercent;
            changed = true;
        }

        if (_config.UndercutAmount != DefaultConfig.UndercutAmount)
        {
            _config.UndercutAmount = DefaultConfig.UndercutAmount;
            changed = true;
        }

        if (Math.Abs(_config.MarketValidationThreshold - DefaultConfig.MarketValidationThreshold) > 0.0001f)
        {
            _config.MarketValidationThreshold = DefaultConfig.MarketValidationThreshold;
            changed = true;
        }

        if (Math.Abs(_config.ActionIntervalSeconds - DefaultConfig.ActionIntervalSeconds) > 0.0001d)
        {
            _config.ActionIntervalSeconds = DefaultConfig.ActionIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.RetainerSyncIntervalSeconds - DefaultConfig.RetainerSyncIntervalSeconds) > 0.0001d)
        {
            _config.RetainerSyncIntervalSeconds = DefaultConfig.RetainerSyncIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.FrameworkTickIntervalSeconds - DefaultConfig.FrameworkTickIntervalSeconds) > 0.0001d)
        {
            _config.FrameworkTickIntervalSeconds = DefaultConfig.FrameworkTickIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.ItemSearchResultThrottleBackoffSeconds - DefaultConfig.ItemSearchResultThrottleBackoffSeconds) > 0.0001d)
        {
            _config.ItemSearchResultThrottleBackoffSeconds = DefaultConfig.ItemSearchResultThrottleBackoffSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbBaseIntervalSeconds - DefaultConfig.MbBaseIntervalSeconds) > 0.0001d)
        {
            _config.MbBaseIntervalSeconds = DefaultConfig.MbBaseIntervalSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbIntervalMinSeconds - DefaultConfig.MbIntervalMinSeconds) > 0.0001d)
        {
            _config.MbIntervalMinSeconds = DefaultConfig.MbIntervalMinSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbIntervalMaxSeconds - DefaultConfig.MbIntervalMaxSeconds) > 0.0001d)
        {
            _config.MbIntervalMaxSeconds = DefaultConfig.MbIntervalMaxSeconds;
            changed = true;
        }

        if (Math.Abs(_config.MbJitterMaxSeconds - DefaultConfig.MbJitterMaxSeconds) > 0.0001d)
        {
            _config.MbJitterMaxSeconds = DefaultConfig.MbJitterMaxSeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrNoItemsSettleSeconds - DefaultConfig.IsrNoItemsSettleSeconds) > 0.0001d)
        {
            _config.IsrNoItemsSettleSeconds = DefaultConfig.IsrNoItemsSettleSeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterInitialDelaySeconds - DefaultConfig.IsrHqFilterInitialDelaySeconds) > 0.0001d)
        {
            _config.IsrHqFilterInitialDelaySeconds = DefaultConfig.IsrHqFilterInitialDelaySeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterUiDebounceSeconds - DefaultConfig.IsrHqFilterUiDebounceSeconds) > 0.0001d)
        {
            _config.IsrHqFilterUiDebounceSeconds = DefaultConfig.IsrHqFilterUiDebounceSeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterOpenRetrySeconds - DefaultConfig.IsrHqFilterOpenRetrySeconds) > 0.0001d)
        {
            _config.IsrHqFilterOpenRetrySeconds = DefaultConfig.IsrHqFilterOpenRetrySeconds;
            changed = true;
        }

        if (Math.Abs(_config.IsrHqFilterPostOpenSeconds - DefaultConfig.IsrHqFilterPostOpenSeconds) > 0.0001d)
        {
            _config.IsrHqFilterPostOpenSeconds = DefaultConfig.IsrHqFilterPostOpenSeconds;
            changed = true;
        }

        if (Math.Abs(_config.SmartSortVelocityWeight - DefaultConfig.SmartSortVelocityWeight) > 0.0001f)
        {
            _config.SmartSortVelocityWeight = DefaultConfig.SmartSortVelocityWeight;
            _config.SmartSortPriceWeight = DefaultConfig.SmartSortPriceWeight;
            changed = true;
            smartSortChanged = true;
        }

        if (_config.SmartSortRefreshMinutes != DefaultConfig.SmartSortRefreshMinutes)
        {
            _config.SmartSortRefreshMinutes = DefaultConfig.SmartSortRefreshMinutes;
            changed = true;
            smartSortChanged = true;
        }

        if (Math.Abs(_config.SmartSortVelocityCap - DefaultConfig.SmartSortVelocityCap) > 0.0001d)
        {
            _config.SmartSortVelocityCap = DefaultConfig.SmartSortVelocityCap;
            changed = true;
            smartSortChanged = true;
        }

        if (Math.Abs(_config.SmartSortVelocityLogBase - DefaultConfig.SmartSortVelocityLogBase) > 0.0001d)
        {
            _config.SmartSortVelocityLogBase = DefaultConfig.SmartSortVelocityLogBase;
            changed = true;
            smartSortChanged = true;
        }

        if (Math.Abs(_config.SmartSortPriceLogBase - DefaultConfig.SmartSortPriceLogBase) > 0.0001d)
        {
            _config.SmartSortPriceLogBase = DefaultConfig.SmartSortPriceLogBase;
            changed = true;
            smartSortChanged = true;
        }

        if (changed)
        {
            SaveConfig();

            if (smartSortChanged)
                _plugin.NotifySmartSortSettingChanged();
        }
    }

    private void DrawSmartSortSettings()
    {
        var smartSortEnabled = _config.EnableUniversalisSmartSort;
        if (ImGui.Checkbox("Enable smart sell sorting", ref smartSortEnabled))
        {
            _config.EnableUniversalisSmartSort = smartSortEnabled;
            SaveConfig();
            _plugin.NotifySmartSortSettingChanged();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Automatically reorder sell items using Universalis sale velocity and average price data.");
        }

        if (!smartSortEnabled)
            return;
    }

    private void DrawOpenLogsButton()
    {

        if (ImGui.Button("Open Logs"))
            _plugin.OpenLogWindow();

        ImGui.Spacing();
    }

    #endregion

    #region Save helpers

    private void SaveConfig() => _config.Save();

    private static ContextMenuQuickAddModifier SanitizeContextMenuQuickAddModifier(ContextMenuQuickAddModifier modifier)
        => Enum.IsDefined(modifier) ? modifier : ContextMenuQuickAddModifier.None;

    private static ContextMenuQuickListModifier SanitizeContextMenuQuickListModifier(ContextMenuQuickListModifier modifier)
        => Enum.IsDefined(modifier) ? modifier : ContextMenuQuickListModifier.None;

    private static string GetContextMenuQuickAddModifierLabel(ContextMenuQuickAddModifier modifier)
        => modifier switch
        {
            ContextMenuQuickAddModifier.None => "Disabled",
            ContextMenuQuickAddModifier.Shift => "Shift",
            ContextMenuQuickAddModifier.LeftShift => "Left Shift",
            ContextMenuQuickAddModifier.RightShift => "Right Shift",
            ContextMenuQuickAddModifier.Ctrl => "Ctrl",
            ContextMenuQuickAddModifier.LeftCtrl => "Left Ctrl",
            ContextMenuQuickAddModifier.RightCtrl => "Right Ctrl",
            ContextMenuQuickAddModifier.Alt => "Alt",
            ContextMenuQuickAddModifier.LeftAlt => "Left Alt",
            ContextMenuQuickAddModifier.RightAlt => "Right Alt",
            _ => "Disabled",
        };

    private static string GetContextMenuQuickListModifierLabel(ContextMenuQuickListModifier modifier)
        => modifier switch
        {
            ContextMenuQuickListModifier.None => "Disabled",
            ContextMenuQuickListModifier.Shift => "Shift",
            ContextMenuQuickListModifier.LeftShift => "Left Shift",
            ContextMenuQuickListModifier.RightShift => "Right Shift",
            ContextMenuQuickListModifier.Ctrl => "Ctrl",
            ContextMenuQuickListModifier.LeftCtrl => "Left Ctrl",
            ContextMenuQuickListModifier.RightCtrl => "Right Ctrl",
            ContextMenuQuickListModifier.Alt => "Alt",
            ContextMenuQuickListModifier.LeftAlt => "Left Alt",
            ContextMenuQuickListModifier.RightAlt => "Right Alt",
            _ => "Disabled",
        };

    private static string GetRetainerLabelForLog(int rowIndex)
        => rowIndex < 0
            ? "retainer?"
            : $"retainer{rowIndex + 1}";

    #endregion
}
