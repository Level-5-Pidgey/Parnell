using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;

using RetainerRepricer.Ui;

namespace RetainerRepricer.Windows;

/// <summary>
/// Lightweight overlay that surfaces start/stop controls and the current run status next to the bell UI.
/// </summary>
public sealed unsafe class MainWindow : Window, IDisposable
{
    #region Constants (overlay sizing / anchoring)

    private const float OverlayWidth = 140f;
    private const float OverlayHeight = 38f;

    // Position tweaks relative to the anchor window.
    private const float AnchorOffsetY = 38f; // raise overlay above anchor
    private const float AnchorInsetX = 4f;   // small right inset from anchor edge

    #endregion

    #region Fields

    private readonly Plugin _plugin;

    #endregion

    #region Lifecycle

    public MainWindow(Plugin plugin)
        : base(
            "Retainer Repricer##Overlay",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse
        )
    {
        _plugin = plugin;

        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        // Always registered so WindowSystem can evaluate DrawConditions().
        IsOpen = true;
    }

    public void Dispose()
    {
        // Nothing to dispose.
    }

    #endregion

    #region Addon helpers (visibility + anchoring)

    private static AtkUnitBase* GetVisibleAddonUnit(string addonName)
    {
        var a = Plugin.GameGui.GetAddonByName(addonName, 1);
        if (a.IsNull) return null;

        var u = (AtkUnitBase*)a.Address;
        if (u == null || !u->IsVisible) return null;

        return u;
    }

    private static bool IsAddonVisible(string addonName)
        => GetVisibleAddonUnit(addonName) != null;

    private static bool IsAddonOpen(string addonName)
        => !Plugin.GameGui.GetAddonByName(addonName, 1).IsNull;

    private static AtkUnitBase* GetAnchorUnit()
    {
        // Prefer anchoring to whatever is currently visible.
        // This keeps the overlay reachable after RetainerList disappears.
        var u = GetVisibleAddonUnit("RetainerList");
        if (u != null) return u;

        u = GetVisibleAddonUnit("RetainerSellList");
        if (u != null) return u;

        u = GetVisibleAddonUnit("RetainerSell");
        if (u != null) return u;

        u = GetVisibleAddonUnit("SelectString");
        if (u != null) return u;

        u = GetVisibleAddonUnit("Talk");
        if (u != null) return u;

        return null;
    }

    private bool AnyFlowAddonVisible()
    {
        // "Flow" windows where the Stop button should be available.
        if (IsAddonVisible("RetainerList")) return true;
        if (_plugin.IsRunning)
        {
            if (IsAddonVisible("RetainerSellList")) return true;
            if (IsAddonVisible("RetainerSell")) return true;
            if (IsAddonVisible("SelectString")) return true;
            if (IsAddonVisible("Talk")) return true;
            if (IsAddonOpen("ItemSearchResult")) return true;
            if (IsAddonOpen("ItemHistory")) return true;
        }

        return false;
    }

    #endregion

    #region Draw conditions

    public override bool DrawConditions()
    {
        // Master kill switch: no overlay when disabled.
        if (!_plugin.Configuration.PluginEnabled)
            return false;

        // Overlay toggle: only affects this window.
        if (!_plugin.Configuration.OverlayEnabled)
            return false;

        // Only show during the retainer/sell/market flow, so Stop stays reachable.
        if (!AnyFlowAddonVisible())
            return false;

        // Only sync roster while RetainerList is visible (keeps it cheap).
        if (IsAddonVisible("RetainerList"))
            _plugin.TrySyncRetainersThrottled();

        var anchor = GetAnchorUnit();
        if (anchor == null)
            return false;

        ImGui.SetNextWindowSize(new Vector2(OverlayWidth, OverlayHeight), ImGuiCond.Always);
        AnchorToUnit(anchor);

        return true;
    }

    private void AnchorToUnit(AtkUnitBase* unit)
    {
        if (unit == null) return;

        var x = unit->X;
        var y = unit->Y;
        var w = unit->GetScaledWidth(true);

        var overlayX = (x + w) - AnchorInsetX - OverlayWidth + _plugin.Configuration.OverlayOffsetX;
        var overlayY = (y - AnchorOffsetY) + _plugin.Configuration.OverlayOffsetY;

        ImGui.SetNextWindowPos(new Vector2(overlayX, overlayY), ImGuiCond.Always);
    }

    #endregion

    #region Draw

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));

        DrawStartStopButton();
        ImGui.SameLine();
        DrawConfigButton();
        ImGui.SameLine();
        DrawRightAlignedStatusText();

        ImGui.PopStyleVar(2);
    }

    private void DrawStartStopButton()
    {
        var icon = _plugin.IsRunning ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;

        ImGui.PushID("rr-startstop");
        if (ImGuiComponents.IconButton(icon))
        {
            if (_plugin.IsRunning)
            {
                Plugin.Log.Information("[RR][Overlay] Stop button clicked");
                _plugin.StopRun();
            }
            else
            {
                // Don't allow starting while plugin is disabled.
                if (_plugin.Configuration.PluginEnabled)
                {
                    Plugin.Log.Information("[RR][Overlay] Start button clicked");
                    _plugin.StartRunFromRetainerList();
                }
            }
        }
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            TooltipHelper.Show(_plugin.Configuration, _plugin.IsRunning ? "Stop run" : "Start run");
    }

    private void DrawConfigButton()
    {
        ImGui.PushID("rr-config");
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
        {
            Plugin.Log.Debug("[RR][Overlay] Config button clicked");
            _plugin.ToggleConfigUi();
        }
        ImGui.PopID();

        if (ImGui.IsItemHovered())
            TooltipHelper.Show(_plugin.Configuration, "Open config");
    }

    private void DrawRightAlignedStatusText()
    {
        var status = _plugin.IsRunning ? "Running" : "Idle";

        var avail = ImGui.GetContentRegionAvail().X;
        var textW = ImGui.CalcTextSize(status).X;

        ImGui.SameLine(MathF.Max(0, ImGui.GetCursorPosX() + avail - textW));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(status);
    }

    #endregion
}
