using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RetainerRepricer.Services;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace RetainerRepricer.Windows;

public sealed class LogWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private bool _autoScroll = true;
    private PluginLogLevel _minimumLevel = PluginLogLevel.Information;

    public LogWindow(Plugin plugin)
        : base("Retainer Repricer Logs###RetainerRepricerLogs")
    {
        _plugin = plugin;
        Size = new Vector2(900f, 480f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = false;
    }

    public void Open()
    {
        IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawLogEntries();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("Clear"))
            _plugin.PluginLogBuffer.Clear();

        ImGui.SameLine();
        if (ImGui.Button("Copy Visible"))
            ImGui.SetClipboardText(BuildVisibleLogText());

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("Minimum level", _minimumLevel.ToString()))
        {
            foreach (PluginLogLevel level in Enum.GetValues<PluginLogLevel>())
            {
                var selected = level == _minimumLevel;
                if (ImGui.Selectable(level.ToString(), selected))
                    _minimumLevel = level;

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private void DrawLogEntries()
    {
        if (!ImGui.BeginChild("##rr-log-entries", new Vector2(0f, 0f), false, ImGuiWindowFlags.HorizontalScrollbar))
            return;

        var shouldStickToBottom = _autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY();

        try
        {
            var entries = _plugin.PluginLogBuffer.GetSnapshot();
            foreach (var entry in entries)
            {
                if (entry.Level < _minimumLevel)
                    continue;

                ImGui.PushStyleColor(ImGuiCol.Text, GetColor(entry.Level));
                ImGui.TextUnformatted($"{entry.TimestampUtc:HH:mm:ss.fff} [{GetLevelLabel(entry.Level)}] {entry.Message}");
                ImGui.PopStyleColor();
            }

            if (shouldStickToBottom)
                ImGui.SetScrollHereY(1f);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private string BuildVisibleLogText()
    {
        var builder = new StringBuilder();
        IReadOnlyList<PluginLogEntry> entries = _plugin.PluginLogBuffer.GetSnapshot();
        foreach (var entry in entries)
        {
            if (entry.Level < _minimumLevel)
                continue;

            builder.Append(entry.TimestampUtc.ToString("HH:mm:ss.fff"));
            builder.Append(' ');
            builder.Append('[');
            builder.Append(GetLevelLabel(entry.Level));
            builder.Append("] ");
            builder.AppendLine(entry.Message);
        }

        return builder.ToString();
    }

    private static Vector4 GetColor(PluginLogLevel level)
        => level switch
        {
            PluginLogLevel.Error => new Vector4(0.95f, 0.35f, 0.35f, 1f),
            PluginLogLevel.Warning => new Vector4(1f, 0.70f, 0.30f, 1f),
            PluginLogLevel.Debug => new Vector4(0.75f, 0.82f, 1f, 1f),
            PluginLogLevel.Verbose => new Vector4(0.68f, 0.68f, 0.68f, 1f),
            _ => new Vector4(1f, 1f, 1f, 1f),
        };

    private static string GetLevelLabel(PluginLogLevel level)
        => level switch
        {
            PluginLogLevel.Verbose => "VRB",
            PluginLogLevel.Debug => "DBG",
            PluginLogLevel.Information => "INF",
            PluginLogLevel.Warning => "WRN",
            PluginLogLevel.Error => "ERR",
            _ => "INF",
        };
}
