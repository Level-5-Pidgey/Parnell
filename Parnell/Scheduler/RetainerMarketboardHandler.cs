using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Utility;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Parnell.Helpers;
using Parnell.Services;

namespace Parnell.Scheduler
{
    public unsafe class RetainerMarketboardHandler
    {
        private static bool SkipCurrentItem;

        public static void EnqueueRetainerSteps()
        {
            Parnell.TaskManager.Enqueue(ClickSellItems);
            Parnell.TaskManager.Enqueue(ProcessAllRetainerItems);
            Parnell.TaskManager.EnqueueDelay(500);
            Parnell.TaskManager.Enqueue(CloseRetainerSellList);
            Parnell.TaskManager.EnqueueDelay(100);
            Parnell.TaskManager.Enqueue(Close);
        }

        private static void ClearState()
        {
            SkipCurrentItem = false;
        }

        private static unsafe bool? ProcessAllRetainerItems()
        {
            ClearState();

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sellList) && GenericHelpers.IsAddonReady(sellList))
            {
                var sellListManager = sellList->UldManager;
                if (sellListManager.NodeListCount < 10)
                {
                    return false;
                }

                var node = (AtkComponentNode*)sellListManager.NodeList[10];
                var list = (AtkComponentList*)node->Component;
                var length = list->ListLength;

                for (var i = length - 1; i >= 0; i--)
                {
                    var index = i;
                    Parnell.TaskManager.Insert(SetNewPrice);
                    Parnell.TaskManager.InsertDelay(200);
                    Parnell.TaskManager.Insert(ClickComparePrice);
                    Parnell.TaskManager.InsertDelay(5000);
                    Parnell.TaskManager.Insert(ClickAdjustPrice);
                    Parnell.TaskManager.InsertDelay(200);
                    Parnell.TaskManager.Insert(() => OpenItemContextMenu(index));
                }

                return true;
            }

            return false;
        }

        private static bool? ClickComparePrice()
        {
            if (SkipCurrentItem)
                return true;

            if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
            {
                // if we have a cached price, dont click compare
                var itemName = addon->ItemName->NodeText;
                var nameText = itemName.StringPtr.AsDalamudSeString().TextValue;
                var itemData = Svc.Data.GetExcelSheet<Item>().FirstOrDefault(x => x.Name == nameText);
                if (Parnell.PriceService.HasDataOnItem(itemData.RowId))
                {
                    Svc.Log.Debug($"{nameText}: using cached price");
                }
                else
                {
                    Svc.Log.Debug($"Clicking compare prices");
                    ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 4);
                }

                return true;
            }

            return false;
        }
        
        private static bool? OpenItemContextMenu(int index)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                ECommons.Automation.Callback.Fire(addon, true, 0, index, 1);
                return true;
            }
            return false;
        }

        private static bool? ClickAdjustPrice()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                var reader = new ReaderContextMenu(addon);
                if (IsItemMannequin(reader.Entries))
                {
                    SkipCurrentItem = true;
                    addon->Close(true);
                }
                else
                {
                    ECommons.Automation.Callback.Fire(addon, true, 0, 0, 0, 0, 0);
                }

                return true;
            }
            return false;
        }

        private static bool IsItemMannequin(List<ReaderContextMenu.ContextMenuEntry> contextMenuEntries) =>
            !contextMenuEntries.Any(e => 
                                        e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase) || 
                                        e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase) || 
                                        e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase) || 
                                        e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));

        private static bool? SetNewPrice()
        {
            try
            {
                if (SkipCurrentItem) return true;

                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var itemSearchResult))
                {
                    itemSearchResult->Close(true);
                }
                
                if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell) && GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
                {
                    var itemName = retainerSell->ItemName->NodeText;
                    var isHq = itemName.ToString().Contains(Lang.HqSymbol);
                    var nameText = itemName.StringPtr.AsDalamudSeString().TextValue;
                    var itemData = Svc.Data.GetExcelSheet<Item>().FirstOrDefault(x => x.Name == nameText);
                    var currentPrice = retainerSell->AskingPrice->Value;
                    var newPrice = Parnell.PriceService.GetAppropriatePriceForItem(itemData.RowId, isHq);

                    if (newPrice > 0 && currentPrice != newPrice)
                    {
                        retainerSell->AskingPrice->SetValue((int) newPrice);
                        ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 0);
                    }
                    else
                    {
                        ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 1);
                    }
                    retainerSell->AtkUnitBase.Close(true);
                    return true;
                }
                return false;
            }
            finally
            {
                SkipCurrentItem = false;
            }
        }

        public static bool? ClickSellItems()
        {
            var retainerListingsText = Svc.Data.GetExcelSheet<Addon>().GetRow(2380).Text.ToString();
            return AddonHelpers.SelectString.TrySelectSpecificEntry(retainerListingsText);
        }

        public static bool? CloseRetainerSellList()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                addon->Close(true);
                return true;
            }
            return false;
        }

        public static bool? Close()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                addon->Close(true);
                return true;
            }
            return false;
        }
    }
}
