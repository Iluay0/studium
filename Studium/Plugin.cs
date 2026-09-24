using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Studium.Combat;
using Studium.Windows;

namespace Studium;

public sealed class Plugin : IDalamudPlugin
{
    public const string DisplayName = "Studium";

    private static readonly string[] Commands = ["/studium", "/dps"];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("Studium");
    public MeterWindow MeterWindow { get; }
    public SettingsWindow SettingsWindow { get; }
    public DebugWindow DebugWindow { get; }
    public DrillDownWindow DrillDownWindow { get; }
    public HistoryWindow HistoryWindow { get; }
    public GameCombatEventSource CombatEvents { get; }
    public GameNames Names { get; }
    public FightService Fights { get; }
    public FightHistory History { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate())
            Configuration.Save();

        Names = new GameNames(DataManager);
        CombatEvents = new GameCombatEventSource(GameInterop, ObjectTable, Log, Names);

        // Hooks are live from here on; if anything below fails, release them so the game isn't left hooked.
        try
        {
            Fights = new FightService(CombatEvents, Framework, Condition, ObjectTable, PartyList, DutyState, ClientState, Names, Configuration);
            History = new FightHistory(Configuration, Fights.Tracker, Framework, Log,
                Path.Combine(PluginInterface.GetPluginConfigDirectory(), "fights"));

            MeterWindow = new MeterWindow(this) { IsOpen = Configuration.MeterOpen };
            SettingsWindow = new SettingsWindow(this);
            DebugWindow = new DebugWindow(CombatEvents, ObjectTable, PartyList, Names);
            DrillDownWindow = new DrillDownWindow(this);
            HistoryWindow = new HistoryWindow(this);
            windowSystem.AddWindow(MeterWindow);
            windowSystem.AddWindow(SettingsWindow);
            windowSystem.AddWindow(DebugWindow);
            windowSystem.AddWindow(DrillDownWindow);
            windowSystem.AddWindow(HistoryWindow);

            foreach (var command in Commands)
            {
                CommandManager.AddHandler(command, new CommandInfo(OnCommand)
                {
                    HelpMessage = "Toggle the meter. Subcommands: config, history.",
                });
            }

            ApplyUiHide();
            Configuration.Saved += ApplyUiHide;

            PluginInterface.UiBuilder.Draw += windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMeter;
            PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        }
        catch
        {
            (History as IDisposable)?.Dispose();
            (Fights as IDisposable)?.Dispose();
            CombatEvents.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMeter;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;

        foreach (var command in Commands)
            CommandManager.RemoveHandler(command);

        windowSystem.RemoveAllWindows();
        MeterWindow.Dispose();
        DebugWindow.Dispose();
        History.Dispose();
        Fights.Dispose();
        CombatEvents.Dispose();
    }

    public void ToggleMeter()
    {
        MeterWindow.Toggle();
        if (MeterWindow.IsOpen && !MeterWindow.AllowedByVisibility)
        {
            var when = Configuration.Visibility == Core.MeterVisibility.InDuty ? "in duties" : "in combat";
            ChatGui.Print($"[Studium] The meter is open but only shows {when}. Change it in /dps config → Meter → Show meter.");
        }
    }

    /// <summary>Dalamud hides plugin windows in cutscenes by default; our setting decides.</summary>
    private void ApplyUiHide() => PluginInterface.UiBuilder.DisableCutsceneUiHide = !Configuration.HideInCutscenes;

    public void OpenSettings()
    {
        SettingsWindow.IsOpen = true;
        SettingsWindow.BringToFront();
    }

    public void OpenHistory()
    {
        HistoryWindow.IsOpen = true;
        HistoryWindow.BringToFront();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                ToggleMeter();
                break;
            case "config":
            case "settings":
                OpenSettings();
                break;
            case "history":
                OpenHistory();
                break;
            case "debug":
                DebugWindow.Toggle();
                break;
            default:
                ChatGui.PrintError($"[Studium] Unknown subcommand \"{args.Trim()}\". Use {command}, {command} config or {command} history.");
                break;
        }
    }
}
