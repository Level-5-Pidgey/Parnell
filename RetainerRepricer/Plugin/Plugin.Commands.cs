using System;
using System.Threading.Tasks;
using RetainerRepricer.Services;

namespace RetainerRepricer;

/// <summary>
/// Command routing for /repricer operations.
/// </summary>
public sealed partial class Plugin
{
    private void OnCommand(string command, string args)
    {
        var tokens = string.IsNullOrWhiteSpace(args)
            ? Array.Empty<string>()
            : args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var primary = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : string.Empty;
        var secondary = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : string.Empty;

        switch (primary)
        {
            case "":
                OpenSellListTab();
                return;

            case "?":
            case "help":
                PrintHelp();
                return;

            case "start":
                HandleStartCommand(secondary);
                return;

            case "stop":
                HandleStopCommand();
                return;

            case "config":
            case "c":
                OpenSettingsTab();
                return;

            case "logs":
            case "log":
                OpenLogWindow();
                return;

            case "debug":
                OpenDebugWindow();
                return;

            default:
                PrintHelp();
                return;
        }
    }

    private void HandleStartCommand(string modeArg)
    {
        var (mode, recognized) = ResolveRunMode(modeArg);
        if (!recognized && !string.IsNullOrEmpty(modeArg))
        {
            PrintError($"Unknown start mode '{modeArg}'. Using default (repricing + selling).");
        }

        _ = StartRun(mode, notifyChatOnFailure: true);
    }

    private void HandleStopCommand()
    {
        if (IsRunning)
        {
            StopRun();
            return;
        }

        PrintError("Not currently running.");
    }

    internal async Task DumpUniversalisDebugAsync(uint itemId, bool isHq)
    {
        var region = GetWorldDcRegionKey();
        if (string.IsNullOrWhiteSpace(region))
        {
            PrintError("Cannot resolve world or DC for Universalis debug request.");
            return;
        }

        var baseUrl = UniversalisApiClient.AggregatedBaseUrl;

        try
        {
            var rawJsonTask = _universalisClient.GetAggregatedPayloadAsync(baseUrl, region, itemId);
            var statsTask = _universalisClient.GetListingStatsAsync(baseUrl, region, itemId, isHq, cacheDuration: TimeSpan.Zero);

            await Task.WhenAll(rawJsonTask, statsTask).ConfigureAwait(false);

            var rawJson = rawJsonTask.Result;
            var stats = statsTask.Result;

            if (rawJson == null)
            {
                await Framework.RunOnFrameworkThread(() => PrintError($"Universalis debug request failed for item {itemId}."))
                    .ConfigureAwait(false);
                return;
            }

            Log.Information($"[RR][Debug] Universalis aggregated payload for item {itemId} (HQ={isHq}): {rawJson}");

            if (stats?.AveragePrice is { } avg)
            {
                var velocityText = stats.DailySaleVelocity is { } vel
                    ? vel.ToString("0.###")
                    : "n/a";
                Log.Information($"[RR][Debug] Computed average={avg:0.###}, velocity={velocityText} (HQ={isHq}).");
            }
            else
            {
                Log.Information($"[RR][Debug] No computed average returned for item {itemId} (HQ={isHq}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[RR][Debug] Failed to fetch Universalis data for item {itemId}.");
            await Framework.RunOnFrameworkThread(() => PrintError("Universalis debug request failed. See log for details."))
                .ConfigureAwait(false);
        }
    }

    private static (RunMode mode, bool recognized) ResolveRunMode(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return (RunMode.PriceAndSell, true);

        return arg.ToLowerInvariant() switch
        {
            "price" => (RunMode.PriceOnly, true),
            "sell" => (RunMode.SellOnly, true),
            "both" => (RunMode.PriceAndSell, true),
            _ => (RunMode.PriceAndSell, false)
        };
    }

    private void PrintHelp()
    {
        PrintInfo("/repricer, /rr - Open the MB Sell List tab.");
        PrintInfo("/repricer help or /repricer ? - Show this help (aliases work for /rr).");
        PrintInfo("/repricer start [mode] - Begin automation (omit mode to run both).");
        PrintInfo("--Modes: price = reprice listings only, sell = Sell List only");
        PrintInfo("/repricer stop - Stop the current run and unwind open UI");
        PrintInfo("/repricer config | c - Open the Settings tab of the configuration window");
        PrintInfo("/repricer logs | log - Open the plugin log window");
        PrintInfo("/repricer debug - Open the button-driven debug window");
    }
}
