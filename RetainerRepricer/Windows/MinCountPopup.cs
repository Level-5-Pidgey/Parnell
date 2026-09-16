using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using RetainerRepricer.Ui;

namespace RetainerRepricer.Windows;

/// <summary>
/// Simple pop-up that lets the user enter a per-item minimum count directly from the context menu.
/// </summary>
internal sealed class MinCountPopup : Window, IDisposable
{
    private const string HqIcon = "\uE03C";
    private const float ItemLabelWidth = 47f;
    private const float MinValueColumnWidth = 100f;
    private const float BaseMinWidth = 220f;
    private const float BaseMaxWidth = 720f;
    private const float BaseMinHeight = 160f;
    private const float BaseMaxHeight = 320f;
    private const float NumericInputWidth = 40f;
    private const float HeightPadding = 12f;
    private const float TableColumnMinWidth = 90f;

    private readonly Configuration _config;
    private uint _pendingItemId;
    private bool _pendingIsHq;
    private string _pendingItemName = string.Empty;
    private Action<uint, bool, int, int, bool>? _pendingCallback;
    private Vector2? _pendingAnchorPosition;
    private string _inputText = "1";
    private string _priorityText = "1";
    private int _priorityMax = 1;
    private bool _initialPositionSet;
    private bool _smartSortEnabled;
    private bool _pendingInputFocus;
    private bool _needsSizeRefresh;
    private Vector2? _pendingWindowSize;
    private bool _showKeepToggle;
    private bool _keepPreserved;

