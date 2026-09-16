using Dalamud.Bindings.ImGui;

namespace RetainerRepricer.Ui;

internal static class TooltipHelper
{
    public static void Show(Configuration? config, string? text)
    {
        if (config?.ShowTooltips != true)
            return;

        if (string.IsNullOrWhiteSpace(text))
            return;

        ImGui.SetTooltip(text);
    }
}
