using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation.NeoTaskManager;
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
        private bool skipCurrentItem;
        
        private readonly TaskManager taskManager;
        private readonly PriceService priceService;

        public RetainerMarketboardHandler(TaskManager taskManager, PriceService priceService)
        {
            this.taskManager = taskManager;
            this.priceService = priceService;
        }

        public void EnqueueRetainerSteps()
        {
            taskManager.Enqueue(ClickSellItems);
            taskManager.Enqueue(ProcessAllRetainerItems);
            taskManager.EnqueueDelay(500);
            taskManager.Enqueue(CloseRetainerSellList);
            taskManager.EnqueueDelay(100);
            taskManager.Enqueue(Close);
        }

        private void ClearState()
        {
            skipCurrentItem = false;
        }

        private unsafe bool? ProcessAllRetainerItems()
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
                    taskManager.Insert(SetNewPrice);
                    taskManager.InsertDelay(200);
                    taskManager.Insert(ComparePriceIfNeeded);
                    taskManager.Insert(ClickAdjustPrice);
                    taskManager.InsertDelay(200);
                    taskManager.Insert(() => OpenItemContextMenu(index));
                }

                return true;
            }

            return false;
        }

        private bool? ComparePriceIfNeeded()
        {
            if (skipCurrentItem)
                return true;

            if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
            {
                var itemName = addon->ItemName->NodeText;
                var nameText = itemName.StringPtr.AsDalamudSeString();
                var cleanedName = Utils.SanitiseDalamudString(nameText);
                var itemData = Svc.Data.GetExcelSheet<Item>().FirstOrDefault(x => x.Name == cleanedName);
                if (priceService.HasDataOnItem(itemData.RowId))
                {
                    Svc.Log.Debug($"{cleanedName}: using cached price. Skipping price comparison.");
                    taskManager.InsertDelay(500);
                }
                else
                {
                    Svc.Log.Debug($"No cached price for {cleanedName}. Clicking compare prices.");
                    ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 4);
                    taskManager.InsertDelay(3000);
                }

                return true;
            }

            return false;
        }
        
        private bool? OpenItemContextMenu(int index)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                ECommons.Automation.Callback.Fire(addon, true, 0, index, 1);
                return true;
            }
            return false;
        }

        private bool? ClickAdjustPrice()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                var reader = new ReaderContextMenu(addon);
                if (IsItemMannequin(reader.Entries))
                {
                    skipCurrentItem = true;
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

        private bool? SetNewPrice()
        {
            try
            {
                if (skipCurrentItem) return true;

                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var itemSearchResult))
                {
                    itemSearchResult->Close(true);
                }
                
                if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell) && GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
                {
                    var itemName = retainerSell->ItemName->NodeText;
                    var isHq = itemName.ToString().Contains(Lang.HqSymbol);

                    var nameText = itemName.StringPtr.AsDalamudSeString();
                    var sanitised = Utils.SanitiseDalamudString(nameText);
                    var itemData = Svc.Data.GetExcelSheet<Item>().FirstOrDefault(x => x.Name == sanitised);

                    var newPrice = priceService.GetAppropriatePriceForItem(itemData.RowId, isHq);
                    var currentPrice = retainerSell->AskingPrice->Value;
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
                    taskManager.InsertDelay(500);

                    return true;
                }
                return false;
            }
            finally
            {
                skipCurrentItem = false;
            }
        }

        public bool? ClickSellItems()
        {
            var retainerListingsText = Svc.Data.GetExcelSheet<Addon>().GetRow(2380).Text.ToString();
            return AddonHelpers.SelectString.TrySelectSpecificEntry(retainerListingsText);
        }

        public bool? CloseRetainerSellList()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
            {
                addon->Close(true);
                return true;
            }
            return false;
        }

        public bool? Close()
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
