using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Echo.Windows;

namespace Echo;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string[] Commands = ["/echo", "/dps"];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("Echo");
    public MeterWindow MeterWindow { get; }
    public SettingsWindow SettingsWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        MeterWindow = new MeterWindow(this) { IsOpen = Configuration.MeterOpen };
        SettingsWindow = new SettingsWindow(this);
        windowSystem.AddWindow(MeterWindow);
        windowSystem.AddWindow(SettingsWindow);

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

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMeter;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;

        foreach (var command in Commands)
            CommandManager.RemoveHandler(command);

        windowSystem.RemoveAllWindows();
    }

    public void ToggleMeter() => MeterWindow.Toggle();

    public void OpenSettings()
    {
        SettingsWindow.IsOpen = true;
        SettingsWindow.BringToFront();
    }

    public void OpenHistory() =>
        ChatGui.Print("[Echo] The history browser isn't built yet.");

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
            default:
                ChatGui.PrintError($"[Echo] Unknown subcommand \"{args.Trim()}\". Use {command}, {command} config or {command} history.");
                break;
        }
    }
}
