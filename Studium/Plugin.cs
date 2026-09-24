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

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("Studium");
    public MeterWindow MeterWindow { get; }
    public SettingsWindow SettingsWindow { get; }
    public DebugWindow DebugWindow { get; }
    public GameCombatEventSource CombatEvents { get; }
    public GameNames Names { get; }
    public FightService Fights { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Names = new GameNames(DataManager);
        CombatEvents = new GameCombatEventSource(GameInterop, ObjectTable, Log);

        // Hooks are live from here on; if anything below fails, release them so the game isn't left hooked.
        try
        {
            Fights = new FightService(CombatEvents, Framework, Condition, ObjectTable, PartyList, DutyState, ClientState, Names);

            MeterWindow = new MeterWindow(this) { IsOpen = Configuration.MeterOpen };
            SettingsWindow = new SettingsWindow(this);
            DebugWindow = new DebugWindow(CombatEvents, ObjectTable, PartyList, Names);
            windowSystem.AddWindow(MeterWindow);
            windowSystem.AddWindow(SettingsWindow);
            windowSystem.AddWindow(DebugWindow);

            foreach (var command in Commands)
            {
                CommandManager.AddHandler(command, new CommandInfo(OnCommand)
                {
                    HelpMessage = "Toggle the meter. Subcommands: config, history.",
                });
            }

            PluginInterface.UiBuilder.Draw += windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMeter;
            PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        }
        catch
        {
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
        Fights.Dispose();
        CombatEvents.Dispose();
    }

    public void ToggleMeter() => MeterWindow.Toggle();

    public void OpenSettings()
    {
        SettingsWindow.IsOpen = true;
        SettingsWindow.BringToFront();
    }

    public void OpenHistory() =>
        ChatGui.Print("[Studium] The history browser isn't built yet.");

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
