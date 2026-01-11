using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Schedulers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Parnell.Handlers;
using Parnell.Scheduler;
using Parnell.Services;
using Parnell.Tickable;

namespace Parnell;

public sealed class Parnell : IDalamudPlugin
{
    public string Name => "Parnell";

    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    public static IMarketBoard MarketBoard { get; private set; } = null!;

    private const string CommandName = "/updatelistings";
    public Configuration Configuration { get; init; }
    public static TaskManager TaskManager { get; private set; } = null!;
    public static PriceService PriceService { get; private set; } = null!;

    public MarketboardHandler MarketboardHandler { get; private set; } = null!;

    public Parnell(IDalamudPluginInterface pi)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ECommonsMain.Init(pi, this, Module.DalamudReflector);
        
        PriceService = new PriceService();
        _ = new TickScheduler(Init);

        CommandManager.AddHandler(CommandName, new CommandInfo(MarketUpdateCommand)
        {
            HelpMessage = "Updates retainer listings based on DC price floors."
        });
    }

    private void Init()
    {
        var debug = false;
        #if DEBUG
        debug = true;
        #endif
        TaskManager = new TaskManager(new TaskManagerConfiguration(abortOnTimeout: true, showError: true, showDebug: debug));
        MarketboardHandler = new MarketboardHandler();
        Framework.Update += Tick;
    }

    private static void Tick(IFramework framework)
    {
        SkipChatter.Tick();
        if (MainScheduler.Enabled && Svc.Objects.LocalPlayer != null)
        {
            MainScheduler.Tick();
        }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        TaskManager.Dispose();
        ECommonsMain.Dispose();
        Framework.Update -= Tick;
    }

    private unsafe void MarketUpdateCommand(string command, string args)
    {
        MainScheduler.Enabled = !MainScheduler.Enabled;
        if (!MainScheduler.Enabled)
        {
            TaskManager.Abort();
        }
        else
        {
            var retainerManager = RetainerManager.Instance();
            if (!retainerManager->IsReady)
            {
                return;
            }

            foreach (var gameRetainer in retainerManager->Retainers)
            {
                var retainer = new Retainer(gameRetainer);

                if (retainer.Name == string.Empty || retainer.MarketItemCount == 0)
                {
                    continue;
                }

                RetainerListHandlers.Retainers.Add(new Retainer(gameRetainer));
            }
        }

        Svc.Log.Info($"Toggled operations, enabled = {MainScheduler.Enabled}.");
    }
}
