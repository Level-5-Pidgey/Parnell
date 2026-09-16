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
    
    private readonly TaskManager taskManager;
    private readonly PriceService priceService;
    private readonly MainScheduler mainScheduler;
    private readonly RetainerListHandlers retainerListHandlers;
    private readonly RetainerMarketboardHandler retainerMarketboardHandler;
    private readonly InteractWithBell interactWithBell;
    private readonly SkipChatter skipChatter;

    public MarketboardHandler MarketboardHandler { get; private set; }
    public AutoRetainerHandler AutoRetainerHandler { get; private set; }

    public Parnell(IDalamudPluginInterface pi)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ECommonsMain.Init(pi, this, Module.DalamudReflector);
        
        var debug = false;
        #if DEBUG
        debug = true;
        #endif
        
        taskManager = new TaskManager(new TaskManagerConfiguration(abortOnTimeout: true, showError: true, showDebug: debug));
        priceService = new PriceService();
        retainerListHandlers = new RetainerListHandlers();
        retainerMarketboardHandler = new RetainerMarketboardHandler(taskManager, priceService);
        interactWithBell = new InteractWithBell(taskManager);
        mainScheduler = new MainScheduler(taskManager, priceService, retainerListHandlers, retainerMarketboardHandler, interactWithBell);
        skipChatter = new SkipChatter(taskManager, mainScheduler);
        
        MarketboardHandler = new MarketboardHandler(priceService, MarketBoard);
        AutoRetainerHandler = new AutoRetainerHandler(mainScheduler);
        
        Framework.Update += Tick;

        CommandManager.AddHandler(CommandName, new CommandInfo(MarketUpdateCommand)
        {
            HelpMessage = "Updates retainer listings based on DC price floors."
        });
    }

    private void Tick(IFramework framework)
    {
        skipChatter.Tick();
        if (mainScheduler.Enabled && Svc.Objects.LocalPlayer != null)
        {
            mainScheduler.Tick();
        }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        taskManager.Dispose();
        ECommonsMain.Dispose();
        Framework.Update -= Tick;
    }

    private unsafe void MarketUpdateCommand(string command, string args)
    {
        mainScheduler.Enabled = !mainScheduler.Enabled;
        if (!mainScheduler.Enabled)
        {
            taskManager.Abort();
        }
        else
        {
            if (mainScheduler.Retainers.Count > 0)
            {
                return;
            }

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

                mainScheduler.Retainers.Add(new Retainer(gameRetainer));
            }
        }

        Svc.Log.Info($"Toggled operations, enabled = {mainScheduler.Enabled}.");
    }
}
