namespace RetainerRepricer.Ui;

internal static class NodePaths
{
    #region ItemSearchResult (market board)

    // Main listing list component inside ItemSearchResult.
    public const int ItemSearchResult_ListNodeId = 26;

    // Status/error message text node.
    // Empty when normal; shows strings like "No items found." / "Please wait and try your search again".
    public const int ItemSearchResult_ErrorMessageNodeId = 5;

    // Row renderer nodes (AtkComponentListItemRenderer).
    public const ushort HqIconNodeId = 3;
    public const ushort UnitPriceNodeId = 5;
    public const ushort QuantityNodeId = 6;
    public const ushort TotalPriceNodeId = 8;
    public const ushort SellerNodeId = 10;

    #endregion

    #region RetainerList

    // List component inside RetainerList.
    public const ushort RetainerListNodeId = 27;

    // Row renderer nodes (AtkComponentListItemRenderer).
    public const ushort RetainerNameNodeId = 3;

    #endregion

    #region RetainerSellList

    // "N/20" style count text node.
    public const int RetainerSellList_CountNodeId = 19;

    #endregion

    #region RetainerSell

    // Item name text (contains HQ glyph when HQ).
    public const int RetainerSell_ItemNameNodeId = 7;

    // Asking price is a NumericInput component with a child text node.
    public const int RetainerSell_AskingPriceNumericNodeId = 10; // NumericInput component node
    public const ushort RetainerSell_AskingPriceTextNodeId = 5;  // child text node inside component

    #endregion
}
