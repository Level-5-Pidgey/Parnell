using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerRepricer.Windows;

/// <summary>
/// Button-driven diagnostics for inspecting the game state used by repricing.
/// </summary>
public sealed class DebugWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private int _inventoryItemId;
    private bool _inventoryItemIsHq;

    public DebugWindow(Plugin plugin)
        : base("Retainer Repricer Debug###RetainerRepricerDebug")
    {
        _plugin = plugin;
        Size = new Vector2(620f, 420f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = false;
    }

    public void Dispose()
    {
    }

    public void Open()
    {
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Diagnostics are written to the Retainer Repricer log buffer and Dalamud log.");
        ImGui.Separator();

        ImGui.TextUnformatted("Market and retainer dumps");
        if (ImGui.Button("Dump Market Rows / HQ State"))
            _plugin.DumpMarketRowsForDebug();

        ImGui.SameLine();
        if (ImGui.Button("Dump Retainer Rows"))
            _plugin.DumpRetainerRowsForDebug();

        ImGui.SameLine();
        if (ImGui.Button("Test Universalis Gate"))
            _plugin.TestUniversalisGateForDebug();

        ImGui.Spacing();
        ImGui.TextUnformatted("Inventory slot dump");
        ImGui.SetNextItemWidth(150f);
        ImGui.InputInt("Item ID", ref _inventoryItemId);
        ImGui.SameLine();
        ImGui.Checkbox("HQ", ref _inventoryItemIsHq);
        ImGui.SameLine();
        _inventoryItemId = Math.Clamp(_inventoryItemId, 0, int.MaxValue);
        ImGui.BeginDisabled(_inventoryItemId == 0);
        if (ImGui.Button("Dump Universalis Data"))
            _ = _plugin.DumpUniversalisDebugAsync((uint)_inventoryItemId, _inventoryItemIsHq);

        ImGui.SameLine();
        if (ImGui.Button("Dump Matching Inventory Slots"))
            _plugin.DumpInventoryByItemIdForDebug((uint)_inventoryItemId);
        ImGui.EndDisabled();

        ImGui.BeginDisabled(_inventoryItemId == 0);
        if (ImGui.Button("Dump Matching InventoryGrid Renderers"))
            _plugin.DumpInventoryGridForDebug((uint)_inventoryItemId);
        ImGui.EndDisabled();

        ImGui.Spacing();
        if (ImGui.Button("Open Logs"))
            _plugin.OpenLogWindow();
    }
}
