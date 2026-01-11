using System;
using System.Collections.Generic;
using System.Linq;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Parnell.Helpers;

public static unsafe class AddonHelpers
{
    public const int FrameDelay = 10;
    
    public static bool IsReady(this ref AtkUnitBase addon) => 
        addon.IsVisible && addon.UldManager.LoadedState == AtkLoadState.Loaded && addon.IsFullyLoaded();
    
    internal static bool GenericThrottle => FrameThrottler.Throttle("ParnellGenericThrottle", FrameDelay);

    internal static void RethrottleGeneric(int num)
    {
        FrameThrottler.Throttle("ParnellGenericThrottle", num, true);
    }
    internal static void RethrottleGeneric()
    {
        FrameThrottler.Throttle("ParnellGenericThrottle", FrameDelay, true);
    }

    public static class SelectString
    {
        internal static bool? TrySelectSpecificEntry(Func<string, bool> inputTextTest, Func<bool>? throttler = null)
        {
            if(GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
            {
                InternalLog.Debug($"Entries: {new AddonMaster.SelectString(addon).Entries.Select(x => x.Text).Print("\n")}");
                if(new AddonMaster.SelectString(addon).Entries.TryGetFirst(x => inputTextTest(x.Text), out var entry))
                {
                    InternalLog.Debug($"Entry found: {entry}");
                    if(throttler?.Invoke() ?? GenericThrottle)
                    {
                        entry.Select();
                        InternalLog.Debug($"{nameof(TrySelectSpecificEntry)}: selecting {entry}");
                        return true;
                    }
                }
            }
            else
            {
                RethrottleGeneric();
            }

            return false;
        }

        internal static bool? TrySelectSpecificEntry(IEnumerable<string> text, Func<bool>? throttler = null) =>
            TrySelectSpecificEntry((x) => x.StartsWithAny(text), throttler);

        internal static bool? TrySelectSpecificEntry(string text, Func<bool>? throttler = null) =>
            TrySelectSpecificEntry([text], throttler);
    }
}