    public MinCountPopup(Configuration config)
        : base(
            "Set Minimum Count##RR_MinCount",
            ImGuiWindowFlags.None
        )
    {
        _config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(BaseMinWidth, BaseMinHeight),
            MaximumSize = new(BaseMaxWidth, BaseMaxHeight),
        };
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        ApplyDynamicSizeConstraints();
        base.PreDraw();
    }

    public void Show(
        uint itemId,
        bool isHq,
        string itemName,
        Action<uint, bool, int, int, bool> onConfirm,
        Vector2? anchorPosition = null,
        int defaultPriority = 1,
        bool smartSortEnabled = false,
        bool allowPreserveToggle = false)
    {
        _pendingItemId = itemId;
        _pendingIsHq = isHq;
        _pendingItemName = itemName;
        _pendingCallback = onConfirm;
        _pendingAnchorPosition = anchorPosition;
        _inputText = "1";
        _priorityMax = Math.Max(1, defaultPriority);
        _priorityText = _priorityMax.ToString();
        _initialPositionSet = false;
        _smartSortEnabled = smartSortEnabled;
        _pendingInputFocus = true;
        _needsSizeRefresh = true;
        _pendingWindowSize = null;
        _showKeepToggle = allowPreserveToggle;
        _keepPreserved = false;

        IsOpen = true;
    }

    public override void Draw()
    {
        ApplyPendingPosition();

        var spacingY = ImGui.GetStyle().ItemSpacing.Y;

        var itemLabel = BuildItemLabel();
        DrawCenteredLabelValueRow("Item", itemLabel, ItemLabelWidth, MinValueColumnWidth);
        ImGui.Dummy(new Vector2(0, spacingY));

        DrawInputTable();
        ImGui.Dummy(new Vector2(0, spacingY));

        DrawCenteredButtons();

        AdjustWindowHeightToContent();
    }

    private void DrawCenteredButtons()
    {
        const float buttonWidth = 100f;
        const float buttonSpacing = 16f;

        var region = ImGui.GetContentRegionAvail().X;
        var totalButtonWidth = (buttonWidth * 2) + buttonSpacing;
        var indent = MathF.Max(0f, (region - totalButtonWidth) * 0.5f);

        ImGui.Indent(indent);

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(buttonSpacing, ImGui.GetStyle().ItemSpacing.Y));
        if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
        {
            _pendingInputFocus = false;
            IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button("Add to Sell", new Vector2(buttonWidth, 0)))
        {
            ConfirmAdd();
        }

        ImGui.PopStyleVar();

        ImGui.Unindent(indent);
    }

    private void AdjustWindowHeightToContent()
    {
        var style = ImGui.GetStyle();
        var usedHeight = ImGui.GetCursorPosY() + style.WindowPadding.Y;
        var windowSize = ImGui.GetWindowSize();
        var desiredHeight = Math.Clamp(usedHeight + HeightPadding, BaseMinHeight, BaseMaxHeight);

        if (Math.Abs(windowSize.Y - desiredHeight) > 0.5f)
            ImGui.SetWindowSize(new Vector2(windowSize.X, desiredHeight));
    }

    public void Dispose()
    {
    }

    private void DrawCenteredLabelValueRow(string label, string value, float labelWidth, float minValueWidth)
    {
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var region = ImGui.GetContentRegionAvail().X;
        var valueWidth = MathF.Max(minValueWidth, region - labelWidth - spacingX);
        var rowWidth = labelWidth + spacingX + valueWidth;
        var rowStartOffset = MathF.Max(0f, (region - rowWidth) * 0.5f);

        var cursor = ImGui.GetCursorPos();
        var rowStartX = cursor.X + rowStartOffset;
        var valueStartX = rowStartX + labelWidth + spacingX;

        ImGui.SetCursorPos(new Vector2(rowStartX, cursor.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        ImGui.SetCursorPos(new Vector2(valueStartX, cursor.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.PushTextWrapPos(valueStartX + valueWidth);
        ImGui.TextWrapped(value);
        ImGui.PopTextWrapPos();

        var nextY = MathF.Max(ImGui.GetCursorPosY(), cursor.Y + ImGui.GetTextLineHeightWithSpacing());
        ImGui.SetCursorPos(new Vector2(cursor.X, nextY));
    }

    private void DrawInputTable()
    {
        var columnCount = _showKeepToggle ? 3 : 2;
        var style = ImGui.GetStyle();
        var cellPaddingX = style.CellPadding.X;
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var targetColumnWidth = MathF.Max(TableColumnMinWidth, contentWidth / columnCount);

        if (!ImGui.BeginTable(
                "##min_popup_table",
                columnCount,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedSame))
            return;

        ImGui.TableSetupColumn("Min Inv", ImGuiTableColumnFlags.WidthFixed, targetColumnWidth);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, targetColumnWidth);
        if (_showKeepToggle)
            ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, targetColumnWidth);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawHeaderCell("Min Inv", 0);
        DrawHeaderCell("Priority", 1);
        if (_showKeepToggle)
            DrawHeaderCell("Keep", 2);

        ImGui.TableNextRow();

        // Min inventory column
        ImGui.TableSetColumnIndex(0);
        var minColumnCursorX = ImGui.GetCursorPosX();
        var minColumnWidth = ImGui.GetColumnWidth();
        var minColumnInnerWidth = Math.Max(0f, minColumnWidth - (cellPaddingX * 2f));
        var minColumnHalfDiff = Math.Max(0f, (minColumnInnerWidth - NumericInputWidth) * 0.5f);
        ImGui.SetCursorPosX(minColumnCursorX + minColumnHalfDiff);
        ImGui.SetNextItemWidth(NumericInputWidth);
        ApplyInputFocusIfNeeded();
        if (ImGui.InputTextWithHint(
                "##mincount_input",
                "1",
                ref _inputText,
                8,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll)
            && string.IsNullOrWhiteSpace(_inputText))
        {
            _inputText = "1";
        }

        if (ImGui.IsItemFocused()
            && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
        {
            ConfirmAdd();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            TooltipHelper.Show(_config, "Item will be listed once you have at least this many in inventory.");
        }

        // Priority column
        ImGui.TableSetColumnIndex(1);
        ImGui.BeginDisabled(_smartSortEnabled);
        var priorityColumnCursorX = ImGui.GetCursorPosX();
        var priorityColumnWidth = ImGui.GetColumnWidth();
        var priorityColumnInnerWidth = Math.Max(0f, priorityColumnWidth - (cellPaddingX * 2f));
        var priorityColumnHalfDiff = Math.Max(0f, (priorityColumnInnerWidth - NumericInputWidth) * 0.5f);
        ImGui.SetCursorPosX(priorityColumnCursorX + priorityColumnHalfDiff);
        ImGui.SetNextItemWidth(NumericInputWidth);
        if (ImGui.InputTextWithHint("##priority_input", _priorityMax.ToString(), ref _priorityText, 8, ImGuiInputTextFlags.CharsDecimal)
            && string.IsNullOrWhiteSpace(_priorityText))
        {
            _priorityText = _priorityMax.ToString();
        }
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var tooltip = _smartSortEnabled
                ? "Priority is managed by smart sorting. Current order shown for reference."
                : "Lower numbers run first. Enter 1 to add at the top, or leave the default to append.";
            TooltipHelper.Show(_config, tooltip);
        }

        // Keep column (optional)
        var keepColumnWidthValue = 0f;
        if (_showKeepToggle)
        {
            ImGui.TableSetColumnIndex(2);
            var keepColumnCursorX = ImGui.GetCursorPosX();
            var keepColumnWidth = ImGui.GetColumnWidth();
            keepColumnWidthValue = keepColumnWidth;
            var checkboxWidth = ImGui.GetFrameHeight();
            var keepInnerWidth = Math.Max(0f, keepColumnWidth - (cellPaddingX * 2f));
            var keepHalfDiff = Math.Max(0f, (keepInnerWidth - checkboxWidth) * 0.5f);
            ImGui.SetCursorPosX(keepColumnCursorX + keepHalfDiff);
            ImGui.Checkbox("##keep_checkbox", ref _keepPreserved);

            if (ImGui.IsItemHovered())
            {
                TooltipHelper.Show(_config, "Keep this entry on the sell list even when auto prune runs.");
            }
        }

        ImGui.EndTable();
    }

    private static void DrawHeaderCell(string label, int columnIndex)
    {
        ImGui.TableSetColumnIndex(columnIndex);
        var width = ImGui.GetColumnWidth();
        var textWidth = ImGui.CalcTextSize(label).X;
        var cursor = ImGui.GetCursorPos();
        var cellPaddingX = ImGui.GetStyle().CellPadding.X;
        var innerWidth = Math.Max(0f, width - (cellPaddingX * 2f));
        var padding = Math.Max(0f, (innerWidth - textWidth) * 0.5f);
        ImGui.SetCursorPosX(cursor.X + padding);
        ImGui.TextUnformatted(label);
    }

    private string BuildItemLabel()
    {
        if (string.IsNullOrWhiteSpace(_pendingItemName))
            return _pendingIsHq ? HqIcon : string.Empty;

        return _pendingIsHq
            ? string.Concat(_pendingItemName, " ", HqIcon)
            : _pendingItemName;
    }

    private void ApplyPendingPosition()
    {
        var viewport = ImGui.GetMainViewport();
        var margin = new Vector2(4f, 4f);
        var fallbackSize = Size ?? new Vector2(250, 140);

        if (_pendingAnchorPosition.HasValue)
        {
            var windowSize = ImGui.GetWindowSize();
            if (windowSize.X <= 0 || windowSize.Y <= 0)
                windowSize = fallbackSize;

            var halfSize = windowSize / 2f;
            var cursorOffset = new Vector2(0f, 8f);
            var anchor = _pendingAnchorPosition.Value - halfSize + cursorOffset;
            var min = viewport.Pos + margin;
            var max = viewport.Pos + viewport.Size - windowSize - margin;
            var target = new Vector2(
                Math.Clamp(anchor.X, min.X, Math.Max(min.X, max.X)),
                Math.Clamp(anchor.Y, min.Y, Math.Max(min.Y, max.Y))
            );

            ImGui.SetWindowPos(target, ImGuiCond.Always);

            _pendingAnchorPosition = null;
            _initialPositionSet = true;
            return;
        }

        if (_initialPositionSet)
            return;

        var currentSize = ImGui.GetWindowSize();
        if (currentSize.X <= 0 || currentSize.Y <= 0)
            currentSize = fallbackSize;

        var center = viewport.GetCenter();
        var centeredPosition = new Vector2(center.X - (currentSize.X / 2), center.Y - (currentSize.Y / 2));
        ImGui.SetWindowPos(centeredPosition, ImGuiCond.Once);

        _initialPositionSet = true;
    }

    private void ApplyInputFocusIfNeeded()
    {
        if (!_pendingInputFocus)
            return;

        ImGui.SetKeyboardFocusHere();
        _pendingInputFocus = false;
    }

    private void ConfirmAdd()
    {
        if (!int.TryParse(_inputText, out var minCount))
        {
            minCount = 1;
        }

        minCount = Math.Clamp(minCount, 1, 999);

        if (!int.TryParse(_priorityText, out var priority))
        {
            priority = _priorityMax;
        }

        priority = Math.Clamp(priority, 1, _priorityMax);

        _pendingCallback?.Invoke(_pendingItemId, _pendingIsHq, minCount, priority, _showKeepToggle && _keepPreserved);
        _pendingInputFocus = false;
        IsOpen = false;
    }

    private void ApplyDynamicSizeConstraints()
    {
        var label = BuildItemLabel();
        var sizeInfo = CalculateSizeInfo(label);

        ImGui.SetNextWindowSizeConstraints(sizeInfo.MinSize, sizeInfo.MaxSize);

        if (_needsSizeRefresh)
        {
            _pendingWindowSize = sizeInfo.IdealSize;
            _needsSizeRefresh = false;
        }

        if (_pendingWindowSize.HasValue)
        {
            ImGui.SetNextWindowSize(_pendingWindowSize.Value, ImGuiCond.Always);
            _pendingWindowSize = null;
        }
    }

    private readonly struct WindowSizeInfo
    {
        public WindowSizeInfo(Vector2 minSize, Vector2 maxSize, Vector2 idealSize)
        {
            MinSize = minSize;
            MaxSize = maxSize;
            IdealSize = idealSize;
        }

        public Vector2 MinSize { get; }
        public Vector2 MaxSize { get; }
        public Vector2 IdealSize { get; }
    }

    private WindowSizeInfo CalculateSizeInfo(string label)
    {
        var style = ImGui.GetStyle();
        var spacingX = style.ItemSpacing.X;
        var spacingY = style.ItemSpacing.Y;
        var paddingX = style.WindowPadding.X * 2f;
        var paddingY = style.WindowPadding.Y * 2f;
        var textWidth = ImGui.CalcTextSize(label).X;
        var desiredValueWidth = Math.Max(MinValueColumnWidth, textWidth);
        var requiredWidth = ItemLabelWidth + spacingX + desiredValueWidth + paddingX;
        var minimumTableWidth = (TableColumnMinWidth * (_showKeepToggle ? 3 : 2)) + paddingX;
        requiredWidth = Math.Max(requiredWidth, minimumTableWidth);
        var width = Math.Clamp(requiredWidth, BaseMinWidth, BaseMaxWidth);

        var availableValueWidth = Math.Max(1f, width - ItemLabelWidth - spacingX - paddingX);
        var lineCount = Math.Max(1, (int)MathF.Ceiling(textWidth / availableValueWidth));
        var extraLines = Math.Max(0, lineCount - 1);
        var lineHeight = ImGui.GetTextLineHeight();
        var labelHeight = lineHeight + (extraLines * lineHeight);

        var tableHeaderHeight = ImGui.GetTextLineHeight();
        var tableRowHeight = ImGui.GetFrameHeight();
        var tablePaddingY = style.CellPadding.Y * 2f;
        var tableHeight = tableHeaderHeight + tableRowHeight + tablePaddingY;
        var buttonHeight = ImGui.GetFrameHeight();

        var computedHeight = paddingY + labelHeight + (spacingY * 2f) + tableHeight + buttonHeight + HeightPadding;

        var minHeight = Math.Max(BaseMinHeight, computedHeight);
        var maxHeight = Math.Max(minHeight, BaseMaxHeight + (extraLines * lineHeight));

        var minSize = new Vector2(width, minHeight);
        var maxSize = new Vector2(BaseMaxWidth, maxHeight);
        var idealSize = new Vector2(width, minHeight);

        return new WindowSizeInfo(minSize, maxSize, idealSize);
    }
}
