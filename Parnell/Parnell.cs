using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.Schedulers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Parnell.Automation;
using Parnell.Services;
using Parnell.Tickable;

namespace Parnell;

public sealed class Plugin : IDalamudPlugin
{
    internal static Plugin Instance;
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

    private const string CommandName = "/updatelistings";
    public Configuration Configuration { get; init; }
    public static TaskManager TaskManager { get; private set; } = null!;
    public static PriceService PriceService { get; private set; } = null!;

    public Plugin(IDalamudPluginInterface pi)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ECommonsMain.Init(pi, this, Module.DalamudReflector);
        
        PriceService = new PriceService();
        var scheduler = new TickScheduler(Init);
        
        CommandManager.AddHandler(CommandName, new CommandInfo(MarketUpdateCommand)
        {
            HelpMessage = "Updates retainer listings based on DC price floors."
        });

        Framework.Update += OnFrameworkUpdate;
    }

    private void Init()
    {
        var debug = false;
        #if DEBUG
        debug = true;
        #endif
        TaskManager = new TaskManager(new TaskManagerConfiguration(abortOnTimeout: true, showError: true, showDebug: debug));
        Framework.Update += Tick;
    }

    private void Tick(IFramework framework)
    {
        SkipChatter.Tick();
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        TaskManager.Dispose();
        ECommonsMain.Dispose();
        Framework.Update -= Tick;
    }

    private void MarketUpdateCommand(string command, string args)
    {
        MarketUpdateTask.Enable();
    }
}
